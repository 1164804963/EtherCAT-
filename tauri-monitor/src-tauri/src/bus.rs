use crate::{esi::{self, Device, Field, Identity}, live::Session, timing};
use serde::{Deserialize, Serialize};
use std::{sync::{mpsc, Arc, Mutex}, thread, time::{Duration, Instant}};
use tokio::sync::oneshot;

type Result<T> = std::result::Result<T, String>;

#[derive(Serialize)]
pub struct Adapter {
    pub id: String,
    pub description: String,
}

#[derive(Clone, Serialize)]
pub struct Slave {
    pub vendor: u32,
    pub product: u32,
    pub revision: u32,
    pub dc: bool,
}

impl Slave {
    pub fn identity(&self) -> Identity { Identity { vendor: self.vendor, product: self.product, revision: self.revision } }
}

#[derive(Clone, Copy, Default, PartialEq, Eq, Serialize)]
pub struct Status {
    pub state: i32,
    pub al: i32,
}

#[derive(Serialize)]
pub struct Connection {
    pub slave: Slave,
    pub status: Status,
}

pub struct MappingSize { pub rx_bits: usize, pub tx_bits: usize, pub expected: i32 }

#[derive(Default)]
pub struct WireTiming { pub send: Option<f64>, pub receive: Option<f64> }

pub trait Bridge {
    fn adapters(&mut self) -> Result<Vec<Adapter>>;
    fn open(&mut self, id: &str) -> Result<()>;
    fn identity(&mut self) -> Result<Slave>;
    fn status(&mut self) -> Result<Status>;
    fn close(&mut self);
    fn set_state(&mut self, state: i32) -> Result<()>;
    fn mapping(&mut self, online: Option<(usize, usize)>) -> Result<MappingSize>;
    fn exchange(&mut self, output: &[u8], input: &mut [u8], measure: bool) -> (i32, WireTiming);
    fn transfer(&mut self, index: u16, sub: u8, value: Option<Vec<u8>>) -> Result<Vec<u8>>;
    fn configure_dc(&mut self, assign: u32, ns: u32, shift: i32) -> Result<()>;
}

#[derive(Deserialize)]
pub struct Axis { pub axis: u8, pub angle: u16, pub torque: u16, pub speed: u16 }

#[derive(Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Action {
    #[serde(skip_deserializing)] Import { devices: Vec<Device> },
    Layout,
    Select { index: usize },
    State { target: i32 },
    Arm { enabled: bool },
    Pdo { offset: usize, value: String },
    Hand { targets: Vec<Axis> },
    Sdo { index: u16, sub: u8, value: Option<String>, #[serde(default)] raw: bool },
    Dc { mode: Option<usize>, hz: f64 },
    Trace { long: bool },
    #[serde(skip_deserializing)] ForgetTrace,
}

#[derive(Serialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Reply {
    Ok,
    Layout { options: Vec<(String, Identity)>, selected: Option<usize>, device: Option<Device>,
        online: Option<(Vec<Field>, Vec<Field>)>, hand: bool },
    Sdo { value: Vec<u8>, message: String, read_back: bool },
}

#[derive(Clone, Default, Serialize)]
pub struct Snapshot {
    pub connected: bool, pub online: bool, pub lost: bool, pub status: Status, pub slave: Option<Slave>,
    pub verified: bool, pub identity_matches: bool, pub armed: bool, pub dc_ns: Option<u32>,
    pub cycle_ms: u32, pub early: bool, pub mapped: bool, pub expected: i32, pub wkc: i32,
    pub skipped: u64, pub bad_wkc: u64, pub cycles: u64, pub interval: f64, pub maximum: f64,
    pub output: Option<Vec<u8>>, pub input: Option<Vec<u8>>, pub note: String,
    pub trace_active: bool, pub trace_present: bool, pub trace_seconds: f64,
    pub last_auto_lock: Option<OutputLock>,
}

#[derive(Clone, Serialize)]
pub struct OutputLock { pub sequence: u64, pub cycle: u64, pub reason: String }

#[derive(Default)]
struct Published { snapshot: Snapshot, plot: Vec<(u64, f64)> }

#[derive(Serialize)]
pub struct Telemetry { status: Snapshot, plot: Vec<(u64, f64)> }

#[cfg(windows)]
pub fn load_bridge() -> Result<Box<dyn Bridge>> {
    Ok(Box::new(crate::soem::Soem::load()?))
}

#[cfg(not(windows))]
pub fn load_bridge() -> Result<Box<dyn Bridge>> {
    Err("真机诊断需要 Windows x64。".into())
}

enum Command {
    List(oneshot::Sender<Result<Vec<Adapter>>>),
    Scan(String, u32, bool, oneshot::Sender<Result<Connection>>),
    Status(oneshot::Sender<Result<Status>>),
    Disconnect(oneshot::Sender<Result<()>>),
    Control(Action, oneshot::Sender<Result<Reply>>),
    TakeTrace(oneshot::Sender<Result<Arc<timing::Trace>>>),
}

pub struct Bus { queue: mpsc::SyncSender<Command>, published: Arc<Mutex<Published>> }

impl Bus {
    pub fn start() -> std::io::Result<Self> {
        Self::start_with(|| Session::new(None))
    }

    fn start_with(create: impl FnOnce() -> Session + Send + 'static) -> std::io::Result<Self> {
        let (sender, receiver) = mpsc::sync_channel(1);
        let published = Arc::new(Mutex::new(Published::default()));
        let view = published.clone();
        thread::Builder::new().name("ethercat-soem".into()).spawn(move || {
            let mut session = create();
            let timer = timing::Timer::new();
            let mut refresh = Instant::now();
            loop {
                let mut wake = None;
                let deadline = session.deadline.filter(|_| session.can_exchange());
                let command = if let Some(deadline) = deadline.filter(|_| session.period.as_millis() == 1) {
                    match receiver.try_recv() {
                        Ok(command) => Some(command),
                        Err(mpsc::TryRecvError::Empty) => { wake = timer.wait(deadline, session.early); None }
                        Err(mpsc::TryRecvError::Disconnected) => break,
                    }
                } else {
                    let wait = deadline.map_or(Duration::from_millis(20), |time| time.saturating_duration_since(Instant::now()).min(Duration::from_millis(20)));
                    match receiver.recv_timeout(wait) {
                        Ok(command) => Some(command),
                        Err(mpsc::RecvTimeoutError::Timeout) => None,
                        Err(mpsc::RecvTimeoutError::Disconnected) => break,
                    }
                };
                let handled = command.is_some();
                match command {
                    None => (),
                    Some(Command::List(sender)) => { let _ = sender.send(session.bridge().and_then(|bridge| bridge.adapters())); }
                    Some(Command::Scan(adapter, period, early, sender)) => { let _ = sender.send(session.scan(&adapter, period, early)); }
                    Some(Command::Status(sender)) => { let _ = sender.send(session.fresh()); }
                    Some(Command::Control(action, sender)) => { let _ = sender.send(session.action(action)); }
                    Some(Command::TakeTrace(sender)) => { let _ = sender.send(session.take_trace()); }
                    Some(Command::Disconnect(sender)) => {
                        session.disconnect();
                        let _ = sender.send(Ok(()));
                    }
                }
                let now = Instant::now();
                session.step(now, wake);
                if handled || now >= refresh {
                    if let Ok(mut view) = view.try_lock() {
                        view.snapshot = session.snapshot();
                        view.plot.clear();
                        view.plot.extend(session.plot.iter().copied());
                        refresh = now + Duration::from_millis(50);
                    }
                }
            }
        })?;
        Ok(Self { queue: sender, published })
    }

    async fn request<T>(&self, make: impl FnOnce(oneshot::Sender<Result<T>>) -> Command) -> Result<T> {
        let (sender, receiver) = oneshot::channel();
        self.queue.try_send(make(sender)).map_err(|error| match error {
            mpsc::TrySendError::Full(_) => "通信线程正忙，请等待当前操作完成。".to_string(),
            mpsc::TrySendError::Disconnected(_) => "通信线程已退出。".to_string(),
        })?;
        receiver.await.map_err(|_| "通信线程已退出。".to_string())?
    }
}

#[tauri::command]
pub async fn list_adapters(bus: tauri::State<'_, Bus>) -> Result<Vec<Adapter>> {
    bus.request(Command::List).await
}

#[tauri::command]
pub async fn scan(bus: tauri::State<'_, Bus>, adapter: String, period_ms: Option<u32>, early: Option<bool>) -> Result<Connection> {
    bus.request(move |sender| Command::Scan(adapter, period_ms.unwrap_or(10), early.unwrap_or(true), sender)).await
}

#[tauri::command]
pub async fn read_status(bus: tauri::State<'_, Bus>) -> Result<Status> {
    bus.request(Command::Status).await
}

#[tauri::command]
pub async fn disconnect(bus: tauri::State<'_, Bus>) -> Result<()> {
    bus.request(Command::Disconnect).await
}

pub async fn shutdown(handle: &tauri::AppHandle) {
    use tauri::Manager;
    let bus = handle.state::<Bus>();
    let (sender, receiver) = oneshot::channel();
    let mut command = Command::Disconnect(sender);
    loop {
        match bus.queue.try_send(command) {
            Ok(()) => { let _ = receiver.await; return; }
            Err(mpsc::TrySendError::Full(pending)) => { command = pending; tokio::time::sleep(Duration::from_millis(5)).await; }
            Err(mpsc::TrySendError::Disconnected(_)) => return,
        }
    }
}

#[tauri::command]
pub fn telemetry(bus: tauri::State<'_, Bus>, after: Option<u64>) -> Result<Telemetry> {
    let view = bus.published.lock().map_err(|_| "周期快照不可用。")?;
    let after = after.filter(|&x| x <= view.snapshot.cycles).unwrap_or(0);
    Ok(Telemetry { status: view.snapshot.clone(), plot: view.plot.iter().filter(|(cycle, _)| *cycle > after).copied().collect() })
}

#[tauri::command]
pub async fn control(bus: tauri::State<'_, Bus>, action: Action) -> Result<Reply> {
    bus.request(|sender| Command::Control(action, sender)).await
}

#[tauri::command]
pub async fn import_esi(bus: tauri::State<'_, Bus>, xml: Vec<String>) -> Result<Reply> {
    if xml.is_empty() || xml.iter().map(String::len).sum::<usize>() > 32_000_000 { return Err("请选择总大小不超过 32 MB 的 ESI。".into()); }
    let devices = tauri::async_runtime::spawn_blocking(move || -> Result<_> {
        let mut devices = Vec::new();
        for document in xml { devices.extend(esi::parse(&document)?); }
        Ok(devices)
    }).await.map_err(|error| error.to_string())??;
    control(bus, Action::Import { devices }).await
}

#[cfg(windows)]
#[tauri::command]
pub async fn save_trace(bus: tauri::State<'_, Bus>) -> Result<Option<String>> {
    let Some(file) = rfd::AsyncFileDialog::new().set_file_name("ethercat-timing.csv").add_filter("CSV", &["csv"]).save_file().await else { return Ok(None); };
    let trace = bus.request(Command::TakeTrace).await?;
    let path = file.path().to_path_buf();
    let saved = tauri::async_runtime::spawn_blocking(move || {
        trace.save(&path).map(|_| path.display().to_string()).map_err(|error| error.to_string())
    }).await.map_err(|error| error.to_string())??;
    let _ = control(bus, Action::ForgetTrace).await;
    Ok(Some(saved))
}

#[cfg(not(windows))]
#[tauri::command]
pub async fn save_trace() -> Result<Option<String>> { Err("真机计时导出需要 Windows。".into()) }

#[cfg(test)]
mod tests {
    use super::*;
    use std::{cell::Cell, rc::Rc};

    struct TestBridge {
        opens: Rc<Cell<u32>>,
        closes: Rc<Cell<u32>>,
        status_fails: bool,
    }

    impl Bridge for TestBridge {
        fn adapters(&mut self) -> Result<Vec<Adapter>> {
            Ok(vec![Adapter { id: "explicit-port".into(), description: String::new() }])
        }
        fn open(&mut self, _: &str) -> Result<()> {
            self.opens.set(self.opens.get() + 1);
            Ok(())
        }
        fn identity(&mut self) -> Result<Slave> {
            Ok(Slave { vendor: 1, product: 2, revision: 3, dc: false })
        }
        fn status(&mut self) -> Result<Status> {
            if self.status_fails {
                Err("simulated AL timeout".into())
            } else {
                Ok(Status { state: 2, al: 0 })
            }
        }
        fn close(&mut self) {
            self.closes.set(self.closes.get() + 1);
        }
        fn set_state(&mut self, _: i32) -> Result<()> { Ok(()) }
        fn mapping(&mut self, _: Option<(usize, usize)>) -> Result<MappingSize> { Err("fixture has no PDO".into()) }
        fn exchange(&mut self, _: &[u8], _: &mut [u8], _: bool) -> (i32, WireTiming) { (-1, WireTiming::default()) }
        fn transfer(&mut self, _: u16, _: u8, _: Option<Vec<u8>>) -> Result<Vec<u8>> { Err("fixture has no SDO".into()) }
        fn configure_dc(&mut self, _: u32, _: u32, _: i32) -> Result<()> { Ok(()) }
    }

    #[test]
    fn failed_scan_validates_adapter_and_closes_before_retry() {
        let opens = Rc::new(Cell::new(0));
        let closes = Rc::new(Cell::new(0));
        let bridge = TestBridge { opens: opens.clone(), closes: closes.clone(), status_fails: true };
        let mut session = Session::new(Some(Box::new(bridge)));
        assert!(session.scan("unknown-port", 1, true).is_err());
        assert_eq!(opens.get(), 0);
        for _ in 0..2 {
            assert!(session.scan("explicit-port", 1, true).is_err());
            assert!(!session.open);
        }
        drop(session);
        assert_eq!(opens.get(), 2);
        assert_eq!(closes.get(), 2);
    }

    #[test]
    fn desktop_ipc_lists_scans_reads_and_disconnects() {
        use tauri::ipc::{CallbackFn, InvokeBody, InvokeResponseBody};
        use tauri::test::{get_ipc_response, mock_builder, mock_context, noop_assets, INVOKE_KEY};
        use tauri::webview::InvokeRequest;

        let bus = Bus::start_with(|| Session::new(Some(Box::new(TestBridge {
                opens: Rc::new(Cell::new(0)),
                closes: Rc::new(Cell::new(0)),
                status_fails: false,
            })))).unwrap();
        let app = crate::with_bus(mock_builder(), bus).build(mock_context(noop_assets())).unwrap();
        let window = tauri::WebviewWindowBuilder::new(&app, "main", Default::default()).build().unwrap();
        let invoke = |command: &str, json: &str| {
            get_ipc_response(&window, InvokeRequest {
                cmd: command.into(), callback: CallbackFn(0), error: CallbackFn(1),
                url: "http://tauri.localhost".parse().unwrap(),
                body: InvokeBody::Json(json.parse().unwrap()),
                headers: Default::default(), invoke_key: INVOKE_KEY.into(),
            })
        };
        let json = |command, arguments| match invoke(command, arguments).unwrap() {
            InvokeResponseBody::Json(value) => value,
            _ => panic!("expected JSON"),
        };
        assert!(json("list_adapters", "{}").contains("explicit-port"));
        assert!(invoke("scan", r#"{"adapter":"unlisted-port"}"#).is_err());
        assert!(json("scan", r#"{"adapter":"explicit-port"}"#).contains(r#""status":{"state":2,"al":0}"#));
        assert_eq!(json("read_status", "{}"), r#"{"state":2,"al":0}"#);
        assert_eq!(json("disconnect", "{}"), "null");
        assert!(invoke("read_status", "{}").is_err());
    }
}
