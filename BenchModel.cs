using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace EtherCatBench
{
    enum SlaveState { INIT, PRE_OP, SAFE_OP, OP }

    sealed class PdoField
    {
        public string Direction, Name, Type;
        public uint Pdo, Index, Sub, Bits;
        public bool Fixed, Required;

        public string Key
        {
            get { return string.Format("{0}/{1:X4}/{2:X4}/{3:X2}/{4}", Direction, Pdo, Index, Sub, Bits); }
        }
    }

    sealed partial class EsiDevice
    {
        public uint Vendor, Product, Revision;
        public string Name;
        public readonly List<PdoField> Fields = new List<PdoField>();

        static IEnumerable<XElement> Children(XElement parent, string name)
        {
            return parent.Elements().Where(e => e.Name.LocalName == name);
        }

        static XElement Child(XElement parent, string name)
        {
            return Children(parent, name).FirstOrDefault();
        }

        static string Value(XElement parent, string name)
        {
            return (string)Child(parent, name) ?? "";
        }

        public static uint Number(string value)
        {
            value = (value ?? "").Trim();
            if (value.StartsWith("#x", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.Parse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return uint.Parse(value, CultureInfo.InvariantCulture);
        }

        static XmlReaderSettings CreateSettings()
        {
            return new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 16000000
            };
        }

        public static List<EsiDevice> Load(string path)
        {
            using (var reader = XmlReader.Create(path, CreateSettings()))
                return Load(reader);
        }

        public static List<EsiDevice> LoadXml(string xml)
        {
            using (var text = new StringReader(xml ?? ""))
            using (var reader = XmlReader.Create(text, CreateSettings()))
                return Load(reader);
        }

        static List<EsiDevice> Load(XmlReader reader)
        {
            var document = XDocument.Load(reader);
            var root = document.Root;
            if (root == null || root.Name.LocalName != "EtherCATInfo")
                throw new FormatException("根节点应为 EtherCATInfo。");

            var vendor = Child(root, "Vendor");
            var descriptions = Child(root, "Descriptions");
            var devices = descriptions == null ? null : Child(descriptions, "Devices");
            if (vendor == null || devices == null)
                throw new FormatException("找不到 Vendor 或 Descriptions/Devices。");

            var result = new List<EsiDevice>();
            foreach (var device in Children(devices, "Device"))
            {
                var type = Child(device, "Type");
                if (type == null)
                    throw new FormatException("设备缺少 Type。");

                var item = new EsiDevice {
                    Vendor = Number(Value(vendor, "Id")),
                    Product = Number((string)type.Attribute("ProductCode")),
                    Revision = Number((string)type.Attribute("RevisionNo") ?? "0"),
                    Name = Value(device, "Name")
                };
                if (item.Name.Length == 0) item.Name = type.Value;
                foreach (var pdo in device.Elements().Where(p => p.Name.LocalName == "RxPdo" || p.Name.LocalName == "TxPdo"))
                {
                    uint pdoIndex = Number(Value(pdo, "Index"));
                    bool isFixed = EnabledAttribute(pdo, "Fixed");
                    bool required = EnabledAttribute(pdo, "Mandatory") ||
                        (isFixed && !string.IsNullOrEmpty((string)pdo.Attribute("Sm")));
                    foreach (var entry in Children(pdo, "Entry"))
                    {
                        item.Fields.Add(new PdoField {
                            Direction = pdo.Name.LocalName == "RxPdo" ? "RxPDO" : "TxPDO",
                            Pdo = pdoIndex,
                            Index = Number(Value(entry, "Index")),
                            Sub = Number(Child(entry, "SubIndex") == null ? "0" : Value(entry, "SubIndex")),
                            Bits = Number(Value(entry, "BitLen")),
                            Name = Value(entry, "Name"),
                            Type = Value(entry, "DataType"),
                            Fixed = isFixed,
                            Required = required
                        });
                    }
                }
                item.ReadDetails(device);
                result.Add(item);
            }
            if (result.Count == 0) throw new FormatException("ESI 中没有设备。");
            return result;
        }

        static bool EnabledAttribute(XElement element, string name)
        {
            string value = (string)element.Attribute(name);
            return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString()
        {
            return string.Format("{0} ({1:X8}/{2:X8})", Name, Product, Revision);
        }

        public bool MatchesIdentity
        {
            get { return Vendor == DemoSlave.Vendor && Product == DemoSlave.Product && Revision == DemoSlave.Revision; }
        }

        public string MappingCheck()
        {
            if (!MatchesIdentity) return "身份不匹配";
            var expected = DemoSlave.Mapping.Select(f => f.Key);
            var declared = Fields.Select(f => f.Key);
            var differences = new List<string>();
            var missing = expected.Except(declared).ToArray();
            var extra = declared.Except(expected).ToArray();
            if (missing.Length > 0) differences.Add("缺少 " + string.Join(", ", missing));
            if (extra.Length > 0) differences.Add("额外 " + string.Join(", ", extra));
            if (Fields.Any(f => !f.Fixed)) differences.Add("存在非固定 PDO");
            if (Fields.Any(f => DemoSlave.Mapping.Any(m => m.Key == f.Key &&
                !string.Equals(m.Type, f.Type, StringComparison.OrdinalIgnoreCase))))
                differences.Add("字段数据类型不一致");
            if (differences.Count > 0) return string.Join("；", differences.ToArray());
            foreach (var direction in new[] { "RxPDO", "TxPDO" })
            {
                var wanted = DemoSlave.Mapping.Where(f => f.Direction == direction).Select(f => f.Key);
                var actual = Fields.Where(f => f.Direction == direction).Select(f => f.Key);
                if (!wanted.SequenceEqual(actual)) return direction + " 条目顺序或数量不一致";
            }
            return "身份及 PDO 映射匹配";
        }
    }

    sealed partial class DemoSlave
    {
        public const uint Vendor = 0x0000FFFF, Product = 0x92520001, Revision = 0x00010000;
        public static readonly PdoField[] Mapping = {
            new PdoField { Direction = "RxPDO", Pdo = 0x1600, Index = 0x7000, Sub = 1, Bits = 16, Name = "测试序号", Type = "UINT", Fixed = true },
            new PdoField { Direction = "TxPDO", Pdo = 0x1A00, Index = 0x6000, Sub = 1, Bits = 16, Name = "已处理序号", Type = "UINT", Fixed = true }
        };

        public bool Found { get; private set; }
        public bool Link { get; private set; }
        public bool Enabled { get; private set; }
        public bool OutputLocked { get; private set; }
        public SlaveState State { get; private set; }
        public ushort Output { get; private set; }
        public ushort Feedback { get; private set; }
        public uint Counter { get; private set; }
        public uint BadWkc { get; private set; }
        public int Actual { get; private set; }
        public int Expected { get { return cycling ? 3 : 0; } }

        bool cycling;
        ushort? pending;
        ushort parameter = 100;

        public DemoSlave() { Link = true; OutputLocked = true; }

        public void Scan() { Found = Link; }

        public bool Move(SlaveState target)
        {
            if (!Found || !Link || Math.Abs((int)target - (int)State) > 1) return false;
            State = target;
            cycling = State == SlaveState.OP;
            if (!cycling) ResetOutputs();
            return true;
        }

        public void ToggleLink()
        {
            Link = !Link;
            Sync0Ns = 0;
            if (!Link)
            {
                ResetOutputs();
                return;
            }
            Found = false;
            State = SlaveState.INIT;
            cycling = false;
            Actual = 0;
        }

        void ResetOutputs()
        {
            Enabled = false;
            OutputLocked = true;
            LastOutput = LastInput = Output = Feedback = 0;
            Actual = 0;
            pending = null;
        }

        public bool SetEnabled(bool enable)
        {
            if (!enable)
            {
                Enabled = false;
                Output = 0;
                pending = null;
                return true;
            }
            Enabled = Link && Found && State == SlaveState.OP;
            return Enabled;
        }

        public void SetOutputLock(bool locked)
        {
            OutputLocked = locked;
            if (locked)
            {
                Output = 0;
                Feedback = 0;
                pending = (ushort)0;
            }
        }

        public bool Send(ushort value)
        {
            if (OutputLocked || !Enabled || !Link || State != SlaveState.OP) return false;
            Output = value;
            return true;
        }

        public void Tick()
        {
            if (!cycling) return;
            if (OutputLocked)
            {
                Output = 0;
                pending = (ushort)0;
            }
            Actual = Link ? Expected : 0;
            Counter++;
            if (!Link) { BadWkc++; return; }
            if (pending.HasValue) Feedback = pending.Value;
            LastOutput = Output;
            LastInput = Feedback;
            pending = Output;
        }

        public string Sdo(uint index, uint sub, ushort? write)
        {
            if (!Link || !Found || State == SlaveState.INIT) return "邮箱不可用，需连接并进入 PRE-OP 或以上状态。";
            if (index == 0x1018)
            {
                uint[] values = { 4, Vendor, Product, Revision, 1 };
                if (sub >= values.Length) return "Abort 0x06090011 · 子索引不存在";
                if (write.HasValue) return "Abort 0x06010002 · 对象只读";
                return sub == 0 ? "读取成功 · " + values[sub] : string.Format("读取成功 · 0x{0:X8}", values[sub]);
            }
            if (index == 0x2000)
            {
                if (sub != 0) return "Abort 0x06090011 · 子索引不存在";
                if (write.HasValue) parameter = write.Value;
                return string.Format("{0}成功 · {1} (0x{1:X4})", write.HasValue ? "写入" : "读取", parameter);
            }
            return "Abort 0x06020000 · 对象不存在";
        }
    }
}
