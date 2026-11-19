use crate::bus::{Adapter, Bridge, MappingSize, Slave, Status, WireTiming};
use std::{
    ffi::{c_char, c_void, CStr, CString, OsString},
    os::windows::ffi::{OsStrExt, OsStringExt},
    path::{Path, PathBuf},
};

const SYSTEM32: u32 = 0x0000_0800;
const DLL_LOAD_DIR: u32 = 0x0000_0100;

type Version = unsafe extern "C" fn() -> i32;
type AdapterFn = unsafe extern "C" fn(i32, *mut c_char, *mut c_char, i32) -> i32;
type Open = unsafe extern "C" fn(*const c_char, *mut c_char, i32) -> i32;
type Info = unsafe extern "C" fn(*mut u32, *mut u32, *mut u32, *mut i32) -> i32;
type ReadStatus = unsafe extern "C" fn(*mut i32, *mut i32) -> i32;
type Close = unsafe extern "C" fn();
type SetState = unsafe extern "C" fn(i32) -> i32;
type Map = unsafe extern "C" fn(i32, i32, *mut i32, *mut i32, *mut i32) -> i32;
type Cycle = unsafe extern "C" fn(*const u8, i32, *mut u8, i32) -> i32;
type TimedCycle = unsafe extern "C" fn(*const u8, i32, *mut u8, i32, *mut i64, *mut i64) -> i32;
type Sdo = unsafe extern "C" fn(i32, u16, u8, *mut u8, *mut i32, *mut u32) -> i32;
type Dc = unsafe extern "C" fn(u32, u32, i32) -> i32;

#[link(name = "kernel32")]
extern "system" {
    fn GetSystemDirectoryW(buffer: *mut u16, size: u32) -> u32;
    fn LoadLibraryExW(path: *const u16, file: *mut c_void, flags: u32) -> *mut c_void;
    fn GetProcAddress(module: *mut c_void, name: *const c_char) -> *mut c_void;
    fn FreeLibrary(module: *mut c_void) -> i32;
    fn QueryPerformanceFrequency(value: *mut i64) -> i32;
}

struct Library(*mut c_void);

impl Library {
    fn load(path: &Path) -> Result<Self, String> {
        let wide: Vec<_> = path.as_os_str().encode_wide().chain(Some(0)).collect();
        let handle = unsafe { LoadLibraryExW(wide.as_ptr(), std::ptr::null_mut(), DLL_LOAD_DIR | SYSTEM32) };
        if handle.is_null() {
            return Err(format!("无法加载 {}：{}", path.display(), std::io::Error::last_os_error()));
        }
        Ok(Self(handle))
    }

    fn symbol(&self, name: &CStr) -> Result<*mut c_void, String> {
        let pointer = unsafe { GetProcAddress(self.0, name.as_ptr()) };
        if pointer.is_null() {
            return Err(format!("SOEM 桥接缺少入口 {}。", name.to_string_lossy()));
        }
        Ok(pointer)
    }
}

impl Drop for Library {
    fn drop(&mut self) {
        unsafe { FreeLibrary(self.0) };
    }
}

fn npcap_directory() -> Result<PathBuf, String> {
    let mut buffer = [0u16; 512];
    let length = unsafe { GetSystemDirectoryW(buffer.as_mut_ptr(), buffer.len() as u32) } as usize;
    if length == 0 || length >= buffer.len() {
        return Err("无法定位 Windows 系统目录。".into());
    }
    Ok(PathBuf::from(OsString::from_wide(&buffer[..length])).join("Npcap"))
}

pub struct Soem {
    adapter: AdapterFn,
    open: Open,
    info: Info,
    status: ReadStatus,
    close: Close,
    set_state: SetState,
    map: Map,
    cycle: Cycle,
    timed_cycle: Option<TimedCycle>,
    frequency: i64,
    sdo: Sdo,
    dc: Dc,
    _bridge: Library,
    _npcap: Library,
}

impl Soem {
    pub fn load() -> Result<Self, String> {
        if !cfg!(target_arch = "x86_64") {
            return Err("真机诊断需要 Windows x64。".into());
        }
        let executable = std::env::current_exe().map_err(|error| error.to_string())?;
        let path = executable.parent().ok_or("找不到程序目录。")?.join("EtherCatBench.Soem.dll");
        if !path.is_file() {
            return Err("请将 EtherCatBench.Soem.dll（ABI v3）放在程序同一目录。".into());
        }
        let npcap = Library::load(&npcap_directory()?.join("wpcap.dll"))?;
        let bridge = Library::load(&path)?;
        // The bridge header specifies the C calling convention and argument layouts.
        unsafe {
            let version: Version = std::mem::transmute(bridge.symbol(c"tb_version")?);
            if version() != 3 {
                return Err("SOEM 桥接版本不匹配，需要 ABI v3。".into());
            }
            let mut frequency = 0;
            QueryPerformanceFrequency(&mut frequency);
            Ok(Self {
                adapter: std::mem::transmute(bridge.symbol(c"tb_adapter")?),
                open: std::mem::transmute(bridge.symbol(c"tb_open")?),
                info: std::mem::transmute(bridge.symbol(c"tb_info")?),
                status: std::mem::transmute(bridge.symbol(c"tb_status")?),
                close: std::mem::transmute(bridge.symbol(c"tb_close")?),
                set_state: std::mem::transmute(bridge.symbol(c"tb_set_state")?),
                map: std::mem::transmute(bridge.symbol(c"tb_map")?),
                cycle: std::mem::transmute(bridge.symbol(c"tb_cycle")?),
                timed_cycle: bridge.symbol(c"tb_cycle_timed").ok().map(|p| std::mem::transmute::<*mut c_void, TimedCycle>(p)),
                frequency,
                sdo: std::mem::transmute(bridge.symbol(c"tb_sdo")?),
                dc: std::mem::transmute(bridge.symbol(c"tb_dc")?),
                _bridge: bridge,
                _npcap: npcap,
            })
        }
    }
}

fn text(buffer: &[u8]) -> String {
    let length = buffer.iter().position(|&byte| byte == 0).unwrap_or(buffer.len());
    String::from_utf8_lossy(&buffer[..length]).into_owned()
}

impl Bridge for Soem {
    fn adapters(&mut self) -> Result<Vec<Adapter>, String> {
        let mut adapters = Vec::new();
        for index in 0..512 {
            let mut name = [0u8; 512];
            let mut description = [0u8; 512];
            let found = unsafe { (self.adapter)(index, name.as_mut_ptr().cast(), description.as_mut_ptr().cast(), 512) };
            if found != 1 {
                break;
            }
            let id = text(&name);
            if id.starts_with(r"\Device\NPF_{") {
                adapters.push(Adapter { id, description: text(&description) });
            }
        }
        Ok(adapters)
    }

    fn open(&mut self, id: &str) -> Result<(), String> {
        let id = CString::new(id).map_err(|_| "无效的 Npcap 网口名称。")?;
        let mut detail = [0u8; 512];
        let result = unsafe { (self.open)(id.as_ptr(), detail.as_mut_ptr().cast(), 512) };
        let message = match result {
            1 => return Ok(()),
            -1 => "打开网口失败",
            -2 => "SOEM 初始化失败",
            -3 => "Npcap 发送失败",
            -4 => "Npcap 非阻塞接收设置失败",
            -5 => "扫站超时，Windows 已请求取消 I/O",
            -6 => "无法启动扫站超时保护",
            0 => "未收到有效的 EtherCAT 从站回复",
            _ => return Err(format!("当前支持单从站，扫描结果为 {result}。")),
        };
        let detail = text(&detail);
        Err(if detail.is_empty() { message.into() } else { format!("{message} · {detail}") })
    }

    fn identity(&mut self) -> Result<Slave, String> {
        let mut slave = Slave { vendor: 0, product: 0, revision: 0, dc: false };
        let mut dc = 0;
        let result = unsafe { (self.info)(&mut slave.vendor, &mut slave.product, &mut slave.revision, &mut dc) };
        if result != 1 {
            return Err("读取从站身份失败。".into());
        }
        slave.dc = dc != 0;
        Ok(slave)
    }

    fn status(&mut self) -> Result<Status, String> {
        let mut status = Status { state: 0, al: 0 };
        if unsafe { (self.status)(&mut status.state, &mut status.al) } != 1 {
            return Err("未收到 AL 状态回复。".into());
        }
        Ok(status)
    }

    fn close(&mut self) {
        unsafe { (self.close)() };
    }

    fn set_state(&mut self, state: i32) -> Result<(), String> {
        if unsafe { (self.set_state)(state) } == 1 { Ok(()) } else { Err("从站未确认 AL 状态请求。".into()) }
    }

    fn mapping(&mut self, online: Option<(usize, usize)>) -> Result<MappingSize, String> {
        let (out, input) = online.map_or((-1, -1), |(out, input)| (out as i32, input as i32));
        let (mut rx_bits, mut tx_bits, mut expected) = (0, 0, 0);
        if unsafe { (self.map)(out, input, &mut rx_bits, &mut tx_bits, &mut expected) } != 1 ||
            !(0..=32768).contains(&rx_bits) || !(0..=32768).contains(&tx_bits) || expected <= 0 {
            return Err("SOEM PDO 映射失败或返回了无效长度。".into());
        }
        Ok(MappingSize { rx_bits: rx_bits as usize, tx_bits: tx_bits as usize, expected })
    }

    fn exchange(&mut self, output: &[u8], input: &mut [u8], measure: bool) -> (i32, WireTiming) {
        let args = (output.as_ptr(), output.len() as i32, input.as_mut_ptr(), input.len() as i32);
        if let Some(transfer) = self.timed_cycle.filter(|_| measure && self.frequency > 0) {
            let (mut send, mut receive) = (-1, -1);
            let wkc = unsafe { transfer(args.0, args.1, args.2, args.3, &mut send, &mut receive) };
            let micros = |ticks| (ticks >= 0).then_some(ticks as f64 * 1e6 / self.frequency as f64);
            (wkc, WireTiming { send: micros(send), receive: micros(receive) })
        } else { (unsafe { (self.cycle)(args.0, args.1, args.2, args.3) }, WireTiming::default()) }
    }

    fn transfer(&mut self, index: u16, sub: u8, value: Option<Vec<u8>>) -> Result<Vec<u8>, String> {
        let write = i32::from(value.is_some());
        let mut data = value.unwrap_or_else(|| vec![0; 4096]);
        if data.is_empty() || data.len() > 4096 { return Err("SDO 长度须为 1–4096 字节。".into()); }
        let mut size = data.len() as i32;
        let mut abort = 0;
        if unsafe { (self.sdo)(write, index, sub, data.as_mut_ptr(), &mut size, &mut abort) } != 1 {
            return Err(if abort != 0 { format!("SDO Abort 0x{abort:08X}") } else { "SDO 无响应。".into() });
        }
        if size < 0 || size as usize > data.len() { return Err("从站返回异常 SDO 长度。".into()); }
        data.truncate(size as usize);
        Ok(data)
    }

    fn configure_dc(&mut self, assign: u32, ns: u32, shift: i32) -> Result<(), String> {
        if unsafe { (self.dc)(assign, ns, shift) } == 1 { Ok(()) }
        else { Err("DC 下发或寄存器回读失败。".into()) }
    }
}
