use std::time::{Duration, Instant};

pub const LEAD_US: u64 = 500;

#[cfg(windows)]
mod win {
    use super::*;
    use std::{ffi::c_void, ptr};

    #[repr(C)]
    #[derive(Default)]
    struct FileTime { low: u32, high: u32 }

    #[link(name = "kernel32")]
    extern "system" {
        fn CreateWaitableTimerExW(a: *mut c_void, name: *const u16, flags: u32, access: u32) -> *mut c_void;
        fn SetWaitableTimer(timer: *mut c_void, due: *const i64, period: i32, cb: *mut c_void, arg: *mut c_void, resume: i32) -> i32;
        fn WaitForSingleObject(handle: *mut c_void, ms: u32) -> u32;
        fn CloseHandle(handle: *mut c_void) -> i32;
        fn GetCurrentProcess() -> *mut c_void;
        fn GetProcessTimes(handle: *mut c_void, created: *mut FileTime, exited: *mut FileTime, kernel: *mut FileTime, user: *mut FileTime) -> i32;
        fn GetActiveProcessorCount(group: u16) -> u32;
    }

    pub struct Timer(*mut c_void);

    impl Timer {
        pub fn new() -> Self {
            Self(unsafe { CreateWaitableTimerExW(ptr::null_mut(), ptr::null(), 2, 0x0010_0002) })
        }

        pub fn wait(&self, deadline: Instant, early: bool) -> Option<f64> {
            let lead = Duration::from_micros(if early { LEAD_US } else { 0 });
            let due = deadline.saturating_duration_since(Instant::now()).saturating_sub(lead);
            let wake = if !self.0.is_null() && !due.is_zero() {
                let ticks = -((due.as_nanos() / 100).max(1) as i64);
                if unsafe { SetWaitableTimer(self.0, &ticks, 0, ptr::null_mut(), ptr::null_mut(), 0) } != 0 {
                    unsafe { WaitForSingleObject(self.0, 4) };
                    let now = Instant::now();
                    Some(if now >= deadline { us(now - deadline) } else { -us(deadline - now) })
                } else { None }
            } else { None };
            while Instant::now() < deadline { std::hint::spin_loop(); }
            wake
        }
    }

    impl Drop for Timer {
        fn drop(&mut self) { if !self.0.is_null() { unsafe { CloseHandle(self.0) }; } }
    }

    pub fn cpu() -> Option<f64> {
        let (mut created, mut exited, mut kernel, mut user) = (FileTime::default(), FileTime::default(), FileTime::default(), FileTime::default());
        let ok = unsafe { GetProcessTimes(GetCurrentProcess(), &mut created, &mut exited, &mut kernel, &mut user) };
        let count = unsafe { GetActiveProcessorCount(0xffff) };
        let ticks = |t: FileTime| (u64::from(t.high) << 32) | u64::from(t.low);
        (ok != 0 && count > 0).then(|| (ticks(kernel) + ticks(user)) as f64 / (1e7 * f64::from(count)))
    }
}

#[cfg(windows)]
pub use win::{cpu, Timer};

#[cfg(not(windows))]
pub struct Timer;
#[cfg(not(windows))]
impl Timer {
    pub fn new() -> Self { Self }
    pub fn wait(&self, deadline: Instant, _: bool) -> Option<f64> {
        std::thread::sleep(deadline.saturating_duration_since(Instant::now()));
        None
    }
}
#[cfg(not(windows))]
pub fn cpu() -> Option<f64> { None }

pub fn us(duration: Duration) -> f64 { duration.as_secs_f64() * 1e6 }

pub fn advance(deadline: Instant, period: Duration, finished: Instant) -> (Instant, u64) {
    let passed = (finished.saturating_duration_since(deadline).as_nanos() / period.as_nanos()) as u32;
    (deadline + period * (passed + 1), u64::from(passed))
}

pub struct Point {
    pub now: Instant,
    pub interval: Option<f64>,
    pub exchange: f64,
    pub wkc: i32,
    pub expected: i32,
    pub skipped: u64,
    pub state: i32,
    pub wait: Option<f64>,
    pub late: Option<f64>,
    pub al: f64,
    pub send: Option<f64>,
    pub receive: Option<f64>,
    pub code: Option<i32>,
    pub al_age: Option<f64>,
}

pub struct Trace {
    pub rows: Vec<Point>,
    pub started: Instant,
    pub stopped: Option<Instant>,
    start_cpu: Option<f64>,
    cpu_percent: Option<f64>,
    lead: u64,
    long: bool,
}

impl Trace {
    pub fn new(long: bool, lead: u64) -> Self {
        let rows = Vec::with_capacity(if long { 660_000 } else { 120_000 });
        Self { rows, started: Instant::now(), stopped: None, start_cpu: cpu(), cpu_percent: None, lead, long }
    }

    pub fn check(&mut self, now: Instant) {
        if self.stopped.is_none() && (self.rows.len() == self.rows.capacity() ||
            (self.long && now.duration_since(self.started) >= Duration::from_secs(600))) {
            self.stop(now);
        }
    }

    pub fn stop(&mut self, now: Instant) {
        if self.stopped.is_some() { return; }
        self.stopped = Some(now);
        let elapsed = self.seconds(now);
        self.cpu_percent = self.start_cpu.zip(cpu()).filter(|_| elapsed > 0.0)
            .map(|(initial, final_cpu)| (final_cpu - initial) / elapsed * 100.0);
    }

    pub fn seconds(&self, now: Instant) -> f64 { self.stopped.unwrap_or(now).duration_since(self.started).as_secs_f64() }

    pub fn save(&self, path: &std::path::Path) -> std::io::Result<()> {
        use std::io::Write;
        let mut writer = std::io::BufWriter::new(std::fs::File::create(path)?);
        writeln!(writer, "elapsed_us,interval_us,exchange_us,wkc,expected_wkc,skipped_slots,al_state,timer_return_offset_us,dispatch_late_us,al_before_exchange_us,send_us,receive_us,scheduler_spin_lead_us,capture_wall_seconds,process_cpu_percent,al_status_code,al_status_age_us")?;
        let opt = |value: Option<f64>| value.map(|n| format!("{n:.3}")).unwrap_or_default();
        if let Some(first) = self.rows.first() {
            for (index, row) in self.rows.iter().enumerate() {
                let start = index == 0;
                writeln!(writer, "{:.3},{},{:.3},{},{},{},{},{},{},{:.3},{},{},{},{},{},{},{}",
                    us(row.now - first.now), opt(if start { None } else { row.interval }), row.exchange,
                    row.wkc, row.expected, row.skipped, row.state, opt(row.wait), opt(row.late), row.al,
                    opt(row.send), opt(row.receive), self.lead,
                    opt(start.then(|| self.seconds(row.now))), opt(if start { self.cpu_percent } else { None }),
                    row.code.map(|code| format!("0x{code:04X}")).unwrap_or_default(), opt(row.al_age))?;
            }
        }
        writer.flush()
    }
}
