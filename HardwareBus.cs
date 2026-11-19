using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace EtherCatBench
{
    static class Soem
    {
        const string Library = "EtherCatBench.Soem.dll";
        const CallingConvention C = CallingConvention.Cdecl;
        static IntPtr pcap, bridge;
        static bool compatible;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string symbol);

        internal static bool HasTimedCycle { get; private set; }

        public static void Load()
        {
            if (compatible) return;
            const string mismatch = "SOEM 桥接库版本不匹配。请使用与 EXE 同目录的 DLL。";
            if (bridge != IntPtr.Zero) throw new InvalidOperationException(mismatch);
            if (!Environment.Is64BitProcess) throw new InvalidOperationException("真机模式需要 Windows x64。");
            var driver = Path.Combine(Environment.SystemDirectory, @"Npcap\wpcap.dll");
            if (pcap == IntPtr.Zero)
            {
                if (!File.Exists(driver)) throw new InvalidOperationException("未找到 Npcap 驱动。请在测试电脑单独安装 Npcap。");
                pcap = LoadLibraryEx(driver, IntPtr.Zero, 0x00000008);
                if (pcap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Npcap 无法加载。");
            }
            var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Library);
            if (!File.Exists(file)) throw new FileNotFoundException("真机模式缺少 " + Library, file);
            bridge = LoadLibraryEx(file, IntPtr.Zero, 0x00000100 | 0x00000800);
            if (bridge == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "SOEM 桥接库无法加载。");
            if (tb_version() != 3) throw new InvalidOperationException(mismatch);
            HasTimedCycle = Stopwatch.IsHighResolution && GetProcAddress(bridge, "tb_cycle_timed") != IntPtr.Zero;
            compatible = true;
        }

        [DllImport(Library, CallingConvention = C)] internal static extern int tb_version();
        [DllImport(Library, CallingConvention = C, CharSet = CharSet.Ansi)]
        internal static extern int tb_adapter(int index, StringBuilder name, StringBuilder description, int capacity);
        [DllImport(Library, CallingConvention = C, CharSet = CharSet.Ansi)]
        internal static extern int tb_open(string adapter, StringBuilder detail, int detailCapacity);
        [DllImport(Library, CallingConvention = C)] internal static extern void tb_close();
        [DllImport(Library, CallingConvention = C)]
        internal static extern int tb_info(out uint vendor, out uint product, out uint revision, out int dc);
        [DllImport(Library, CallingConvention = C)] internal static extern int tb_status(out int state, out int al);
        [DllImport(Library, CallingConvention = C)] internal static extern int tb_set_state(int state);
        [DllImport(Library, CallingConvention = C)]
        internal static extern int tb_map(int onlineOutputBits, int onlineInputBits,
            out int outputBits, out int inputBits, out int expectedWkc);
        [DllImport(Library, CallingConvention = C)]
        internal static extern int tb_cycle(byte[] output, int outputLength, [Out] byte[] input, int inputLength);
        [DllImport(Library, CallingConvention = C)]
        internal static extern int tb_cycle_timed(byte[] output, int outputLength, [Out] byte[] input,
            int inputLength, out long sendTicks, out long receiveTicks);
        [DllImport(Library, CallingConvention = C)]
        internal static extern int tb_sdo(int write, ushort index, byte sub, [In, Out] byte[] data, ref int size, out uint abort);
        [DllImport(Library, CallingConvention = C)] internal static extern int tb_dc(uint assign, uint ns, int shift);
    }

    sealed class HardwareAdapter
    {
        public readonly string Id, Label;
        HardwareAdapter(string id, string label) { Id = id; Label = label; }
        public override string ToString() { return Label; }

        public static HardwareAdapter[] List()
        {
            Soem.Load();
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                    n.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx ||
                    n.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT ||
                    n.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet).ToArray();
            var result = new List<HardwareAdapter>();
            var id = new StringBuilder(512);
            var description = new StringBuilder(512);
            for (int i = 0; i < 512 && Soem.tb_adapter(i, id, description, id.Capacity) == 1; i++)
            {
                string captureName = id.ToString();
                int start = captureName.LastIndexOf('{');
                Guid captureGuid;
                if (start < 0 || !Guid.TryParse(captureName.Substring(start), out captureGuid)) continue;
                var nic = adapters.FirstOrDefault(n => {
                    Guid idGuid;
                    return Guid.TryParse(n.Id, out idGuid) && idGuid == captureGuid;
                });
                string captureDescription = description.ToString();
                if (nic != null && !nic.Description.StartsWith("Bluetooth", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new HardwareAdapter(captureName, nic.Name + " — " + nic.Description +
                        " (" + (nic.OperationalStatus == OperationalStatus.Up ? "链路已连接" : "链路未连接") + ")"));
                }
                else if (nic == null && captureDescription.StartsWith("TwinCAT-Intel", StringComparison.OrdinalIgnoreCase))
                {
                    // Npcap can expose this miniport even when .NET omits it.
                    string connection = ConnectionName(captureGuid);
                    result.Add(new HardwareAdapter(captureName,
                        (string.IsNullOrEmpty(connection) ? "" : connection + " — ") + captureDescription));
                }
            }
            return result.ToArray();
        }

        static string ConnectionName(Guid adapter)
        {
            const string networkClass = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\";
            try { return Registry.GetValue(networkClass + adapter.ToString("B") + @"\Connection", "Name", null) as string; }
            catch (System.Security.SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    struct SlaveIdentity
    {
        public uint Vendor, Product, Revision;
        public bool Dc;
        public bool Matches(EsiDevice device)
        {
            return device != null && Vendor == device.Vendor && Product == device.Product && Revision == device.Revision;
        }
    }

    sealed class HardwareFrame
    {
        public SlaveIdentity Identity;
        public bool Connected, Online, Armed;
        public int State, Al, Wkc, Expected, CycleMs;
        public int OutBits, InBits;
        public long Cycles, BadWkc;
        public int AlMisses;
        public long AlTimeouts;
        public double LastIntervalMs, MaxIntervalMs, AverageIntervalMs;
        public long MissedSlots;
        public int TraceSamples;
        public double TraceSeconds;
        public bool TraceRecording;
        public bool HighResolutionTimer, EarlyWake;
        public uint DcNs;
        public string Note;
        public byte[] Rx, Tx;
        public PdoField[] Mapping;
    }

    sealed class LinkWatch
    {
        const int LossWindowMs = 500;
        const int MinimumMisses = 5;
        long firstMiss, lastPdo = -1;

        public int AlMisses { get; private set; }
        public long AlTimeouts { get; private set; }

        public bool ObserveAl(bool replied, long now)
        {
            if (replied)
            {
                AlMisses = 0;
                return false;
            }
            if (AlMisses == 0) firstMiss = now;
            AlMisses++;
            AlTimeouts++;
            return AlMisses >= MinimumMisses && now - firstMiss >= LossWindowMs &&
                (lastPdo < 0 || now - lastPdo >= LossWindowMs);
        }

        public void ObservePdo(long now) { lastPdo = now; }
        public void ClearPdo() { lastPdo = -1; }
    }

    sealed class PdoTimer : WaitHandle
    {
        internal const int SpinLeadUs = 500;
        const uint HighResolution = 0x2;
        const uint ModifyAndWait = 0x00100002;
        const long UnitsPerSecond = 10000000;

        public bool IsHighResolution { get; private set; }

        [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", ExactSpelling = true)]
        static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, IntPtr name, uint flags, uint access);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime,
            int period, IntPtr callback, IntPtr state, bool resume);

        public PdoTimer()
        {
            var nativeTimer = CreateWaitableTimerEx(IntPtr.Zero, IntPtr.Zero, HighResolution, ModifyAndWait);
            IsHighResolution = nativeTimer != IntPtr.Zero;
            if (IsHighResolution) SafeWaitHandle = new SafeWaitHandle(nativeTimer, true);
        }

        public long? WaitUntil(long deadline, bool measure, bool earlyWake)
        {
            long remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0) return null;
            long? waitOffset = null;
            long timerRemaining = remaining - (earlyWake ? Stopwatch.Frequency * SpinLeadUs / 1000000 : 0);
            if (IsHighResolution && timerRemaining > 0)
            {
                long due = -Math.Max(1, timerRemaining * UnitsPerSecond / Stopwatch.Frequency);
                if (SetWaitableTimer(SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    WaitOne(4);
                    if (measure) waitOffset = Stopwatch.GetTimestamp() - deadline;
                }
                else IsHighResolution = false;
            }
            while (Stopwatch.GetTimestamp() < deadline) Thread.SpinWait(16);
            return waitOffset;
        }

        internal static long Advance(long deadline, long period, long finishedAt, out long missed)
        {
            missed = Math.Max(0, (finishedAt - deadline) / period);
            return deadline + (missed + 1) * period;
        }
    }

    sealed class HardwareBus : IDisposable
    {
        readonly BlockingCollection<Action> commands = new BlockingCollection<Action>();
        readonly TaskCompletionSource<bool> stopped = new TaskCompletionSource<bool>();
        readonly Stopwatch time = Stopwatch.StartNew();
        readonly Thread worker;
        readonly ConcurrentQueue<string> events = new ConcurrentQueue<string>();
        LinkWatch link = new LinkWatch();
        int stopRequested;
        volatile HardwareFrame frame = new HardwareFrame { Note = "请选择专用有线网口。" };
        bool connected, online, armed, mapped, lost;
        int state, al, wkc, expected, cycleMs = 10, outBits, inBits;
        long cycles, badWkc, nextStatus, nextPublish, lastCycle, nextFastCycle, intervalSamples, missedSlots;
        double lastIntervalMs, maxIntervalMs, intervalSumMs;
        bool highResolutionTimer;
        bool earlyWake;
        uint dcNs;
        byte[] rx, tx, lastRx;
        PdoTrace trace;
        bool traceRecording;
        long traceAlTicks;
        long? lastAlSampleTicks;
        SlaveIdentity identity;
        PdoField[] actualMapping;
        string note = "";
        string mappingNote = "";

        public HardwareBus()
        {
            worker = new Thread(Run) { IsBackground = true, Name = "EtherCAT SOEM" };
            worker.Start();
        }

        public HardwareFrame Frame { get { return frame; } }

        public bool TryReadEvent(out string message) { return events.TryDequeue(out message); }

        void RecordEvent(string message)
        {
            events.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message);
            string discarded;
            while (events.Count > 128 && events.TryDequeue(out discarded)) { }
        }

        Task<T> Submit<T>(Func<T> command)
        {
            if (Volatile.Read(ref stopRequested) != 0 || !worker.IsAlive)
                throw new InvalidOperationException("通信后台已停止，请重新打开窗口。");
            var result = new TaskCompletionSource<T>();
            commands.Add(delegate {
                try
                {
                    T value = command();
                    Publish();
                    result.SetResult(value);
                }
                catch (Exception error)
                {
                    Publish();
                    result.SetException(error);
                }
            });
            return result.Task;
        }

        public Task<int> Connect(string adapter, int interval, bool useEarlyWake = false)
        {
            return Submit(delegate {
                Close();
                Soem.Load();
                var detail = new StringBuilder(256);
                int found = Soem.tb_open(adapter, detail, detail.Capacity);
                if (found != 1)
                    throw new InvalidOperationException(DescribeScanFailure(found, detail.ToString()));
                connected = true;
                link = new LinkWatch();
                nextStatus = nextPublish = 0;
                cycles = badWkc = 0;
                ResetTiming();
                cycleMs = interval;
                earlyWake = interval == 1 && useEarlyWake;
                try
                {
                    if (Volatile.Read(ref stopRequested) != 0)
                        throw new OperationCanceledException("扫站已取消。");
                    int dc;
                    if (Soem.tb_info(out identity.Vendor, out identity.Product, out identity.Revision, out dc) != 1)
                        throw new InvalidOperationException("未能读取从站身份。");
                    identity.Dc = dc != 0;
                    if (!PollState()) throw new InvalidOperationException("已发现并初始化从站，但读取 AL 状态寄存器时无响应。");
                    note = "已连接单从站。";
                    return state;
                }
                catch { Close(); throw; }
            });
        }

        internal static string DescribeScanFailure(int status, string detail)
        {
            string message;
            switch (status)
            {
                case -1: message = "Npcap 打开网口失败"; break;
                case -2: message = "已收到从站响应，但 SOEM 初始化失败"; break;
                case -3: message = "网口已打开，但 Npcap 发送 EtherCAT 帧失败"; break;
                case -4: message = "网口已打开，但 Npcap 无法启用非阻塞接收"; break;
                case -5: message = "扫站超过 3 秒，已向 Windows 请求取消阻塞的驱动 I/O"; break;
                case -6: message = "无法启动扫站超时监控"; break;
                case 0: message = "Npcap 接受了发送请求，但未收到有效 EtherCAT 从站响应"; break;
                default: message = "当前真机模式支持单从站，检测到 " + status + " 个"; break;
            }
            return string.IsNullOrWhiteSpace(detail) ? message + "。" : message + "（" + detail + "）。";
        }

        public Task<bool> Disconnect()
        {
            return Submit(delegate { Close(); return true; });
        }

        public Task<bool> StartTrace(PdoTrace buffer)
        {
            return Submit(delegate {
                if (!CanExchange()) throw new InvalidOperationException("请先进入 SAFE-OP 或 OP。");
                if (trace != null) throw new InvalidOperationException("请先保存上一次 PDO 采样。");
                trace = buffer;
                traceAlTicks = 0;
                trace.Start(time.ElapsedTicks);
                traceRecording = true;
                return true;
            });
        }

        public Task<PdoTrace> StopTrace()
        {
            return Submit(delegate {
                FinishTrace(time.ElapsedTicks);
                var recording = trace;
                trace = null;
                return recording;
            });
        }

        public Task<int> RequestState(int target)
        {
            return Submit(delegate {
                RequireOnline();
                return RequestStateCore(target);
            });
        }

        int RequestStateCore(int target)
        {
            if (target != 8)
            {
                LockOutputs();
                if (mapped && state == 8) Exchange();
            }
            if (target == 4 && !mapped)
            {
                if ((state & 0xf) != 2) throw new InvalidOperationException("请先进入 PRE-OP，再配置 PDO 并进入 SAFE-OP。");
                string rxNote, txNote;
                var onlineRx = ReadMapping(0x1C12, "RxPDO", out rxNote);
                var onlineTx = ReadMapping(0x1C13, "TxPDO", out txNote);
                var onlineOutBits = onlineRx == null ? -1 : Bits(onlineRx, "RxPDO");
                var onlineInBits = onlineTx == null ? -1 : Bits(onlineTx, "TxPDO");
                bool standard = onlineOutBits >= 0 && onlineInBits >= 0 && HasStandardPdoManagers();
                mappingNote = rxNote + " · " + txNote;
                if (Soem.tb_map(standard ? onlineOutBits : -1, standard ? onlineInBits : -1,
                    out outBits, out inBits, out expected) != 1)
                    throw new InvalidOperationException("PDO 映射失败。 " + mappingNote);
                rx = new byte[(outBits + 7) / 8];
                tx = new byte[(inBits + 7) / 8];
                lastRx = new byte[rx.Length];
                actualMapping =
                    (outBits == 0 || onlineOutBits == outBits) && (inBits == 0 || onlineInBits == inBits)
                        ? (outBits == 0 ? new PdoField[0] : onlineRx)
                            .Concat(inBits == 0 ? new PdoField[0] : onlineTx).ToArray()
                        : null;
                mapped = true;
            }
            if (target == 8)
            {
                if (!mapped || (state & 0xf) != 4) throw new InvalidOperationException("请先进入 SAFE-OP。");
                Exchange();
            }
            if (Soem.tb_set_state(target) != 1)
                throw new InvalidOperationException("从站未确认 AL 状态请求。");

            long until = time.ElapsedMilliseconds + 2000;
            bool confirmed = false;
            do
            {
                bool replied = PollState();
                confirmed = replied && state == target;
                if (confirmed || lost || (replied && (state & 0x10) != 0)) break;
                if (mapped && target >= 4) Exchange();
                Thread.Sleep(cycleMs);
            } while (time.ElapsedMilliseconds < until);
            note = "请求 " + StateName(target) + "，实际 " +
                (lost ? "离线" : link.AlMisses != 0 ? "尚未确认，最后确认 " + StateName(state) : StateName(state)) +
                "，AL 0x" + al.ToString("X4");
            if (target == 4 && mapped) note += " · " + mappingNote;
            if (!confirmed) throw new InvalidOperationException(note);
            if (target == 8) ResetTiming();
            if (target <= 2)
            {
                mapped = false;
                expected = wkc = 0;
                outBits = inBits = 0;
                rx = tx = lastRx = null;
                actualMapping = null;
                lastCycle = 0;
                link.ClearPdo();
                if (target == 1) dcNs = 0;
            }
            return state;
        }

        public Task<uint> ConfigureDc(EsiDevice device, DcMode mode, decimal hz)
        {
            return Submit(delegate {
                RequireOnline();
                if (mode.Assign != 0 && (!identity.Matches(device) || !identity.Dc ||
                    !device.DcModes.Contains(mode) || mode.Assign != 0x0300 || mode.UsesSync1))
                    throw new InvalidOperationException("该 ESI 模式与在线设备不匹配，或需要尚未支持的同步方式。");
                if (mode.Assign != 0 && (hz < 1m || hz > 1000000m))
                    throw new InvalidOperationException("请输入 1–1,000,000 Hz 的 SYNC0 频率。");
                uint ns = mode.Assign == 0 ? 0 : (uint)decimal.Round(1000000000m / hz, 0, MidpointRounding.AwayFromZero);
                if (mode.MinimumNs > 0 && ns != 0 && ns < mode.MinimumNs)
                    throw new InvalidOperationException("SYNC0 周期小于 ESI 声明的最短周期。");
                if (state != 2) RequestStateCore(2);
                if (Soem.tb_dc(mode.Assign, ns, mode.Assign == 0 ? 0 : mode.ShiftNs) != 1)
                    throw new InvalidOperationException("从站已在 PRE-OP，但 DC 配置写入或回读校验失败。");
                dcNs = ns;
                note = ns == 0 ? "Free Run 已应用。" : "已配置 SYNC0 " + ns + " ns。";
                return ns;
            });
        }

        public Task<byte[]> Sdo(ushort index, byte sub, byte[] value, EsiDevice device, SdoEntry permissions = null)
        {
            return Submit(delegate {
                RequireOnline();
                if (state != 2 && state != 4 && state != 8)
                    throw new InvalidOperationException("当前状态下邮箱不可用。");
                if (state == 8 && armed)
                    throw new InvalidOperationException("先锁定 PDO 输出再执行 SDO。邮箱等待可能拉长 PDO 周期。");
                if (value != null && state != 2 && ((index >= 0x1600 && index <= 0x17FF) ||
                    (index >= 0x1A00 && index <= 0x1BFF) || index == 0x1C12 || index == 0x1C13))
                    throw new InvalidOperationException("改变 PDO 映射或分配需要 PRE-OP。");
                if (device != null && !identity.Matches(device))
                    throw new InvalidOperationException("选中的 ESI 设备与在线从站身份不符。");
                if (permissions != null && (value == null ? !permissions.ReadableIn(state) : !permissions.WritableIn(state)))
                    throw new InvalidOperationException("ESI 未允许在当前状态执行此 SDO 操作。");
                return SdoTransfer(index, sub, value);
            });
        }

        public Task<bool> SetArmed(bool enabled, EsiDevice device)
        {
            return Submit(delegate {
                if (!enabled) { LockOutputs(); return false; }
                RequireOutputs(device);
                armed = true;
                return true;
            });
        }

        public Task<bool> WritePdo(EsiDevice device, PdoField field, byte[] value)
        {
            return Submit(delegate {
                RequireOutputs(device);
                if (!armed) throw new InvalidOperationException("请先解锁 PDO 输出。");
                var entries = actualMapping.Where(p => p.Direction == "RxPDO").ToArray();
                if (entries.Count(e => e.Key == field.Key) != 1 || field.Index == 0)
                    throw new InvalidOperationException("该条目是填充位，或在同一 PDO 中重复分配，无法唯一定位。");
                int offset = 0;
                foreach (var entry in entries)
                {
                    if (entry.Key == field.Key)
                    {
                        FieldValue.Insert(rx, offset, (int)entry.Bits, value);
                        return true;
                    }
                    offset += (int)entry.Bits;
                }
                throw new InvalidOperationException("该字段未分配为在线 RxPDO。");
            });
        }

        public Task<bool> WriteHandTargets(EsiDevice device, HandTarget[] targets)
        {
            return Submit(delegate {
                RequireOutputs(device);
                if (!armed) throw new InvalidOperationException("请先解锁 PDO 输出。");
                if (!HandPdoLayout.FromMapping(device.Fields, true).Complete)
                    throw new InvalidOperationException("所选 ESI 的灵巧手字段或数据类型不匹配。");
                rx = HandPdoLayout.FromMapping(actualMapping).StageTargets(rx, targets);
                return true;
            });
        }

        public static bool MatchesMapping(HardwareFrame snapshot, EsiDevice device)
        {
            return snapshot.Connected && snapshot.Identity.Matches(device) && MatchesMapping(snapshot.Mapping, device);
        }

        static bool MatchesMapping(PdoField[] mapping, EsiDevice device)
        {
            if (device == null || mapping == null) return false;
            var groups = mapping.ToLookup(p => new { p.Direction, p.Pdo });
            if (device.Fields.Any(p => p.Required && !groups.Contains(new { p.Direction, p.Pdo })))
                return false;
            foreach (var group in groups)
            {
                var declared = device.Fields.Where(p => p.Direction == group.Key.Direction && p.Pdo == group.Key.Pdo);
                if (!group.Select(p => p.Key).SequenceEqual(declared.Select(p => p.Key))) return false;
            }
            return true;
        }

        void RequireOutputs(EsiDevice device)
        {
            RequireOnline();
            if (state != 8 || !mapped || expected <= 0 || wkc != expected ||
                !identity.Matches(device) || !MatchesMapping(actualMapping, device))
                throw new InvalidOperationException("输出需要稳定 OP、正常 WKC、身份和在线 PDO 映射匹配。");
        }

        void RequireOnline()
        {
            if (!connected) throw new InvalidOperationException("尚未连接从站。");
            if (!PollState())
                throw new InvalidOperationException(lost ? "从站无响应，请检查网线并重新连接。" :
                    "本次 AL 状态读取超时，请等待状态恢复后再操作。");
        }

        byte[] SdoTransfer(ushort index, byte sub, byte[] value)
        {
            int size = value == null ? 4096 : value.Length;
            var data = value ?? new byte[size];
            uint abort;
            if (Soem.tb_sdo(value == null ? 0 : 1, index, sub, data, ref size, out abort) != 1)
                throw new InvalidOperationException(abort == 0 ? "SDO 无响应或传输失败。" : "SDO Abort 0x" + abort.ToString("X8"));
            if (size < 0 || size > data.Length) throw new InvalidOperationException("从站返回异常 SDO 长度。");
            Array.Resize(ref data, size);
            return data;
        }

        bool HasStandardPdoManagers()
        {
            try
            {
                var managers = SdoTransfer(0x1C00, 0, null);
                return ReadCount(managers) == 4 && ReadCount(SdoTransfer(0x1C00, 3, null)) == 3 &&
                    ReadCount(SdoTransfer(0x1C00, 4, null)) == 4;
            }
            catch (InvalidOperationException) { return false; }
        }

        PdoField[] ReadMapping(ushort assignment, string direction, out string detail)
        {
            try
            {
                var fields = new List<PdoField>();
                ReadAssignment(fields, assignment, direction);
                detail = string.Format("0x{0:X4}：{1} 项 / {2} bit", assignment,
                    fields.Count, fields.Sum(p => (int)p.Bits));
                return fields.ToArray();
            }
            catch (InvalidOperationException error)
            {
                detail = string.Format("0x{0:X4}：{1}", assignment, error.Message);
                return null;
            }
        }

        static int ReadCount(byte[] data)
        {
            if (data.Length == 1 || (data.Length == 2 && data[1] == 0)) return data[0];
            throw new InvalidOperationException("计数或类型长度异常。");
        }

        byte[] ReadPdoObject(ushort index, byte sub)
        {
            try { return SdoTransfer(index, sub, null); }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException(string.Format("读取 0x{0:X4}:{1:X2} 失败：{2}", index, sub, error.Message));
            }
        }

        void ReadAssignment(List<PdoField> fields, ushort assignment, string direction)
        {
            int count = ReadCount(ReadPdoObject(assignment, 0));
            for (int i = 1; i <= count; i++)
            {
                var map = ReadPdoObject(assignment, (byte)i);
                if (map.Length != 2) throw new InvalidOperationException("PDO 映射索引长度异常。");
                ushort pdo = BitConverter.ToUInt16(map, 0);
                if (pdo < (direction == "RxPDO" ? 0x1600 : 0x1A00) ||
                    pdo > (direction == "RxPDO" ? 0x17FF : 0x1BFF))
                    throw new InvalidOperationException("PDO 映射索引超出方向范围。");
                int size = ReadCount(ReadPdoObject(pdo, 0));
                for (int j = 1; j <= size; j++)
                {
                    var entry = ReadPdoObject(pdo, (byte)j);
                    if (entry.Length != 4 || entry[0] == 0 || fields.Count >= 1024)
                        throw new InvalidOperationException("PDO 条目超出支持范围。");
                    fields.Add(new PdoField {
                        Direction = direction, Pdo = pdo, Index = BitConverter.ToUInt16(entry, 2),
                        Sub = entry[1], Bits = entry[0], Name = "", Type = ""
                    });
                }
            }
        }

        static int Bits(PdoField[] fields, string direction)
        {
            return fields.Where(f => f.Direction == direction).Sum(f => (int)f.Bits);
        }

        bool CanExchange()
        {
            return mapped && online && ((state & 0xf) == 4 || (state & 0xf) == 8);
        }

        void ResetTiming()
        {
            lastCycle = nextFastCycle = intervalSamples = missedSlots = 0;
            lastIntervalMs = maxIntervalMs = intervalSumMs = 0;
        }

        void Run()
        {
            PdoTimer precision = null;
            try
            {
                long nextCycle = 0;
                long oneMillisecond = Stopwatch.Frequency / 1000;
                while (!commands.IsCompleted)
                {
                    long now = time.ElapsedMilliseconds;
                    Action command;
                    long? waitOffset = null;
                    if (cycleMs == 1 && CanExchange())
                    {
                        if (precision == null) precision = new PdoTimer();
                        if (!commands.TryTake(out command, 0) && nextFastCycle != 0)
                            waitOffset = precision.WaitUntil(nextFastCycle, traceRecording, earlyWake);
                        highResolutionTimer = precision.IsHighResolution;
                    }
                    else
                    {
                        nextFastCycle = 0;
                        int delay = connected ? (int)Math.Max(0, Math.Min(20, nextCycle - now)) : 50;
                        commands.TryTake(out command, delay);
                    }
                    if (command != null) command();
                    if (Volatile.Read(ref stopRequested) != 0) break;
                    if (traceRecording && trace.HasExpired(time.ElapsedTicks)) FinishTrace(time.ElapsedTicks);
                    if (!connected) continue;

                    // 先发到期的 1 ms PDO，再做周期状态轮询。
                    if (cycleMs == 1 && CanExchange())
                    {
                        if (precision == null) precision = new PdoTimer();
                        highResolutionTimer = precision.IsHighResolution;
                        long tick = Stopwatch.GetTimestamp();
                        if (nextFastCycle == 0) nextFastCycle = tick;
                        if (tick >= nextFastCycle)
                        {
                            Exchange(nextFastCycle, waitOffset);
                            long missed;
                            nextFastCycle = PdoTimer.Advance(nextFastCycle, oneMillisecond,
                                Stopwatch.GetTimestamp(), out missed);
                            missedSlots += missed;
                        }
                    }

                    now = time.ElapsedMilliseconds;
                    if (now >= nextStatus)
                    {
                        PollState();
                        nextStatus = now + 100;
                    }
                    if (now >= nextCycle)
                    {
                        if (cycleMs != 1 && CanExchange()) Exchange();
                        nextCycle = now + (cycleMs == 1 ? 20 : cycleMs);
                    }
                    if (now >= nextPublish)
                    {
                        Publish();
                        nextPublish = now + 50;
                    }
                }
            }
            catch (Exception error)
            {
                note = error.Message;
            }
            finally
            {
                if (precision != null) precision.Dispose();
                try { Close(); }
                catch (Exception error) { note = error.Message; }
                finally
                {
                    Publish();
                    stopped.TrySetResult(true);
                }
            }
        }

        void Exchange(long deadline = 0, long? waitOffset = null)
        {
            long now = time.ElapsedTicks;
            if (traceRecording && trace.HasExpired(now)) FinishTrace(now);
            long? dispatchOffset = traceRecording && deadline != 0
                ? (long?)(Stopwatch.GetTimestamp() - deadline) : null;
            long interval = lastCycle == 0 ? 0 : now - lastCycle;
            if (lastCycle != 0)
            {
                lastIntervalMs = 1000d * (now - lastCycle) / Stopwatch.Frequency;
                maxIntervalMs = Math.Max(maxIntervalMs, lastIntervalMs);
                intervalSumMs += lastIntervalMs;
                intervalSamples++;
            }
            lastCycle = now;
            long send = -1, receive = -1;
            wkc = traceRecording && Soem.HasTimedCycle
                ? Soem.tb_cycle_timed(rx, rx.Length, tx, tx.Length, out send, out receive)
                : Soem.tb_cycle(rx, rx.Length, tx, tx.Length);
            cycles++;
            if (traceRecording)
            {
                bool hasSpace = trace.Add(new PdoTrace.Sample {
                    Start = now, Interval = interval, Duration = time.ElapsedTicks - now, Skipped = missedSlots,
                    Wkc = wkc, Expected = expected, State = state, WaitOffset = waitOffset,
                    DispatchOffset = dispatchOffset, Al = traceAlTicks, Send = send, Receive = receive,
                    SpinLeadUs = earlyWake ? PdoTimer.SpinLeadUs : 0,
                    AlStatusCode = link.AlMisses == 0 && lastAlSampleTicks.HasValue ? (int?)al : null,
                    AlStatusAge = link.AlMisses == 0 ? now - lastAlSampleTicks : null
                });
                traceAlTicks = 0;
                if (!hasSpace) FinishTrace(time.ElapsedTicks);
            }
            if (expected > 0 && wkc == expected)
            {
                link.ObservePdo(time.ElapsedMilliseconds);
                Buffer.BlockCopy(rx, 0, lastRx, 0, rx.Length);
            }
            else if (wkc != expected)
            {
                badWkc++;
                if (armed) RecordEvent("PDO WKC " + wkc + "/" + expected + "，已锁定输出。");
                LockOutputs();
            }
        }

        bool PollState()
        {
            if (lost) return false;
            int observedState, observedAl;
            long alStart = traceRecording ? time.ElapsedTicks : 0;
            bool replied = Soem.tb_status(out observedState, out observedAl) == 1;
            if (traceRecording) traceAlTicks += time.ElapsedTicks - alStart;
            int previousMisses = link.AlMisses;
            bool confirmedLoss = link.ObserveAl(replied, time.ElapsedMilliseconds);
            if (replied)
            {
                lastAlSampleTicks = time.ElapsedTicks;
                if (previousMisses > 0)
                    RecordEvent("AL 状态查询恢复，之前连续超时 " + previousMisses + " 次。");
                if (online && (state != observedState || al != observedAl))
                    RecordEvent("从站状态 " + StateName(state) + " → " + StateName(observedState) +
                        "，AL 0x" + observedAl.ToString("X4"));
                state = observedState;
                al = observedAl;
                online = true;
                if (state != 8) LockOutputs();
                return true;
            }

            LockOutputs();
            if (link.AlMisses == 1)
                RecordEvent("AL 状态查询超时，已锁定输出，继续核对状态和 PDO。");
            if (confirmedLoss)
            {
                RecordEvent("确认从站无响应：AL 连续超时 " + link.AlMisses +
                    " 次，最近 500 ms 内无有效 PDO。最后状态 " + StateName(state) +
                    "，AL 0x" + al.ToString("X4") + "，WKC " + wkc + "/" + expected + "。");
                online = false;
                lost = true;
                wkc = 0;
                dcNs = 0;
            }
            return false;
        }

        void LockOutputs()
        {
            armed = false;
            if (rx != null) Array.Clear(rx, 0, rx.Length);
        }

        void Close()
        {
            FinishTrace(time.ElapsedTicks);
            if (connected)
            {
                LockOutputs();
                if (mapped && online && (state & 0xf) == 8) Exchange();
                Soem.tb_close();
            }
            connected = online = armed = mapped = lost = false;
            expected = wkc = state = al = 0;
            lastAlSampleTicks = null;
            outBits = inBits = 0;
            dcNs = 0;
            highResolutionTimer = false;
            earlyWake = false;
            nextFastCycle = 0;
            rx = tx = lastRx = null;
            link.ClearPdo();
            actualMapping = null;
        }

        void FinishTrace(long now)
        {
            if (!traceRecording) return;
            traceRecording = false;
            trace.Finish(now);
        }

        void Publish()
        {
            frame = new HardwareFrame {
                Connected = connected, Online = online, Identity = identity, Armed = armed, State = state, Al = al,
                Wkc = wkc, Expected = expected, Cycles = cycles, BadWkc = badWkc, CycleMs = cycleMs,
                AlMisses = link.AlMisses, AlTimeouts = link.AlTimeouts,
                OutBits = outBits, InBits = inBits,
                LastIntervalMs = lastIntervalMs, MaxIntervalMs = maxIntervalMs, DcNs = dcNs, Note = note,
                AverageIntervalMs = intervalSamples == 0 ? 0 : intervalSumMs / intervalSamples,
                MissedSlots = missedSlots, HighResolutionTimer = highResolutionTimer, EarlyWake = earlyWake,
                TraceSamples = trace == null ? 0 : trace.Count, TraceRecording = traceRecording,
                TraceSeconds = trace == null ? 0 : trace.ElapsedSeconds(time.ElapsedTicks, traceRecording),
                Mapping = actualMapping,
                Rx = lastRx == null || !online || expected <= 0 || wkc != expected ? null : (byte[])lastRx.Clone(),
                Tx = tx == null || !online || expected <= 0 || wkc != expected ? null : (byte[])tx.Clone()
            };
        }

        public static string StateName(int raw)
        {
            string stateName;
            switch (raw & 0xf)
            {
                case 1: stateName = "INIT"; break;
                case 2: stateName = "PRE-OP"; break;
                case 3: stateName = "BOOT"; break;
                case 4: stateName = "SAFE-OP"; break;
                case 8: stateName = "OP"; break;
                default: stateName = "未知状态 0x" + raw.ToString("X2"); break;
            }
            return stateName + ((raw & 0x10) != 0 ? " + ERROR" : "");
        }

        public Task StopAsync()
        {
            if (Interlocked.Exchange(ref stopRequested, 1) == 0)
                commands.CompleteAdding();
            return stopped.Task;
        }

        public void Dispose()
        {
            StopAsync();
        }
    }

    static class FieldValue
    {
        public static string Bytes(byte[] value)
        {
            return value == null ? "—" : BitConverter.ToString(value).Replace("-", " ");
        }

        public static byte[] Hex(string text)
        {
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
            var normalized = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (normalized.Length == 0 || normalized.Length > 8192 || normalized.Length % 2 != 0)
                throw new FormatException("请输入 1–4096 字节的十六进制，低字节在前，例如 2A 00。");
            var data = new byte[normalized.Length / 2];
            for (int i = 0; i < data.Length; i++)
                data[i] = byte.Parse(normalized.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return data;
        }

        static bool Signed(string type)
        {
            return type == "SINT" || type == "INT" || type == "DINT" || type == "LINT";
        }

        static bool Integer(string type)
        {
            return Signed(type) || type == "BOOL" || type == "BIT" || type == "BYTE" ||
                type == "USINT" || type == "UINT" || type == "UDINT" || type == "ULINT" ||
                type == "WORD" || type == "DWORD" || type == "LWORD";
        }

        public static byte[] Encode(string text, string type, int bits)
        {
            if (bits < 1 || bits > 32768)
                throw new FormatException("该对象未声明有效位宽，请用手工十六进制 SDO。");
            type = (type ?? "").Trim().ToUpperInvariant();
            int length = (bits + 7) / 8;
            if (type == "REAL" && bits == 32)
                return BitConverter.GetBytes(float.Parse(text, CultureInfo.InvariantCulture));
            if (type == "LREAL" && bits == 64)
                return BitConverter.GetBytes(double.Parse(text, CultureInfo.InvariantCulture));
            if (!Integer(type) || bits > 64)
            {
                var raw = Hex(text);
                if (raw.Length != length || (bits % 8 != 0 && raw[length - 1] >= (1 << (bits % 8))))
                    throw new FormatException("字段需要精确的 " + length + " 字节，末字节未使用位须为零。");
                return raw;
            }

            ulong limit = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1, number;
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
                number = ulong.Parse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            else if (Signed(type))
            {
                long value = long.Parse(text, CultureInfo.InvariantCulture);
                if (bits < 64 && (value < -(1L << (bits - 1)) || value >= (1L << (bits - 1))))
                    throw new FormatException("数值超出字段位宽。");
                number = unchecked((ulong)value) & limit;
            }
            else number = ulong.Parse(text, CultureInfo.InvariantCulture);

            if (number > limit) throw new FormatException("数值超出字段位宽。");
            return BitConverter.GetBytes(number).Take(length).ToArray();
        }

        public static string Decode(byte[] data, string type, int bits)
        {
            type = (type ?? "").Trim().ToUpperInvariant();
            if (type == "REAL" && bits == 32 && data.Length == 4)
                return BitConverter.ToSingle(data, 0).ToString("R", CultureInfo.InvariantCulture);
            if (type == "LREAL" && bits == 64 && data.Length == 8)
                return BitConverter.ToDouble(data, 0).ToString("R", CultureInfo.InvariantCulture);
            if (!Integer(type) || bits < 1 || bits > 64 || data.Length != (bits + 7) / 8) return Bytes(data);
            ulong raw = 0;
            for (int i = 0; i < data.Length; i++) raw |= (ulong)data[i] << (8 * i);
            ulong mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
            raw &= mask;
            if (Signed(type))
            {
                if ((raw & (1UL << (bits - 1))) != 0) raw |= ~mask;
                return unchecked((long)raw).ToString(CultureInfo.InvariantCulture);
            }
            return raw.ToString(CultureInfo.InvariantCulture);
        }

        public static byte[] Extract(byte[] source, int offset, int bits)
        {
            if (source == null || bits <= 0 || offset < 0 || offset > source.Length * 8 - bits) return null;
            var result = new byte[(bits + 7) / 8];
            for (int i = 0; i < bits; i++)
                if ((source[(offset + i) / 8] & (1 << ((offset + i) % 8))) != 0)
                    result[i / 8] |= (byte)(1 << (i % 8));
            return result;
        }

        public static void Insert(byte[] destination, int offset, int bits, byte[] data)
        {
            if (bits <= 0 || offset < 0 || offset > destination.Length * 8 - bits || data.Length != (bits + 7) / 8)
                throw new ArgumentException("PDO 字段超出过程数据范围。");
            for (int i = 0; i < bits; i++)
            {
                int position = offset + i;
                int mask = 1 << (position % 8);
                if ((data[i / 8] & (1 << (i % 8))) != 0) destination[position / 8] |= (byte)mask;
                else destination[position / 8] &= (byte)~mask;
            }
        }
    }
}
