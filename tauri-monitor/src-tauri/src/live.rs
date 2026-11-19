use crate::{bus::{load_bridge, Action, Bridge, Connection, OutputLock, Reply, Slave, Snapshot, Status}, esi::{self, Device, Field, Result}, timing};
use std::{collections::{HashSet, VecDeque}, sync::Arc, time::{Duration, Instant}};

const WKC_FAULT_LIMIT: u8 = 3;

pub struct Session {
    bridge: Option<Box<dyn Bridge>>,
    pub open: bool,
    slave: Option<Slave>,
    status: Status,
    last_al: Option<Instant>,
    last_pdo: Option<Instant>,
    misses: u32,
    lost: bool,
    devices: Vec<Device>,
    selected: Option<usize>,
    mapping: Option<Mapping>,
    verified: bool,
    armed: bool,
    lock_sequence: u64,
    last_auto_lock: Option<OutputLock>,
    dc_ns: Option<u32>,
    pub period: Duration,
    pub early: bool,
    pub deadline: Option<Instant>,
    next_status: Instant,
    last_cycle: Option<Instant>,
    cycles: u64,
    skipped: u64,
    bad_wkc: u64,
    wkc_faults: u8,
    wkc: i32,
    interval: f64,
    max_interval: f64,
    pub plot: VecDeque<(u64, f64)>,
    trace: Option<timing::Trace>,
    saved_trace: Option<Arc<timing::Trace>>,
    trace_al: f64,
    note: String,
}

struct Mapping {
    rx_bits: usize,
    tx_bits: usize,
    expected: i32,
    rx: Vec<Field>,
    tx: Vec<Field>,
    output: Vec<u8>,
    sent: Vec<u8>,
    input: Vec<u8>,
}

impl Session {
    pub fn new(bridge: Option<Box<dyn Bridge>>) -> Self {
        Self { bridge, open: false, slave: None, status: Status { state: 0, al: 0 },
            last_al: None, last_pdo: None, misses: 0, lost: false, devices: Vec::new(), selected: None,
            mapping: None, verified: false, armed: false, lock_sequence: 0, last_auto_lock: None,
            dc_ns: None, period: Duration::from_millis(10),
            early: true, deadline: None, next_status: Instant::now(), last_cycle: None, cycles: 0,
            skipped: 0, bad_wkc: 0, wkc_faults: 0, wkc: 0, interval: 0., max_interval: 0., plot: VecDeque::with_capacity(2000),
            trace: None, saved_trace: None, trace_al: 0., note: String::new() }
    }

    pub fn bridge(&mut self) -> Result<&mut Box<dyn Bridge>> {
        if self.bridge.is_none() { self.bridge = Some(load_bridge()?); }
        Ok(self.bridge.as_mut().expect("bridge initialized"))
    }

    pub fn scan(&mut self, adapter: &str, period: u32, early: bool) -> Result<Connection> {
        if self.open { return Err("请先断开当前从站。".into()); }
        if !(1..=2000).contains(&period) { return Err("主站周期须为 1–2000 ms。".into()); }
        let bridge = self.bridge()?;
        if !bridge.adapters()?.iter().any(|port| port.id == adapter) {
            return Err("该网口已不在 Npcap 列表中，请重新枚举。".into());
        }
        bridge.open(adapter)?;
        self.open = true;
        self.period = Duration::from_millis(period.into());
        self.early = period == 1 && early;
        let result = (|| {
            let slave = self.bridge()?.identity()?;
            let status = self.fresh()?;
            self.slave = Some(slave.clone());
            self.last_auto_lock = None;
            self.select_identity();
            self.note = "已连接单从站。".into();
            Ok(Connection { slave, status })
        })();
        if result.is_err() { self.disconnect(); }
        result
    }

    pub fn fresh(&mut self) -> Result<Status> {
        if !self.open || self.lost { return Err("从站未连接或失联，请断开后重连。".into()); }
        let started = Instant::now();
        let result = self.bridge()?.status();
        let now = Instant::now();
        if self.trace.as_ref().is_some_and(|trace| trace.stopped.is_none()) { self.trace_al += timing::us(now - started); }
        self.next_status = now + Duration::from_millis(100);
        match result {
            Ok(status) => {
                if self.status != status || self.misses != 0 {
                    self.note = format!("从站状态 0x{:02X} · AL 0x{:04X}", status.state, status.al);
                }
                self.status = status;
                self.last_al = Some(now);
                self.misses = 0;
                if status.state != 8 {
                    self.lock_on_fault(|| format!("从站离开正常 OP（状态 0x{:02X}，AL 0x{:04X}）", status.state, status.al));
                }
                Ok(status)
            }
            Err(error) => {
                self.misses += 1;
                self.lock_on_fault(|| format!("AL 状态查询失败（{error}）"));
                let old = |sample: Option<Instant>| sample.is_none_or(|t| now - t >= Duration::from_millis(500));
                self.lost = self.misses >= 5 && old(self.last_al) && old(self.last_pdo);
                self.note = if self.lost { "AL 和 PDO 均已失联，请断开后重连。".into() } else { error.clone() };
                Err(error)
            }
        }
    }

    pub fn lock(&mut self) {
        self.armed = false;
        if let Some(mapping) = &mut self.mapping { mapping.output.fill(0); }
    }

    fn lock_on_fault(&mut self, reason: impl FnOnce() -> String) {
        if self.armed {
            self.lock_sequence += 1;
            self.last_auto_lock = Some(OutputLock { sequence: self.lock_sequence, cycle: self.cycles, reason: reason() });
        }
        self.lock();
    }

    pub fn disconnect(&mut self) {
        if let Some(trace) = &mut self.trace { trace.stop(Instant::now()); }
        self.lock();
        if self.open {
            if self.status.state == 8 && self.can_exchange() { self.exchange(None, None); }
            if let Some(bridge) = &mut self.bridge { bridge.close(); }
        }
        self.open = false;
        self.slave = None;
        self.mapping = None;
        self.verified = false;
        self.status = Status { state: 0, al: 0 };
        self.dc_ns = None;
        self.last_al = None;
        self.last_pdo = None;
        self.misses = 0;
        self.lost = false;
        self.wkc = 0;
        self.bad_wkc = 0;
        self.cycles = 0;
        self.reset_timing();
        self.note.clear();
    }

    fn reset_timing(&mut self) {
        self.last_cycle = None;
        self.deadline = None;
        self.interval = 0.;
        self.max_interval = 0.;
        self.skipped = 0;
        self.wkc_faults = 0;
        self.plot.clear();
    }

    fn selected(&self) -> Option<&Device> { self.selected.and_then(|index| self.devices.get(index)) }
    fn identity_matches(&self) -> bool {
        self.selected().zip(self.slave.as_ref()).is_some_and(|(device, slave)| device.identity == slave.identity())
    }

    fn select_identity(&mut self) {
        if let Some(slave) = &self.slave {
            if let Some(found) = self.devices.iter().position(|device| device.identity == slave.identity()) { self.selected = Some(found); }
        } else if self.selected.is_none() && !self.devices.is_empty() { self.selected = Some(0); }
        self.refresh_verified();
    }

    fn refresh_verified(&mut self) {
        self.verified = self.identity_matches() && self.mapping.as_ref().zip(self.selected()).is_some_and(|(map, device)| {
            fields_size(&map.rx) == map.rx_bits && fields_size(&map.tx) == map.tx_bits &&
                esi::verified_fields(&map.rx, &device.rx).is_some() && esi::verified_fields(&map.tx, &device.tx).is_some()
        });
    }

    fn prepare_mapping(&mut self) -> Result<()> {
        if self.status.state & 15 != 2 { return Err("请先进入 PRE-OP。".into()); }
        let bridge = self.bridge()?;
        let rx = assignment(&mut **bridge, 0x1C12, 0x1600).ok();
        let tx = assignment(&mut **bridge, 0x1C13, 0x1A00).ok();
        let standard = rx.is_some() && tx.is_some() && (|| {
            Ok::<_, String>(count(bridge.transfer(0x1C00, 0, None)?)? == 4 &&
                count(bridge.transfer(0x1C00, 3, None)?)? == 3 && count(bridge.transfer(0x1C00, 4, None)?)? == 4)
        })().unwrap_or(false);
        let hints = if standard { rx.as_ref().zip(tx.as_ref()).map(|(rx, tx)| (fields_size(rx), fields_size(tx))) } else { None };
        let size = bridge.mapping(hints)?;
        let matching = |fields: Option<Vec<Field>>, bits| fields.filter(|entries| fields_size(entries) == bits).unwrap_or_default();
        self.mapping = Some(Mapping { rx_bits: size.rx_bits, tx_bits: size.tx_bits, expected: size.expected,
            rx: matching(rx, size.rx_bits), tx: matching(tx, size.tx_bits),
            output: vec![0; size.rx_bits.div_ceil(8)], sent: vec![0; size.rx_bits.div_ceil(8)], input: vec![0; size.tx_bits.div_ceil(8)] });
        self.refresh_verified();
        self.wkc = 0;
        Ok(())
    }

    fn request_state(&mut self, target: i32) -> Result<()> {
        if !matches!(target, 1 | 2 | 4 | 8) { return Err("无效 EtherCAT 状态。".into()); }
        self.fresh()?;
        if self.status.state == target { return Ok(()); }
        self.lock();
        if self.status.state == 8 && self.can_exchange() { self.exchange(None, None); }
        if target == 4 && self.mapping.is_none() { self.prepare_mapping()?; }
        if target == 8 {
            if self.status.state != 4 || self.mapping.is_none() { return Err("请先进入 SAFE-OP 并映射 PDO。".into()); }
            self.exchange(None, None);
        }
        self.bridge()?.set_state(target)?;
        let until = Instant::now() + Duration::from_secs(2);
        loop {
            if let Ok(status) = self.fresh() {
                if status.state == target { break; }
                if status.state & 16 != 0 { return Err(format!("状态 0x{:02X} · AL 0x{:04X}", status.state, status.al)); }
            }
            if self.lost || Instant::now() >= until { return Err("等待目标 AL 状态超时。".into()); }
            if target >= 4 && self.mapping.is_some() { self.exchange(None, None); }
            std::thread::sleep(self.period.min(Duration::from_millis(10)));
        }
        if target <= 2 {
            self.mapping = None;
            self.verified = false;
            self.wkc = 0;
            self.last_pdo = None;
        }
        if target == 1 { self.dc_ns = None; }
        self.reset_timing();
        Ok(())
    }

    fn authorize_outputs(&mut self) -> Result<()> {
        self.fresh()?;
        if self.status.state != 8 || !self.verified || !self.good_wkc() {
            return Err("输出需要稳定 OP、正常 WKC、ESI 身份和在线 PDO 映射匹配。".into());
        }
        Ok(())
    }

    fn good_wkc(&self) -> bool { self.mapping.as_ref().is_some_and(|map| map.expected > 0 && self.wkc == map.expected) }
    pub fn can_exchange(&self) -> bool { self.open && !self.lost && self.mapping.is_some() && matches!(self.status.state & 15, 4 | 8) }

    fn field_operate(&mut self, offset: usize, input: &str) -> Result<()> {
        self.authorize_outputs()?;
        if !self.armed { return Err("请先解锁输出。".into()); }
        let map = self.mapping.as_ref().unwrap();
        let fields = esi::verified_fields(&map.rx, &self.selected().unwrap().rx).unwrap();
        let field = fields.iter().find(|field| field.offset == offset && field.index != 0).ok_or("无此在线 RxPDO 字段。")?;
        if fields.iter().filter(|other| other.key() == field.key()).count() != 1 { return Err("PDO 字段重复，无法唯一定位。".into()); }
        let value = esi::encode(input, &field.kind, field.bits)?;
        esi::insert(&mut self.mapping.as_mut().unwrap().output, field, &value)
    }

    fn hand_operate(&mut self, targets: Vec<crate::bus::Axis>) -> Result<()> {
        self.authorize_outputs()?;
        if !self.armed || targets.is_empty() || targets.len() > 6 { return Err("请先解锁并选择有效轴。".into()); }
        let map = self.mapping.as_ref().unwrap();
        let rx = esi::verified_fields(&map.rx, &self.selected().unwrap().rx).unwrap();
        let tx = esi::verified_fields(&map.tx, &self.selected().unwrap().tx).unwrap();
        if !hand_layout(&rx, &tx) { return Err("该 ESI 与在线映射未满足现有六轴灵巧手配置。".into()); }
        let mut output = map.output.clone();
        let mut seen = HashSet::new();
        for target in targets {
            if target.axis >= 6 || !seen.insert(target.axis) { return Err("轴号无效或重复。".into()); }
            for (group, value) in [target.angle, target.torque, target.speed].into_iter().enumerate() {
                let sub = target.axis + 1 + (group as u8) * 6;
                let field = unique(&rx, 0x7000, sub, "UINT", 16).unwrap();
                esi::insert(&mut output, field, &value.to_le_bytes())?;
            }
        }
        self.mapping.as_mut().unwrap().output = output;
        Ok(())
    }

    fn sdo(&mut self, index: u16, sub: u8, input: Option<String>, raw: bool) -> Result<Reply> {
        let state = self.fresh()?.state;
        if !matches!(state, 2 | 4 | 8) || self.armed { return Err("SDO 需要可用邮箱及锁定输出。".into()); }
        if input.is_some() && state != 2 && mapping_object(index) {
            return Err("PDO 映射相关对象仅可在 PRE-OP 写入。".into());
        }
        let entry = self.selected().and_then(|dev| dev.entries.iter().find(|e| e.index == index && e.sub == sub));
        if (!raw || entry.is_some()) && !self.identity_matches() { return Err("请先匹配在线设备与 ESI。".into()); }
        if !raw && entry.is_none() { return Err("ESI 中没有此对象。".into()); }
        if let Some(entry) = entry {
            let states = if input.is_some() { entry.write_states } else { entry.read_states };
            if states & state == 0 { return Err("ESI 未允许在当前状态访问该对象。".into()); }
        }
        let may_read_back = entry.is_some_and(|entry| entry.read_states & state != 0);
        let data = input.as_ref().map(|value| {
            if raw { esi::hex_bytes(value) } else { let e = entry.unwrap(); esi::encode(value, &e.kind, e.bits) }
        }).transpose()?;
        let sent = data.clone();
        let result = self.bridge()?.transfer(index, sub, data)?;
        if let Some(sent) = sent {
            if mapping_object(index) {
                self.mapping = None;
                self.verified = false;
            }
            if may_read_back {
                return Ok(match self.bridge()?.transfer(index, sub, None) {
                    Ok(value) => Reply::Sdo { message: if value == sent { "已写入并回读一致" } else { "已写入，回读值不同" }.into(), value, read_back: true },
                    Err(error) => Reply::Sdo { message: format!("已写入，回读失败：{error}"), value: sent, read_back: false },
                });
            }
            return Ok(Reply::Sdo { message: "已写入".into(), value: sent, read_back: false });
        }
        Ok(Reply::Sdo { message: "已读取".into(), value: result, read_back: true })
    }

    fn dc(&mut self, mode: Option<usize>, hz: f64) -> Result<()> {
        self.fresh()?;
        let (assign, ns, shift) = if let Some(index) = mode {
            if !self.identity_matches() || !self.slave.as_ref().is_some_and(|slave| slave.dc) { return Err("DC 需要支持 DC 的从站及匹配的 ESI。".into()); }
            let mode = self.selected().and_then(|device| device.dc.get(index)).ok_or("无此 ESI DC 模式。")?;
            if mode.assign != 0x300 || mode.sync1 || !(1.0..=1_000_000.0).contains(&hz) { return Err("请选择 SYNC0 模式及 1–1,000,000 Hz。".into()); }
            let ns = (1e9 / hz).round() as u32;
            if ns < mode.minimum_ns { return Err("SYNC0 周期小于 ESI 声明的下限。".into()); }
            (mode.assign, ns, mode.shift_ns)
        } else { (0, 0, 0) };
        self.request_state(2)?;
        self.dc_ns = None;
        self.bridge()?.configure_dc(assign, ns, shift)?;
        self.dc_ns = Some(ns);
        Ok(())
    }

    pub fn action(&mut self, action: Action) -> Result<Reply> {
        match action {
            Action::Import { devices } => {
                self.lock();
                for device in devices {
                    if let Some(existing) = self.devices.iter_mut().find(|d| d.identity == device.identity) { *existing = device; }
                    else { self.devices.push(device); }
                }
                self.select_identity();
                return Ok(self.layout());
            }
            Action::Select { index } => {
                if index >= self.devices.len() { return Err("无此 ESI 设备。".into()); }
                self.lock(); self.selected = Some(index); self.refresh_verified();
                return Ok(self.layout());
            }
            Action::Layout => return Ok(self.layout()),
            Action::State { target } => { self.request_state(target)?; return Ok(self.layout()); }
            Action::Arm { enabled } => {
                if enabled { self.authorize_outputs()?; self.armed = true; } else { self.lock(); }
            }
            Action::Pdo { offset, value } => self.field_operate(offset, &value)?,
            Action::Hand { targets } => self.hand_operate(targets)?,
            Action::Sdo { index, sub, value, raw } => return self.sdo(index, sub, value, raw),
            Action::Dc { mode, hz } => { self.dc(mode, hz)?; return Ok(self.layout()); }
            Action::Trace { long } => {
                if !self.can_exchange() { return Err("请先进入 SAFE-OP 或 OP。".into()); }
                if self.trace.is_some() || self.saved_trace.is_some() { return Err("请先导出已有记录。".into()); }
                self.trace = Some(timing::Trace::new(long, if self.early { timing::LEAD_US } else { 0 }));
                self.trace_al = 0.;
            }
            Action::ForgetTrace => { self.saved_trace = None; }
        }
        Ok(Reply::Ok)
    }

    pub fn take_trace(&mut self) -> Result<Arc<timing::Trace>> {
        if let Some(trace) = &self.saved_trace { return Ok(trace.clone()); }
        let mut trace = self.trace.take().ok_or("暂无计时记录。")?;
        trace.stop(Instant::now());
        let trace = Arc::new(trace);
        self.saved_trace = Some(trace.clone());
        Ok(trace)
    }

    fn layout(&self) -> Reply {
        let online = self.mapping.as_ref().map(|map| {
            if self.verified {
                let dev = self.selected().unwrap();
                (esi::verified_fields(&map.rx, &dev.rx).unwrap(), esi::verified_fields(&map.tx, &dev.tx).unwrap())
            } else { (map.rx.clone(), map.tx.clone()) }
        });
        let hand = self.verified && online.as_ref().is_some_and(|(rx, tx)| hand_layout(rx, tx));
        Reply::Layout { options: self.devices.iter().map(|device| (device.name.clone(), device.identity)).collect(),
            selected: self.selected, device: self.selected().cloned(),
            online, hand }
    }

    pub fn step(&mut self, now: Instant, wait: Option<f64>) {
        if let Some(trace) = &mut self.trace { trace.check(now); }
        if self.can_exchange() {
            let deadline = *self.deadline.get_or_insert(now);
            if now >= deadline {
                self.exchange(Some(deadline), wait);
                let (next, missed) = timing::advance(deadline, self.period, Instant::now());
                self.deadline = Some(next);
                self.skipped += missed;
            }
        } else { self.deadline = None; }
        if self.open && !self.lost && now >= self.next_status { let _ = self.fresh(); }
    }

    fn exchange(&mut self, deadline: Option<Instant>, wait: Option<f64>) {
        let (Some(map), Some(bridge)) = (&mut self.mapping, &mut self.bridge) else { return; };
        let start = Instant::now();
        let measure = self.trace.as_ref().is_some_and(|trace| trace.stopped.is_none());
        let (wkc, wire) = bridge.exchange(&map.output, &mut map.input, measure);
        let end = Instant::now();
        self.wkc = wkc;
        self.cycles += 1;
        let interval = self.last_cycle.map(|last| timing::us(start - last));
        self.last_cycle = Some(start);
        if let Some(interval) = interval {
            self.interval = interval;
            self.max_interval = self.max_interval.max(interval);
            if self.plot.len() == 2000 { self.plot.pop_front(); }
            self.plot.push_back((self.cycles, interval));
        }
        if measure {
            let trace = self.trace.as_mut().unwrap();
            trace.rows.push(timing::Point { now: start, interval, exchange: timing::us(end - start), wkc,
                expected: map.expected, skipped: self.skipped, state: self.status.state, wait,
                late: deadline.map(|time| timing::us(start.saturating_duration_since(time))),
                al: self.trace_al, send: wire.send, receive: wire.receive,
                code: self.last_al.filter(|_| self.misses == 0).map(|_| self.status.al),
                al_age: self.last_al.filter(|_| self.misses == 0).map(|al| timing::us(start - al)) });
            self.trace_al = 0.;
            trace.check(end);
        }
        if wkc == map.expected && map.expected > 0 {
            self.wkc_faults = 0;
            self.last_pdo = Some(end);
            map.sent.copy_from_slice(&map.output);
        } else {
            self.bad_wkc += 1;
            self.wkc_faults = self.wkc_faults.saturating_add(1);
            if wkc == 0 || self.wkc_faults >= WKC_FAULT_LIMIT {
                let expected = map.expected;
                let failures = self.wkc_faults;
                self.lock_on_fault(|| {
                    if wkc == 0 { format!("PDO WKC 为 0（期望 {expected}）") }
                    else { format!("连续 {failures} 次 PDO 收发异常（WKC {wkc}，期望 {expected}）") }
                });
            }
        }
    }

    pub fn snapshot(&self) -> Snapshot {
        let fresh = !self.lost && self.misses == 0 && self.good_wkc() && self.can_exchange();
        let map = self.mapping.as_ref();
        let trace = self.trace.as_ref().or(self.saved_trace.as_deref());
        Snapshot { connected: self.open, online: self.open && !self.lost && self.misses == 0, status: self.status,
            lost: self.lost,
            slave: self.slave.clone(), verified: self.verified, identity_matches: self.identity_matches(),
            armed: self.armed, last_auto_lock: self.last_auto_lock.clone(), dc_ns: self.dc_ns,
            cycle_ms: self.period.as_millis() as u32, early: self.early,
            mapped: map.is_some(), expected: map.map_or(0, |map| map.expected), wkc: self.wkc,
            skipped: self.skipped, bad_wkc: self.bad_wkc, cycles: self.cycles, interval: self.interval,
            maximum: self.max_interval, output: map.filter(|_| fresh).map(|map| map.sent.clone()),
            input: map.filter(|_| fresh).map(|map| map.input.clone()), note: self.note.clone(),
            trace_active: trace.is_some_and(|trace| trace.stopped.is_none()), trace_present: trace.is_some(),
            trace_seconds: trace.map_or(0., |trace| trace.seconds(Instant::now())) }
    }
}

impl Drop for Session { fn drop(&mut self) { self.disconnect(); } }

fn fields_size(fields: &[Field]) -> usize { fields.iter().map(|field| field.bits as usize).sum() }

fn mapping_object(index: u16) -> bool {
    matches!(index, 0x1600..=0x17ff | 0x1a00..=0x1bff | 0x1c00 | 0x1c10..=0x1c2f)
}

fn count(data: Vec<u8>) -> Result<u8> {
    match data.as_slice() { [value] | [value, 0] => Ok(*value), _ => Err("在线 PDO 计数长度异常。".into()) }
}

fn assignment(bridge: &mut dyn Bridge, index: u16, min: u16) -> Result<Vec<Field>> {
    let mut fields = Vec::new();
    let mut groups = HashSet::new();
    let mut offset = 0;
    for sub in 1..=count(bridge.transfer(index, 0, None)?)? {
        let bytes = bridge.transfer(index, sub, None)?;
        let pdo = u16::from_le_bytes(bytes.try_into().map_err(|_| "无效 PDO 分配索引。")?);
        if !(min..=min + 0x1ff).contains(&pdo) || !groups.insert(pdo) { return Err("PDO 索引无效或重复。".into()); }
        for sub in 1..=count(bridge.transfer(pdo, 0, None)?)? {
            let data = bridge.transfer(pdo, sub, None)?;
            let [bits, sub, lo, hi]: [u8; 4] = data.try_into().map_err(|_| "无效映射条目。")?;
            if bits == 0 || fields.len() >= 1024 || offset + bits as usize > 32768 { return Err("在线 PDO 超出映射范围。".into()); }
            fields.push(Field { pdo, index: u16::from_le_bytes([lo, hi]), sub, bits: bits.into(), offset,
                name: String::new(), kind: String::new(), required: false });
            offset += bits as usize;
        }
    }
    Ok(fields)
}

fn unique<'a>(fields: &'a [Field], index: u16, sub: u8, kind: &str, bits: u16) -> Option<&'a Field> {
    let mut found = fields.iter().filter(|field| field.index == index && field.sub == sub);
    let field = found.next()?;
    (found.next().is_none() && field.kind == kind && field.bits == bits).then_some(field)
}

fn hand_layout(rx: &[Field], tx: &[Field]) -> bool {
    (1..=18).all(|sub| unique(rx, 0x7000, sub, "UINT", 16).is_some()) &&
    (1..=12).all(|sub| unique(tx, 0x6000, sub, "INT", 16).is_some()) &&
    (1..=64).all(|sub| unique(tx, 0x6001, sub, "USINT", 8).is_some()) &&
    (0x6002..=0x6006).all(|index| (1..=48).all(|sub| unique(tx, index, sub, "USINT", 8).is_some()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::bus::{Axis, MappingSize, WireTiming};
    use std::{cell::Cell, collections::HashMap, rc::Rc};

    type StatusOverride = Rc<Cell<Option<std::result::Result<Status, &'static str>>>>;

    struct Mock {
        device: Device,
        state: i32,
        wkc: Rc<Cell<i32>>,
        status_override: StatusOverride,
        values: HashMap<(u16, u8), Vec<u8>>,
    }

    impl Bridge for Mock {
        fn adapters(&mut self) -> Result<Vec<crate::bus::Adapter>> {
            Ok(vec![crate::bus::Adapter { id: "bench".into(), description: "fake".into() }])
        }
        fn open(&mut self, _: &str) -> Result<()> { self.state = 2; Ok(()) }
        fn identity(&mut self) -> Result<Slave> {
            let d = self.device.identity;
            Ok(Slave { vendor: d.vendor, product: d.product, revision: d.revision, dc: true })
        }
        fn status(&mut self) -> Result<Status> {
            self.status_override.get().unwrap_or(Ok(Status { state: self.state, al: 0 })).map_err(str::to_string)
        }
        fn close(&mut self) { self.state = 1; }
        fn set_state(&mut self, state: i32) -> Result<()> { self.state = state; Ok(()) }
        fn mapping(&mut self, hints: Option<(usize, usize)>) -> Result<MappingSize> {
            let size = (fields_size(&self.device.rx), fields_size(&self.device.tx));
            assert_eq!(hints, Some(size));
            Ok(MappingSize { rx_bits: size.0, tx_bits: size.1, expected: 3 })
        }
        fn exchange(&mut self, output: &[u8], input: &mut [u8], _: bool) -> (i32, WireTiming) {
            if !input.is_empty() { input[0] = output[0]; }
            (self.wkc.get(), WireTiming::default())
        }
        fn transfer(&mut self, index: u16, sub: u8, value: Option<Vec<u8>>) -> Result<Vec<u8>> {
            if let Some(value) = value {
                self.values.insert((index, sub), value.clone());
                return Ok(value);
            }
            self.values.get(&(index, sub)).cloned().ok_or_else(|| "SDO Abort 0x06020000".into())
        }
        fn configure_dc(&mut self, _: u32, _: u32, _: i32) -> Result<()> {
            assert_eq!(self.state, 2);
            Ok(())
        }
    }

    fn fixture(device: &Device) -> (Session, Rc<Cell<i32>>, StatusOverride) {
        let mut values = HashMap::from([((0x1C00,0), vec![4]), ((0x1C00,3), vec![3]), ((0x1C00,4), vec![4]),
            ((0x2000,0),vec![0,0]), ((0x1018,1),device.identity.vendor.to_le_bytes().to_vec())]);
        for (assignment, fields) in [(0x1C12, &device.rx),(0x1C13, &device.tx)] {
            let mut indexes: Vec<u16> = Vec::new();
            for entry in fields {
                if !indexes.contains(&entry.pdo) { indexes.push(entry.pdo); }
            }
            values.insert((assignment,0),vec![indexes.len() as u8]);
            for (i,pdo) in indexes.iter().enumerate() {
                values.insert((assignment,(i+1) as u8),pdo.to_le_bytes().to_vec());
                let entries: Vec<_> = fields.iter().filter(|entry| entry.pdo == *pdo).collect();
                values.insert((*pdo,0),vec![u8::try_from(entries.len()).unwrap()]);
                for (j,entry) in entries.iter().enumerate() {
                    let [lo,hi] = entry.index.to_le_bytes();
                    values.insert((*pdo,(j+1) as u8),vec![entry.bits as u8,entry.sub,lo,hi]);
                }
            }
        }
        let wkc = Rc::new(Cell::new(3));
        let status_override = Rc::new(Cell::new(None));
        let bus = Session::new(Some(Box::new(Mock { device: device.clone(), state: 2, wkc: wkc.clone(),
            status_override: status_override.clone(), values })));
        (bus, wkc, status_override)
    }

    fn demo() -> Device { esi::parse(include_str!("../../../DemoESI.xml")).unwrap().remove(0) }

    #[test]
    fn safeop_to_op_requires_identity_mapping_and_explicit_unlock() {
        let device = demo();
        let (mut bus,wkc,_) = fixture(&device);
        assert!(bus.scan("bench", 1, true).is_ok());
        let mut wrong = device.clone();
        wrong.rx[0].sub = 2;
        bus.action(Action::Import { devices: vec![wrong] }).unwrap();
        bus.request_state(4).unwrap();
        bus.exchange(None,None);
        assert!(bus.snapshot().input.is_some());
        assert!(!bus.snapshot().verified);
        bus.request_state(8).unwrap();
        assert!(bus.action(Action::Arm { enabled:true }).is_err());
        bus.action(Action::Import { devices: vec![device] }).unwrap();
        assert!(bus.snapshot().verified);
        assert!(bus.field_operate(0,"42").is_err());
        bus.action(Action::Arm { enabled:true }).unwrap();
        bus.field_operate(0,"42").unwrap();
        bus.exchange(None,None);
        assert_eq!(bus.snapshot().output.unwrap(),vec![42,0]);
        assert_eq!(bus.snapshot().input.unwrap(),vec![42,0]);
        wkc.set(-1);
        bus.exchange(None,None);
        assert!(bus.snapshot().armed);
        assert!(bus.snapshot().input.is_none());
        assert!(bus.field_operate(0,"43").is_err());
        assert!(bus.snapshot().last_auto_lock.is_none());
        wkc.set(3);
        bus.exchange(None,None);
        bus.field_operate(0,"43").unwrap();

        wkc.set(1);
        for _ in 0..2 { bus.exchange(None,None); }
        assert!(bus.snapshot().armed);
        wkc.set(-1);
        bus.exchange(None,None);
        assert!(!bus.snapshot().armed);
        assert_eq!(bus.mapping.as_ref().unwrap().output,vec![0,0]);
        let event = bus.snapshot().last_auto_lock.unwrap();
        assert!(event.reason.contains("连续 3 次") && event.reason.contains("WKC -1"));
        assert_eq!(event.cycle,bus.snapshot().cycles);
        wkc.set(3);
        bus.exchange(None,None);
        let recovered = bus.snapshot();
        assert!(!recovered.armed);
        assert_eq!(recovered.last_auto_lock.unwrap().sequence,event.sequence);

        bus.action(Action::Arm { enabled:true }).unwrap();
        bus.field_operate(0,"44").unwrap();
        wkc.set(0);
        bus.exchange(None,None);
        assert!(!bus.snapshot().armed);
        assert_eq!(bus.mapping.as_ref().unwrap().output,vec![0,0]);
        assert!(bus.snapshot().last_auto_lock.unwrap().reason.contains("WKC 为 0"));
    }

    #[test]
    fn al_fault_reasons_survive_poll_recovery_and_are_not_overwritten_while_locked() {
        let device = demo();
        let (mut bus,wkc,al) = fixture(&device);
        bus.action(Action::Import { devices: vec![device] }).unwrap();
        bus.scan("bench",1,true).unwrap();
        bus.request_state(4).unwrap();
        bus.request_state(8).unwrap();
        bus.action(Action::Arm { enabled:true }).unwrap();
        al.set(Some(Err("mock AL timeout")));
        assert!(bus.fresh().is_err());
        let transient = bus.snapshot();
        assert!(transient.connected && !transient.online && !transient.lost && !transient.armed);
        let first = bus.snapshot().last_auto_lock.unwrap();
        assert!(first.reason.contains("AL 状态查询失败"));
        wkc.set(1);
        bus.exchange(None,None);
        assert_eq!(bus.snapshot().last_auto_lock.unwrap().sequence, first.sequence);
        wkc.set(3);
        al.set(None);
        bus.fresh().unwrap();
        bus.exchange(None,None);
        assert!(!bus.snapshot().armed);
        bus.action(Action::Arm { enabled:true }).unwrap();
        al.set(Some(Ok(Status { state:0x14, al:0x001B })));
        bus.fresh().unwrap();
        assert!(!bus.snapshot().armed);
        let second = bus.snapshot().last_auto_lock.unwrap();
        assert_eq!(second.sequence, first.sequence + 1);
        assert!(second.reason.contains("状态 0x14，AL 0x001B"));

        let stale = Instant::now() - Duration::from_secs(1);
        bus.last_al = Some(stale);
        bus.last_pdo = Some(stale);
        al.set(Some(Err("mock AL timeout")));
        for _ in 0..5 { assert!(bus.fresh().is_err()); }
        let lost = bus.snapshot();
        assert!(lost.connected && !lost.online && lost.lost);
        bus.disconnect();
        let disconnected = bus.snapshot();
        assert!(!disconnected.connected && !disconnected.lost);
    }

    #[test]
    fn mailbox_permissions_readback_and_dc_auto_preop() {
        let device = demo();
        let (mut bus,_,_) = fixture(&device);
        bus.action(Action::Import { devices: vec![device] }).unwrap();
        bus.scan("bench", 1, true).unwrap();
        assert!(bus.sdo(0x1018,1,Some("01 00 00 00".into()),true).is_err());
        let Reply::Sdo { value,message,read_back } = bus.sdo(0x2000,0,Some("258".into()),false).unwrap() else { panic!("missing SDO response") };
        assert!(read_back);
        assert_eq!(value,vec![2,1]);
        assert!(message.contains("一致"));
        bus.request_state(4).unwrap();
        bus.request_state(8).unwrap();
        bus.action(Action::Arm { enabled:true }).unwrap();
        assert!(bus.sdo(0x2000,0,None,false).is_err());
        bus.dc(Some(0),1000.).unwrap();
        assert_eq!(bus.snapshot().status.state,2);
        assert!(!bus.snapshot().armed);
        assert_eq!(bus.snapshot().dc_ns,Some(1_000_000));
    }

    #[test]
    fn product_esi_checks_full_coe_mapping_and_atomic_six_axis_targets() {
        let Some(path) = std::env::var_os("ETHERCAT_TEST_PRODUCT_ESI") else { return; };
        let device = esi::parse(&std::fs::read_to_string(path).unwrap()).unwrap().remove(0);
        assert_eq!((fields_size(&device.rx),fields_size(&device.tx)),(288,2624));
        let (mut bus,_,_) = fixture(&device);
        bus.action(Action::Import { devices: vec![device] }).unwrap();
        bus.scan("bench",1,true).unwrap();
        bus.request_state(4).unwrap();
        assert!(matches!(bus.layout(), Reply::Layout { hand:true, .. }));
        bus.request_state(8).unwrap();
        bus.action(Action::Arm { enabled:true }).unwrap();
        let targets = || (0..6).map(|axis| Axis { axis, angle:u16::from(axis)+1, torque:50, speed:100 }).collect();
        bus.hand_operate(targets()).unwrap();
        let old = bus.mapping.as_ref().unwrap().output.clone();
        let mut invalid = targets();
        invalid[0].angle = 255;
        invalid[5].axis = 0;
        assert!(bus.hand_operate(invalid).is_err());
        assert_eq!(old,bus.mapping.as_ref().unwrap().output);
        assert_eq!(old[0..2], [1,0]);
        assert_eq!(old[34..36], [100,0]);
    }
}
