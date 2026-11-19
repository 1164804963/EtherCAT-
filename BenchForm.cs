using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace EtherCatBench
{
    sealed partial class BenchForm : Form
    {
        readonly DemoSlave slave = new DemoSlave();
        readonly Timer timer = new Timer();
        readonly CsvLog csv = new CsvLog();

        bool paused;
        bool updatingUi;
        bool lastEnabled;
        string lastCsvSignature = "";
        string lastSelfTestReport = "";

        Button btnScan, btnLink, btnInit, btnPreOp, btnSafeOp, btnOp, btnTick;
        Button btnSdoIdentity, btnSdoRead, btnSdoWrite, btnSdoParam, btnSdoClear;
        Button btnPdoSend, btnPdoClear;
        Button btnEsiBrowse, btnEsiLoad;
        Button btnCsvBrowse, btnCsvStart, btnCsvStop, btnCsvSnapshot;
        Button btnSelfTestRun, btnSelfTestSave, btnSelfTestClear;

        Label lblLinkValue, lblFoundValue, lblStateValue, lblHeaderWkc;
        Label lblPdoOutput, lblPdoFeedback, lblPdoWkc, lblPdoCounter, lblPdoBadWkc, lblPdoEnabled;
        Label lblEsiVerdict, lblCsvStatus, lblSelfTestVerdict;
        Label lblStatusLink, lblStatusFound, lblStatusState, lblStatusWkc, lblStatusCsv;

        TextBox txtSdoIndex, txtSdoValue, txtSdoLog, txtPdoLog, txtEsiPath, txtEsiResult, txtCsvPath, txtCsvLog, txtSelfTest;
        NumericUpDown numSdoSub, numPdoOutput, numAutoStep, numInterval;
        CheckBox chkSdoHex, chkPdoEnable, chkOutputLock, chkAutoSend, chkPause, chkEsiAuto, chkCsvChanges;

        public BenchForm()
        {
            Text = "EtherCAT 从站模拟调试台 · LAN9252";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1080, 760);
            MinimumSize = new Size(980, 680);
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.FromArgb(245, 247, 250);

            var root = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(0)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildTabs(), 0, 1);
            root.Controls.Add(BuildStatusBar(), 0, 2);
            Controls.Add(root);

            timer.Interval = 100;
            timer.Tick += Timer_Tick;
            timer.Start();

            lastEnabled = slave.Enabled;
            Shown += BenchForm_Shown;
            FormClosing += BenchForm_FormClosing;

            UpdateUi();
        }

        Control BuildHeader()
        {
            var header = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
                Padding = new Padding(8, 8, 8, 4)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28F));

            var link = new GroupBox {
                Text = "链路与扫描（LAN9252 模拟）", Dock = DockStyle.Fill, Margin = new Padding(4)
            };
            var linkGrid = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3,
                Padding = new Padding(8, 6, 8, 4)
            };
            linkGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            linkGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            linkGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            linkGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            linkGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            btnScan = new Button { Text = "扫描从站", Dock = DockStyle.Fill, Margin = new Padding(2) };
            btnScan.Click += BtnScan_Click;
            btnLink = new Button { Text = "模拟掉线", Dock = DockStyle.Fill, Margin = new Padding(2) };
            btnLink.Click += BtnLink_Click;
            linkGrid.Controls.Add(btnScan, 0, 0);
            linkGrid.Controls.Add(btnLink, 1, 0);

            lblLinkValue = new Label {
                Text = "链路：已连接", AutoSize = true, Anchor = AnchorStyles.Left,
                Margin = new Padding(4, 5, 2, 0)
            };
            lblFoundValue = new Label {
                Text = "从站：未扫描", AutoSize = true, Anchor = AnchorStyles.Left,
                Margin = new Padding(2, 5, 4, 0)
            };
            linkGrid.Controls.Add(lblLinkValue, 0, 1);
            linkGrid.Controls.Add(lblFoundValue, 1, 1);
            var hint = new Label {
                Text = "提示：先扫描，再逐级进入 PRE-OP / SAFE-OP / OP。",
                AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left,
                Margin = new Padding(4, 2, 2, 0)
            };
            linkGrid.Controls.Add(hint, 0, 2);
            linkGrid.SetColumnSpan(hint, 2);
            link.Controls.Add(linkGrid);
            header.Controls.Add(link, 0, 0);

            var state = new GroupBox {
                Text = "状态机（逐级切换）", Dock = DockStyle.Fill, Margin = new Padding(4)
            };
            var stateGrid = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3,
                Padding = new Padding(8, 6, 8, 4)
            };
            for (int i = 0; i < 4; i++)
                stateGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            stateGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            stateGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            stateGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            btnInit = new Button { Text = "INIT", Dock = DockStyle.Fill, Margin = new Padding(2) };
            btnPreOp = new Button { Text = "PRE-OP", Dock = DockStyle.Fill, Margin = new Padding(2) };
            btnSafeOp = new Button { Text = "SAFE-OP", Dock = DockStyle.Fill, Margin = new Padding(2) };
            btnOp = new Button { Text = "OP", Dock = DockStyle.Fill, Margin = new Padding(2) };
            btnInit.Click += delegate { MoveState(SlaveState.INIT); };
            btnPreOp.Click += delegate { MoveState(SlaveState.PRE_OP); };
            btnSafeOp.Click += delegate { MoveState(SlaveState.SAFE_OP); };
            btnOp.Click += delegate { MoveState(SlaveState.OP); };
            stateGrid.Controls.Add(btnInit, 0, 0);
            stateGrid.Controls.Add(btnPreOp, 1, 0);
            stateGrid.Controls.Add(btnSafeOp, 2, 0);
            stateGrid.Controls.Add(btnOp, 3, 0);

            lblStateValue = new Label {
                Text = "当前：INIT", AutoSize = true, Anchor = AnchorStyles.Left,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                Margin = new Padding(4, 5, 2, 0)
            };
            stateGrid.Controls.Add(lblStateValue, 0, 1);
            stateGrid.SetColumnSpan(lblStateValue, 4);
            var stateHint = new Label {
                Text = "INIT → PRE-OP → SAFE-OP → OP（跳级会被拒绝）",
                AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left,
                Margin = new Padding(4, 2, 2, 0)
            };
            stateGrid.Controls.Add(stateHint, 0, 2);
            stateGrid.SetColumnSpan(stateHint, 4);
            state.Controls.Add(stateGrid);
            header.Controls.Add(state, 1, 0);

            var cycle = new GroupBox {
                Text = "周期与单步", Dock = DockStyle.Fill, Margin = new Padding(4)
            };
            var cycleGrid = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4,
                Padding = new Padding(8, 6, 8, 4)
            };
            cycleGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76F));
            cycleGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            cycleGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            cycleGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            cycleGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            cycleGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var cycleLabel = new Label {
                Text = "周期(ms)", AutoSize = true, Anchor = AnchorStyles.Left,
                Margin = new Padding(4, 5, 2, 0)
            };
            numInterval = new NumericUpDown {
                Minimum = 50, Maximum = 5000, Value = 100, Increment = 50,
                Dock = DockStyle.Fill, Margin = new Padding(2, 2, 2, 2)
            };
            numInterval.ValueChanged += delegate { timer.Interval = (int)numInterval.Value; };
            cycleGrid.Controls.Add(cycleLabel, 0, 0);
            cycleGrid.Controls.Add(numInterval, 1, 0);

            chkPause = new CheckBox {
                Text = "暂停自动周期", AutoSize = true, Anchor = AnchorStyles.Left,
                Margin = new Padding(4, 4, 2, 0)
            };
            chkPause.CheckedChanged += delegate {
                paused = chkPause.Checked;
                AppendPdo(paused ? "周期已暂停，可点击“单步周期”手动推进。" : "周期已恢复。");
            };
            cycleGrid.Controls.Add(chkPause, 0, 1);
            cycleGrid.SetColumnSpan(chkPause, 2);

            btnTick = new Button { Text = "单步周期（手动 Tick）", Dock = DockStyle.Fill, Margin = new Padding(2) };
            btnTick.Click += BtnTick_Click;
            cycleGrid.Controls.Add(btnTick, 0, 2);
            cycleGrid.SetColumnSpan(btnTick, 2);

            lblHeaderWkc = new Label {
                Text = "WKC 0 / 0", AutoSize = true, Anchor = AnchorStyles.Left,
                Font = new Font("Consolas", 11F, FontStyle.Bold),
                Margin = new Padding(4, 2, 2, 0)
            };
            cycleGrid.Controls.Add(lblHeaderWkc, 0, 3);
            cycleGrid.SetColumnSpan(lblHeaderWkc, 2);
            cycle.Controls.Add(cycleGrid);
            header.Controls.Add(cycle, 2, 0);

            return header;
        }

        Control BuildTabs()
        {
            var tabs = new TabControl {
                Dock = DockStyle.Fill, Padding = new Point(16, 6),
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            tabs.TabPages.Add(BuildLiveTab());
            tabs.TabPages.Add(BuildEsiTables());
            tabs.TabPages.Add(BuildDcTab());
            tabs.TabPages.Add(BuildSdoTab());
            tabs.TabPages.Add(BuildPdoTab());
            tabs.TabPages.Add(BuildEsiTab());
            tabs.TabPages.Add(BuildCsvTab());
            tabs.TabPages.Add(BuildSelfTestTab());
            return tabs;
        }

        TabPage BuildSdoTab()
        {
            var page = new TabPage("SDO 访问") {
                Padding = new Padding(10), UseVisualStyleBackColor = true
            };
            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 176F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var box = new GroupBox {
                Text = "对象字典读写（0x1018 身份 / 0x2000 自定义参数）",
                Dock = DockStyle.Fill, Padding = new Padding(8)
            };
            var grid = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 4,
                Padding = new Padding(8, 6, 8, 6)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            grid.Controls.Add(Caption("索引(Hex)"), 0, 0);
            txtSdoIndex = new TextBox { Text = "1018", Dock = DockStyle.Fill, Margin = new Padding(2, 4, 6, 2) };
            grid.Controls.Add(txtSdoIndex, 1, 0);
            grid.Controls.Add(Caption("子索引"), 2, 0);
            numSdoSub = new NumericUpDown {
                Minimum = 0, Maximum = 255, Value = 1,
                Dock = DockStyle.Fill, Margin = new Padding(2, 4, 6, 2)
            };
            grid.Controls.Add(numSdoSub, 3, 0);

            grid.Controls.Add(Caption("写入值"), 0, 1);
            txtSdoValue = new TextBox { Text = "100", Dock = DockStyle.Fill, Margin = new Padding(2, 4, 6, 2) };
            grid.Controls.Add(txtSdoValue, 1, 1);
            chkSdoHex = new CheckBox {
                Text = "按 Hex 解析", AutoSize = true, Anchor = AnchorStyles.Left,
                Margin = new Padding(2, 6, 2, 0)
            };
            grid.Controls.Add(chkSdoHex, 2, 1);
            var valueHint = new Label {
                Text = "0x1018 只读；0x2000:00 可读写 UINT16。",
                AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left,
                Margin = new Padding(2, 6, 2, 0)
            };
            grid.Controls.Add(valueHint, 3, 1);

            var buttons = new FlowLayoutPanel {
                Dock = DockStyle.Fill, AutoSize = false, WrapContents = false,
                AutoScroll = true, Margin = new Padding(0)
            };
            btnSdoIdentity = new Button { Text = "读取 0x1018 身份", Width = 150, Height = 30, Margin = new Padding(2) };
            btnSdoRead = new Button { Text = "读取", Width = 80, Height = 30, Margin = new Padding(2) };
            btnSdoWrite = new Button { Text = "写入", Width = 80, Height = 30, Margin = new Padding(2) };
            btnSdoParam = new Button { Text = "切到 0x2000", Width = 112, Height = 30, Margin = new Padding(2) };
            btnSdoClear = new Button { Text = "清空日志", Width = 92, Height = 30, Margin = new Padding(2) };
            btnSdoIdentity.Click += BtnSdoIdentity_Click;
            btnSdoRead.Click += BtnSdoRead_Click;
            btnSdoWrite.Click += BtnSdoWrite_Click;
            btnSdoParam.Click += BtnSdoParam_Click;
            btnSdoClear.Click += delegate { txtSdoLog.Clear(); };
            buttons.Controls.Add(btnSdoIdentity);
            buttons.Controls.Add(btnSdoRead);
            buttons.Controls.Add(btnSdoWrite);
            buttons.Controls.Add(btnSdoParam);
            buttons.Controls.Add(btnSdoClear);
            grid.Controls.Add(buttons, 0, 2);
            grid.SetColumnSpan(buttons, 4);

            var hint = new Label {
                Text = "SDO 邮箱在 PRE-OP / SAFE-OP / OP 可用；输入 0x 前缀或勾选 Hex 均可。",
                AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left,
                Margin = new Padding(4, 4, 2, 0)
            };
            grid.Controls.Add(hint, 0, 3);
            grid.SetColumnSpan(hint, 4);
            box.Controls.Add(grid);

            var logBox = new GroupBox {
                Text = "SDO 报文记录", Dock = DockStyle.Fill, Padding = new Padding(8)
            };
            txtSdoLog = MakeLogBox();
            logBox.Controls.Add(txtSdoLog);

            layout.Controls.Add(box, 0, 0);
            layout.Controls.Add(logBox, 0, 1);
            page.Controls.Add(layout);
            return page;
        }

        TabPage BuildPdoTab()
        {
            var page = new TabPage("PDO 测试") {
                Padding = new Padding(10), UseVisualStyleBackColor = true
            };
            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 238F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var top = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0)
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));

            var controlBox = new GroupBox {
                Text = "周期输出控制（OP）", Dock = DockStyle.Fill, Padding = new Padding(8),
                Margin = new Padding(0, 0, 6, 0)
            };
            var control = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6,
                Padding = new Padding(8, 6, 8, 6)
            };
            control.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            control.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            control.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            control.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            control.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            control.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            control.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            control.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            chkPdoEnable = new CheckBox {
                Text = "启用输出（需扫描、链路正常且在 OP）",
                AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 5, 2, 0)
            };
            chkPdoEnable.CheckedChanged += ChkPdoEnable_CheckedChanged;
            control.Controls.Add(chkPdoEnable, 0, 0);
            control.SetColumnSpan(chkPdoEnable, 2);

            chkOutputLock = new CheckBox {
                Text = "输出锁（禁止写入，周期输出保持 0）",
                AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 5, 2, 0)
            };
            chkOutputLock.CheckedChanged += ChkOutputLock_CheckedChanged;
            control.Controls.Add(chkOutputLock, 0, 1);
            control.SetColumnSpan(chkOutputLock, 2);

            control.Controls.Add(Caption("输出序号"), 0, 2);
            numPdoOutput = new NumericUpDown {
                Minimum = 0, Maximum = 65535, Value = 0,
                Dock = DockStyle.Fill, Margin = new Padding(2, 4, 6, 2)
            };
            control.Controls.Add(numPdoOutput, 1, 2);

            control.Controls.Add(Caption("自动步长"), 0, 3);
            numAutoStep = new NumericUpDown {
                Minimum = 1, Maximum = 1000, Value = 1,
                Dock = DockStyle.Fill, Margin = new Padding(2, 4, 6, 2)
            };
            control.Controls.Add(numAutoStep, 1, 3);

            chkAutoSend = new CheckBox {
                Text = "自动递增发送（每周期）", AutoSize = true,
                Anchor = AnchorStyles.Left, Margin = new Padding(4, 5, 2, 0)
            };
            chkAutoSend.CheckedChanged += delegate {
                if (updatingUi) return;
                AppendPdo(chkAutoSend.Checked ? "自动递增发送已启用。" : "自动递增发送已停止。");
            };
            control.Controls.Add(chkAutoSend, 0, 4);
            control.SetColumnSpan(chkAutoSend, 2);

            var buttons = new FlowLayoutPanel {
                Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Margin = new Padding(0)
            };
            btnPdoSend = new Button { Text = "发送一次", Width = 100, Height = 30, Margin = new Padding(2) };
            btnPdoClear = new Button { Text = "输出清零", Width = 100, Height = 30, Margin = new Padding(2) };
            btnPdoSend.Click += BtnPdoSend_Click;
            btnPdoClear.Click += BtnPdoClear_Click;
            buttons.Controls.Add(btnPdoSend);
            buttons.Controls.Add(btnPdoClear);
            var sendHint = new Label {
                Text = "回显滞后一个周期。", AutoSize = true, ForeColor = Color.DimGray,
                Margin = new Padding(10, 9, 2, 0)
            };
            buttons.Controls.Add(sendHint);
            control.Controls.Add(buttons, 0, 5);
            control.SetColumnSpan(buttons, 2);
            controlBox.Controls.Add(control);

            var dataBox = new GroupBox {
                Text = "实时数据", Dock = DockStyle.Fill, Padding = new Padding(8),
                Margin = new Padding(6, 0, 0, 0)
            };
            var data = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6,
                Padding = new Padding(8, 6, 8, 6)
            };
            data.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88F));
            data.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (int i = 0; i < 6; i++)
                data.RowStyles.Add(new RowStyle(SizeType.Percent, 16.66F));
            lblPdoOutput = AddReadout(data, 0, "输出值");
            lblPdoFeedback = AddReadout(data, 1, "回显值");
            lblPdoWkc = AddReadout(data, 2, "WKC");
            lblPdoCounter = AddReadout(data, 3, "周期计数");
            lblPdoBadWkc = AddReadout(data, 4, "坏 WKC");
            lblPdoEnabled = AddReadout(data, 5, "输出状态");
            dataBox.Controls.Add(data);

            top.Controls.Add(controlBox, 0, 0);
            top.Controls.Add(dataBox, 1, 0);

            var logBox = new GroupBox {
                Text = "PDO 事件记录", Dock = DockStyle.Fill, Padding = new Padding(8)
            };
            txtPdoLog = MakeLogBox();
            logBox.Controls.Add(txtPdoLog);

            layout.Controls.Add(top, 0, 0);
            layout.Controls.Add(logBox, 0, 1);
            page.Controls.Add(layout);
            return page;
        }

        TabPage BuildEsiTab()
        {
            var page = new TabPage("ESI 核对") {
                Padding = new Padding(10), UseVisualStyleBackColor = true
            };
            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(0)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var pathBox = new GroupBox {
                Text = "本地 ESI 文件", Dock = DockStyle.Fill, Padding = new Padding(8)
            };
            var pathGrid = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2,
                Padding = new Padding(8, 6, 8, 4)
            };
            pathGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66F));
            pathGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            pathGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            pathGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118F));
            pathGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            pathGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            pathGrid.Controls.Add(Caption("文件"), 0, 0);
            txtEsiPath = new TextBox {
                Text = Path.Combine(Application.StartupPath, "DemoESI.xml"),
                Dock = DockStyle.Fill, Margin = new Padding(2, 4, 6, 2)
            };
            pathGrid.Controls.Add(txtEsiPath, 1, 0);
            btnEsiBrowse = new Button { Text = "浏览...", Dock = DockStyle.Fill, Margin = new Padding(2, 3, 2, 2) };
            btnEsiBrowse.Click += BtnEsiBrowse_Click;
            pathGrid.Controls.Add(btnEsiBrowse, 2, 0);
            btnEsiLoad = new Button { Text = "读取并核对", Dock = DockStyle.Fill, Margin = new Padding(2, 3, 2, 2) };
            btnEsiLoad.Click += delegate { LoadEsi(true); };
            pathGrid.Controls.Add(btnEsiLoad, 3, 0);

            chkEsiAuto = new CheckBox {
                Text = "启动时自动读取", Checked = true, AutoSize = true,
                Anchor = AnchorStyles.Left, Margin = new Padding(2, 4, 2, 0)
            };
            pathGrid.Controls.Add(chkEsiAuto, 0, 1);
            pathGrid.SetColumnSpan(chkEsiAuto, 2);
            var dtdHint = new Label {
                Text = "安全：DtdProcessing=Prohibit，XmlResolver=null（禁 DTD / 外部实体）",
                AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left,
                Margin = new Padding(2, 4, 2, 0)
            };
            pathGrid.Controls.Add(dtdHint, 2, 1);
            pathGrid.SetColumnSpan(dtdHint, 2);
            pathBox.Controls.Add(pathGrid);

            lblEsiVerdict = new Label {
                Text = "尚未读取 ESI。", AutoSize = true, Anchor = AnchorStyles.Left,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                ForeColor = Color.DimGray, Margin = new Padding(6, 4, 2, 0)
            };
            layout.Controls.Add(pathBox, 0, 0);
            layout.Controls.Add(lblEsiVerdict, 0, 1);

            var resultBox = new GroupBox {
                Text = "身份与固定 PDO 映射核对结果", Dock = DockStyle.Fill, Padding = new Padding(8)
            };
            txtEsiResult = MakeLogBox();
            resultBox.Controls.Add(txtEsiResult);
            layout.Controls.Add(resultBox, 0, 2);
            page.Controls.Add(layout);
            return page;
        }

        TabPage BuildCsvTab()
        {
            var page = new TabPage("CSV 日志") {
                Padding = new Padding(10), UseVisualStyleBackColor = true
            };
            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var fileBox = new GroupBox {
                Text = "CSV 记录", Dock = DockStyle.Fill, Padding = new Padding(8)
            };
            var fileGrid = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4,
                Padding = new Padding(8, 6, 8, 4)
            };
            fileGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66F));
            fileGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            fileGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            fileGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            fileGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            fileGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            fileGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            fileGrid.Controls.Add(Caption("文件"), 0, 0);
            txtCsvPath = new TextBox {
                Text = Path.Combine(Application.StartupPath,
                    string.Format("EtherCatBench-log-{0:yyyyMMdd-HHmmss}.csv", DateTime.Now)),
                Dock = DockStyle.Fill, Margin = new Padding(2, 4, 6, 2)
            };
            fileGrid.Controls.Add(txtCsvPath, 1, 0);
            btnCsvBrowse = new Button { Text = "选择...", Dock = DockStyle.Fill, Margin = new Padding(2, 3, 2, 2) };
            btnCsvBrowse.Click += BtnCsvBrowse_Click;
            fileGrid.Controls.Add(btnCsvBrowse, 2, 0);

            var buttons = new FlowLayoutPanel {
                Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Margin = new Padding(0)
            };
            btnCsvStart = new Button { Text = "开始记录", Width = 96, Height = 30, Margin = new Padding(2) };
            btnCsvStop = new Button { Text = "停止记录", Width = 96, Height = 30, Margin = new Padding(2) };
            btnCsvSnapshot = new Button { Text = "写入当前快照", Width = 116, Height = 30, Margin = new Padding(2) };
            btnCsvStart.Click += BtnCsvStart_Click;
            btnCsvStop.Click += BtnCsvStop_Click;
            btnCsvSnapshot.Click += BtnCsvSnapshot_Click;
            buttons.Controls.Add(btnCsvStart);
            buttons.Controls.Add(btnCsvStop);
            buttons.Controls.Add(btnCsvSnapshot);
            fileGrid.Controls.Add(buttons, 0, 1);
            fileGrid.SetColumnSpan(buttons, 3);

            chkCsvChanges = new CheckBox {
                Text = "仅记录变化（推荐，避免重复行）", Checked = true, AutoSize = true,
                Anchor = AnchorStyles.Left, Margin = new Padding(4, 5, 2, 0)
            };
            fileGrid.Controls.Add(chkCsvChanges, 0, 2);
            fileGrid.SetColumnSpan(chkCsvChanges, 2);

            lblCsvStatus = new Label {
                Text = "未记录", AutoSize = true, Anchor = AnchorStyles.Left,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                ForeColor = Color.DimGray, Margin = new Padding(2, 5, 2, 0)
            };
            fileGrid.Controls.Add(lblCsvStatus, 2, 2);

            var note = new Label {
                Text = "UTF-8（带 BOM），逗号分隔；追加写入，不覆盖已有日志。",
                AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left,
                Margin = new Padding(4, 2, 2, 0)
            };
            fileGrid.Controls.Add(note, 0, 3);
            fileGrid.SetColumnSpan(note, 3);
            fileBox.Controls.Add(fileGrid);

            var logBox = new GroupBox {
                Text = "日志事件", Dock = DockStyle.Fill, Padding = new Padding(8)
            };
            txtCsvLog = MakeLogBox();
            logBox.Controls.Add(txtCsvLog);

            layout.Controls.Add(fileBox, 0, 0);
            layout.Controls.Add(logBox, 0, 1);
            page.Controls.Add(layout);
            return page;
        }

        TabPage BuildSelfTestTab()
        {
            var page = new TabPage("自测") {
                Padding = new Padding(10), UseVisualStyleBackColor = true
            };
            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(0)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var buttons = new FlowLayoutPanel {
                Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Margin = new Padding(0)
            };
            btnSelfTestRun = new Button { Text = "运行全部自测", Width = 126, Height = 30, Margin = new Padding(2) };
            btnSelfTestSave = new Button { Text = "保存报告", Width = 100, Height = 30, Margin = new Padding(2) };
            btnSelfTestClear = new Button { Text = "清空", Width = 76, Height = 30, Margin = new Padding(2) };
            btnSelfTestRun.Click += BtnSelfTestRun_Click;
            btnSelfTestSave.Click += BtnSelfTestSave_Click;
            btnSelfTestClear.Click += delegate {
                txtSelfTest.Clear();
                lastSelfTestReport = "";
                lblSelfTestVerdict.Text = "尚未运行自测。";
                lblSelfTestVerdict.ForeColor = Color.DimGray;
            };
            buttons.Controls.Add(btnSelfTestRun);
            buttons.Controls.Add(btnSelfTestSave);
            buttons.Controls.Add(btnSelfTestClear);
            var hint = new Label {
                Text = "自测使用独立模型实例，不会改变当前界面中的从站状态。",
                AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(12, 9, 2, 0)
            };
            buttons.Controls.Add(hint);

            lblSelfTestVerdict = new Label {
                Text = "尚未运行自测。", AutoSize = true, Anchor = AnchorStyles.Left,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                ForeColor = Color.DimGray, Margin = new Padding(6, 4, 2, 0)
            };

            var box = new GroupBox {
                Text = "自测报告", Dock = DockStyle.Fill, Padding = new Padding(8)
            };
            txtSelfTest = MakeLogBox();
            box.Controls.Add(txtSelfTest);

            layout.Controls.Add(buttons, 0, 0);
            layout.Controls.Add(lblSelfTestVerdict, 0, 1);
            layout.Controls.Add(box, 0, 2);
            page.Controls.Add(layout);
            return page;
        }

        Control BuildStatusBar()
        {
            var bar = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1,
                BackColor = Color.FromArgb(236, 239, 244), Padding = new Padding(8, 0, 8, 0)
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            lblStatusLink = StatusLabel("链路：已连接");
            lblStatusFound = StatusLabel("从站：未扫描");
            lblStatusState = StatusLabel("状态：INIT");
            lblStatusWkc = StatusLabel("WKC：0 / 0");
            lblStatusCsv = StatusLabel("CSV：未记录");
            bar.Controls.Add(lblStatusLink, 0, 0);
            bar.Controls.Add(lblStatusFound, 1, 0);
            bar.Controls.Add(lblStatusState, 2, 0);
            bar.Controls.Add(lblStatusWkc, 3, 0);
            bar.Controls.Add(lblStatusCsv, 4, 0);
            return bar;
        }

        static Label Caption(string text)
        {
            return new Label {
                Text = text, AutoSize = true, Anchor = AnchorStyles.Left,
                ForeColor = Color.DimGray, Margin = new Padding(4, 6, 2, 0)
            };
        }

        static Label StatusLabel(string text)
        {
            return new Label {
                Text = text, AutoSize = true, Anchor = AnchorStyles.Left,
                TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 6, 8, 0)
            };
        }

        static TextBox MakeLogBox()
        {
            return new TextBox {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Both, WordWrap = false,
                Font = new Font("Consolas", 9.5F), BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        static Label AddReadout(TableLayoutPanel grid, int row, string caption)
        {
            var name = new Label {
                Text = caption, AutoSize = true, Anchor = AnchorStyles.Left,
                ForeColor = Color.DimGray, Margin = new Padding(6, 7, 4, 0)
            };
            var value = new Label {
                Text = "-", AutoSize = true, Anchor = AnchorStyles.Left,
                Font = new Font("Consolas", 12F, FontStyle.Bold),
                Margin = new Padding(4, 4, 4, 0)
            };
            grid.Controls.Add(name, 0, row);
            grid.Controls.Add(value, 1, row);
            return value;
        }

        void BenchForm_Shown(object sender, EventArgs e)
        {
            AppendSdo("就绪：请先扫描从站，再逐级进入 PRE-OP / SAFE-OP / OP。");
            AppendPdo("就绪：OP 状态并启用输出后，可发送测试序号并观察回显与 WKC。");
            AppendCsv("提示：先选择路径，再点击“开始记录”。默认文件带时间戳，避免覆盖旧日志。");
            if (chkEsiAuto.Checked) LoadEsi(false);
            UpdateUi();
        }

        void BenchForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            timer.Stop();
            csv.Close();
        }

        void Timer_Tick(object sender, EventArgs e)
        {
            if (!paused)
            {
                AutoSendIfNeeded();
                slave.Tick();
                WriteCsv(false);
            }
            UpdateUi();
        }

        void AutoSendIfNeeded()
        {
            if (!chkAutoSend.Checked || slave.OutputLocked || !slave.Enabled) return;
            int next = (int)numPdoOutput.Value + (int)numAutoStep.Value;
            if (next > 65535) next -= 65536;
            numPdoOutput.Value = next;
            if (!slave.Send((ushort)next))
            {
                updatingUi = true;
                chkAutoSend.Checked = false;
                updatingUi = false;
                AppendPdo("自动发送已停止：链路或状态不允许。");
            }
        }

        void BtnScan_Click(object sender, EventArgs e)
        {
            slave.Scan();
            if (slave.Found)
                AppendPdo("扫描完成：发现 LAN9252 模拟从站。");
            else
                AppendPdo("扫描失败：链路断开，未发现从站。");
            UpdateUi();
        }

        void BtnLink_Click(object sender, EventArgs e)
        {
            slave.ToggleLink();
            lastStateRequest = slave.Link ? "链路恢复，等待扫站" : "链路断开";
            if (slave.Link)
            {
                AppendPdo("链路已恢复：从站回到 INIT 且未扫描，请重新扫描。");
            }
            else
            {
                AppendPdo("链路已断开：输出自动禁用并归零，WKC 将变为无效。");
            }
            lastEnabled = slave.Enabled;
            UpdateUi();
        }

        void MoveState(SlaveState target)
        {
            lastStateRequest = "请求 " + StateText(target);
            if (!slave.Move(target))
            {
                string reason = !slave.Link ? "链路断开" : !slave.Found ? "请先扫描从站" : "状态切换不允许跳级";
                lastStateRequest += " · " + reason;
                AppendPdo(lastStateRequest);
            }
            else
            {
                AppendPdo("状态切换：" + StateText(target));
            }
            UpdateUi();
        }

        void BtnTick_Click(object sender, EventArgs e)
        {
            slave.Tick();
            AppendPdo("手动单步周期：WKC " + slave.Actual + " / " + slave.Expected + "。");
            UpdateUi();
        }

        void BtnSdoIdentity_Click(object sender, EventArgs e)
        {
            string first = slave.Sdo(0x1018, 0, null);
            AppendSdo("── 读取 0x1018 设备身份 ──");
            if (first.IndexOf("读取成功") < 0)
            {
                AppendSdo("读取失败：" + first);
                return;
            }
            string[] names = { "子索引数量", "厂商 ID", "产品码", "修订号", "序列号" };
            AppendSdo(string.Format("0x1018:00  {0}", first));
            for (uint sub = 1; sub <= 4; sub++)
            {
                string result = slave.Sdo(0x1018, sub, null);
                AppendSdo(string.Format("0x1018:{0:X2}  {1} · {2}", sub, names[sub], result));
            }
            UpdateUi();
        }

        void BtnSdoRead_Click(object sender, EventArgs e)
        {
            uint index;
            if (!TryParseHex(txtSdoIndex.Text, out index))
            {
                MessageBox.Show(this, "索引必须是十六进制，例如 1018 或 0x1018。",
                    "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            uint sub = (uint)numSdoSub.Value;
            string result = slave.Sdo(index, sub, null);
            AppendSdo(string.Format("读 0x{0:X4}:{1:X2} → {2}", index, sub, result));
            UpdateUi();
        }

        void BtnSdoWrite_Click(object sender, EventArgs e)
        {
            uint index;
            if (!TryParseHex(txtSdoIndex.Text, out index))
            {
                MessageBox.Show(this, "索引必须是十六进制，例如 1018 或 0x1018。",
                    "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ushort value;
            if (!TryParseU16(txtSdoValue.Text, chkSdoHex.Checked, out value))
            {
                MessageBox.Show(this, "写入值必须是 0..65535 的整数；可输入 0x 前缀。",
                    "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            uint sub = (uint)numSdoSub.Value;
            string result = slave.Sdo(index, sub, value);
            AppendSdo(string.Format("写 0x{0:X4}:{1:X2} ← {2} (0x{2:X4}) → {3}",
                index, sub, value, result));
            UpdateUi();
        }

        void BtnSdoParam_Click(object sender, EventArgs e)
        {
            txtSdoIndex.Text = "2000";
            numSdoSub.Value = 0;
            txtSdoValue.Text = "100";
            chkSdoHex.Checked = false;
            AppendSdo("已定位到自定义参数 0x2000:00（UINT16，可读写）。");
        }

        void ChkPdoEnable_CheckedChanged(object sender, EventArgs e)
        {
            if (updatingUi) return;
            if (chkPdoEnable.Checked)
            {
                if (!slave.SetEnabled(true))
                {
                    updatingUi = true;
                    chkPdoEnable.Checked = false;
                    updatingUi = false;
                    AppendPdo("启用失败：需先扫描、链路正常并进入 OP。");
                }
                else
                {
                    AppendPdo("输出已启用。");
                }
            }
            else
            {
                slave.SetEnabled(false);
                AppendPdo("输出已禁用，输出值归零。");
            }
            UpdateUi();
        }

        void ChkOutputLock_CheckedChanged(object sender, EventArgs e)
        {
            if (updatingUi) return;
            slave.SetOutputLock(chkOutputLock.Checked);
            AppendPdo(chkOutputLock.Checked
                ? "输出锁已启用：拒绝写入，周期输出保持 0。"
                : "输出锁已解除：可继续发送测试序号。");
            UpdateUi();
        }

        void BtnPdoSend_Click(object sender, EventArgs e)
        {
            if (slave.OutputLocked)
            {
                AppendPdo("发送被拒绝：输出锁已启用。");
                return;
            }
            if (!slave.Enabled)
            {
                AppendPdo("发送失败：请先在 OP 状态启用输出。");
                return;
            }
            ushort value = (ushort)numPdoOutput.Value;
            if (slave.Send(value))
                AppendPdo(string.Format("发送输出 {0} / 0x{0:X4}，等待下一周期回显。", value));
            else
                AppendPdo("发送失败：链路或状态不允许。");
            UpdateUi();
        }

        void BtnPdoClear_Click(object sender, EventArgs e)
        {
            if (slave.OutputLocked)
            {
                AppendPdo("输出清零被拒绝：输出锁已启用。");
                return;
            }
            if (!slave.Enabled)
            {
                AppendPdo("输出清零失败：请先启用输出。");
                return;
            }
            numPdoOutput.Value = 0;
            slave.Send(0);
            AppendPdo("输出已清零，将在下一周期回显 0。");
            UpdateUi();
        }

        void BtnEsiBrowse_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择本地 ESI 文件";
                dialog.Filter = "ESI/XML 文件 (*.xml)|*.xml|所有文件 (*.*)|*.*";
                dialog.CheckFileExists = true;
                try
                {
                    string path = txtEsiPath.Text.Trim();
                    if (path.Length > 0)
                    {
                        string folder = Path.GetDirectoryName(path);
                        if (folder != null && Directory.Exists(folder)) dialog.InitialDirectory = folder;
                    }
                }
                catch
                {
                }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    txtEsiPath.Text = dialog.FileName;
                    LoadEsi(true);
                }
            }
        }

        void LoadEsi(bool showDialog)
        {
            string path = txtEsiPath.Text.Trim();
            if (path.Length == 0 || !File.Exists(path))
            {
                SetImportedDevices(new EsiDevice[0]);
                lblEsiVerdict.Text = "未找到 ESI 文件，请检查路径。";
                lblEsiVerdict.ForeColor = Color.Firebrick;
                txtEsiResult.Text = "文件不存在：" + path;
                if (showDialog)
                    MessageBox.Show(this, "未找到 ESI 文件：" + path, "读取失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var devices = EsiDevice.Load(path);
                SetImportedDevices(devices);
                var text = new StringBuilder();
                text.AppendLine("文件：" + path);
                text.AppendLine("安全设置：DtdProcessing=Prohibit，XmlResolver=null（拒绝 DTD 与外部实体）");
                text.AppendLine("设备数量：" + devices.Count);
                text.AppendLine();

                bool identityOk = true;
                bool mappingOk = true;
                foreach (var device in devices)
                {
                    text.AppendLine("设备：" + device.Name);
                    text.AppendLine(string.Format(
                        "厂商 ID：0x{0:X8}    产品码：0x{1:X8}    修订号：0x{2:X8}",
                        device.Vendor, device.Product, device.Revision));
                    bool match = device.MatchesIdentity;
                    identityOk = identityOk && match;
                    text.AppendLine("身份核对：" + (match ? "匹配模拟从站" : "不匹配模拟从站"));
                    text.AppendLine("固定 PDO 映射：");
                    if (device.Fields.Count == 0)
                        text.AppendLine("  （没有 RxPdo / TxPdo 条目）");
                    foreach (var field in device.Fields)
                    {
                        text.AppendLine(string.Format(
                            "  {0}  0x{1:X4}  Fixed={2}  0x{3:X4}:{4:X2}  {5} bit  {6}  {7}",
                            field.Direction, field.Pdo, field.Fixed ? "1" : "0",
                            field.Index, field.Sub, field.Bits, field.Name, field.Type));
                    }
                    string mapping = device.MappingCheck();
                    mappingOk = mappingOk && mapping == "身份及 PDO 映射匹配";
                    text.AppendLine("映射核对：" + mapping);
                    text.AppendLine();
                }

                txtEsiResult.Text = text.ToString();
                if (identityOk && mappingOk)
                {
                    lblEsiVerdict.Text = "核对通过：身份及固定 PDO 映射与模拟从站一致。";
                    lblEsiVerdict.ForeColor = Color.SeaGreen;
                }
                else
                {
                    lblEsiVerdict.Text = "核对未通过：请查看下方身份或映射差异。";
                    lblEsiVerdict.ForeColor = Color.Firebrick;
                }
            }
            catch (Exception ex)
            {
                SetImportedDevices(new EsiDevice[0]);
                lblEsiVerdict.Text = "ESI 解析失败：" + ex.Message;
                lblEsiVerdict.ForeColor = Color.Firebrick;
                txtEsiResult.Text = "读取失败：" + path + "\r\n\r\n" + ex.GetType().Name +
                    ": " + ex.Message;
                if (showDialog)
                    MessageBox.Show(this, "ESI 解析失败：" + ex.Message, "读取失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void BtnCsvBrowse_Click(object sender, EventArgs e)
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "选择 CSV 日志文件";
                dialog.Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*";
                dialog.DefaultExt = "csv";
                dialog.AddExtension = true;
                dialog.OverwritePrompt = false;
                dialog.FileName = string.Format("EtherCatBench-log-{0:yyyyMMdd-HHmmss}.csv", DateTime.Now);
                try
                {
                    string path = txtCsvPath.Text.Trim();
                    if (path.Length > 0)
                    {
                        string folder = Path.GetDirectoryName(path);
                        if (folder != null && Directory.Exists(folder)) dialog.InitialDirectory = folder;
                    }
                }
                catch
                {
                }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    txtCsvPath.Text = dialog.FileName;
            }
        }

        void BtnCsvStart_Click(object sender, EventArgs e)
        {
            if (csv.IsOpen)
            {
                AppendCsv("日志已在记录中。");
                return;
            }
            string path = txtCsvPath.Text.Trim();
            if (path.Length == 0)
            {
                MessageBox.Show(this, "请先选择 CSV 日志路径。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                csv.Start(path);
                lastCsvSignature = "";
                WriteCsv(true);
                AppendCsv("开始记录：" + path);
                UpdateCsvStatus();
            }
            catch (Exception ex)
            {
                AppendCsv("启动记录失败：" + ex.Message);
                MessageBox.Show(this, "启动记录失败：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void BtnCsvStop_Click(object sender, EventArgs e)
        {
            if (!csv.IsOpen)
            {
                AppendCsv("当前没有正在记录的日志。");
                return;
            }
            csv.Close();
            AppendCsv("已停止记录。");
            UpdateCsvStatus();
        }

        void BtnCsvSnapshot_Click(object sender, EventArgs e)
        {
            if (!csv.IsOpen)
            {
                AppendCsv("请先开始记录，再写入快照。");
                return;
            }
            WriteCsv(true);
            AppendCsv("已写入当前状态快照。");
        }

        void WriteCsv(bool force)
        {
            if (!csv.IsOpen) return;
            string signature = CsvSignature();
            if (!force && chkCsvChanges.Checked && signature == lastCsvSignature) return;
            try
            {
                csv.Write(slave, DateTime.Now);
                lastCsvSignature = signature;
                UpdateCsvStatus();
            }
            catch (Exception ex)
            {
                csv.Close();
                AppendCsv("写入失败，已停止记录：" + ex.Message);
                UpdateCsvStatus();
            }
        }

        string CsvSignature()
        {
            return string.Join("|", new[] {
                slave.Link ? "1" : "0",
                slave.Found ? "1" : "0",
                slave.State.ToString(),
                slave.Enabled ? "1" : "0",
                slave.OutputLocked ? "1" : "0",
                slave.Output.ToString(CultureInfo.InvariantCulture),
                slave.Feedback.ToString(CultureInfo.InvariantCulture),
                slave.Actual.ToString(CultureInfo.InvariantCulture),
                slave.Expected.ToString(CultureInfo.InvariantCulture)
            });
        }

        void UpdateCsvStatus()
        {
            if (csv.IsOpen)
            {
                lblCsvStatus.Text = string.Format("记录中：{0} · 本次 {1} 行",
                    Path.GetFileName(csv.FilePath), csv.Rows);
                lblCsvStatus.ForeColor = Color.SeaGreen;
                lblStatusCsv.Text = "CSV：" + csv.Rows + " 行";
            }
            else
            {
                lblCsvStatus.Text = "未记录";
                lblCsvStatus.ForeColor = Color.DimGray;
                lblStatusCsv.Text = "CSV：未记录";
            }
        }

        void BtnSelfTestRun_Click(object sender, EventArgs e)
        {
            int failures;
            string report = BenchSelfTest.Run(txtEsiPath.Text.Trim(), out failures);
            lastSelfTestReport = report;
            txtSelfTest.Text = report;
            txtSelfTest.SelectionStart = 0;
            txtSelfTest.SelectionLength = 0;
            if (failures == 0)
            {
                lblSelfTestVerdict.Text = "自测全部通过。";
                lblSelfTestVerdict.ForeColor = Color.SeaGreen;
            }
            else
            {
                lblSelfTestVerdict.Text = string.Format("自测完成：{0} 项失败，请查看报告。", failures);
                lblSelfTestVerdict.ForeColor = Color.Firebrick;
            }
        }

        void BtnSelfTestSave_Click(object sender, EventArgs e)
        {
            if (lastSelfTestReport.Length == 0)
            {
                MessageBox.Show(this, "请先运行自测。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "保存自测报告";
                dialog.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                dialog.FileName = "selftest-report.txt";
                dialog.InitialDirectory = Application.StartupPath;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dialog.FileName, lastSelfTestReport, new UTF8Encoding(true));
                    MessageBox.Show(this, "报告已保存：" + dialog.FileName, "保存成功",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "保存失败：" + ex.Message, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        void UpdateUi()
        {
            lblLinkValue.Text = "链路：" + (slave.Link ? "已连接" : "已断开");
            lblLinkValue.ForeColor = slave.Link ? Color.SeaGreen : Color.Firebrick;
            lblFoundValue.Text = "从站：" + (slave.Found ? "已扫描" : "未扫描");
            lblFoundValue.ForeColor = slave.Found ? Color.SeaGreen : Color.DimGray;
            btnLink.Text = slave.Link ? "模拟掉线" : "恢复链路";

            lblStateValue.Text = "当前：" + (slave.Link && slave.Found ? StateText(slave.State) : "—");
            lblStateValue.ForeColor = StateColor(slave.State);

            string wkc = slave.Actual + " / " + slave.Expected;
            lblHeaderWkc.Text = "WKC " + wkc;
            lblHeaderWkc.ForeColor = WkcColor();

            lblPdoOutput.Text = FormatU16(slave.Output);
            lblPdoFeedback.Text = FormatU16(slave.Feedback);
            lblPdoWkc.Text = wkc;
            lblPdoWkc.ForeColor = WkcColor();
            lblPdoCounter.Text = slave.Counter.ToString(CultureInfo.InvariantCulture);
            lblPdoBadWkc.Text = slave.BadWkc.ToString(CultureInfo.InvariantCulture);
            lblPdoBadWkc.ForeColor = slave.BadWkc == 0 ? Color.DimGray : Color.Firebrick;

            if (!slave.Link)
                lblPdoEnabled.Text = "链路断开";
            else if (slave.OutputLocked)
                lblPdoEnabled.Text = "输出锁定";
            else if (slave.Enabled)
                lblPdoEnabled.Text = "已启用";
            else
                lblPdoEnabled.Text = "未启用";
            lblPdoEnabled.ForeColor = slave.Enabled ? Color.SeaGreen : Color.DimGray;

            if (chkPdoEnable.Checked != slave.Enabled)
            {
                updatingUi = true;
                chkPdoEnable.Checked = slave.Enabled;
                updatingUi = false;
            }
            if (chkOutputLock.Checked != slave.OutputLocked)
            {
                updatingUi = true;
                chkOutputLock.Checked = slave.OutputLocked;
                updatingUi = false;
            }

            if (lastEnabled != slave.Enabled)
            {
                if (!slave.Enabled && !slave.Link)
                    AppendPdo("链路异常：输出已自动禁用并归零。");
                lastEnabled = slave.Enabled;
            }

            lblStatusLink.Text = "链路：" + (slave.Link ? "已连接" : "已断开");
            lblStatusLink.ForeColor = slave.Link ? Color.SeaGreen : Color.Firebrick;
            lblStatusFound.Text = "从站：" + (slave.Found ? "已扫描" : "未扫描");
            lblStatusState.Text = "状态：" + (slave.Link && slave.Found ? StateText(slave.State) : "—");
            lblStatusState.ForeColor = StateColor(slave.State);
            lblStatusWkc.Text = "WKC：" + wkc;
            lblStatusWkc.ForeColor = WkcColor();
            lblStatusCsv.Text = csv.IsOpen ? "CSV：" + csv.Rows + " 行" : "CSV：未记录";
            UpdateLiveTables();
        }

        Color WkcColor()
        {
            if (slave.Expected == 0) return Color.DimGray;
            return slave.Actual == slave.Expected ? Color.SeaGreen : Color.Firebrick;
        }

        static Color StateColor(SlaveState state)
        {
            if (state == SlaveState.OP) return Color.SeaGreen;
            if (state == SlaveState.SAFE_OP) return Color.DarkOrange;
            return Color.DimGray;
        }

        static string StateText(SlaveState state)
        {
            switch (state)
            {
                case SlaveState.PRE_OP: return "PRE-OP";
                case SlaveState.SAFE_OP: return "SAFE-OP";
                case SlaveState.OP: return "OP";
                default: return "INIT";
            }
        }

        static string FormatU16(ushort value)
        {
            return string.Format("{0} / 0x{0:X4}", value);
        }

        static bool TryParseHex(string text, out uint value)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);
            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        static bool TryParseU16(string text, bool preferHex, out ushort value)
        {
            text = (text ?? "").Trim();
            value = 0;
            if (text.Length == 0) return false;
            uint parsed;
            bool ok;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
                ok = uint.TryParse(text.Substring(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out parsed);
            else if (preferHex)
                ok = uint.TryParse(text, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out parsed);
            else
                ok = uint.TryParse(text, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out parsed);
            if (!ok || parsed > ushort.MaxValue) return false;
            value = (ushort)parsed;
            return true;
        }

        void AppendSdo(string message)
        {
            AppendLine(txtSdoLog, message);
        }

        void AppendPdo(string message)
        {
            AppendLine(txtPdoLog, message);
        }

        void AppendCsv(string message)
        {
            AppendLine(txtCsvLog, message);
        }

        static void AppendLine(TextBox box, string message)
        {
            if (box == null) return;
            box.AppendText(string.Format("[{0:HH:mm:ss.fff}] {1}\r\n", DateTime.Now, message));
            if (box.TextLength > 60000)
            {
                string current = box.Text;
                box.Text = current.Substring(current.Length - 40000);
                box.SelectionStart = box.TextLength;
            }
        }
    }

    sealed class CsvLog : IDisposable
    {
        const string Header = "时间,链路,已扫描,状态,输出启用,输出锁,输出值,回显值,WKC实际,WKC期望,周期计数,坏WKC";

        StreamWriter writer;

        public string FilePath { get; private set; }
        public long Rows { get; private set; }
        public bool IsOpen { get { return writer != null; } }

        public void Start(string path)
        {
            Close();
            bool hasContent = false;
            try
            {
                hasContent = File.Exists(path) && new FileInfo(path).Length > 0;
            }
            catch
            {
            }
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            writer = new StreamWriter(stream, new UTF8Encoding(true));
            writer.AutoFlush = true;
            FilePath = path;
            Rows = 0;
            if (!hasContent) writer.WriteLine(Header);
        }

        public void Write(DemoSlave slave, DateTime time)
        {
            if (writer == null) return;
            string line = string.Join(",", new[] {
                CsvValue(time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                CsvValue(slave.Link ? "已连接" : "已断开"),
                CsvValue(slave.Found ? "已扫描" : "未扫描"),
                CsvValue(slave.State.ToString()),
                slave.Enabled ? "1" : "0",
                slave.OutputLocked ? "1" : "0",
                slave.Output.ToString(CultureInfo.InvariantCulture),
                slave.Feedback.ToString(CultureInfo.InvariantCulture),
                slave.Actual.ToString(CultureInfo.InvariantCulture),
                slave.Expected.ToString(CultureInfo.InvariantCulture),
                slave.Counter.ToString(CultureInfo.InvariantCulture),
                slave.BadWkc.ToString(CultureInfo.InvariantCulture)
            });
            writer.WriteLine(line);
            Rows++;
        }

        public void Close()
        {
            if (writer == null) return;
            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch
            {
            }
            writer = null;
        }

        public void Dispose()
        {
            Close();
        }

        internal static string CsvValue(string value)
        {
            if (value == null) return "";
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}
