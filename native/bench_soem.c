#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>

#define HAVE_REMOTE
#include <soem/soem.h>
#include <soem/ec_main.h>
#include <soem/ec_type.h>
#include <soem/ec_base.h>
#include <soem/ec_config.h>
#include <soem/ec_coe.h>
#include <soem/ec_dc.h>

#define TB_OPEN_DEADLINE_MS 3000
#define TB_CANCEL_RETRY_MS 100

#ifndef EC_TIMEOUTRET
#define EC_TIMEOUTRET 2000
#endif

#ifndef EC_TIMEOUTRXM
#define EC_TIMEOUTRXM 700000
#endif

#define TB_EXPORT __declspec(dllexport)
#define TB_IO_MAP_SIZE 262144u
#define TB_SDO_MAX 4096

static ecx_contextt g_ctx;
static uint8_t g_iomap[TB_IO_MAP_SIZE];

static int g_open;
static int g_mapped;
static int g_dc_configured;
static int g_dc_on;
static uint32_t g_obytes;
static uint32_t g_ibytes;
static int g_expected;
static int g_online_outbits = -1;
static int g_online_inbits = -1;

TB_EXPORT void tb_close(void);

struct tb_open_deadline
{
    HANDLE owner;
    volatile LONG expired;
};

static int tb_deadline_expired(struct tb_open_deadline *guard)
{
    return InterlockedCompareExchange(&guard->expired, 0, 0) != 0;
}

static void CALLBACK tb_cancel_stalled_io(PVOID data, BOOLEAN fired)
{
    struct tb_open_deadline *guard = data;
    (void)fired;
    InterlockedExchange(&guard->expired, 1);
    CancelSynchronousIo(guard->owner);
}

static void tb_copy_text(char *dst, const char *src, int cap)
{
    size_t n;

    if (dst == NULL || cap <= 0)
        return;

    if (src == NULL)
        src = "";

    n = strlen(src);
    if (n > (size_t)(cap - 1))
        n = (size_t)(cap - 1);

    memcpy(dst, src, n);
    dst[n] = '\0';
}

static void tb_drain_errors(uint16_t index, uint8_t sub, uint32_t *abort_code)
{
    ec_errort err;

    while (ecx_poperror(&g_ctx, &err))
    {
        if (abort_code != NULL &&
            err.Etype == EC_ERR_TYPE_SDO_ERROR &&
            err.Slave == 1 &&
            err.Index == index &&
            err.SubIdx == sub)
        {
            *abort_code = (uint32_t)err.AbortCode;
        }
    }
}

static int tb_read_esc_type(int *send_result)
{
    ecx_portt *port = &g_ctx.port;
    uint16_t type_word = 0;
    uint8_t index = ecx_getindex(port);
    int wkc = EC_NOFRAME;

    ecx_setupdatagram(port, &port->txbuf[index], EC_CMD_BRD, index, 0,
                      ECT_REG_TYPE, (uint16_t)sizeof(type_word), &type_word);
    *send_result = ecx_outframe_red(port, index);
    if (*send_result == 0)
        wkc = ecx_waitinframe(port, index, 20000);
    ecx_setbufstat(port, index, EC_BUF_EMPTY);
    return wkc;
}

static void tb_reset_state(void)
{
    memset(&g_ctx, 0, sizeof(g_ctx));
    g_open = 0;
    g_mapped = 0;
    g_dc_configured = 0;
    g_dc_on = 0;
    g_obytes = 0;
    g_ibytes = 0;
    g_expected = 0;
}

TB_EXPORT int tb_version(void)
{
    return 3;
}

TB_EXPORT int tb_adapter(int index, char *name, char *desc, int cap)
{
    ec_adaptert *list;
    ec_adaptert *cur;
    int i;

    if (index < 0)
        return 0;

    list = ec_find_adapters();
    if (list == NULL)
        return 0;

    cur = list;
    for (i = 0; i < index && cur != NULL; ++i)
        cur = cur->next;

    if (cur == NULL)
    {
        ec_free_adapters(list);
        return 0;
    }

    tb_copy_text(name, cur->name, cap);
    tb_copy_text(desc, cur->desc, cap);
    ec_free_adapters(list);
    return 1;
}

static int tb_open_guarded(const char *nic, char *detail, int detail_cap,
                           struct tb_open_deadline *guard)
{
    int count, init_count, attempt, sent, send_result;
    char message[PCAP_ERRBUF_SIZE];

    if (g_open)
        tb_close();

    memset(&g_ctx, 0, sizeof(g_ctx));

    if (!ecx_init(&g_ctx, nic))
    {
        pcap_t *diagnostic;
        if (tb_deadline_expired(guard))
            return -5;
        message[0] = '\0';
        diagnostic = pcap_open(nic, 65536, PCAP_OPENFLAG_PROMISCUOUS |
            PCAP_OPENFLAG_MAX_RESPONSIVENESS | PCAP_OPENFLAG_NOCAPTURE_LOCAL, -1, NULL, message);
        if (diagnostic != NULL)
        {
            pcap_close(diagnostic);
            tb_copy_text(detail, "pcap_open succeeded on a diagnostic retry", detail_cap);
        }
        else
            tb_copy_text(detail, message, detail_cap);
        memset(&g_ctx, 0, sizeof(g_ctx));
        return -1;
    }

    // SOEM checks its deadline between pcap_next_ex calls. A blocking
    // capture handle would keep that deadline from being reached.
    message[0] = '\0';
    if (pcap_setnonblock(g_ctx.port.sockhandle, 1, message) != 0)
    {
        tb_copy_text(detail, message, detail_cap);
        ecx_close(&g_ctx);
        tb_reset_state();
        return -4;
    }

    count = EC_NOFRAME;
    sent = 0;
    send_result = 0;
    for (attempt = 0; attempt < 3 && count <= 0 && !tb_deadline_expired(guard); ++attempt)
    {
        count = tb_read_esc_type(&send_result);
        if (send_result == 0)
            ++sent;
    }

    if (tb_deadline_expired(guard))
    {
        ecx_close(&g_ctx);
        tb_reset_state();
        return -5;
    }

    if (sent == 0)
    {
        const char *pcap_error = pcap_geterr(g_ctx.port.sockhandle);
        snprintf(message, sizeof(message), "pcap_sendpacket=%d%s%s", send_result,
                 pcap_error && pcap_error[0] ? ": " : "",
                 pcap_error ? pcap_error : "");
        tb_copy_text(detail, message, detail_cap);
        ecx_close(&g_ctx);
        tb_reset_state();
        return -3;
    }

    if (count <= 0 || count > 1)
    {
        snprintf(message, sizeof(message), "Npcap TX accepted=%d/%d, ESC TYPE BRD WKC=%d",
                 sent, attempt, count);
        tb_copy_text(detail, message, detail_cap);
        ecx_close(&g_ctx);
        tb_reset_state();
        return count < 0 ? 0 : count;
    }

    init_count = ecx_config_init(&g_ctx);
    if (tb_deadline_expired(guard) || init_count <= 0 ||
        g_ctx.slavecount != 1 ||
        g_ctx.slavelist[1].configadr == 0)
    {
        snprintf(message, sizeof(message), "config_init=%d, slavecount=%d, station=0x%04X",
            init_count, g_ctx.slavecount, (unsigned)g_ctx.slavelist[1].configadr);
        tb_copy_text(detail, message, detail_cap);
        ecx_close(&g_ctx);
        tb_reset_state();
        return -2;
    }

    g_open = 1;
    g_mapped = 0;
    g_dc_configured = 0;
    g_dc_on = 0;
    g_obytes = 0;
    g_ibytes = 0;
    g_expected = 0;
    return 1;
}

TB_EXPORT int tb_open(const char *nic, char *detail, int detail_cap)
{
    struct tb_open_deadline guard = { NULL, 0 };
    HANDLE timer = NULL;
    int result;

    tb_copy_text(detail, "", detail_cap);
    if (nic == NULL || nic[0] == '\0')
    {
        tb_copy_text(detail, "No adapter selected", detail_cap);
        return -1;
    }

    guard.owner = OpenThread(THREAD_TERMINATE, FALSE, GetCurrentThreadId());
    if (guard.owner == NULL ||
        !CreateTimerQueueTimer(&timer, NULL, tb_cancel_stalled_io, &guard,
                               TB_OPEN_DEADLINE_MS, TB_CANCEL_RETRY_MS, WT_EXECUTEDEFAULT))
    {
        if (guard.owner != NULL)
            CloseHandle(guard.owner);
        tb_copy_text(detail, "Could not start the scan timeout guard", detail_cap);
        return -6;
    }

    result = tb_open_guarded(nic, detail, detail_cap, &guard);
    DeleteTimerQueueTimer(NULL, timer, INVALID_HANDLE_VALUE);
    CloseHandle(guard.owner);
    if (tb_deadline_expired(&guard) && result != 1)
    {
        tb_copy_text(detail, "3 s deadline; Windows synchronous I/O cancellation requested", detail_cap);
        return -5;
    }
    return result;
}

TB_EXPORT void tb_close(void)
{
    if (!g_open)
    {
        tb_reset_state();
        return;
    }

    if (g_ctx.slavelist[1].hasdc &&
        (g_dc_on || g_ctx.slavelist[1].DCactive))
    {
        ecx_dcsync0(&g_ctx, 1, 0, 0, 0);
    }

    g_ctx.slavelist[1].state = EC_STATE_INIT;
    ecx_writestate(&g_ctx, 1);

    ecx_close(&g_ctx);
    tb_reset_state();
}

TB_EXPORT int tb_info(uint32_t *vendor, uint32_t *product, uint32_t *rev, int *hasdc)
{
    if (!g_open || g_ctx.slavecount < 1)
    {
        if (vendor != NULL)
            *vendor = 0;
        if (product != NULL)
            *product = 0;
        if (rev != NULL)
            *rev = 0;
        if (hasdc != NULL)
            *hasdc = 0;
        return 0;
    }

    if (vendor != NULL)
        *vendor = g_ctx.slavelist[1].eep_man;
    if (product != NULL)
        *product = g_ctx.slavelist[1].eep_id;
    if (rev != NULL)
        *rev = g_ctx.slavelist[1].eep_rev;
    if (hasdc != NULL)
        *hasdc = g_ctx.slavelist[1].hasdc ? 1 : 0;

    return 1;
}

TB_EXPORT int tb_status(int *state, int *al)
{
    uint8_t buf[6];
    uint16_t adr;
    int wkc;
    int st;
    int code;

    if (!g_open || g_ctx.slavecount < 1)
        return 0;

    adr = g_ctx.slavelist[1].configadr;
    if (adr == 0)
        return 0;

    memset(buf, 0, sizeof(buf));
    wkc = ecx_FPRD(&g_ctx.port, adr, ECT_REG_ALSTAT,
                   (uint16_t)sizeof(buf), buf, EC_TIMEOUTRET);
    if (wkc <= 0)
        return 0;

    st = (int)buf[0] | ((int)buf[1] << 8);
    code = (int)buf[4] | ((int)buf[5] << 8);

    g_ctx.slavelist[1].state = (uint16_t)st;
    g_ctx.slavelist[1].ALstatuscode = (uint16_t)code;

    if (state != NULL)
        *state = st;
    if (al != NULL)
        *al = code;

    return 1;
}

TB_EXPORT int tb_set_state(int state)
{
    if (!g_open || g_ctx.slavecount < 1)
        return 0;

    if (state != 1 && state != 2 && state != 4 && state != 8)
        return 0;

    if (state == EC_STATE_INIT && g_ctx.slavelist[1].DCactive)
    {
        ecx_dcsync0(&g_ctx, 1, 0, 0, 0);
        g_dc_on = 0;
        g_dc_configured = 0;
    }
    g_ctx.slavelist[1].state = (uint16_t)(state | EC_STATE_ACK);
    return ecx_writestate(&g_ctx, 1) > 0 ? 1 : 0;
}

/* Used by the SOEM configuration unit when SM2/SM3 were read and validated
   individually before mapping. SOEM still programs the SMs and FMMUs. */
int tb_apply_online_pdo_sizes(ecx_contextt *context, uint16 slave,
                              uint32 *outbits, uint32 *inbits)
{
    ec_slavet *entry;
    if (context != &g_ctx || slave != 1 ||
        g_online_outbits < 0 || g_online_inbits < 0)
        return 0;

    entry = &context->slavelist[slave];
    *outbits = (uint32)g_online_outbits;
    *inbits = (uint32)g_online_inbits;
    entry->SMtype[2] = 3;
    entry->SMtype[3] = 4;
    entry->SM[2].SMlength = htoes((uint16)((*outbits + 7u) / 8u));
    entry->SM[3].SMlength = htoes((uint16)((*inbits + 7u) / 8u));
    return 1;
}

TB_EXPORT int tb_map(int online_outbits, int online_inbits,
                     int *outbits, int *inbits, int *expected)
{
    int st;
    int al;
    int mapped_wkc;
    uint32_t ob;
    uint32_t ib;
    uint32_t gob;
    uint32_t gib;
    int exp;

    if (!g_open || g_ctx.slavecount < 1)
        return 0;

    if (!tb_status(&st, &al))
        return 0;

    if ((st & 0x0f) != EC_STATE_PRE_OP)
        return 0;

    g_mapped = 0;
    memset(g_iomap, 0, sizeof(g_iomap));
    g_ctx.manualstatechange = 1;
    if (online_outbits >= 0 && online_inbits >= 0 &&
        online_outbits <= 32768 && online_inbits <= 32768 &&
        (online_outbits || online_inbits))
    {
        if ((online_outbits && g_ctx.slavelist[1].SM[2].StartAddr == 0) ||
            (online_inbits && g_ctx.slavelist[1].SM[3].StartAddr == 0))
            return 0;
        g_online_outbits = online_outbits;
        g_online_inbits = online_inbits;
    }
    mapped_wkc = ecx_config_map_group(&g_ctx, g_iomap, 0);
    g_online_outbits = g_online_inbits = -1;
    if (mapped_wkc < 0)
        return 0;

    ob = (g_ctx.slavelist[1].Obits + 7u) / 8u;
    ib = (g_ctx.slavelist[1].Ibits + 7u) / 8u;
    gob = g_ctx.grouplist[0].Obytes;
    gib = g_ctx.grouplist[0].Ibytes;

    if (g_ctx.slavelist[1].Ostartbit != 0 ||
        g_ctx.slavelist[1].Istartbit != 0)
        return 0;

    if (ob > 4096u || ib > 4096u || gob > 4096u || gib > 4096u)
        return 0;

    if ((ob > 0 && g_ctx.slavelist[1].outputs == NULL) ||
        (ib > 0 && g_ctx.slavelist[1].inputs == NULL))
        return 0;

    exp = (int)g_ctx.grouplist[0].outputsWKC * 2 +
          (int)g_ctx.grouplist[0].inputsWKC;

    if (ob > 0)
        memset(g_ctx.slavelist[1].outputs, 0, ob);

    g_mapped = 1;
    g_obytes = ob;
    g_ibytes = ib;
    g_expected = exp;

    if (outbits != NULL)
        *outbits = (int)g_ctx.slavelist[1].Obits;
    if (inbits != NULL)
        *inbits = (int)g_ctx.slavelist[1].Ibits;
    if (expected != NULL)
        *expected = exp;

    return 1;
}

static int tb_cycle_impl(const uint8_t *output, int olen, uint8_t *input, int ilen,
                         int64_t *send_ticks, int64_t *receive_ticks)
{
    int sent;
    int wkc;
    LARGE_INTEGER before;
    LARGE_INTEGER after;
    BOOL timing;

    if (!g_open || !g_mapped)
        return -1;

    if (olen < 0 || ilen < 0)
        return -1;

    if (olen != (int)g_obytes || ilen != (int)g_ibytes)
        return -1;

    if ((olen > 0 && output == NULL) || (ilen > 0 && input == NULL))
        return -1;

    if (olen > 0)
        memcpy(g_ctx.slavelist[1].outputs, output, (size_t)olen);

    timing = send_ticks && QueryPerformanceCounter(&before);
    sent = ecx_send_processdata_group(&g_ctx, 0);
    if (timing && QueryPerformanceCounter(&after))
        *send_ticks = after.QuadPart - before.QuadPart;
    if (sent <= 0)
        return -1;

    timing = receive_ticks && QueryPerformanceCounter(&before);
    wkc = ecx_receive_processdata_group(&g_ctx, 0, EC_TIMEOUTRET);
    if (timing && QueryPerformanceCounter(&after))
        *receive_ticks = after.QuadPart - before.QuadPart;
    if (wkc < 0)
        return -1;

    if (wkc == g_expected && ilen > 0)
        memcpy(input, g_ctx.slavelist[1].inputs, (size_t)ilen);

    return wkc;
}

TB_EXPORT int tb_cycle(const uint8_t *output, int olen, uint8_t *input, int ilen)
{
    return tb_cycle_impl(output, olen, input, ilen, NULL, NULL);
}

/* Optional ABI v3 export. An unavailable or unattempted stage returns -1 ticks. */
TB_EXPORT int tb_cycle_timed(const uint8_t *output, int olen, uint8_t *input, int ilen,
                            int64_t *send_ticks, int64_t *receive_ticks)
{
    if (send_ticks) *send_ticks = -1;
    if (receive_ticks) *receive_ticks = -1;
    return tb_cycle_impl(output, olen, input, ilen, send_ticks, receive_ticks);
}

TB_EXPORT int tb_sdo(int write, uint16_t index, uint8_t sub,
                     uint8_t *data, int *size, uint32_t *abort)
{
    int sz;
    int ret;

    if (!g_open || g_ctx.slavecount < 1 || size == NULL)
        return 0;

    sz = *size;
    if (sz < 0 || sz > TB_SDO_MAX)
        return 0;
    if (sz > 0 && data == NULL)
        return 0;
    if (!write && sz == 0)
        return 0;

    if (abort != NULL)
        *abort = 0;

    tb_drain_errors(index, sub, NULL);

    if (write)
    {
        ret = ecx_SDOwrite(&g_ctx, 1, index, sub, 0, sz, data, EC_TIMEOUTRXM);
    }
    else
    {
        ret = ecx_SDOread(&g_ctx, 1, index, sub, 0, &sz, data, EC_TIMEOUTRXM);
        if (sz < 0 || sz > TB_SDO_MAX)
        {
            tb_drain_errors(index, sub, abort);
            *size = 0;
            return 0;
        }
    }

    *size = sz;
    tb_drain_errors(index, sub, abort);
    return ret > 0 ? 1 : 0;
}

TB_EXPORT int tb_dc(uint32_t assign, uint32_t cycle_ns, int32_t shift_ns)
{
    uint8_t act_byte;
    uint8_t cycle_bytes[4];
    uint32_t cycle_read;
    uint16_t adr;
    int st;
    int al;
    int wkc;

    if (!g_open || g_ctx.slavecount < 1)
        return 0;

    if (assign != 0u && assign != 0x300u)
        return 0;

    if (!tb_status(&st, &al))
        return 0;

    if ((st & 0x0f) != EC_STATE_PRE_OP)
        return 0;

    if (assign != 0u)
    {
        if (!g_ctx.slavelist[1].hasdc || cycle_ns == 0u)
            return 0;
    }
    else if (!g_ctx.slavelist[1].hasdc)
    {
        return 1;
    }

    if (!g_dc_configured)
    {
        if (!ecx_configdc(&g_ctx))
            return 0;
        g_dc_configured = 1;
    }

    if (assign != 0u)
        ecx_dcsync0(&g_ctx, 1, 1, cycle_ns, shift_ns);
    else
        ecx_dcsync0(&g_ctx, 1, 0, 0, 0);

    adr = g_ctx.slavelist[1].configadr;
    if (adr == 0)
        return 0;

    act_byte = 0;
    wkc = ecx_FPRD(&g_ctx.port, adr, ECT_REG_DCSYNCACT,
                   (uint16_t)sizeof(act_byte), &act_byte, EC_TIMEOUTRET);
    if (wkc <= 0)
        return 0;

    memset(cycle_bytes, 0, sizeof(cycle_bytes));
    wkc = ecx_FPRD(&g_ctx.port, adr, ECT_REG_DCCYCLE0,
                   (uint16_t)sizeof(cycle_bytes), cycle_bytes, EC_TIMEOUTRET);
    if (wkc <= 0)
        return 0;

    cycle_read = (uint32_t)cycle_bytes[0] |
                 ((uint32_t)cycle_bytes[1] << 8) |
                 ((uint32_t)cycle_bytes[2] << 16) |
                 ((uint32_t)cycle_bytes[3] << 24);

    if (assign == 0x300u)
    {
        if ((act_byte & 0x03u) != 0x03u)
            return 0;
        if (cycle_read != cycle_ns)
            return 0;
        g_dc_on = 1;
    }
    else
    {
        if ((act_byte & 0x07u) != 0u)
            return 0;
        if (cycle_read != 0u)
            return 0;
        g_dc_on = 0;
    }

    return 1;
}
