using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace EtherCatBench
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                string argument = (args[0] ?? "").ToLowerInvariant();
                if (argument == "/selftest" || argument == "-selftest" || argument == "--selftest" || argument == "--self-test")
                {
                    int failures;
                    string esiPath = Path.Combine(Application.StartupPath, "DemoESI.xml");
                    string report = BenchSelfTest.Run(esiPath, out failures);
                    try
                    {
                        Console.WriteLine(report);
                    }
                    catch
                    {
                    }
                    try
                    {
                        File.WriteAllText(
                            Path.Combine(Application.StartupPath, "selftest-report.txt"),
                            report, new UTF8Encoding(true));
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Console.WriteLine("报告写入失败：" + ex.Message);
                        }
                        catch
                        {
                        }
                    }
                    Environment.ExitCode = failures == 0 ? 0 : 1;
                    return;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args != null && args.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase)))
                Application.Run(new BenchForm());
            else
                Application.Run(new HardwareForm());
        }
    }

    static class BenchSelfTest
    {
        sealed class TestReport
        {
            public int Failures;
            public readonly List<string> Lines = new List<string>();

            public void Check(string name, Func<bool> test)
            {
                try
                {
                    bool ok = test();
                    if (!ok) Failures++;
                    Lines.Add((ok ? "[通过] " : "[失败] ") + name);
                }
                catch (Exception ex)
                {
                    Failures++;
                    Lines.Add("[失败] " + name + " · " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        public static string Run(string esiPath, out int failures)
        {
            var report = new TestReport();
            report.Lines.Add("EtherCAT 从站模拟调试台 · 自测报告");
            report.Lines.Add("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.Lines.Add("ESI：" + esiPath);
            report.Lines.Add("");

            report.Check("初始状态为 INIT、未扫描、输出关闭", delegate
            {
                var s = new DemoSlave();
                return s.Link && !s.Found && s.State == SlaveState.INIT && !s.Enabled
                    && s.OutputLocked && s.Output == 0 && s.Feedback == 0
                    && s.Counter == 0 && s.BadWkc == 0;
            });

            report.Check("扫描后只能逐级切换状态，跳级被拒绝", delegate
            {
                var s = new DemoSlave();
                if (s.Move(SlaveState.PRE_OP)) return false;
                s.Scan();
                if (!s.Found) return false;
                if (s.Move(SlaveState.OP)) return false;
                if (s.Move(SlaveState.SAFE_OP)) return false;
                if (!s.Move(SlaveState.PRE_OP)) return false;
                if (!s.Move(SlaveState.SAFE_OP)) return false;
                if (!s.Move(SlaveState.OP)) return false;
                return s.State == SlaveState.OP && s.Expected == 3;
            });

            report.Check("PDO 测试序号回显与 WKC=3", delegate
            {
                var s = new DemoSlave();
                if (!EnterOp(s)) return false;
                if (!s.SetEnabled(true) || !s.OutputLocked || s.Send(42)) return false;
                s.SetOutputLock(false);
                if (!s.Send(42)) return false;
                s.Tick();
                s.Tick();
                return s.Output == 42 && s.Feedback == 42 && s.Actual == 3
                    && s.Expected == 3 && s.Counter == 2;
            });

            report.Check("输出锁拒绝写入并归零", delegate
            {
                var s = new DemoSlave();
                if (!EnterOp(s)) return false;
                s.SetOutputLock(false);
                if (!s.SetEnabled(true) || !s.Send(42)) return false;
                s.Tick();
                s.Tick();
                s.SetOutputLock(true);
                bool blocked = !s.Send(99);
                s.Tick();
                bool zero = s.Output == 0 && s.Feedback == 0;
                s.SetOutputLock(false);
                bool sent = s.Send(7);
                s.Tick();
                s.Tick();
                return blocked && zero && sent && s.Feedback == 7 && !s.OutputLocked;
            });

            report.Check("掉线后输出禁用且坏 WKC 递增", delegate
            {
                var s = new DemoSlave();
                if (!EnterOp(s)) return false;
                s.SetOutputLock(false);
                if (!s.SetEnabled(true) || !s.Send(11)) return false;
                s.Tick();
                uint before = s.BadWkc;
                s.ToggleLink();
                bool offline = !s.Link && !s.Enabled && s.Output == 0 && s.Feedback == 0;
                s.Tick();
                return offline && s.BadWkc == before + 1 && s.Actual == 0 && !s.Send(12);
            });

            report.Check("掉线恢复需重新扫描并逐级回到 OP", delegate
            {
                var s = new DemoSlave();
                if (!EnterOp(s)) return false;
                s.SetOutputLock(false);
                if (!s.SetEnabled(true) || !s.Send(11)) return false;
                s.Tick();
                s.ToggleLink();
                s.Tick();
                uint bad = s.BadWkc;
                s.ToggleLink();
                bool reset = s.Link && !s.Found && s.State == SlaveState.INIT && s.OutputLocked && !s.Enabled && bad > 0;
                if (!reset) return false;
                if (!EnterOp(s)) return false;
                s.SetOutputLock(false);
                if (!s.SetEnabled(true) || !s.Send(21)) return false;
                s.Tick();
                s.Tick();
                return s.Found && s.State == SlaveState.OP && s.Enabled
                    && s.Feedback == 21 && s.BadWkc == bad;
            });

            report.Check("离开 OP 后 WKC 归零且输出复位", delegate
            {
                var s = new DemoSlave();
                if (!EnterOp(s)) return false;
                s.SetOutputLock(false);
                if (!s.SetEnabled(true) || !s.Send(5)) return false;
                s.Tick();
                s.Tick();
                if (!s.Move(SlaveState.SAFE_OP)) return false;
                s.Tick();
                return s.State == SlaveState.SAFE_OP && !s.Enabled && s.Output == 0
                    && s.Feedback == 0 && s.Expected == 0 && s.Actual == 0;
            });

            report.Check("SDO 0x1018 身份与 0x2000 自定义参数", delegate
            {
                var s = new DemoSlave();
                bool noMailbox = s.Sdo(0x1018, 1, null).IndexOf("邮箱不可用") >= 0;
                s.Scan();
                noMailbox = noMailbox && s.Sdo(0x1018, 1, null).IndexOf("邮箱不可用") >= 0;
                if (!s.Move(SlaveState.PRE_OP)) return false;
                string vendor = s.Sdo(0x1018, 1, null);
                string readOnly = s.Sdo(0x1018, 1, 1);
                string badSub = s.Sdo(0x1018, 9, null);
                string parameter0 = s.Sdo(0x2000, 0, null);
                string parameterWrite = s.Sdo(0x2000, 0, 321);
                string parameter1 = s.Sdo(0x2000, 0, null);
                string unknown = s.Sdo(0x3000, 0, null);
                return noMailbox
                    && vendor.IndexOf("0x0000FFFF") >= 0
                    && readOnly.IndexOf("0x06010002") >= 0
                    && badSub.IndexOf("0x06090011") >= 0
                    && parameter0.IndexOf("100") >= 0
                    && parameterWrite.IndexOf("写入成功") >= 0
                    && parameter1.IndexOf("321") >= 0
                    && unknown.IndexOf("0x06020000") >= 0;
            });

            report.Check("ESI 身份与固定 PDO 映射匹配", delegate
            {
                if (!File.Exists(esiPath)) return false;
                var devices = EsiDevice.Load(esiPath);
                if (devices.Count == 0) return false;
                var device = devices[0];
                bool fixedMapping = device.Fields.Count > 0;
                foreach (var field in device.Fields)
                    fixedMapping = fixedMapping && field.Fixed;
                return device.Vendor == 0x0000FFFF && device.Product == 0x92520001
                    && device.Revision == 0x00010000 && device.Fields.Count == 2
                    && fixedMapping && device.MatchesIdentity
                    && device.MappingCheck() == "身份及 PDO 映射匹配";
            });

            report.Check("ESI 解析器拒绝 DTD（禁外部实体）", delegate
            {
                string xml = "<?xml version=\"1.0\"?><!DOCTYPE EtherCATInfo [<!ENTITY x \"y\">]>" +
                    "<EtherCATInfo><Vendor><Id>1</Id></Vendor>" +
                    "<Descriptions><Devices/></Descriptions></EtherCATInfo>";
                try
                {
                    EsiDevice.LoadXml(xml);
                }
                catch (XmlException)
                {
                    return true;
                }
                catch (FormatException)
                {
                    return true;
                }
                return false;
            });

            report.Check("SDO 0x1018 与 ESI 身份一致", delegate
            {
                if (!File.Exists(esiPath)) return false;
                var device = EsiDevice.Load(esiPath)[0];
                var s = new DemoSlave();
                if (!EnterOp(s)) return false;
                string vendor = s.Sdo(0x1018, 1, null);
                string product = s.Sdo(0x1018, 2, null);
                string revision = s.Sdo(0x1018, 3, null);
                return vendor.IndexOf(string.Format("0x{0:X8}", device.Vendor)) >= 0
                    && product.IndexOf(string.Format("0x{0:X8}", device.Product)) >= 0
                    && revision.IndexOf(string.Format("0x{0:X8}", device.Revision)) >= 0;
            });

            report.Check("实时 PDO 区分待发与已交换帧，TxPDO 下一周期回显", delegate
            {
                var s = new DemoSlave();
                if (!EnterOp(s) || !s.SetEnabled(true)) return false;
                s.SetOutputLock(false);
                if (!s.Send(123) || s.LastOutput != 0) return false;
                s.Tick();
                if (s.LastOutput != 123 || s.LastInput != 0) return false;
                s.Tick();
                if (s.LastInput != 123 || s.Actual != 3) return false;
                s.ToggleLink();
                return s.LastOutput == 0 && s.LastInput == 0 && s.Actual == 0;
            });

            report.Check("ESI 展开记录型 SDO，并识别读写权限和类型", delegate
            {
                var device = EsiDevice.Load(esiPath).Single();
                var revision = device.Sdos.Single(s => s.Index == 0x1018 && s.Sub == 3);
                var parameter = device.Sdos.Single(s => s.Index == 0x2000 && s.Sub == 0);
                return device.Sdos.Count == 6 && revision.Readable && !revision.Writable &&
                    revision.Type == "UDINT" && revision.Bits == 32 &&
                    parameter.Readable && parameter.Writable && parameter.Type == "UINT" && parameter.Bits == 16;
            });

            report.Check("无 SDO 字典且含多设备的 ESI 仍可读取，PDO 类型差异阻止映射匹配", delegate
            {
                const string minimal = "<EtherCATInfo><Vendor><Id>1</Id></Vendor><Descriptions><Devices>" +
                    "<Device><Type ProductCode='1'>A</Type></Device><Device><Type ProductCode='2'>B</Type></Device>" +
                    "</Devices></Descriptions></EtherCATInfo>";
                var devices = EsiDevice.LoadXml(minimal);
                var mismatch = EsiDevice.LoadXml(File.ReadAllText(esiPath).Replace(
                    "<DataType>UINT</DataType>", "<DataType>INT</DataType>")).Single();
                return devices.Count == 2 && devices.All(d => !d.MatchesIdentity && d.Sdos.Count == 0) &&
                    mismatch.MatchesIdentity && !mismatch.MatchesMapping;
            });

            report.Check("DC 模式声明、PRE-OP 限制、Hz/ns 换算、无效频率和掉线复位", delegate
            {
                var s = new DemoSlave();
                var mode = EsiDevice.Load(esiPath).Single().DcModes.Single();
                var offline = new DcMode { Name = "仅查看", Assign = 0x0700, Simulated = false };
                var freeRun = new DcMode { Name = "Free Run" };
                string message;
                if (!mode.Simulated || mode.Assign != 0x0300 || mode.CycleNs != 1000000) return false;
                if (s.ConfigureSync(mode, 1000, out message)) return false;
                s.Scan();
                if (!s.Move(SlaveState.PRE_OP) || s.ConfigureSync(mode, 0, out message) ||
                    s.ConfigureSync(mode, 1000001, out message) || s.ConfigureSync(offline, 1000, out message)) return false;
                if (!s.ConfigureSync(mode, 1000, out message) || s.Sync0Ns != 1000000 ||
                    !s.ConfigureSync(mode, 333.333m, out message) || s.Sync0Ns != 3000003 ||
                    !s.ConfigureSync(freeRun, 0, out message) || s.Sync0Ns != 0 ||
                    !s.ConfigureSync(mode, 2000, out message) || s.Sync0Ns != 500000) return false;
                if (!s.Move(SlaveState.SAFE_OP) || s.ConfigureSync(freeRun, 0, out message)) return false;
                s.ToggleLink();
                s.ToggleLink();
                return s.Sync0Ns == 0;
            });

            report.Check("孤立 AL 超时可以恢复，连续超时且没有 PDO 才判掉线", delegate
            {
                var transient = new LinkWatch();
                if (transient.ObserveAl(false, 100) || transient.AlMisses != 1 ||
                    transient.ObserveAl(true, 200) || transient.AlMisses != 0 || transient.AlTimeouts != 1)
                    return false;
                var livePdo = new LinkWatch();
                for (int ms = 0; ms <= 1000; ms += 100)
                {
                    livePdo.ObservePdo(ms);
                    if (livePdo.ObserveAl(false, ms)) return false;
                }
                for (int ms = 1100; ms < 1500; ms += 100)
                    if (livePdo.ObserveAl(false, ms)) return false;
                if (!livePdo.ObserveAl(false, 1500)) return false;

                var down = new LinkWatch();
                for (int ms = 0; ms < 500; ms += 100)
                    if (down.ObserveAl(false, ms)) return false;
                return down.ObserveAl(false, 500);
            });

            report.Check("PDO 绝对时隙按 1 ms 推进，跳过过期时隙", delegate
            {
                long missed;
                return PdoTimer.Advance(1000, 100, 1010, out missed) == 1100 && missed == 0 &&
                    PdoTimer.Advance(1100, 100, 1390, out missed) == 1400 && missed == 2 &&
                    PdoTimer.Advance(1400, 100, 1500, out missed) == 1600 && missed == 1;
            });

            report.Check("CSV 字段转义（逗号/引号/换行）", delegate
            {
                return CsvLog.CsvValue("a,b") == "\"a,b\""
                    && CsvLog.CsvValue("a\"b") == "\"a\"\"b\""
                    && CsvLog.CsvValue("a\r\nb") == "\"a\r\nb\""
                    && CsvLog.CsvValue("plain") == "plain";
            });

            report.Lines.Add("");
            report.Lines.Add(report.Failures == 0
                ? "结果：全部通过"
                : "结果：" + report.Failures + " 项失败");
            failures = report.Failures;
            return string.Join("\r\n", report.Lines.ToArray()) + "\r\n";
        }

        static bool EnterOp(DemoSlave slave)
        {
            slave.Scan();
            if (!slave.Found) return false;
            if (!slave.Move(SlaveState.PRE_OP)) return false;
            if (!slave.Move(SlaveState.SAFE_OP)) return false;
            return slave.Move(SlaveState.OP);
        }
    }
}
