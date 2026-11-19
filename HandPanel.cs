using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EtherCatBench
{
    sealed partial class HardwareForm
    {
        TabPage handPage;
        TabControl handDetails;
        TabPage handSensors;
        DataGridView handAxes, handParameters;
        Label handStatus, handSdoStatus;
        Button handUnlock, handLock, handApplyAxis, handApplyAll, handSdoRead, handSdoReadAll, handSdoWrite;
        ComboBox handTouchGroup;
        TextBox handSdoValue;
        SampleMatrix handTemperatures, handTouch;
        EsiDevice handDevice;
        bool handProfile, handMappingMatches, handConnected;
        SlaveIdentity handIdentity;
        PdoField[] handFields;
        HandPdoLayout handMapping = HandPdoLayout.FromMapping(null);
        HardwareFrame lastHandFrame;

        TabPage BuildHandTab()
        {
            handPage = new TabPage("灵巧手");
            var panel = RowsWithFill(30, 36, 165);
            handStatus = LabelFor("导入对应的灵巧手 ESI 后连接从站。");
            panel.Controls.Add(Bar(handStatus), 0, 0);

            handUnlock = ButtonFor("解锁输出", async delegate {
                await Execute("解锁灵巧手输出", async delegate { await bus.SetArmed(true, SelectedDevice); });
            });
            handLock = ButtonFor("锁定并清零", async delegate {
                await Execute("锁定灵巧手输出", async delegate { await bus.SetArmed(false, null); });
            });
            handApplyAxis = ButtonFor("应用选中轴", async delegate { await ApplyHandTargets(false); });
            handApplyAll = ButtonFor("应用全部", async delegate { await ApplyHandTargets(true); });
            panel.Controls.Add(Bar(handUnlock, handLock, handApplyAxis, handApplyAll,
                LabelFor("目标值为原始整数。请填写要提交的轴的三项。")), 0, 1);

            handAxes = Grid("轴", "目标角度", "目标力矩", "目标速度", "已下发 角度 / 力矩 / 速度", "反馈角度", "反馈力矩");
            handAxes.RowTemplate.Height = 21;
            handAxes.ReadOnly = false;
            handAxes.SelectionMode = DataGridViewSelectionMode.CellSelect;
            handAxes.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            for (int i = 0; i < handAxes.Columns.Count; i++)
            {
                bool editable = i >= 1 && i <= 3;
                handAxes.Columns[i].ReadOnly = !editable;
                handAxes.Columns[i].DefaultCellStyle.BackColor = editable ? Color.FromArgb(234, 245, 253) : Color.White;
                handAxes.Columns[i].FillWeight = i == 0 ? 54 : i == 4 ? 175 : 100;
            }
            for (int i = 0; i < HandPdoLayout.Axes; i++)
                handAxes.Rows.Add("轴 " + (i + 1), "", "", "", "—", "—", "—");
            panel.Controls.Add(handAxes, 0, 2);

            handDetails = new TabControl { Dock = DockStyle.Fill };
            handDetails.TabPages.Add(BuildHandSensors());
            handDetails.TabPages.Add(BuildHandParameters());
            handDetails.SelectedIndexChanged += delegate {
                lastHandFrame = null;
                RenderHand(bus.Frame);
            };
            panel.Controls.Add(handDetails, 0, 3);
            handPage.Controls.Add(panel);
            return handPage;
        }

        TabPage BuildHandSensors()
        {
            handSensors = new TabPage("温度与触觉");
            handSensors.AutoScroll = true;
            var columns = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Height = 224 };
            handSensors.ClientSizeChanged += delegate { columns.Height = Math.Max(224, handSensors.ClientSize.Height); };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var temperature = RowsWithFill(30);
            temperature.Controls.Add(Bar(LabelFor("温度像素 · 64 点 · 原始字节")), 0, 0);
            handTemperatures = new SampleMatrix(HandPdoLayout.TemperatureSamples, 8) { Dock = DockStyle.Fill };
            temperature.Controls.Add(handTemperatures, 0, 1);
            columns.Controls.Add(temperature, 0, 0);

            var tactile = RowsWithFill(30);
            handTouchGroup = ListBox(95);
            for (int i = 0; i < HandPdoLayout.TouchGroups; i++) handTouchGroup.Items.Add("触觉 " + (i + 1));
            handTouchGroup.SelectedIndex = 0;
            handTouchGroup.SelectedIndexChanged += delegate {
                lastHandFrame = null;
                RenderHand(bus.Frame);
            };
            tactile.Controls.Add(Bar(handTouchGroup, LabelFor("48 点 · 按索引排列")), 0, 0);
            handTouch = new SampleMatrix(HandPdoLayout.TouchSamples, 8) { Dock = DockStyle.Fill };
            tactile.Controls.Add(handTouch, 0, 1);
            columns.Controls.Add(tactile, 1, 0);
            handSensors.Controls.Add(columns);
            return handSensors;
        }

        TabPage BuildHandParameters()
        {
            var page = new TabPage("设备信息与配置");
            var panel = RowsWithFill(36, 27);
            handSdoValue = new TextBox { Width = 95 };
            handSdoRead = ButtonFor("读取选中项", async delegate { await ReadOrWriteHandParameter(false); });
            handSdoReadAll = ButtonFor("读取全部", async delegate { await ReadAllHandParameters(); });
            handSdoWrite = ButtonFor("写入选中项", async delegate { await ReadOrWriteHandParameter(true); });
            handSdoStatus = LabelFor("请导入声明设备信息或配置项的 ESI。");
            panel.Controls.Add(Bar(handSdoRead, handSdoReadAll, LabelFor("写入值"), handSdoValue, handSdoWrite), 0, 0);
            panel.Controls.Add(Bar(handSdoStatus), 0, 1);
            handParameters = Grid("对象", "名称", "权限", "上次读回", "结果");
            handParameters.CurrentCellChanged += delegate { DrawActions(bus.Frame); };
            panel.Controls.Add(handParameters, 0, 2);
            page.Controls.Add(panel);
            return page;
        }

        void RebuildHandParameters(EsiDevice device)
        {
            handParameters.Rows.Clear();
            if (device != null)
            {
                var groups = device.Sdos.Where(entry => entry.Index == 0x8000 || entry.Index == 0x9000)
                    .GroupBy(entry => entry.Index).OrderByDescending(group => group.Key);
                foreach (var group in groups)
                {
                    bool record = group.Any(entry => entry.Sub != 0);
                    foreach (var entry in group.Where(entry => !record || entry.Sub != 0).OrderBy(entry => entry.Sub))
                    {
                        string access = entry.Access.Length == 0 ? "未声明" : entry.Access;
                        int row = handParameters.Rows.Add(Address(entry.Index, entry.Sub), entry.Name, access, "—", "");
                        handParameters.Rows[row].Tag = entry;
                        handParameters.Rows[row].Cells[2].ToolTipText =
                            (entry.ReadRestrictions == null ? "" : "读：" + entry.ReadRestrictions + "  ") +
                            (entry.WriteRestrictions == null ? "" : "写：" + entry.WriteRestrictions);
                    }
                }
            }
            ResetHandParameterResults();
        }

        void ResetHandParameterResults()
        {
            foreach (DataGridViewRow row in handParameters.Rows)
            {
                row.Cells[3].Value = "—";
                row.Cells[4].Value = "";
            }
            handSdoStatus.Text = handParameters.Rows.Count == 0 ? "请导入声明设备信息或配置项的 ESI。" :
                "读写权限来自所选 ESI。可读写项写入后自动回读。";
        }

        async Task ApplyHandTargets(bool all)
        {
            int axis = handAxes.CurrentRow == null ? -1 : handAxes.CurrentRow.Index;
            if (!all && axis < 0) return;
            await Execute(all ? "提交六轴目标" : "提交轴 " + (axis + 1) + " 目标", async delegate {
                if (!handAxes.EndEdit()) throw new InvalidOperationException("请先完成目标值编辑。");
                int[] axes = all ? Enumerable.Range(0, HandPdoLayout.Axes).ToArray() : new[] { axis };
                var targets = axes.Select(i => new HandTarget {
                    Axis = i, Angle = HandDraft(i, 1, "角度"),
                    Torque = HandDraft(i, 2, "力矩"), Speed = HandDraft(i, 3, "速度")
                }).ToArray();
                await bus.WriteHandTargets(SelectedDevice, targets);
            });
        }

        ushort HandDraft(int axis, int column, string name)
        {
            string text = Convert.ToString(handAxes.Rows[axis].Cells[column].Value, CultureInfo.InvariantCulture);
            try { return BitConverter.ToUInt16(FieldValue.Encode(text, "UINT", 16), 0); }
            catch (FormatException) { }
            catch (OverflowException) { }
            throw new FormatException("轴 " + (axis + 1) + " 的目标" + name + "请输入 0–65535 或 0x 十六进制值。");
        }

        async Task ReadOrWriteHandParameter(bool write)
        {
            var row = handParameters.CurrentRow;
            var parameter = row == null ? null : row.Tag as SdoEntry;
            int state = bus.Frame.State;
            if (parameter == null || (write ? !parameter.WritableIn(state) : !parameter.ReadableIn(state))) return;
            var device = SelectedDevice;
            await Execute((write ? "写入 " : "读取 ") + Address(parameter.Index, parameter.Sub), async delegate {
                if (!bus.Frame.Identity.Matches(device))
                    throw new InvalidOperationException("请先导入匹配的灵巧手 ESI 并连接从站。");
                byte[] target = write ? FieldValue.Encode(handSdoValue.Text, parameter.Type, (int)parameter.Bits) : null;
                bool written = false;
                try
                {
                    if (write)
                    {
                        await bus.Sdo((ushort)parameter.Index, (byte)parameter.Sub, target, device, parameter);
                        written = true;
                    }
                    if (!CanContinueHandRead(row)) return;
                    if (!parameter.Readable)
                    {
                        row.Cells[4].Value = "已写入（ESI 只写）";
                        return;
                    }
                    if (!parameter.ReadableIn(bus.Frame.State))
                    {
                        row.Cells[4].Value = "已写入（ESI 限制当前状态回读）";
                        return;
                    }
                    await ReadHandParameter(row, parameter, device, target);
                }
                catch (InvalidOperationException error)
                {
                    if (CanContinueHandRead(row))
                        row.Cells[4].Value = (written ? "写入已接受，回读失败" : write ? "写入未确认" : "读取失败") + " · " + error.Message;
                    throw;
                }
            });
        }

        async Task ReadAllHandParameters()
        {
            var device = SelectedDevice;
            var rows = handParameters.Rows.Cast<DataGridViewRow>().ToArray();
            await Execute("读取全部灵巧手参数", async delegate {
                if (!bus.Frame.Identity.Matches(device))
                    throw new InvalidOperationException("请先导入匹配的灵巧手 ESI 并连接从站。");
                int read = 0, failed = 0, skipped = 0;
                foreach (var row in rows)
                {
                    if (!CanContinueHandRead(row)) return;
                    var entry = (SdoEntry)row.Tag;
                    if (!entry.ReadableIn(bus.Frame.State))
                    {
                        row.Cells[4].Value = entry.Readable ? "跳过（ESI 限制当前状态读取）" :
                            entry.Access == "wo" ? "跳过（ESI 只写）" : "跳过（ESI 未声明可读）";
                        skipped++;
                        continue;
                    }
                    handSdoStatus.Text = "正在读取 " + Address(entry.Index, entry.Sub) + "…";
                    try
                    {
                        await ReadHandParameter(row, entry, device, null);
                        read++;
                    }
                    catch (InvalidOperationException error)
                    {
                        if (!CanContinueHandRead(row)) return;
                        row.Cells[4].Value = "读取失败 · " + error.Message;
                        failed++;
                    }
                }
                if (!IsDisposed && !closing && !closePending)
                    handSdoStatus.Text = string.Format("读取完成 · 成功 {0} · 失败 {1} · 跳过 {2}", read, failed, skipped);
            });
        }

        bool CanContinueHandRead(DataGridViewRow row)
        {
            return !IsDisposed && !closing && !closePending && row.DataGridView == handParameters;
        }

        async Task ReadHandParameter(DataGridViewRow row, SdoEntry entry, EsiDevice device, byte[] expected)
        {
            byte[] result = await bus.Sdo((ushort)entry.Index, (byte)entry.Sub, null, device, entry);
            if (!CanContinueHandRead(row)) return;
            string decoded = FieldValue.Decode(result, entry.Type, (int)entry.Bits);
            string hex = FieldValue.Bytes(result);
            row.Cells[3].Value = decoded == hex ? hex : decoded + "  [" + hex + "]";
            row.Cells[4].Value = expected == null ? "已读取" :
                expected.SequenceEqual(result) ? "字典回读一致" : "字典回读不同";
        }

        void RenderHand(HardwareFrame current)
        {
            if (handPage == null) return;
            EsiDevice device = SelectedDevice;
            bool identityChanged = !handIdentity.Equals(current.Identity) || handConnected != current.Connected;
            if (!ReferenceEquals(handDevice, device) || identityChanged)
            {
                bool deviceChanged = !ReferenceEquals(handDevice, device);
                handDevice = device;
                handIdentity = current.Identity;
                handConnected = current.Connected;
                handProfile = device != null && HandPdoLayout.FromMapping(device.Fields, true).Complete;
                if (deviceChanged) RebuildHandParameters(device);
                else ResetHandParameterResults();
                UpdateHandMapping(current);
            }
            else if (!ReferenceEquals(handFields, current.Mapping))
                UpdateHandMapping(current);

            var host = handPage.Parent as TabControl;
            if (host == null || host.SelectedTab != handPage || ReferenceEquals(lastHandFrame, current)) return;
            lastHandFrame = current;
            bool matching = HandIdentityMatches(current);
            bool fresh = matching && current.Online && current.Tx != null && current.AlMisses == 0;
            string feedbackNote = fresh && handMapping.Complete ? "PDO 反馈有效" : "暂无完整 PDO 反馈";
            handStatus.Text = !handProfile ? "请选择包含完整灵巧手字段的产品 ESI。" :
                !current.Connected ? "ESI 已识别，等待连接。" :
                !matching ? "在线从站与灵巧手 ESI 不匹配。" :
                feedbackNote + " · " + (handMappingMatches ? "映射已核对" : "在线映射未核对") +
                    " · " + (current.Armed ? "输出已解锁" : "输出锁定");
            byte[] sent = matching && current.Online ? current.Rx : null;
            byte[] values = fresh ? current.Tx : null;
            for (int i = 0; i < HandPdoLayout.Axes; i++)
            {
                var cells = handAxes.Rows[i].Cells;
                cells[4].Value = string.Join(" / ", new[] {
                    ShowRaw(handMapping.ReadSent(sent, i, 0)), ShowRaw(handMapping.ReadSent(sent, i, 1)),
                    ShowRaw(handMapping.ReadSent(sent, i, 2)) });
                cells[5].Value = ShowRaw(handMapping.ReadFeedback(values, i, 0));
                cells[6].Value = ShowRaw(handMapping.ReadFeedback(values, i, 1));
            }
            if (handDetails.SelectedTab == handSensors)
            {
                handTemperatures.ShowSamples(handMapping.ReadTemperatures(values));
                handTouch.ShowSamples(handMapping.ReadTouch(values, handTouchGroup.SelectedIndex));
            }
        }

        void UpdateHandMapping(HardwareFrame current)
        {
            handFields = current.Mapping;
            handMapping = HandPdoLayout.FromMapping(handFields);
            handMappingMatches = handProfile && handMapping.Complete && HardwareBus.MatchesMapping(current, handDevice);
            lastHandFrame = null;
        }

        bool HandIdentityMatches(HardwareFrame current)
        {
            return handProfile && current.Connected && current.Identity.Matches(handDevice);
        }

        void UpdateHandActions(HardwareFrame current, bool ready)
        {
            if (handPage == null) return;
            bool online = ready && HandIdentityMatches(current) && current.Online && current.AlMisses == 0;
            bool canWrite = online && handMappingMatches && current.State == 8 && current.Wkc > 0 &&
                current.Wkc == current.Expected;
            handUnlock.Enabled = canWrite && !current.Armed;
            handLock.Enabled = online && current.Armed;
            handApplyAxis.Enabled = handApplyAll.Enabled = canWrite && current.Armed;
            bool mailbox = ready && current.Connected && current.Identity.Matches(handDevice) && current.Online &&
                current.AlMisses == 0 && (current.State == 2 || current.State == 4 || (current.State == 8 && !current.Armed));
            var row = handParameters.CurrentRow;
            var parameter = row == null ? null : row.Tag as SdoEntry;
            handSdoRead.Enabled = mailbox && parameter != null && parameter.ReadableIn(current.State);
            handSdoReadAll.Enabled = mailbox && handParameters.Rows.Cast<DataGridViewRow>()
                .Any(item => item.Tag is SdoEntry && ((SdoEntry)item.Tag).ReadableIn(current.State));
            handSdoWrite.Enabled = handSdoValue.Enabled = mailbox && parameter != null && parameter.WritableIn(current.State);
        }

        static string ShowRaw<T>(T? value) where T : struct, IFormattable
        {
            return value.HasValue ? value.Value.ToString(null, CultureInfo.InvariantCulture) : "—";
        }
    }
}
