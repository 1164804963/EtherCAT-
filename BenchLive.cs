using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;

namespace EtherCatBench
{
    sealed class SdoEntry
    {
        public uint Index, Sub, Bits;
        public string Name, Type, Access;
        public bool Writable { get { return Access == "rw" || Access == "wo"; } }
        public bool Readable { get { return Access == "rw" || Access == "ro"; } }
    }

    sealed class DcMode
    {
        public string Name;
        public uint Assign, CycleNs, MinimumNs;
        public int ShiftNs;
        public bool UsesSync1;
        public bool Simulated;
        public override string ToString() { return Name; }
    }

    sealed partial class EsiDevice
    {
        public readonly List<SdoEntry> Sdos = new List<SdoEntry>();
        public readonly List<DcMode> DcModes = new List<DcMode>();
        public bool MatchesMapping { get { return MappingCheck() == "身份及 PDO 映射匹配"; } }

        static uint OptionalNumber(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? 0 : Number(value);
        }

        void ReadDetails(XElement device)
        {
            foreach (var profile in Children(device, "Profile"))
            {
                var dictionary = Child(profile, "Dictionary") ?? Child(profile, "ObjectDictionary");
                if (dictionary == null) continue;
                var objects = Child(dictionary, "Objects");
                var types = Child(dictionary, "DataTypes");
                if (objects == null) continue;
                foreach (var obj in Children(objects, "Object"))
                {
                    uint index = Number(Value(obj, "Index"));
                    var type = Value(obj, "Type").Trim();
                    var record = types == null ? null : Children(types, "DataType")
                        .FirstOrDefault(t => Value(t, "Name").Trim() == type);
                    var items = Children(obj, "SubItem").ToArray();
                    if (items.Length == 0 && record != null)
                        items = Children(record, "SubItem").ToArray();
                    if (items.Length == 0)
                    {
                        AddSdo(index, 0, obj, Value(obj, "Name"), type);
                        continue;
                    }
                    foreach (var item in items)
                    {
                        var sub = Child(item, "SubIdx") ?? Child(item, "SubIndex");
                        if (sub != null)
                            AddSdo(index, Number(sub.Value), item, Value(item, "Name"), Value(item, "Type"));
                    }
                }
            }

            var dc = Child(device, "Dc");
            if (dc == null) return;
            foreach (var op in Children(dc, "OpMode"))
            {
                uint assign = OptionalNumber(Value(op, "AssignActivate"));
                string name = Value(op, "Name").Trim();
                DcModes.Add(new DcMode {
                    Name = (name.Length == 0 ? "未命名模式" : name) + string.Format(" (0x{0:X4})", assign),
                    Assign = assign,
                    CycleNs = OptionalNumber(Value(op, "CycleTimeSync0")),
                    MinimumNs = OptionalNumber(Value(op, "MinCycleTime")),
                    ShiftNs = ParseShift(Value(op, "ShiftTimeSync0")),
                    UsesSync1 = (assign & 0x400) != 0 || OptionalNumber(Value(op, "CycleTimeSync1")) != 0,
                    Simulated = MatchesIdentity && assign == 0x0300 && OptionalNumber(Value(op, "CycleTimeSync1")) == 0
                });
            }
        }

        static int ParseShift(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            value = value.Trim();
            return value.StartsWith("#x", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? unchecked((int)Number(value)) : int.Parse(value, CultureInfo.InvariantCulture);
        }

        void AddSdo(uint index, uint sub, XElement entry, string name, string type)
        {
            if (index > ushort.MaxValue || sub > byte.MaxValue ||
                Sdos.Any(s => s.Index == index && s.Sub == sub)) return;
            var flags = Child(entry, "Flags");
            Sdos.Add(new SdoEntry {
                Index = index, Sub = sub, Name = name, Type = type,
                Bits = OptionalNumber(Value(entry, "BitSize")),
                Access = flags == null ? "" : Value(flags, "Access").Trim().ToLowerInvariant()
            });
        }
    }

    sealed partial class DemoSlave
    {
        public ushort LastOutput { get; private set; }
        public ushort LastInput { get; private set; }
        public uint Sync0Ns { get; private set; }
        public string SyncMode { get; private set; }

        public bool ConfigureSync(DcMode mode, decimal hz, out string message)
        {
            if (!Link || !Found || State != SlaveState.PRE_OP)
            {
                message = "请先扫描并进入 PRE-OP。";
                return false;
            }
            if (mode.Assign == 0)
            {
                Sync0Ns = 0;
                SyncMode = "Free Run";
                message = "已应用 Free Run（模拟）。";
                return true;
            }
            if (!mode.Simulated || mode.Assign != 0x0300)
            {
                message = "样本仅支持匹配 ESI 声明的 SYNC0 模式。";
                return false;
            }
            if (hz < 1 || hz > 1000000)
            {
                message = "请输入 1–1,000,000 Hz 内的模拟频率。";
                return false;
            }
            Sync0Ns = (uint)decimal.Round(1000000000m / hz, 0, MidpointRounding.AwayFromZero);
            SyncMode = mode.Name;
            message = "已应用模拟 SYNC0：" + Sync0Ns + " ns。";
            return true;
        }
    }

    sealed partial class BenchForm
    {
        readonly DcMode freeRun = new DcMode { Name = "Free Run", Simulated = true };
        ComboBox liveDeviceList, dcModeList;
        DataGridView livePdo, esiPdo, esiSdo;
        Label liveStatus, esiStatus, dcResult, dcPreview, dcApplied;
        NumericUpDown syncFrequency;
        TextBox sdoEdit, pdoEdit;
        string lastStateRequest = "尚未请求切换";
        bool esiMappingMatches;

        TabPage BuildLiveTab()
        {
            var page = new TabPage("实时监视");
            var layout = PageLayout(liveStatus = DisplayLabel());
            livePdo = Table("方向", "PDO", "索引:子索引", "名称", "类型", "位数", "实时值", "帧状态");
            foreach (var field in DemoSlave.Mapping) AddPdoRow(livePdo, field);
            layout.Controls.Add(livePdo, 0, 1);
            page.Controls.Add(layout);
            return page;
        }

        TabPage BuildEsiTables()
        {
            var page = new TabPage("ESI 对象表");
            var grids = new TabControl { Dock = DockStyle.Fill };
            var pdo = new TabPage("PDO");
            var sdo = new TabPage("SDO");
            liveDeviceList = new ComboBox { Width = 410, DropDownStyle = ComboBoxStyle.DropDownList };
            liveDeviceList.SelectedIndexChanged += delegate { PopulateDevice(); };
            var layout = PageLayout(Bar(liveDeviceList,
                ActionButton("导入 ESI…", BtnEsiBrowse_Click), esiStatus = DisplayLabel()));
            esiPdo = Table("方向", "PDO", "索引:子索引", "名称", "类型", "位数", "实时值", "帧状态");
            esiSdo = Table("索引:子索引", "名称", "类型", "位数", "权限", "操作结果");
            pdoEdit = new TextBox { Text = "42", Width = 85 };
            sdoEdit = new TextBox { Text = "100", Width = 85 };
            AddTableWithActions(pdo, esiPdo, Bar(new Label { Text = "RxPDO 值", AutoSize = true },
                pdoEdit, ActionButton("应用选中 RxPDO", delegate { SendEsiPdo(); })));
            AddTableWithActions(sdo, esiSdo, Bar(ActionButton("读取选中 SDO", delegate { RunEsiSdo(false); }),
                sdoEdit, ActionButton("写入并回读", delegate { RunEsiSdo(true); })));
            grids.TabPages.Add(pdo);
            grids.TabPages.Add(sdo);
            layout.Controls.Add(grids, 0, 1);
            page.Controls.Add(layout);
            return page;
        }

        TabPage BuildDcTab()
        {
            var page = new TabPage("DC 同步");
            var controls = new FlowLayoutPanel {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill,
                AutoScroll = true, Padding = new Padding(16)
            };
            dcModeList = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
            syncFrequency = new NumericUpDown {
                Minimum = 1, Maximum = 1000000, Value = 1000, DecimalPlaces = 3, Width = 110
            };
            dcPreview = DisplayLabel();
            dcResult = DisplayLabel();
            dcModeList.SelectedIndexChanged += delegate {
                var mode = dcModeList.SelectedItem as DcMode;
                if (mode != null && mode.CycleNs > 0)
                    syncFrequency.Value = Math.Min(syncFrequency.Maximum, Math.Max(
                        syncFrequency.Minimum, decimal.Round(1000000000m / mode.CycleNs, 3)));
                UpdateDcPreview();
            };
            syncFrequency.ValueChanged += delegate { UpdateDcPreview(); };
            controls.Controls.Add(Bar(new Label { Text = "同步模式", AutoSize = true }, dcModeList));
            controls.Controls.Add(Bar(new Label { Text = "SYNC0 频率 (Hz)", AutoSize = true }, syncFrequency));
            controls.Controls.Add(dcPreview);
            controls.Controls.Add(ActionButton("在 PRE-OP 应用", delegate {
                string message;
                slave.ConfigureSync((DcMode)dcModeList.SelectedItem, syncFrequency.Value, out message);
                dcResult.Text = message;
                AppendPdo(message);
                UpdateUi();
            }));
            controls.Controls.Add(dcResult);
            controls.Controls.Add(dcApplied = DisplayLabel());
            controls.Controls.Add(new Label {
                Text = "仅模拟 DC 配置。此频率不会改变 Windows 的 PDO 刷新周期。",
                AutoSize = true, Margin = new Padding(3, 12, 0, 0)
            });
            page.Controls.Add(controls);
            dcModeList.Items.Add(freeRun);
            dcModeList.SelectedIndex = 0;
            return page;
        }

        void UpdateDcPreview()
        {
            var mode = dcModeList.SelectedItem as DcMode;
            if (mode == null) return;
            syncFrequency.Enabled = mode.Simulated && mode.Assign != 0;
            dcPreview.Text = mode.Assign == 0 ? "Free Run · 无 SYNC0 周期" :
                string.Format("AssignActivate 0x{0:X4} · ESI 周期 {1} · {2}", mode.Assign,
                    mode.CycleNs == 0 ? "未给出固定周期" : mode.CycleNs + " ns",
                    mode.Simulated ? decimal.Round(1000000000m / syncFrequency.Value, 0,
                        MidpointRounding.AwayFromZero) + " ns（待应用）" : "仅查看声明");
        }

        void SetImportedDevices(IEnumerable<EsiDevice> devices)
        {
            liveDeviceList.Items.Clear();
            foreach (var device in devices) liveDeviceList.Items.Add(device);
            liveDeviceList.Format -= FormatDevice;
            liveDeviceList.Format += FormatDevice;
            liveDeviceList.FormattingEnabled = true;
            liveDeviceList.SelectedIndex = liveDeviceList.Items.Count == 0 ? -1 : 0;
            if (liveDeviceList.SelectedIndex < 0) PopulateDevice();
        }

        static void FormatDevice(object sender, ListControlConvertEventArgs e)
        {
            var device = e.ListItem as EsiDevice;
            if (device != null) e.Value = string.Format("{0} ({1:X8}/{2:X8})", device.Name, device.Product, device.Revision);
        }

        EsiDevice SelectedDevice { get { return liveDeviceList.SelectedItem as EsiDevice; } }

        void PopulateDevice()
        {
            var device = SelectedDevice;
            esiPdo.Rows.Clear();
            esiSdo.Rows.Clear();
            dcModeList.Items.Clear();
            dcModeList.Items.Add(freeRun);
            dcModeList.SelectedIndex = 0;
            esiStatus.Text = "请导入 ESI";
            esiMappingMatches = device != null && device.MatchesMapping;
            if (device == null) return;
            foreach (var field in device.Fields) AddPdoRow(esiPdo, field);
            if (!esiMappingMatches) RefreshPdo(esiPdo, false, false);
            foreach (var entry in device.Sdos)
                esiSdo.Rows[esiSdo.Rows.Add(Address(entry.Index, entry.Sub), entry.Name, entry.Type,
                    entry.Bits == 0 ? "—" : entry.Bits.ToString(), entry.Access.Length == 0 ? "未知" : entry.Access, "—")].Tag = entry;
            foreach (var mode in device.DcModes.Where(m => m.Assign != 0)) dcModeList.Items.Add(mode);
            esiStatus.Text = (device.MatchesIdentity ? (esiMappingMatches ? "映射匹配" : "PDO 映射有差异") :
                "离线查看 · 身份不匹配") + (device.Sdos.Count == 0 ? " · 无字典，请用手工 SDO" : "");
            UpdateLiveTables();
        }

        void RunEsiSdo(bool write)
        {
            var row = esiSdo.CurrentRow;
            if (row == null) return;
            var entry = (SdoEntry)row.Tag;
            ushort value = 0;
            string result;
            if (!SelectedDevice.MatchesIdentity) result = "身份不匹配，仅可离线查看。";
            else if (write && (!entry.Writable || entry.Bits != 16 ||
                !string.Equals(entry.Type, "UINT", StringComparison.OrdinalIgnoreCase)))
                result = "样本仅支持有写权限的 UINT（16 位）。";
            else if (!write && !entry.Readable && entry.Access.Length != 0)
                result = "该项不可读。";
            else if (write && !TryUInt16(sdoEdit.Text, out value)) result = "请输入 0–65535 或 0x 十六进制。";
            else
            {
                result = slave.Sdo(entry.Index, entry.Sub, write ? (ushort?)value : null);
                if (write && entry.Readable && result.StartsWith("写入成功", StringComparison.Ordinal))
                    result += " / " + slave.Sdo(entry.Index, entry.Sub, null);
            }
            row.Cells[5].Value = result;
        }

        void SendEsiPdo()
        {
            var row = esiPdo.CurrentRow;
            if (row == null) return;
            ushort value;
            string result;
            if (!esiMappingMatches || ((PdoField)row.Tag).Key != DemoSlave.Mapping[0].Key)
                result = "仅支持向匹配的模拟 RxPDO 发送。";
            else if (!TryUInt16(pdoEdit.Text, out value)) result = "请输入 0–65535 或 0x 十六进制。";
            else if (!slave.Send(value)) result = "需要在 OP 启用输出并解除输出锁。";
            else
            {
                numPdoOutput.Value = value;
                chkAutoSend.Checked = false;
                result = "已加入周期输出：" + value;
            }
            esiStatus.Text = result;
            AppendPdo(result);
        }

        static bool TryUInt16(string text, out ushort value)
        {
            try {
                uint parsed = EsiDevice.Number(text);
                if (parsed <= ushort.MaxValue) { value = (ushort)parsed; return true; }
            }
            catch (FormatException) { }
            catch (OverflowException) { }
            value = 0;
            return false;
        }

        void UpdateLiveTables()
        {
            bool online = slave.Found && slave.Link;
            dcApplied.Text = "当前：" + (!online ? "未联机" : slave.Sync0Ns == 0 ? "Free Run（模拟）" : slave.SyncMode + " · " + slave.Sync0Ns + " ns（模拟）");
            liveStatus.Text = string.Format("{0}  ·  当前 {1}  ·  {2}  ·  WKC {3}/{4}  ·  周期 {5}  ·  {6}",
                online ? "在线（模拟）" : "未联机", online ? StateText(slave.State) : "—", lastStateRequest,
                slave.Actual, slave.Expected, slave.Counter,
                slave.Sync0Ns == 0 ? "Free Run" : slave.SyncMode + " · " + slave.Sync0Ns + " ns（模拟）");
            bool valid = online && slave.State == SlaveState.OP && slave.Expected > 0 && slave.Actual == slave.Expected;
            RefreshPdo(livePdo, true, valid);
            if (esiMappingMatches) RefreshPdo(esiPdo, true, valid);
        }

        void RefreshPdo(DataGridView grid, bool mapped, bool valid)
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                var field = (PdoField)row.Tag;
                row.Cells[6].Value = mapped && valid ?
                    FormatU16(field.Direction == "RxPDO" ? slave.LastOutput : slave.LastInput) : "—";
                row.Cells[7].Value = !mapped ? "离线描述" : !valid ? "暂无有效帧" : paused ? "已暂停" : "有效（模拟）";
            }
        }

        static string Address(uint index, uint sub) { return string.Format("0x{0:X4}:{1:X2}", index, sub); }

        static void AddPdoRow(DataGridView grid, PdoField field)
        {
            grid.Rows[grid.Rows.Add(field.Direction, string.Format("0x{0:X4}", field.Pdo),
                Address(field.Index, field.Sub), field.Name, field.Type, field.Bits, "—", "—")].Tag = field;
        }

        static DataGridView Table(params string[] columns)
        {
            var grid = new DataGridView {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Color.White
            };
            foreach (string column in columns)
            {
                int i = grid.Columns.Add("col" + grid.Columns.Count, column);
                grid.Columns[i].SortMode = DataGridViewColumnSortMode.NotSortable;
            }
            grid.Columns[grid.Columns.Count - 1].FillWeight = 190;
            return grid;
        }

        static TableLayoutPanel PageLayout(Control top)
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(10) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(top, 0, 0);
            return layout;
        }

        static void AddTableWithActions(TabPage tab, DataGridView grid, Control actions)
        {
            var layout = PageLayout(actions);
            layout.Controls.Add(grid, 0, 1);
            tab.Controls.Add(layout);
        }

        static FlowLayoutPanel Bar(params Control[] controls)
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = true };
            bar.Controls.AddRange(controls);
            return bar;
        }

        static Label DisplayLabel() { return new Label { AutoSize = true, Margin = new Padding(4, 8, 3, 5) }; }

        static Button ActionButton(string text, EventHandler clicked)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 28 };
            button.Click += clicked;
            return button;
        }
    }
}
