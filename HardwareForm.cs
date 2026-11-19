using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EtherCatBench
{
    sealed class HardwareForm : Form
    {
        const int ScanUiDeadlineMs = 5000;
        const int CloseGraceMs = 1200;

        readonly HardwareBus bus = new HardwareBus();
        readonly Timer refresh = new Timer { Interval = 100 };
        readonly ComboBox adapter = ListBox(360), devices = ListBox(330), dcModes = ListBox(260);
        readonly NumericUpDown cycleMs = new NumericUpDown { Minimum = 1, Maximum = 2000, Value = 10, Width = 68 };
        readonly NumericUpDown hz = new NumericUpDown { Minimum = 1, Maximum = 1000000, Value = 1000, DecimalPlaces = 3, Width = 110 };
        readonly Label status = LabelFor(""), detail = LabelFor(""), request = LabelFor("请选择专用有线网口，再点击连接 / 扫描。");
        readonly Label match = LabelFor("请导入产品 ESI"), raw = LabelFor(""), dcPreview = LabelFor(""), dcActive = LabelFor("");
        readonly Label timing = LabelFor("进入 SAFE-OP 或 OP 后显示 PDO 实测周期。");
        readonly TextBox pdoValue = new TextBox { Width = 120 }, sdoValue = new TextBox { Width = 180 };
        readonly TextBox sdoIndex = new TextBox { Width = 80, Text = "0x1018" }, sdoSub = new TextBox { Width = 52, Text = "1" };
        readonly TextBox sdoRaw = new TextBox { Width = 190 }, sdoResult = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        readonly DataGridView pdoGrid = Grid("方向", "PDO", "索引:子索引", "名称", "位宽", "类型", "周期值");
        readonly DataGridView sdoGrid = Grid("索引:子索引", "名称", "位宽", "类型", "权限", "操作结果");
        readonly TextBox log = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        readonly DcMode freeRun = new DcMode { Name = "Free Run / 禁用 SYNC0" };
        readonly Button connect, disconnect, listAdapters, arm, disarm, send, openEsi, demo, csvStart, csvStop;
        readonly Button[] states;
        Button applyDc;
        HardwareFrame previous;
        PdoField[] tableMapping;
        EsiDevice tableDevice;
        SlaveIdentity tableIdentity;
        bool tableConnected, busy, closing;
        bool scanAbandoned, closePending, closeReady;
        Stopwatch connecting;
        StreamWriter csv;
        long lastLoggedCycle = -1;

        public HardwareForm()
        {
            Text = "EtherCAT 从站测试台 · 真机";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(980, 650);
            ClientSize = new Size(1140, 760);
            Font = new Font("Microsoft YaHei UI", 9);
            BackColor = Color.FromArgb(243, 246, 250);
            var body = RowsWithFill(0);
            body.RowStyles[0].SizeType = SizeType.AutoSize;
            body.Controls.Add(BuildHeader(out connect, out disconnect, out listAdapters, out openEsi, out demo, out states), 0, 0);
            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildPdo(out arm, out disarm, out send));
            tabs.TabPages.Add(BuildSdo());
            tabs.TabPages.Add(BuildDc());
            tabs.TabPages.Add(BuildLog(out csvStart, out csvStop));
            body.Controls.Add(tabs, 0, 1);
            Controls.Add(body);
            devices.SelectedIndexChanged += DeviceChanged;
            refresh.Tick += delegate { Render(); };
            refresh.Start();
            Shown += delegate { LoadAdapters(); };
            SelectDcModes();
            Render();
        }

        Control BuildHeader(out Button connectButton, out Button disconnectButton, out Button refreshButton,
            out Button esiButton, out Button demoButton, out Button[] stateButtons)
        {
            var section = new TableLayoutPanel {
                Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            for (int row = 0; row < section.RowCount; row++)
                section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            refreshButton = ButtonFor("刷新网卡", delegate { LoadAdapters(); });
            connectButton = ButtonFor("连接 / 扫描", async delegate {
                var nic = adapter.SelectedItem as HardwareAdapter;
                if (nic != null) await Execute("连接 " + nic.Label, async delegate { await bus.Connect(nic.Id, (int)cycleMs.Value); });
            });
            disconnectButton = ButtonFor("断开", async delegate {
                await Execute("断开", async delegate { await bus.Disconnect(); });
            });
            demoButton = ButtonFor("模拟模式", delegate {
                using (var form = new BenchForm()) form.ShowDialog(this);
            });
            section.Controls.Add(HeaderBar(LabelFor("有线网卡"), adapter, refreshButton, connectButton, disconnectButton, demoButton), 0, 0);

            esiButton = ButtonFor("导入产品 ESI…", delegate { ImportEsi(); });
            section.Controls.Add(HeaderBar(esiButton, devices, match), 0, 1);
            stateButtons = new[] { 1, 2, 4, 8 }.Select(value => ButtonFor(HardwareBus.StateName(value), async delegate {
                await Execute("请求 " + HardwareBus.StateName(value), async delegate { await bus.RequestState(value); });
            })).ToArray();
            section.Controls.Add(HeaderBar(stateButtons.Cast<Control>().Concat(new Control[] {
                LabelFor("主站 PDO 目标周期 (ms)"), cycleMs, LabelFor("连接时采用")
            }).ToArray()), 0, 2);
            section.Controls.Add(HeaderBar(status, detail), 0, 3);
            section.Controls.Add(HeaderBar(request), 0, 4);
            adapter.SelectedIndexChanged += delegate { DrawActions(bus.Frame); };
            return section;
        }

        TabPage BuildPdo(out Button armButton, out Button disarmButton, out Button sendButton)
        {
            var page = new TabPage("PDO 实时监视");
            var layout = RowsWithFill(42, 34, 34);
            armButton = ButtonFor("解锁输出", async delegate {
                await Execute("解锁输出", async delegate { await bus.SetArmed(true, SelectedDevice); });
            });
            disarmButton = ButtonFor("锁定并清零", async delegate {
                await Execute("锁定输出", async delegate { await bus.SetArmed(false, SelectedDevice); });
            });
            sendButton = ButtonFor("应用选中 RxPDO", async delegate {
                var field = pdoGrid.CurrentRow == null ? null : pdoGrid.CurrentRow.Tag as PdoField;
                if (field == null || field.Direction != "RxPDO" || field.Index == 0) return;
                await Execute("写入 " + Address(field.Index, field.Sub), async delegate {
                    await bus.WritePdo(SelectedDevice, field, FieldValue.Encode(pdoValue.Text, field.Type, (int)field.Bits));
                });
            });
            layout.Controls.Add(Bar(armButton, disarmButton, LabelFor("输入值"), pdoValue, sendButton, LabelFor("TxPDO 只读")), 0, 0);
            layout.Controls.Add(Bar(raw), 0, 1);
            layout.Controls.Add(Bar(timing), 0, 2);
            layout.Controls.Add(pdoGrid, 0, 3);
            page.Controls.Add(layout);
            return page;
        }

        TabPage BuildSdo()
        {
            var page = new TabPage("SDO 对象字典");
            var layout = RowsWithFill(42, 40, 30);
            layout.Controls.Add(Bar(ButtonFor("读选中 SDO", async delegate { await TransferListed(false); }),
                LabelFor("输入值"), sdoValue, ButtonFor("写选中 SDO", async delegate { await TransferListed(true); })), 0, 0);
            layout.Controls.Add(Bar(LabelFor("手工 Index"), sdoIndex, LabelFor("Sub"), sdoSub,
                ButtonFor("读", async delegate { await TransferRaw(false); }),
                LabelFor("十六进制 · 低字节在前"), sdoRaw,
                ButtonFor("写", async delegate { await TransferRaw(true); })), 0, 1);
            layout.Controls.Add(sdoResult, 0, 2);
            layout.Controls.Add(sdoGrid, 0, 3);
            page.Controls.Add(layout);
            return page;
        }

        TabPage BuildDc()
        {
            var page = new TabPage("DC 同步");
            var layout = RowsWithFill(46, 36, 36, 40);
            dcModes.SelectedIndexChanged += delegate {
                var mode = dcModes.SelectedItem as DcMode;
                if (mode != null && mode.CycleNs > 0)
                    hz.Value = Math.Min(hz.Maximum, Math.Max(hz.Minimum, decimal.Round(1000000000m / mode.CycleNs, 3)));
                DrawDc(bus.Frame);
            };
            hz.ValueChanged += delegate { DrawDc(bus.Frame); };
            applyDc = ButtonFor("切到 PRE-OP 并应用", async delegate {
                var mode = dcModes.SelectedItem as DcMode;
                if (mode != null)
                    await Execute("配置 " + mode.Name, async delegate { await bus.ConfigureDc(SelectedDevice, mode, hz.Value); });
            });
            layout.Controls.Add(Bar(LabelFor("模式"), dcModes, LabelFor("SYNC0 频率 (Hz)"), hz, applyDc), 0, 0);
            layout.Controls.Add(Bar(dcPreview), 0, 1);
            layout.Controls.Add(Bar(dcActive), 0, 2);
            layout.Controls.Add(Bar(LabelFor("应用后保持 PRE-OP。SYNC0 和主站 PDO 周期分别设置。")), 0, 3);
            page.Controls.Add(layout);
            return page;
        }

        TabPage BuildLog(out Button start, out Button stop)
        {
            var page = new TabPage("日志");
            var layout = RowsWithFill(40);
            start = ButtonFor("记录 CSV…", delegate { StartCsv(); });
            stop = ButtonFor("停止记录", delegate { CloseCsv(); Render(); });
            layout.Controls.Add(Bar(start, stop, LabelFor("按界面刷新频率采样过程数据与 WKC。")), 0, 0);
            layout.Controls.Add(log, 0, 1);
            page.Controls.Add(layout);
            return page;
        }

        EsiDevice SelectedDevice { get { return devices.SelectedItem as EsiDevice; } }

        void LoadAdapters()
        {
            if (bus.Frame.Connected || busy) return;
            try
            {
                var choices = HardwareAdapter.List();
                adapter.Items.Clear();
                adapter.Items.AddRange(choices);
                adapter.SelectedIndex = -1;
                SetMessage(choices.Length == 0 ? "Npcap 未列出有线接口。" : "已列出 " + choices.Length + " 个有线接口，请选择专用网口。");
            }
            catch (Exception error) { SetMessage(error.Message); }
            DrawActions(bus.Frame);
        }

        void ImportEsi()
        {
            using (var dialog = new OpenFileDialog { Filter = "EtherCAT ESI (*.xml)|*.xml", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var imported = EsiDevice.Load(dialog.FileName);
                    devices.Items.Clear();
                    devices.Items.AddRange(imported.ToArray());
                    devices.SelectedIndex = 0;
                    SetMessage("已导入 " + Path.GetFileName(dialog.FileName) + "，共 " + imported.Count + " 个设备。");
                }
                catch (Exception error) { SetMessage("ESI 读取失败 · " + error.Message); }
            }
        }

        async void DeviceChanged(object sender, EventArgs args)
        {
            SelectDcModes();
            RebuildSdo();
            if (bus.Frame.Armed)
                await Execute("切换 ESI 后锁定输出", async delegate { await bus.SetArmed(false, null); });
            Render();
        }

        void SelectDcModes()
        {
            dcModes.Items.Clear();
            dcModes.Items.Add(freeRun);
            if (SelectedDevice != null)
                dcModes.Items.AddRange(SelectedDevice.DcModes.Where(m => m.Assign != 0).ToArray());
            dcModes.SelectedIndex = 0;
        }

        void RebuildSdo()
        {
            sdoGrid.Rows.Clear();
            if (SelectedDevice == null) return;
            foreach (var e in SelectedDevice.Sdos)
                sdoGrid.Rows[sdoGrid.Rows.Add(Address(e.Index, e.Sub), e.Name, e.Bits, e.Type, e.Access, "")].Tag = e;
        }

        async Task TransferListed(bool write)
        {
            var row = sdoGrid.CurrentRow;
            var e = row == null ? null : row.Tag as SdoEntry;
            var device = SelectedDevice;
            if (e == null || device == null) return;
            await Execute((write ? "写 SDO " : "读 SDO ") + Address(e.Index, e.Sub), async delegate {
                if (write && !e.Writable) throw new InvalidOperationException("ESI 未声明该对象可写。");
                if (!write && e.Access.Length != 0 && !e.Readable) throw new InvalidOperationException("该对象不可读。");
                byte[] value = write ? FieldValue.Encode(sdoValue.Text, e.Type, (int)e.Bits) : null;
                var result = await bus.Sdo((ushort)e.Index, (byte)e.Sub, value, device);
                if (write && e.Readable) result = await bus.Sdo((ushort)e.Index, (byte)e.Sub, null, device);
                if (row.DataGridView != null)
                    row.Cells[5].Value = (write ? e.Readable ? "回读 " : "已写入 " : "") +
                        FieldValue.Decode(result, e.Type, (int)e.Bits) + "  [" + FieldValue.Bytes(result) + "]";
            });
        }

        async Task TransferRaw(bool write)
        {
            await Execute(write ? "手工写 SDO" : "手工读 SDO", async delegate {
                uint index = EsiDevice.Number(sdoIndex.Text);
                uint sub = EsiDevice.Number(sdoSub.Text);
                if (index > ushort.MaxValue || sub > byte.MaxValue) throw new FormatException("Index 或 Sub 超出范围。");
                var data = write ? FieldValue.Hex(sdoRaw.Text) : null;
                byte[] result = await bus.Sdo((ushort)index, (byte)sub, data, null);
                sdoResult.Text = Address(index, sub) + "  " + FieldValue.Bytes(result);
            });
        }

        async Task Execute(string name, Func<Task> action)
        {
            if (busy || closing || closePending || scanAbandoned) return;
            try
            {
                busy = true;
                bool isScan = name.StartsWith("连接 ", StringComparison.Ordinal);
                if (isScan) connecting = Stopwatch.StartNew();
                DrawActions(bus.Frame);
                request.Text = name + " · 正在执行…";
                Task operation = action();
                if (isScan && await Task.WhenAny(operation, Task.Delay(ScanUiDeadlineMs)) != operation)
                {
                    AbandonScan(operation);
                    return;
                }
                await operation;
                if (closing || closePending) return;
                var current = bus.Frame;
                SetMessage(name + " · 完成" + (name.StartsWith("请求 ", StringComparison.Ordinal) ? " · " + current.Note : ""));
            }
            catch (Exception error)
            {
                if (!closing && !closePending) SetMessage(name + " · " + error.Message);
            }
            finally
            {
                busy = false;
                connecting = null;
                Render();
            }
        }

        void AbandonScan(Task operation)
        {
            scanAbandoned = true;
            bus.StopAsync();
            operation.ContinueWith(task => task.Exception.Handle(error => true),
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            if (!closing)
                SetMessage("扫站超时：Windows 网卡驱动未返回。已停止等待，请关闭此窗口后再试。");
        }

        void Render()
        {
            if (closing || closePending) return;
            if (connecting != null && connecting.ElapsedMilliseconds >= 3000)
                request.Text = "扫站超过 3 秒，正在等待 Windows 驱动完成取消…";
            var current = bus.Frame;
            string busEvent;
            while (bus.TryReadEvent(out busEvent)) AppendLog(busEvent);
            if (scanAbandoned)
            {
                status.Text = "扫站超时";
                status.ForeColor = Color.Firebrick;
                DrawActions(current);
                return;
            }
            var device = SelectedDevice;
            if (!ReferenceEquals(tableDevice, device) || !ReferenceEquals(tableMapping, current.Mapping) ||
                tableConnected != current.Connected || !tableIdentity.Equals(current.Identity))
            {
                tableDevice = device;
                tableMapping = current.Mapping;
                tableConnected = current.Connected;
                tableIdentity = current.Identity;
                RebuildPdo(current, device);
            }

            if (!ReferenceEquals(previous, current))
            {
                status.Text = current.Online && current.AlMisses != 0
                    ? "AL 查询超时 " + current.AlMisses + " 次  ·  最后状态 " + HardwareBus.StateName(current.State) +
                        "  ·  WKC " + current.Wkc + "/" + current.Expected
                    : current.Online ? HardwareBus.StateName(current.State) + "  ·  AL 0x" +
                    current.Al.ToString("X4") + "  ·  WKC " + current.Wkc + "/" + current.Expected :
                    current.Connected ? "从站无响应，请断开后重连" : "未连接";
                status.ForeColor = !current.Online ? Color.Firebrick :
                    current.AlMisses == 0 ? Color.SeaGreen : Color.DarkGoldenrod;
                detail.Text = current.Connected ? string.Format(CultureInfo.InvariantCulture,
                    "周期 {0}  PDO 失配 {1}  AL 超时 {2}  间隔 {3:F2} / 最大 {4:F2} ms  ·  {5}",
                    current.Cycles, current.BadWkc, current.AlTimeouts, current.LastIntervalMs, current.MaxIntervalMs,
                    current.Armed ? "输出已解锁" : "输出锁定") : "";
                raw.Text = "RxPDO " + RawPreview(current.Rx, current.OutBits) +
                    "     TxPDO " + RawPreview(current.Tx, current.InBits);
                timing.Text = current.Connected && current.AverageIntervalMs > 0
                    ? FormatTiming(current)
                    : "进入 SAFE-OP 或 OP 后显示 PDO 实测周期。";
                int rxBit = 0, txBit = 0;
                foreach (DataGridViewRow row in pdoGrid.Rows)
                {
                    var field = (PdoField)row.Tag;
                    bool output = field.Direction == "RxPDO";
                    var data = FieldValue.Extract(output ? current.Rx : current.Tx, output ? rxBit : txBit, (int)field.Bits);
                    row.Cells[6].Value = data == null || current.Mapping == null || field.Index == 0
                        ? "—" : FieldValue.Decode(data, field.Type, (int)field.Bits);
                    if (output) rxBit += (int)field.Bits; else txBit += (int)field.Bits;
                }
                DrawDc(current);
                WriteCsv(current);
                previous = current;
            }
            match.Text = current.Connected
                ? string.Format("{0:X8}/{1:X8}/{2:X8}  ·  {3}", current.Identity.Vendor, current.Identity.Product,
                    current.Identity.Revision, device == null ? "未选 ESI" :
                    !current.Identity.Matches(device) ? "ESI 身份不匹配" :
                    current.Mapping == null ? "身份匹配 · 在线映射未核验" :
                    current.InBits == 0 && device.Fields.Any(p => p.Required && p.Direction == "TxPDO") ?
                        "缺少 ESI 声明的必选 TxPDO" :
                    HardwareBus.MatchesMapping(current, device) ? "身份及映射匹配" : "映射有差异")
                : device == null ? "请导入产品 ESI" : "离线 ESI";
            DrawActions(current);
        }

        void RebuildPdo(HardwareFrame current, EsiDevice device)
        {
            pdoGrid.Rows.Clear();
            var fields = current.Mapping ?? (device == null ? new PdoField[0] : device.Fields.ToArray());
            foreach (var field in fields)
            {
                var description = (current.Mapping == null || (current.Identity.Matches(device) && device != null))
                    && device != null ? device.Fields.FirstOrDefault(p => p.Key == field.Key) : null;
                var item = description ?? field;
                pdoGrid.Rows[pdoGrid.Rows.Add(item.Direction, string.Format("0x{0:X4}", item.Pdo),
                    Address(item.Index, item.Sub), item.Name, item.Bits, item.Type, "—")].Tag = item;
            }
            previous = null;
        }

        void DrawDc(HardwareFrame current)
        {
            var mode = dcModes.SelectedItem as DcMode;
            bool usesSync0 = mode != null && mode.Assign != 0;
            hz.Enabled = usesSync0;
            dcActive.Text = !current.Online ? "未联机" : !current.Identity.Dc ? "从站未报告 DC 能力" :
                current.DcNs == 0 ? "本次连接未启用 SYNC0" : "已配置 SYNC0  ·  " + current.DcNs + " ns";
            dcPreview.Text = !usesSync0 ? "关闭该从站 SYNC0。" :
                string.Format(CultureInfo.InvariantCulture, "AssignActivate 0x{0:X4}  ·  SYNC0 {1} ns  ·  Shift {2} ns{3}",
                    mode.Assign, decimal.Round(1000000000m / hz.Value, 0, MidpointRounding.AwayFromZero),
                    mode.ShiftNs, mode.Assign != 0x0300 || mode.UsesSync1 ? "  ·  仅显示" : "");
        }

        void DrawActions(HardwareFrame current)
        {
            if (connect == null) return;
            bool ready = !busy && !closing && !closePending && !scanAbandoned;
            connect.Enabled = ready && !current.Connected && adapter.SelectedItem != null;
            disconnect.Enabled = ready && current.Connected;
            demo.Enabled = ready && !current.Connected;
            adapter.Enabled = listAdapters.Enabled = cycleMs.Enabled = ready && !current.Connected;
            openEsi.Enabled = devices.Enabled = ready;
            foreach (var button in states) button.Enabled = ready && current.Online;
            applyDc.Enabled = ready && current.Connected && current.Online;
            arm.Enabled = ready && !current.Armed && current.State == 8 && current.Online && current.AlMisses == 0 &&
                current.Wkc == current.Expected && current.Expected > 0 && HardwareBus.MatchesMapping(current, SelectedDevice);
            disarm.Enabled = ready && current.Armed;
            send.Enabled = ready && current.Armed && current.AlMisses == 0 && HardwareBus.MatchesMapping(current, SelectedDevice);
            csvStart.Enabled = csv == null;
            csvStop.Enabled = csv != null;
        }

        void SetMessage(string message)
        {
            request.Text = message;
            AppendLog(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
        }

        void AppendLog(string line)
        {
            log.AppendText(line + Environment.NewLine);
            if (log.TextLength > 40000) log.Text = log.Text.Substring(log.TextLength - 30000);
        }

        void StartCsv()
        {
            using (var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv",
                FileName = "ethercat-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    csv = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
                    csv.WriteLine("Timestamp,Online,ALState,ALCode,WKC,ExpectedWKC,Cycles,BadWKC,RxPDO,TxPDO");
                    lastLoggedCycle = -1;
                    SetMessage("CSV 记录已启动。");
                }
                catch (IOException error) { CloseCsv(); SetMessage(error.Message); }
                catch (UnauthorizedAccessException error) { CloseCsv(); SetMessage(error.Message); }
            }
            DrawActions(bus.Frame);
        }

        void WriteCsv(HardwareFrame f)
        {
            if (csv == null || !f.Connected || lastLoggedCycle == f.Cycles) return;
            try
            {
                csv.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:O},{1},{2:X2},{3:X4},{4},{5},{6},{7},{8},{9}",
                    DateTimeOffset.Now, f.Online ? 1 : 0, f.State, f.Al, f.Wkc, f.Expected,
                    f.Cycles, f.BadWkc, FieldValue.Bytes(f.Rx), FieldValue.Bytes(f.Tx)));
                lastLoggedCycle = f.Cycles;
            }
            catch (IOException error) { CloseCsv(); SetMessage(error.Message); }
        }

        void CloseCsv()
        {
            if (csv == null) return;
            try { csv.Dispose(); }
            catch (IOException error) { SetMessage(error.Message); }
            finally { csv = null; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!closeReady && !scanAbandoned && bus.Frame.Connected)
            {
                e.Cancel = true;
                if (!closePending) CloseWhenStopped();
                return;
            }
            base.OnFormClosing(e);
        }

        async void CloseWhenStopped()
        {
            closePending = true;
            DrawActions(bus.Frame);
            request.Text = "正在断开从站并关闭窗口…";
            Task stop = bus.StopAsync();
            await Task.Yield();
            await Task.WhenAny(stop, Task.Delay(CloseGraceMs));
            if (closing || IsDisposed) return;
            closeReady = true;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !closing)
            {
                closing = true;
                refresh.Stop();
                CloseCsv();
                bus.Dispose();
                refresh.Dispose();
            }
            base.Dispose(disposing);
        }

        static string RawPreview(byte[] data, int bits)
        {
            string size = ((bits + 7) / 8) + " B";
            if (bits == 0) return size + "（未映射）";
            if (data == null) return size + " · 无有效帧";
            return size + " · " + FieldValue.Bytes(data.Take(20).ToArray()) +
                (data.Length > 20 ? " …" : "");
        }

        static string FormatTiming(HardwareFrame current)
        {
            string summary = string.Format(CultureInfo.InvariantCulture,
                "目标 {0} ms  ·  实测最近 {1:F3} / 平均 {2:F3} / 最大 {3:F3} ms",
                current.CycleMs, current.LastIntervalMs, current.AverageIntervalMs, current.MaxIntervalMs);
            if (current.CycleMs == 1)
                summary += "  ·  跳过时隙 " + current.MissedSlots +
                    (current.HighResolutionTimer ? "  ·  高分辨率计时" : "  ·  短时自旋");
            return summary;
        }

        static string Address(uint index, uint sub) { return string.Format("0x{0:X4}:{1:X2}", index, sub); }
        static Label LabelFor(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(4, 8, 3, 0) }; }
        static ComboBox ListBox(int width) { return new ComboBox { Width = width, DropDownStyle = ComboBoxStyle.DropDownList }; }

        static Button ButtonFor(string text, EventHandler action)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 27 };
            button.Click += action;
            return button;
        }

        static TableLayoutPanel RowsWithFill(params int[] heights)
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = heights.Length + 1, Padding = new Padding(8) };
            foreach (int height in heights) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            return layout;
        }

        static FlowLayoutPanel Bar(params Control[] controls)
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            bar.Controls.AddRange(controls);
            return bar;
        }

        static FlowLayoutPanel HeaderBar(params Control[] controls)
        {
            var bar = Bar(controls);
            bar.AutoSize = true;
            bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bar.Padding = new Padding(0, 0, 0, 4);
            return bar;
        }

        static DataGridView Grid(params string[] headings)
        {
            var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White };
            for (int i = 0; i < headings.Length; i++)
                grid.Columns[grid.Columns.Add("field" + i, headings[i])].SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.Columns[headings.Length - 1].FillWeight = 180;
            return grid;
        }
    }
}
