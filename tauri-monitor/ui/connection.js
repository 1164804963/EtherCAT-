"use strict";

(() => {
  const invoke = window.__TAURI__?.core?.invoke;
  const byId = id => document.getElementById(id);
  if (!invoke) {
    document.querySelectorAll("#hardware button, #hardware input, #hardware select")
      .forEach(control => { control.disabled = true; });
    byId("hardware-message").textContent = "请通过 Windows 桌面程序打开真机测试界面。";
    return;
  }
  const adapters = byId("adapters");
  const periodMs = byId("live-cycle-ms");
  const early = byId("live-early");
  const files = byId("esi-file");
  const devices = byId("live-device");
  const deviceSummary = byId("live-device-summary");
  const useDevice = byId("live-use-device");
  const message = byId("hardware-message");
  const dcToggle = byId("dc-toggle");
  const dcSettings = byId("dc-settings");
  const dcFrequency = new Intl.NumberFormat("zh-CN", {maximumFractionDigits:3});
  const tabs = [...document.querySelectorAll("#live-tabs [role=tab]")];
  const stateButtons = [...document.querySelectorAll("[data-target]")];
  const names = { 1:"INIT", 2:"PRE-OP", 3:"BOOT", 4:"SAFE-OP", 8:"OP" };
  const { read, decode, hex: rawHex } = globalThis.PdoValues;
  const emptyLayout = { options:[], selected:null, device:null, online:null, hand:false };
  let layout = emptyLayout;
  let frame = { status:{state:0, al:0} };
  let activeTab = "live-pdo";
  let busy = false;
  let timeline = [];
  let pdoRows = [];
  const sdoResults = new Map();
  const sdoDictionary = createDictionary("sdo","sdo-rows");
  const handDictionary = createDictionary("hand-sdo","hand-config");
  const dictionaries = [sdoDictionary,handDictionary];
  let axes = [];
  let handRx = new Map(), handTx = new Map();
  let permissions = "";
  let lastStateNote = "";
  let lastLockSequence = 0;
  let renderPending = false;

  const hex = (value, width) => "0x" + value.toString(16).toUpperCase().padStart(width,"0");
  const addr = entry => hex(entry.index,4) + ":" + hex(entry.sub,2).slice(2);
  const fieldKey = (index, sub) => index + ":" + sub;
  const stateName = state => (names[state & 15] ?? hex(state,2)) + (state & 16 ? " + ERROR" : "");
  const validMailbox = () => frame.online && !frame.armed && [2,4,8].includes(frame.status.state);
  const outputReady = () => frame.online && frame.verified && frame.status.state === 8 &&
    frame.expected > 0 && frame.wkc === frame.expected;
  const control = action => invoke("control", {action});

  function setText(element, text) {
    if (element.textContent !== text) element.textContent = text;
  }

  function log(text) {
    const list = byId("live-events");
    const line = document.createElement("li");
    line.textContent = new Date().toLocaleTimeString("zh-CN") + "  " + text;
    list.prepend(line);
    if (list.children.length > 150) list.lastElementChild.remove();
  }

  function say(text) {
    setText(message, text);
    log(text);
  }

  async function run(label, work) {
    if (busy) return;
    busy = true;
    refreshControls(true);
    setText(message, label + "…");
    const notice = setTimeout(() => setText(message, label + "…通信或文件操作仍在等待。"), 5000);
    try {
      const detail = await work();
      if (detail !== false) say(typeof detail === "string" ? detail : label + " · 已完成");
    } catch (error) { say(String(error)); }
    finally {
      clearTimeout(notice);
      busy = false;
      refreshControls(true);
    }
  }

  function cell(row, text = "") {
    const el = row.insertCell();
    el.textContent = text;
    return el;
  }

  function button(text, action) {
    const node = document.createElement("button");
    node.type = "button";
    node.textContent = text;
    node.addEventListener("click",action);
    return node;
  }

  function input(label) {
    const node = document.createElement("input");
    node.type = "text";
    node.setAttribute("aria-label",label);
    node.autocomplete = "off";
    return node;
  }

  function refreshControls(force = false) {
    const state = frame.status.state;
    const key = [busy,frame.connected,frame.online,frame.verified,frame.identity_matches,frame.armed,
      frame.mapped,state,frame.wkc,frame.expected,frame.trace_active,frame.trace_present,adapters.value,
      periodMs.value,devices.value,byId("dc-mode").value].join("|");
    if (!force && key === permissions) return;
    permissions = key;
    adapters.disabled = byId("refresh-adapters").disabled = periodMs.disabled = busy || !!frame.connected;
    early.disabled = periodMs.disabled || Number(periodMs.value) !== 1;
    byId("scan").disabled = adapters.disabled || !adapters.value || !periodMs.checkValidity();
    byId("disconnect").disabled = busy || !frame.connected;
    files.disabled = devices.disabled = useDevice.disabled = busy;
    byId("sdo-filter").disabled = busy;
    stateButtons.forEach(btn => {
      const target = Number(btn.dataset.target);
      btn.disabled = busy || !frame.online || (target === 4 && (state & 15) !== 2 && !frame.mapped) ||
        (target === 8 && (state !== 4 || !frame.mapped));
    });
    byId("live-arm").disabled = busy || frame.armed || !outputReady();
    byId("live-lock").disabled = !frame.connected || busy || !frame.armed;
    for (const row of pdoRows) if (row.write) row.write.disabled = row.edit.disabled = busy || !frame.armed || !outputReady();
    const handEditable = !busy && frame.armed && outputReady() && layout.hand;
    byId("hand-all").disabled = !handEditable;
    for (const axis of axes) {
      axis.write.disabled = !handEditable;
      axis.inputs.forEach(el => { el.disabled = !handEditable; });
    }
    const mailbox = !busy && validMailbox();
    for (const dictionary of dictionaries) refreshDictionaryControls(dictionary,mailbox);
    byId("hand-read").disabled = !mailbox || !frame.identity_matches ||
      ![...handDictionary.rows.values()].some(row => row.entry.read_states & state);
    byId("raw-read").disabled = byId("raw-write").disabled = !mailbox;
    byId("dc-apply").disabled = busy || !frame.online || (!!byId("dc-mode").value && (!frame.identity_matches || !frame.slave?.dc));
    byId("dc-hz").disabled = busy || !byId("dc-mode").value;
    byId("trace-start").disabled = busy || frame.trace_present || !frame.online || !frame.mapped || ![4,8].includes(state);
    byId("trace-long").disabled = busy || !!frame.trace_present;
    byId("trace-save").disabled = busy || !frame.trace_present;
  }

  function setLayout(next, clearDrafts = false) {
    const previousDc = byId("dc-mode").value;
    const sameDevice = layout.device && next?.device && ["vendor", "product", "revision"].every(key =>
      layout.device.identity[key] === next.device.identity[key]);
    layout = next ?? emptyLayout;
    if (!sameDevice) {
      for (const dictionary of dictionaries) dictionary.selection = null;
      sdoResults.clear();
    }
    const models = layout.options.map(([name,id]) => `${name} · ${hex(id.product,8)}/${hex(id.revision,8)}`);
    devices.replaceChildren(new Option("未匹配，请选择型号", ""),
      ...models.map((name, index) => new Option(name, String(index))));
    devices.options[0].disabled = true;
    devices.value = layout.selected === null ? "" : String(layout.selected);
    devices.hidden = models.length < 2;
    deviceSummary.hidden = !devices.hidden;
    useDevice.hidden = models.length !== 1 || layout.selected !== null;
    setText(deviceSummary, models[0] ?? "未导入 ESI");
    const dc = byId("dc-mode");
    dc.replaceChildren(new Option("Free Run / 禁用 SYNC0", ""));
    layout.device?.dc.forEach((entry,index) => {
      const option = new Option((entry.name || "SYNC0") + " · " + hex(entry.assign,4), String(index));
      option.disabled = entry.assign !== 0x300 || entry.sync1;
      dc.add(option);
    });
    if (sameDevice && [...dc.options].some(option => option.value === previousDc && !option.disabled)) dc.value = previousDc;
    if (clearDrafts) {
      for (const axis of axes) axis.inputs.forEach(el => { el.value = ""; });
      byId("raw-result").textContent = "";
    }
    const [receive, transmit] = layout.online ?? [layout.device?.rx ?? [], layout.device?.tx ?? []];
    handRx = new Map(receive.map(entry => [fieldKey(entry.index, entry.sub), entry]));
    handTx = new Map(transmit.map(entry => [fieldKey(entry.index, entry.sub), entry]));
    buildPdo(receive, transmit);
    buildSdo();
    refreshControls(true);
    scheduleRender();
  }

  function buildPdo(rx, tx) {
    const body = byId("live-pdo-rows");
    body.replaceChildren();
    pdoRows = [];
    for (const [direction, fields] of [["RxPDO",rx],["TxPDO",tx]]) {
      for (const field of fields) {
        const row = body.insertRow();
        for (const fieldText of [direction,hex(field.pdo,4),addr(field),field.name || (field.index ? "在线字段" : "填充"), (field.kind || "RAW") + " · " + field.bits + " bit"]) cell(row,fieldText);
        const current = cell(row,"—");
        const editCell = cell(row);
        const view = {direction,field,current};
        if (direction === "RxPDO" && field.index && layout.online) {
          view.edit = input("待写值 " + addr(field));
          view.edit.size = 6;
          view.write = button("应用", () => run("写入 " + addr(field), async () => {
            await control({kind:"pdo",offset:field.offset,value:view.edit.value});
          }));
          editCell.className = "output-edit";
          editCell.append(view.edit,view.write);
        }
        pdoRows.push(view);
      }
    }
    if (!pdoRows.length) cell(body.insertRow(),"未导入 ESI，或暂无在线 PDO 字段。").colSpan = 7;
  }

  function createDictionary(prefix, bodyId) {
    const content = byId("sdo-dictionary-template").content.cloneNode(true);
    const nodes = Object.fromEntries([...content.querySelectorAll("[data-part]")].map(node => [node.dataset.part,node]));
    for (const [part,node] of Object.entries(nodes)) node.id = part === "rows" ? bodyId : prefix + "-" + part;
    content.querySelector("[data-write-label]").htmlFor = nodes.input.id;
    byId(prefix + "-dictionary").append(content);
    const dictionary = {nodes, rows:new Map(), selection:null, selected:null};
    nodes.read.addEventListener("click", () => operateSdo(dictionary,false));
    nodes.write.addEventListener("click", () => operateSdo(dictionary,true));
    return dictionary;
  }

  function refreshDictionaryControls({nodes,selected}, mailbox) {
    if (!selected) return;
    const {entry} = selected;
    const state = frame.status.state;
    const canAccess = mailbox && frame.identity_matches;
    const readable = !!(entry.read_states & state);
    const writable = !!(entry.write_states & state);
    nodes.read.disabled = !canAccess || !readable;
    nodes.write.disabled = nodes.input.disabled = !canAccess || !writable;
    const availability = !frame.connected ? "连接从站后可操作。" :
      !frame.online ? "等待从站响应。" : !frame.identity_matches ? "当前 ESI 与从站身份未匹配。" :
      frame.armed ? "锁定 PDO 输出后可操作。" : !readable && !writable ? "当前状态下无可用操作。" :
      entry.write_states && !writable ? "当前状态下不可写入。" : entry.read_states && !readable ? "当前状态下不可读取。" : "";
    setText(nodes.availability,availability);
  }

  function buildDictionary(dictionary, entries, emptyMessage) {
    const body = dictionary.nodes.rows;
    body.replaceChildren();
    dictionary.rows.clear();
    for (const entry of entries) {
      const key = fieldKey(entry.index,entry.sub);
      const row = body.insertRow();
      cell(row,addr(entry));
      const name = cell(row);
      const select = document.createElement("button");
      select.type = "button";
      select.textContent = entry.name || addr(entry);
      select.setAttribute("aria-label", (addr(entry) + " " + entry.name).trim());
      name.append(select);
      cell(row,entry.access || "未声明");
      const value = cell(row,sdoResults.get(key)?.value ?? "—");
      row.addEventListener("click", () => {
        if (dictionary.selection !== key) {
          dictionary.selection = key;
          showSdoSelection(dictionary);
        }
      });
      dictionary.rows.set(key,{entry,key,select,value});
    }
    if (!dictionary.rows.size) cell(body.insertRow(),emptyMessage).colSpan = 4;
    showSdoSelection(dictionary);
  }

  function showSdoSelection(dictionary) {
    const {nodes,rows} = dictionary;
    const selected = dictionary.selected = rows.get(dictionary.selection) ?? null;
    for (const row of rows.values()) row.select.setAttribute("aria-pressed", String(row === selected));
    nodes.empty.hidden = !!selected;
    nodes.selected.hidden = !selected;
    nodes.input.value = "";
    if (selected) {
      const {entry,key} = selected;
      const saved = sdoResults.get(key);
      setText(nodes.address,addr(entry));
      setText(nodes.name,entry.name || "未命名对象");
      setText(nodes.type,entry.kind + (entry.bits ? " · " + entry.bits + " bit" : ""));
      setText(nodes.access,entry.access || "未声明");
      setText(nodes.value,saved?.value ?? "—");
      setText(nodes.result,saved?.message ?? "");
      nodes.read.hidden = !entry.read_states;
      nodes["write-controls"].hidden = !entry.write_states;
    }
    refreshDictionaryControls(dictionary,!busy && validMailbox());
  }

  function buildSdoList() {
    const entries = layout.device?.entries ?? [];
    const query = byId("sdo-filter").value.trim().toLowerCase();
    const visible = entries.filter(entry => (addr(entry) + " " + entry.name + " " + entry.kind).toLowerCase().includes(query));
    const empty = !layout.device ? "导入并选择 ESI 设备后查看对象。" : "当前 ESI 没有可显示的对象。";
    buildDictionary(sdoDictionary,visible,entries.length ? "没有符合筛选条件的对象。" : empty);
  }

  function buildSdo() {
    const entries = layout.device?.entries ?? [];
    const handEntries = entries.filter(entry => {
      if (entry.index !== 0x8000 && entry.index !== 0x9000) return false;
      return entry.sub !== 0 || !entries.some(other => other.index === entry.index && other.sub !== 0);
    }).sort((a,b) => b.index - a.index || a.sub - b.sub);
    buildDictionary(handDictionary,handEntries,layout.device ? "当前 ESI 没有可显示的设备配置对象。" : "导入并选择 ESI 设备后查看对象。");
    buildSdoList();
  }

  function operateSdo({nodes,selected}, write) {
    if (!selected) return;
    const {entry} = selected;
    const value = write ? nodes.input.value : null;
    if (write && !confirm("确认写入 " + addr(entry) + "？")) return;
    run((write ? "写入 " : "读取 ") + addr(entry), () => transferSdo(entry,value));
  }

  async function transferSdo(entry, value = null) {
    const key = fieldKey(entry.index,entry.sub);
    const saved = sdoResults.get(key) ?? {value:"—", message:""};
    sdoResults.set(key,saved);
    try {
      const reply = await control({kind:"sdo", index:entry.index, sub:entry.sub, value});
      if (reply.read_back) saved.value = decode(Uint8Array.from(reply.value),entry.kind,entry.bits);
      saved.message = reply.message;
      return reply.message;
    } catch (error) {
      saved.message = String(error);
      throw error;
    } finally {
      for (const {nodes,rows,selected} of dictionaries) {
        const row = rows.get(key);
        if (row) setText(row.value,saved.value);
        if (selected?.key === key) {
          setText(nodes.value,saved.value);
          setText(nodes.result,saved.message);
        }
      }
    }
  }

  function makeAxes() {
    const body = byId("hand-axes");
    axes = Array.from({length:6}, (_,axis) => {
      const row = body.insertRow();
      cell(row,"轴 " + (axis + 1));
      const inputs = ["角度","力矩","速度"].map(name => {
        const entry = input("轴 " + (axis + 1) + " " + name);
        entry.inputMode = "numeric";
        entry.size = 5;
        cell(row).append(entry);
        return entry;
      });
      const sent = cell(row,"—"), angle = cell(row,"—"), torque = cell(row,"—");
      const write = button("提交", () => sendAxes([axis]));
      cell(row).append(write);
      return {inputs,sent,angle,torque,write};
    });
  }

  function sendAxes(indexes) {
    run("提交灵巧手目标", async () => {
      const targets = indexes.map(axis => {
        const [angle, torque, speed] = axes[axis].inputs.map(el => {
          const value = el.value.trim();
          const num = Number(value);
          if (!/^(?:\d+|0x[0-9a-f]+)$/i.test(value) || !Number.isInteger(num) || num < 0 || num > 65535) {
            throw Error("轴 " + (axis + 1) + " 需要 0–65535 的角度、力矩和速度。");
          }
          return num;
        });
        return {axis,angle,torque,speed};
      });
      await control({kind:"hand",targets});
    });
  }

  function readSamples(index, count) {
    if (!layout.hand || !frame.input) return null;
    const result = [];
    for (let sub = 1; sub <= count; sub++) {
      const field = handTx.get(fieldKey(index, sub));
      if (!field) return null;
      result.push(Number(read(frame.input,field)));
    }
    return result;
  }

  function drawSensor(canvas, values) {
    if (values) { drawHeat(canvas,values,255); return; }
    const surface = contextFor(canvas);
    if (!surface) return;
    surface.ctx.fillStyle = "#9cadc0";
    surface.ctx.font = '14px "Microsoft YaHei UI", sans-serif';
    surface.ctx.fillText("暂无有效在线数据", 8, 25);
  }

  function drawLivePlot() {
    const surface = contextFor(byId("live-plot"));
    if (!surface) return;
    const {ctx,width,height} = surface;
    const pad = 46, graphWidth = width - pad - 10;
    const points = timeline.slice(-Math.max(120, Math.floor(graphWidth)));
    const target = frame.cycle_ms || Number(periodMs.value);
    const top = Math.max(2 * target, ...points.map(v => v[1] / 1000));
    const y = ms => height - 11 - (height - 20) * ms / top;
    ctx.font = '12px "Segoe UI", sans-serif';
    ctx.textAlign = "right";
    ctx.textBaseline = "middle";
    for (const ms of [0, target, top]) {
      ctx.fillStyle = "#9cadc0";
      ctx.fillText(ms.toFixed(1), pad - 6, y(ms));
      ctx.strokeStyle = ms === target ? "#b3a0e6" : "#2a3848";
      ctx.setLineDash(ms === target ? [5,5] : []);
      ctx.beginPath();ctx.moveTo(pad,y(ms));ctx.lineTo(width-10,y(ms));ctx.stroke();
    }
    ctx.setLineDash([]);
    if (points.length < 2) return;
    ctx.strokeStyle = "#61e4c3";
    ctx.beginPath();
    points.forEach((point,i) => {
      const x = pad + graphWidth * i / (points.length - 1);
      if (i === 0) ctx.moveTo(x,y(point[1] / 1000)); else ctx.lineTo(x,y(point[1] / 1000));
    });
    ctx.stroke();
  }

  function renderSlaveStatus() {
    const {connected, online, lost, status} = frame;
    const badge = byId("slave-state-badge");
    const wkc = byId("header-wkc");
    const al = byId("header-al");
    const hasAlError = online && (status.al !== 0 || (status.state & 16) !== 0);
    const wkcVisible = online && frame.mapped && frame.expected > 0;

    let label = "未连接";
    let level = "idle";
    if (connected) {
      if (lost) {
        label = "从站无响应";
        level = "fault";
      } else if (!online) {
        label = "AL 查询异常";
        level = "warning";
      } else {
        label = stateName(status.state);
        level = hasAlError ? "fault" : status.state === 8 ? "op" : "ready";
      }
    }
    setText(byId("slave-state"), label);
    if (badge.dataset.level !== level) badge.dataset.level = level;
    wkc.hidden = !wkcVisible;
    wkc.classList.toggle("warning", wkcVisible && frame.wkc !== frame.expected);
    if (wkcVisible) setText(wkc, `WKC ${frame.wkc}/${frame.expected}`);
    al.hidden = !hasAlError;
    if (hasAlError) setText(al, `AL ${hex(status.al,4)}`);
  }

  function render() {
    const {status,slave} = frame;
    renderSlaveStatus();
    setText(byId("slave-al"), "AL " + (frame.online ? hex(status.al,4) : "—"));
    for (const key of ["vendor","product","revision"]) setText(byId("slave-" + key),slave ? hex(slave[key],8) : "—");
    setText(byId("slave-dc"),slave ? (slave.dc ? "支持 DC" : "未报告 DC 能力") : "DC —");
    const dcNs = frame.dc_ns;
    let dcSummary = "未连接";
    let dcDetail = "连接后可应用 DC 同步设置。";
    if (frame.connected) {
      dcSummary = "未核对";
      dcDetail = "本次连接的 SYNC0 配置尚未核对。";
      if (dcNs === 0) {
        dcSummary = "Free Run";
        dcDetail = "已应用 Free Run · SYNC0 已禁用。";
      } else if (dcNs > 0) {
        const hz = 1e9 / dcNs;
        const rate = hz >= 1000 ? `${dcFrequency.format(hz / 1000)} kHz` : `${dcFrequency.format(hz)} Hz`;
        dcSummary = `SYNC0 ${rate}`;
        dcDetail = `已启用 SYNC0 · ${dcNs} ns`;
      }
    }
    setText(byId("dc-summary"),dcSummary);
    setText(byId("dc-live"),dcDetail);
    const lock = frame.last_auto_lock;
    const lockedByFault = !frame.armed && lock;
    const waitingForWkc = frame.armed && frame.wkc !== frame.expected;
    const lockState = byId("live-lock-state");
    lockState.classList.toggle("warning", !!lockedByFault || waitingForWkc);
    setText(lockState,frame.armed ? waitingForWkc ? "输出已解锁 · 等待 WKC 恢复，暂停写入" : "输出已解锁" : lockedByFault ?
      "输出锁定 · 上次自动锁定：" + lock.reason : "输出锁定");
    if (lock && lock.sequence !== lastLockSequence) {
      log("输出自动锁定 · 周期 " + lock.cycle + " · " + lock.reason);
      lastLockSequence = lock.sequence;
    }
    if (frame.note && frame.note !== lastStateNote) log(frame.note);
    lastStateNote = frame.note ?? "";
    refreshControls();
    if (activeTab === "live-pdo") {
      setText(byId("live-timing"),frame.mapped ?
        "目标 " + frame.cycle_ms + " ms · 最近 " + (frame.interval/1000).toFixed(3) + " ms · 最大 " + (frame.maximum/1000).toFixed(3) +
        " ms · WKC " + frame.wkc + "/" + frame.expected + " · 失配 " + frame.bad_wkc + " · 跳过 " + frame.skipped + (frame.early ? " · 提前 500 µs" : "") :
        "等待进入 SAFE-OP");
      setText(byId("live-map-note"),frame.verified ? "已核对在线 PDO 与所选 ESI。" :
        frame.mapped ? "在线映射或身份尚未匹配，输出锁定。" : "导入 ESI 并进入 SAFE-OP 后可核对在线映射。");
      if (byId("live-raw").parentElement.open) {
        setText(byId("live-raw"),"RxPDO " + rawHex(frame.output) + "\nTxPDO " + rawHex(frame.input));
      }
      for (const row of pdoRows) setText(row.current, layout.online ? read(row.direction === "TxPDO" ? frame.input : frame.output,row.field) : "—");
      drawLivePlot();
    } else if (activeTab === "live-hand") {
      setText(byId("hand-match"),layout.hand ? "六轴布局已核对，显示原始值。" : "当前设备未匹配现有六轴布局。");
      axes.forEach((axis,i) => {
        setText(axis.angle, layout.hand ? read(frame.input,handTx.get(fieldKey(0x6000, i+1))) : "—");
        setText(axis.torque, layout.hand ? read(frame.input,handTx.get(fieldKey(0x6000, i+7))) : "—");
        setText(axis.sent, layout.hand && frame.output ? [1,7,13].map(sub => read(frame.output,handRx.get(fieldKey(0x7000, i+sub)))).join(" / ") : "—");
      });
      drawSensor(byId("hand-temperature"),readSamples(0x6001,64));
      drawSensor(byId("hand-touch"),readSamples(0x6002 + Number(byId("hand-group").value),48));
    } else if (activeTab === "live-log") {
      setText(byId("trace-state"),frame.trace_present ?
        (frame.trace_active ? "采集中" : "采样已停止，可保存") + " · " + frame.trace_seconds.toFixed(1) + " 秒" : "");
    }
  }

  function scheduleRender() {
    if (renderPending) return;
    renderPending = true;
    requestAnimationFrame(() => { renderPending = false; render(); });
  }

  function switchTab(tab) {
    activeTab = tab.getAttribute("aria-controls");
    tabs.forEach(btn => {
      const selected = btn === tab;
      btn.setAttribute("aria-selected", String(selected));
      byId(btn.getAttribute("aria-controls")).hidden = !selected;
    });
    scheduleRender();
  }

  async function bytesToXml(file) {
    const data = new Uint8Array(await file.arrayBuffer());
    let encoding = "utf-8";
    if (data[0] === 0xff && data[1] === 0xfe || data[0] === 0x3c && data[1] === 0) encoding = "utf-16le";
    else if (data[0] === 0xfe && data[1] === 0xff || data[0] === 0 && data[1] === 0x3c) encoding = "utf-16be";
    else encoding = new TextDecoder().decode(data.slice(0,160)).match(/<\?xml[^>]*\bencoding=["']([^"']+)/i)?.[1] || encoding;
    return new TextDecoder(encoding,{fatal:true}).decode(data);
  }

  function addressInput(name, maximum) {
    const text = byId(name).value.trim();
    const value = Number(text);
    if (!/^(?:\d+|0x[0-9a-f]+)$/i.test(text) || !Number.isInteger(value) || value > maximum) throw Error("索引或子索引无效。");
    return value;
  }

  makeAxes();
  tabs.forEach(tab => tab.addEventListener("click",() => switchTab(tab)));
  adapters.addEventListener("change", () => refreshControls(true));
  periodMs.addEventListener("input", () => refreshControls(true));
  const refreshAdapters = () => run("刷新网卡", async () => {
    const selected = adapters.value;
    const available = await invoke("list_adapters");
    adapters.replaceChildren(new Option(available.length ? "请选择专用测试网口" : "未找到 Npcap 网口", ""));
    available.forEach(adapter => adapters.add(new Option((adapter.description || "Npcap 网口") + " · " + adapter.id.slice(-9,-1),adapter.id)));
    if (available.some(adapter => adapter.id === selected)) adapters.value = selected;
    return available.length ? "找到 " + available.length + " 个 Npcap 网口。" : "未找到 Npcap 网口。";
  });
  byId("refresh-adapters").addEventListener("click", refreshAdapters);
  byId("scan").addEventListener("click", () => run("扫描从站", async () => {
    if (!adapters.value || !periodMs.reportValidity()) return false;
    const connection = await invoke("scan",{adapter:adapters.value,periodMs:Number(periodMs.value),early:early.checked});
    sdoResults.clear();
    frame = {...frame,connected:true,online:true,lost:false,slave:connection.slave,status:connection.status};
    timeline = [];
    setLayout(await control({kind:"layout"}),true);
  }));
  byId("disconnect").addEventListener("click", () => run("断开", async () => {
    await invoke("disconnect");
    sdoResults.clear();
    frame = {...frame,connected:false,online:false,lost:false,mapped:false,verified:false,armed:false,slave:null,status:{state:0,al:0},output:null,input:null,dc_ns:null};
    timeline = [];
    setLayout(await control({kind:"layout"}),true);
  }));
  files.addEventListener("change", () => run("导入 ESI", async () => {
    const chosen = [...files.files]; files.value = "";
    if (!chosen.length) return false;
    if (chosen.some(file => file.size > 16000000) || chosen.reduce((sum,file) => sum + file.size,0) > 32000000) throw Error("单个 ESI 最多 16 MB，合计最多 32 MB。");
    const next = await invoke("import_esi",{xml:await Promise.all(chosen.map(bytesToXml))});
    sdoResults.clear();
    setLayout(next,true);
  }));
  const selectDevice = index => run("切换 ESI", async () => {
    setLayout(await control({kind:"select",index}),true);
  });
  devices.addEventListener("change", () => selectDevice(Number(devices.value)));
  useDevice.addEventListener("click", () => selectDevice(0));
  stateButtons.forEach(btn => btn.addEventListener("click",() => run("切换 " + btn.textContent,async () => {
    const target = Number(btn.dataset.target);
    setLayout(await control({kind:"state",target}),target < 4);
    timeline = [];
  })));
  byId("live-arm").addEventListener("click", () => {
    if (confirm("解锁后允许向从站下发 PDO 输出。确认台架可以接收命令？")) run("解锁输出", () => control({kind:"arm",enabled:true}));
  });
  byId("live-lock").addEventListener("click", () => run("锁定并清零", () => control({kind:"arm",enabled:false})));
  byId("sdo-filter").addEventListener("input", buildSdoList);
  byId("hand-all").addEventListener("click", () => sendAxes([0,1,2,3,4,5]));
  byId("hand-group").addEventListener("change", scheduleRender);
  byId("hand-read").addEventListener("click", () => run("批量读取设备信息与配置", async () => {
    let readCount = 0, failed = 0, skipped = 0;
    for (const {entry} of handDictionary.rows.values()) {
      if (!(entry.read_states & frame.status.state)) { skipped++; continue; }
      try { await transferSdo(entry); readCount++; } catch { failed++; }
    }
    return "读取完毕 · 成功 " + readCount + " · 失败 " + failed + " · 跳过 " + skipped;
  }));
  for (const [id,write] of [["raw-read",false],["raw-write",true]]) {
    byId(id).addEventListener("click", () => {
      if (write && !confirm("确认执行手工 SDO 写入？")) return;
      run("手工 SDO " + (write ? "写入" : "读取"), async () => {
        const reply = await control({kind:"sdo",raw:true,index:addressInput("raw-index",65535),sub:addressInput("raw-sub",255),
          value:write ? byId("raw-input").value : null});
        byId("raw-result").textContent = rawHex(reply.value) + " · " + reply.message;
        return reply.message;
      });
    });
  }
  dcToggle.addEventListener("click", () => {
    dcSettings.hidden = !dcSettings.hidden;
    dcToggle.setAttribute("aria-expanded", String(!dcSettings.hidden));
  });
  byId("dc-mode").addEventListener("change", () => {
    const mode = byId("dc-mode").value;
    const cycle = mode ? layout.device.dc[Number(mode)].cycle_ns : 0;
    if (cycle > 0) byId("dc-hz").value = String(Math.min(1000000,1e9 / cycle));
    refreshControls(true);
  });
  byId("dc-apply").addEventListener("click", () => run("切到 PRE-OP 并应用 DC", async () => {
    const mode = byId("dc-mode").value;
    setLayout(await control({kind:"dc",mode:mode ? Number(mode) : null,hz:Number(byId("dc-hz").value)}),true);
    timeline = [];
  }));
  byId("trace-start").addEventListener("click", () => run("开始采样", () => control({kind:"trace",long:byId("trace-long").checked})));
  byId("trace-save").addEventListener("click", () => run("保存计时 CSV", async () => {
    const path = await invoke("save_trace");
    return path ? "已保存 " + path : "已取消保存。";
  }));
  document.querySelector(".raw-data").addEventListener("toggle", scheduleRender);
  window.addEventListener("resize", scheduleRender);

  async function poll() {
    try {
      const next = await invoke("telemetry",{after:timeline.at(-1)?.[0] ?? 0});
      if (next.status.cycles < (timeline.at(-1)?.[0] ?? 0) || !next.status.mapped) timeline = [];
      if (next.plot.length) timeline = timeline.concat(next.plot).slice(-2000);
      frame = next.status;
      scheduleRender();
    } catch (error) {
      if (!busy) setText(message,String(error));
    }
    setTimeout(poll,frame.connected ? 50 : 250);
  }

  buildSdo();
  scheduleRender();
  refreshAdapters();
  poll();
})();
