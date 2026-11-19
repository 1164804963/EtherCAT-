use roxmltree::{Document, Node};
use serde::Serialize;
use std::collections::{HashMap, HashSet};

pub type Result<T> = std::result::Result<T, String>;

#[derive(Clone, Copy, PartialEq, Eq, Serialize)]
pub struct Identity {
    pub vendor: u32,
    pub product: u32,
    pub revision: u32,
}

#[derive(Clone, Serialize)]
pub struct Field {
    pub pdo: u16,
    pub index: u16,
    pub sub: u8,
    pub bits: u16,
    pub offset: usize,
    pub name: String,
    pub kind: String,
    #[serde(skip)]
    pub required: bool,
}

impl Field {
    pub fn key(&self) -> (u16, u16, u8, u16) {
        (self.pdo, self.index, self.sub, self.bits)
    }
}

#[derive(Clone, Serialize)]
pub struct Entry {
    pub index: u16,
    pub sub: u8,
    pub bits: u16,
    pub name: String,
    pub kind: String,
    pub access: String,
    pub read_states: i32,
    pub write_states: i32,
}

#[derive(Clone, Serialize)]
pub struct DcMode {
    pub name: String,
    pub assign: u32,
    pub cycle_ns: u32,
    pub minimum_ns: u32,
    pub shift_ns: i32,
    pub sync1: bool,
}

#[derive(Clone, Serialize)]
pub struct Device {
    pub identity: Identity,
    pub name: String,
    pub rx: Vec<Field>,
    pub tx: Vec<Field>,
    pub entries: Vec<Entry>,
    pub dc: Vec<DcMode>,
}

fn children<'a, 'i>(node: Node<'a, 'i>, name: &'static str) -> impl Iterator<Item = Node<'a, 'i>> {
    node.children().filter(move |child| child.tag_name().name() == name)
}

fn child<'a, 'i>(node: Node<'a, 'i>, name: &'static str) -> Option<Node<'a, 'i>> {
    children(node, name).next()
}

fn value<'a, 'i>(node: Node<'a, 'i>, name: &'static str) -> &'a str {
    child(node, name).and_then(|item| item.text()).unwrap_or_default().trim()
}

fn number<T: TryFrom<u32>>(text: &str) -> Result<T> {
    let text = text.trim();
    let raw = if text.starts_with("#x") || text.starts_with("#X") || text.starts_with("0x") || text.starts_with("0X") {
        u32::from_str_radix(&text[2..], 16)
    } else { text.parse() };
    raw.ok().and_then(|n| T::try_from(n).ok()).ok_or_else(|| format!("无效 ESI 数值：{text}"))
}

fn optional<T: TryFrom<u32> + Default>(text: &str) -> Result<T> {
    if text.is_empty() { Ok(T::default()) } else { number(text) }
}

fn parse_fields(device: Node<'_, '_>, tag: &'static str) -> Result<Vec<Field>> {
    let mut fields = Vec::new();
    let mut offset = 0;
    let enabled = |node: Node<'_, '_>, attribute: &str| node.attribute(attribute)
        .is_some_and(|v| v == "1" || v.eq_ignore_ascii_case("true"));
    for pdo in children(device, tag) {
        let index = number(value(pdo, "Index"))?;
        let required = enabled(pdo, "Mandatory") || (enabled(pdo, "Fixed") && pdo.attribute("Sm").is_some());
        for entry in children(pdo, "Entry") {
            let bits = number(value(entry, "BitLen"))?;
            if bits == 0 { return Err("ESI 的 PDO 条目位宽为零。".into()); }
            fields.push(Field { pdo: index, index: number(value(entry, "Index"))?,
                sub: optional(value(entry, "SubIndex"))?, bits, offset, required,
                name: value(entry, "Name").into(), kind: value(entry, "DataType").to_ascii_uppercase() });
            offset += usize::from(bits);
        }
    }
    Ok(fields)
}

fn access_states(schedule: Option<&str>) -> i32 {
    let Some(schedule) = schedule else { return 14 };
    let mut mask = 0;
    for name in schedule.to_ascii_uppercase().split('_') {
        mask |= match name { "PREOP" => 2, "SAFEOP" => 4, "OP" => 8, _ => return 0 };
    }
    mask
}

fn parse_entries(device: Node<'_, '_>) -> Result<Vec<Entry>> {
    let mut entries = Vec::new();
    let mut seen = HashSet::new();
    for profile in children(device, "Profile") {
        let Some(dict) = child(profile, "Dictionary").or_else(|| child(profile, "ObjectDictionary")) else { continue };
        let Some(objects) = child(dict, "Objects") else { continue };
        let types: HashMap<_, _> = child(dict, "DataTypes").into_iter().flat_map(|node| children(node, "DataType"))
            .map(|record| (value(record, "Name"), record)).collect();
        for object in children(objects, "Object") {
            let index = number(value(object, "Index"))?;
            let mut items: Vec<_> = children(object, "SubItem").collect();
            if items.is_empty() {
                if let Some(record) = types.get(value(object, "Type")) {
                    items.extend(children(*record, "SubItem"));
                }
            }
            if items.is_empty() { items.push(object); }
            for item in items {
                let sub = if item == object { 0 } else {
                    let Some(sub) = child(item, "SubIdx").or_else(|| child(item, "SubIndex")) else { continue };
                    number(sub.text().unwrap_or_default())?
                };
                if !seen.insert((index, sub)) { continue }
                let permission = child(item, "Flags").and_then(|flags| child(flags, "Access"));
                let access = permission.and_then(|p| p.text()).unwrap_or_default().trim().to_ascii_lowercase();
                let read_states = if access == "rw" || access == "ro" {
                    access_states(permission.and_then(|p| p.attribute("ReadRestrictions")))
                } else { 0 };
                let write_states = if access == "rw" || access == "wo" {
                    access_states(permission.and_then(|p| p.attribute("WriteRestrictions")))
                } else { 0 };
                entries.push(Entry { index, sub, bits: optional(value(item, "BitSize"))?,
                    name: value(item, "Name").into(), kind: value(item, "Type").to_ascii_uppercase(),
                    access, read_states, write_states });
            }
        }
    }
    Ok(entries)
}

pub fn parse(xml: &str) -> Result<Vec<Device>> {
    if xml.len() > 16_000_000 { return Err("ESI 超过 16 MB。".into()); }
    let doc = Document::parse(xml.trim_start_matches('\u{feff}')).map_err(|e| format!("ESI XML：{e}"))?;
    let root = doc.root_element();
    if root.tag_name().name() != "EtherCATInfo" { return Err("ESI 根节点应为 EtherCATInfo。".into()); }
    let vendor = number(value(child(root, "Vendor").ok_or("ESI 缺少 Vendor。")?, "Id"))?;
    let devices = child(root, "Descriptions").and_then(|d| child(d, "Devices")).ok_or("ESI 缺少 Devices。")?;
    let mut result = Vec::new();
    for device in children(devices, "Device") {
        let kind = child(device, "Type").ok_or("ESI 设备缺少 Type。")?;
        let identity = Identity { vendor, product: number(kind.attribute("ProductCode").unwrap_or_default())?,
            revision: optional(kind.attribute("RevisionNo").unwrap_or_default())? };
        let name = value(device, "Name");
        let mut dc = Vec::new();
        for mode in child(device, "Dc").into_iter().flat_map(|dc| children(dc, "OpMode")) {
            let assign: u32 = optional(value(mode, "AssignActivate"))?;
            let shift = value(mode, "ShiftTimeSync0");
            let shift_ns = if shift.starts_with('#') || shift.starts_with("0x") || shift.starts_with("0X") {
                number::<u32>(shift)? as i32
            } else if shift.is_empty() { 0 } else { shift.parse().map_err(|_| "ESI 的 SYNC0 偏移无效。")? };
            dc.push(DcMode { name: value(mode, "Name").into(), assign,
                cycle_ns: optional(value(mode, "CycleTimeSync0"))?, minimum_ns: optional(value(mode, "MinCycleTime"))?,
                shift_ns, sync1: assign & 0x400 != 0 || optional::<u32>(value(mode, "CycleTimeSync1"))? != 0 });
        }
        result.push(Device { identity, name: if name.is_empty() { kind.text().unwrap_or_default().into() } else { name.into() },
            rx: parse_fields(device, "RxPdo")?, tx: parse_fields(device, "TxPdo")?, entries: parse_entries(device)?, dc });
    }
    if result.is_empty() { return Err("ESI 没有可用设备。".into()); }
    Ok(result)
}

// Assignment is read from the slave. The ESI supplies labels, types and required groups.
pub fn verified_fields(actual: &[Field], declared: &[Field]) -> Option<Vec<Field>> {
    let groups: HashSet<_> = actual.iter().map(|field| field.pdo).collect();
    if declared.iter().any(|field| field.required && !groups.contains(&field.pdo)) { return None; }
    for pdo in groups {
        if !actual.iter().filter(|f| f.pdo == pdo).map(Field::key)
            .eq(declared.iter().filter(|f| f.pdo == pdo).map(Field::key)) { return None; }
    }
    Some(actual.iter().map(|field| {
        let source = declared.iter().find(|candidate| candidate.key() == field.key()).expect("verified field");
        Field { name: source.name.clone(), kind: source.kind.clone(), ..field.clone() }
    }).collect())
}

pub fn encode(text: &str, kind: &str, bits: u16) -> Result<Vec<u8>> {
    let text = text.trim();
    if bits == 0 || bits > 32768 { return Err("无效字段位宽。".into()); }
    if kind == "REAL" && bits == 32 {
        return text.parse::<f32>().ok().filter(|n| n.is_finite()).map(|n| n.to_le_bytes().to_vec()).ok_or("请输入有效实数。".into());
    }
    if kind == "LREAL" && bits == 64 {
        return text.parse::<f64>().ok().filter(|n| n.is_finite()).map(|n| n.to_le_bytes().to_vec()).ok_or("请输入有效实数。".into());
    }
    let size = usize::from(bits).div_ceil(8);
    let signed = matches!(kind, "SINT" | "INT" | "DINT" | "LINT" | "INTEGER8" | "INTEGER16" | "INTEGER32" | "INTEGER64");
    if bits <= 64 && (signed || kind.starts_with("UINT") || kind.starts_with("UNSIGNED") ||
        matches!(kind, "USINT" | "UDINT" | "ULINT" | "BYTE" | "WORD" | "DWORD" | "LWORD" | "BOOL" | "BOOLEAN")) {
        let mask = u64::MAX >> (64 - bits);
        let result = if let Some(hex) = text.strip_prefix("0x").or_else(|| text.strip_prefix("0X")) {
            u64::from_str_radix(hex, 16).ok()
        } else if signed {
            let edge = 1i128 << (bits - 1);
            text.parse::<i128>().ok().filter(|n| *n >= -edge && *n < edge).map(|n| n as u64 & mask)
        } else { text.parse::<u64>().ok() };
        return result.filter(|n| *n <= mask).map(|n| n.to_le_bytes()[..size].to_vec()).ok_or("数值超出字段范围。".into());
    }
    let raw = hex_bytes(text)?;
    if raw.len() != size || (bits % 8 != 0 && raw[size - 1] >> (bits % 8) != 0) {
        return Err(format!("请输入 {size} 个低字节在前的十六进制字节，填充位须为零。"));
    }
    Ok(raw)
}

pub fn hex_bytes(text: &str) -> Result<Vec<u8>> {
    text.split_ascii_whitespace().map(|v| {
        if v.len() != 2 { return Err("十六进制字节请用空格分隔。".into()); }
        u8::from_str_radix(v, 16).map_err(|_| "无效十六进制字节。".into())
    }).collect()
}

pub fn insert(image: &mut [u8], field: &Field, value: &[u8]) -> Result<()> {
    let bits = usize::from(field.bits);
    if field.offset + bits > image.len() * 8 || value.len() != bits.div_ceil(8) {
        return Err("写入超出在线 PDO 范围。".into());
    }
    for i in 0..bits {
        let offset = field.offset + i;
        let mask = 1 << (offset % 8);
        let cell = &mut image[offset / 8];
        if value[i / 8] & (1 << (i % 8)) == 0 { *cell &= !mask; } else { *cell |= mask; }
    }
    Ok(())
}
