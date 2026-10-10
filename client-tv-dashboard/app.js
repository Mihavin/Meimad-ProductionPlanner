"use strict";

// Written for the browsers of factory TVs (Samsung Tizen ships Chromium 47-94): ES2015 only.
// No optional chaining, nullish coalescing, async/await, replaceAll, padStart, destructuring,
// default parameters, URL search-parameter objects or promise finalizers; the layout is sized here instead
// of with CSS custom properties, clamp() or grid.
const DASHBOARD_BUILD = "0.1.38";
const translations = {
  en: {
    machineStatus: "Machine status", language: "Language", waitingForStatus: "Waiting for machine status",
    noMachines: "No display-enabled machines", updateRequired: "Dashboard update required",
    statusUnavailable: "Machine status unavailable", online: "Online", offline: "Offline",
    noCurrentOperation: "No current operation", workOrder: "Work order", started: "In production", paused: "Paused",
    completed: "Completed", waiting: "Waiting", setup: "Setup", part: "Part", ofWorkOrder: "of work order",
    progressUnavailable: "Progress unavailable", connecting: "Connecting", refreshing: "Refreshing machine status",
    connected: "Connected", liveConnected: "Connected — live updates active",
    liveLost: "Live connection lost; showing stale status while reconnecting",
    disconnected: "Disconnected; showing last received machine status",
    disconnectedShort: "Disconnected",
    averageCycle: "Avg cycle", fullscreen: "Full screen", exitFullscreen: "Exit full screen",
    fullscreenUnavailable: "Full screen is not available in this browser; use the browser's own full-screen mode",
    groupAttention: "Attention", groupSetup: "Setup", groupProduction: "In production", groupIdle: "Done / waiting / idle",
    machinesCount: "machine(s)", colMachine: "Mach", colCnc: "CNC", colPart: "Part", colWorkOrder: "WO", colOperation: "Op",
    colOperationName: "Operation", colStatus: "Status", colParts: "Parts", colAverageCycle: "Avg cycle", colProgress: "Progress",
    sumProduction: "running", sumSetup: "setup", sumAttention: "attention", sumIdle: "idle"
  },
  he: {
    machineStatus: "מצב מכונות", language: "שפה", waitingForStatus: "ממתין לנתוני המכונות",
    noMachines: "אין מכונות המוגדרות להצגה", updateRequired: "נדרש עדכון ללוח המכונות",
    statusUnavailable: "נתוני המכונות אינם זמינים", online: "מחובר", offline: "לא מחובר",
    noCurrentOperation: "אין פעולה נוכחית", workOrder: 'פק"ע', started: "בייצור", paused: "מושהית",
    completed: "הושלמה", waiting: "ממתינה", setup: "כיוון", part: "חלק", ofWorkOrder: 'מהפק"ע',
    progressUnavailable: "נתוני התקדמות אינם זמינים", connecting: "מתחבר", refreshing: "מרענן את מצב המכונות",
    connected: "מחובר", liveConnected: "מחובר — עדכונים חיים פעילים",
    liveLost: "החיבור החי נותק; מוצגים נתונים אחרונים בזמן החיבור מחדש",
    disconnected: "מנותק; מוצגים נתוני המכונות האחרונים",
    disconnectedShort: "מנותק",
    averageCycle: "מחזור ממוצע", fullscreen: "מסך מלא", exitFullscreen: "יציאה ממסך מלא",
    fullscreenUnavailable: "מסך מלא אינו זמין בדפדפן זה; השתמשו במצב המסך המלא של הדפדפן",
    groupAttention: "דורש תשומת לב", groupSetup: "כיוון", groupProduction: "בייצור", groupIdle: "הושלם / ממתין / פנוי",
    machinesCount: "מכונות", colMachine: "מכונה", colCnc: "CNC", colPart: "חלק", colWorkOrder: 'פק"ע', colOperation: "פעולה",
    colOperationName: "תיאור הפעולה", colStatus: "מצב", colParts: "חלקים", colAverageCycle: "מחזור ממוצע", colProgress: "התקדמות",
    sumProduction: "בייצור", sumSetup: "בכיוון", sumAttention: "דורשות תשומת לב", sumIdle: "פנויות"
  },
  ru: {
    machineStatus: "Состояние станков", language: "Язык", waitingForStatus: "Ожидание данных о станках",
    noMachines: "Нет станков, включённых для отображения", updateRequired: "Требуется обновление панели",
    statusUnavailable: "Данные о станках недоступны", online: "В сети", offline: "Не в сети",
    noCurrentOperation: "Нет текущей операции", workOrder: "Заказ-наряд", started: "В производстве", paused: "Приостановлена",
    completed: "Завершена", waiting: "Ожидает", setup: "Наладка", part: "Деталь", ofWorkOrder: "заказ-наряда",
    progressUnavailable: "Данные о ходе недоступны", connecting: "Подключение", refreshing: "Обновление состояния станков",
    connected: "Подключено", liveConnected: "Подключено — оперативные обновления активны",
    liveLost: "Связь потеряна; показаны последние данные, выполняется переподключение",
    disconnected: "Отключено; показаны последние полученные данные",
    disconnectedShort: "Отключено",
    averageCycle: "Ср. цикл", fullscreen: "Во весь экран", exitFullscreen: "Выйти из полноэкранного режима",
    fullscreenUnavailable: "Полноэкранный режим недоступен в этом браузере; используйте полноэкранный режим самого браузера",
    groupAttention: "Требуют внимания", groupSetup: "Наладка", groupProduction: "В производстве", groupIdle: "Завершено / ожидает / свободен",
    machinesCount: "станков", colMachine: "Станок", colCnc: "ЧПУ", colPart: "Деталь", colWorkOrder: "З-Н", colOperation: "Оп.",
    colOperationName: "Операция", colStatus: "Состояние", colParts: "Детали", colAverageCycle: "Ср. цикл", colProgress: "Ход",
    sumProduction: "в работе", sumSetup: "наладка", sumAttention: "внимание", sumIdle: "свободны"
  }
};

function queryLanguage() {
  const match = /[?&]lang=([^&#]*)/i.exec(window.location.search || "");
  return match ? decodeURIComponent(match[1]).toLowerCase() : null;
}

function resolveLanguage() {
  const requested = queryLanguage();
  if (requested && translations[requested]) return requested;
  const browserLanguage = String((navigator.languages && navigator.languages[0]) || navigator.language || "en").toLowerCase();
  if (browserLanguage.indexOf("he") === 0) return "he";
  if (browserLanguage.indexOf("ru") === 0) return "ru";
  return "en";
}

const language = resolveLanguage();
const t = translations[language];

const state = {
  etag: null,
  refreshSeconds: 60,
  timer: null,
  liveRefreshTimer: null,
  socket: null,
  reconnectTimer: null,
  hintTimer: null,
  hasSnapshot: false,
  machineCount: 0,
  groupCount: 0,
  machineIds: [],
  clockTimer: null
};
const byId = (id) => document.getElementById(id);

function escapeHtml(value) {
  return String(value === null || value === undefined ? "" : value)
    .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;").replace(/'/g, "&#039;");
}

function pad2(value) {
  return (value < 10 ? "0" : "") + value;
}

// The Server connection: a coloured dot plus the state written next to it.
function setServerStatus(status, description) {
  const indicator = byId("server-status");
  indicator.className = `server-status server-status-${status}`;
  indicator.setAttribute("aria-label", description);
  indicator.title = description;
  const text = byId("server-status-text");
  if (text) {
    text.textContent = status === "connected" ? t.connected : status === "connecting" ? t.connecting : t.disconnectedShort;
    text.title = description;
  }
}

function applyLanguage() {
  document.documentElement.lang = language;
  document.documentElement.dir = language === "he" ? "rtl" : "ltr";
  document.title = `Meimad — ${t.machineStatus}`;
  byId("page-title").textContent = t.machineStatus;
  byId("language-nav").setAttribute("aria-label", t.language);
  Array.prototype.forEach.call(document.querySelectorAll("[data-language]"), (link) => {
    const active = link.getAttribute("data-language") === language;
    link.classList.toggle("active", active);
    if (active) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  });
  byId("machine-grid").innerHTML = `<div class="empty-state">${escapeHtml(t.waitingForStatus)}</div>`;
  setServerStatus("connecting", t.connecting);
  updateFullscreenButton();
  updateClock();
}

// The wall clock in the top bar (the TV's own time, to the minute).
function updateClock() {
  const clock = byId("clock");
  if (!clock) return;
  const now = new Date();
  clock.textContent = `${pad2(now.getHours())}:${pad2(now.getMinutes())}`;
  clearTimeout(state.clockTimer);
  state.clockTimer = setTimeout(updateClock, 60000 - (now.getSeconds() * 1000 + now.getMilliseconds()) + 50);
}

function progressStatus(progress) {
  return t[progress.statusCode] || t.waiting;
}

function clampPercent(value) {
  return Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : null;
}

// The status column: the state word, with the setup percentage while the Machine is being set up.
function progressLabel(progress) {
  if (progress.phase === "setup") {
    const percent = clampPercent(progress.completionPercent);
    const setupPercent = Number.isFinite(progress.setupPercent) ? progress.setupPercent : percent === null ? 0 : percent;
    return progress.statusCode === "paused" ? `${t.paused} \u00b7 ${t.setup} ${setupPercent}%` : `${t.setup} ${setupPercent}%`;
  }
  return progressStatus(progress);
}

// The parts column: "17 / 24" when both counts are known, the planned quantity alone during setup.
function partsLabel(progress) {
  if (progress.phase === "completed" && progress.plannedParts) return `${progress.plannedParts} / ${progress.plannedParts}`;
  if (Number.isFinite(progress.currentPart) && progress.plannedParts) return `${progress.currentPart} / ${progress.plannedParts}`;
  if (progress.plannedParts) return `0 / ${progress.plannedParts}`;
  return "\u2014";
}

// The progress column: the bar shows setup progress during setup, work-order completion otherwise;
// the number is always the work-order completion so the columns stay comparable.
function barPercent(progress) {
  const percent = clampPercent(progress.completionPercent);
  if (progress.phase === "setup") {
    const setupPercent = clampPercent(progress.setupPercent);
    return setupPercent === null ? percent : setupPercent;
  }
  return progress.phase === "completed" ? 100 : percent;
}

function completionLabel(progress) {
  if (progress.phase === "completed") return "100%";
  if (progress.phase === "setup") return t.setup.toLowerCase();
  const percent = clampPercent(progress.completionPercent);
  return percent === null ? "\u2014" : `${percent}%`;
}

// Which band a Machine belongs to. Attention first: a paused operation, or a current operation on a
// Machine whose CNC connection is down (its status may be stale). Then setup, production, and the
// rest (completed, waiting, idle).
function groupOf(machine) {
  if (!machine.current) return "idle";
  const progress = machine.current.progress || {};
  const online = !!(machine.connection && machine.connection.online === true);
  if (progress.statusCode === "paused" || !online) return "attention";
  if (progress.phase === "setup") return "setup";
  if (progress.phase === "production" && progress.statusCode === "started") return "production";
  return "idle";
}

const GROUPS = [
  { key: "attention", label: "groupAttention" },
  { key: "setup", label: "groupSetup" },
  { key: "production", label: "groupProduction" },
  { key: "idle", label: "groupIdle" }
];

function cycleAverageLabel(progress) {
  if (!Number.isFinite(progress.averageCycleSeconds) || !(progress.averageCycleSampleCount >= 1)) return "\u2014";
  const total = Math.max(0, Math.round(progress.averageCycleSeconds));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  return hours > 0
    ? `${hours}:${pad2(minutes)}:${pad2(seconds)}`
    : `${minutes}:${pad2(seconds)}`;
}

function renderMachine(machine, group) {
  const number = escapeHtml(machine.number);
  const name = escapeHtml(machine.name);
  const online = !!(machine.connection && machine.connection.online === true);
  const connectionState = online ? t.online : t.offline;
  // The CNC connection: a coloured dot plus the state written next to it.
  const connection = `<div class="cell machine-connection connection-${online ? "online" : "offline"}" title="${escapeHtml(connectionState)}"><span class="connection-dot" aria-hidden="true"></span><span class="connection-text">${escapeHtml(connectionState)}</span></div>`;
  const identity = `<div class="cell machine-identity" title="${name}"><div class="machine-number">${number}</div></div>`;
  if (!machine.current) {
    return `<article class="machine-row idle" aria-label="${number} ${name}">
      ${identity}${connection}
      <div class="cell operation-name">${escapeHtml(t.noCurrentOperation)} \u00b7 ${name}</div>
      <div class="cell status-label">${escapeHtml(t.waiting)}</div>
      <div class="cell part-count">\u2014</div><div class="cell cycle-average">\u2014</div>
      <div class="progress"><span class="completion-label">\u2014</span></div></article>`;
  }

  const operation = machine.current;
  const progress = operation.progress || {};
  const percent = barPercent(progress);
  const classes = `machine-row ${group} status-${escapeHtml(progress.statusCode || "waiting")}${progress.phase === "setup" ? " setup" : ""}`;
  return `<article class="${classes}" aria-label="${number} ${name}">
    ${identity}${connection}
    <div class="cell part-number" title="${escapeHtml(operation.partNumber)}">${escapeHtml(operation.partNumber)}</div>
    <div class="cell work-order" title="${escapeHtml(t.workOrder)}">${escapeHtml(operation.batchNumber)}</div>
    <div class="cell job-op">OP${escapeHtml(operation.operationNumber)}</div>
    <div class="cell operation-name" title="${escapeHtml(operation.operationName)}">${escapeHtml(operation.operationName)}</div>
    <div class="cell status-label">${escapeHtml(progressLabel(progress))}</div>
    <div class="cell part-count">${escapeHtml(partsLabel(progress))}</div>
    <div class="cell cycle-average" title="${escapeHtml(t.averageCycle)}">${escapeHtml(cycleAverageLabel(progress))}</div>
    <div class="progress"><span class="progress-track"><span style="width:${percent === null ? 0 : percent}%"></span></span><span class="completion-label">${escapeHtml(completionLabel(progress))}</span></div>
  </article>`;
}

function renderColumnHeader() {
  const cell = (cls, key) => `<div class="cell ${cls}">${escapeHtml(t[key])}</div>`;
  return `<div class="column-header" aria-hidden="true">${cell("machine-identity", "colMachine")}${cell("machine-connection", "colCnc")}${cell("part-number", "colPart")}${cell("work-order", "colWorkOrder")}${cell("job-op", "colOperation")}${cell("operation-name", "colOperationName")}${cell("status-label", "colStatus")}${cell("part-count", "colParts")}${cell("cycle-average", "colAverageCycle")}<div class="progress">${escapeHtml(t.colProgress)}</div></div>`;
}

function renderGroups(machines) {
  const grouped = {};
  GROUPS.forEach((group) => { grouped[group.key] = []; });
  machines.forEach((machine) => { grouped[groupOf(machine)].push(machine); });
  let html = renderColumnHeader();
  let groupCount = 0;
  GROUPS.forEach((group) => {
    const members = grouped[group.key];
    if (members.length === 0) return;
    groupCount++;
    html += `<div class="group-header group-${group.key}"><span class="group-swatch" aria-hidden="true"></span>${escapeHtml(t[group.label])}<span class="group-count">\u00b7 ${members.length} ${escapeHtml(t.machinesCount)}</span></div>`;
    html += members.map((machine) => renderMachine(machine, group.key)).join("");
  });
  renderSummary(grouped);
  return { html: html, groupCount: groupCount };
}

// Top-bar counts per band, each a coloured square with the count and the word next to it.
function renderSummary(grouped) {
  const summary = byId("summary");
  if (!summary) return;
  const item = (key, label) => `<span class="summary-item summary-${key}"><span class="summary-swatch" aria-hidden="true"></span>${grouped[key].length} ${escapeHtml(t[label])}</span>`;
  summary.innerHTML = item("production", "sumProduction") + item("setup", "sumSetup") + item("attention", "sumAttention") + item("idle", "sumIdle");
}

// The rows share the height left under the top bar, the column header and the group headers: the
// root font size scales the row text with the row height (rem units in the stylesheet) and never
// grows past what the width allows.
function fitGrid(machineCount, groupCount) {
  const header = document.querySelector(".topbar");
  const headerHeight = header ? header.offsetHeight : 0;
  const rows = Math.max(1, machineCount);
  const groups = groupCount === undefined ? state.groupCount : groupCount;
  // Column header ≈ 1.9 rem, each group header ≈ 1.8 rem, measured in row-height units below.
  const fixedRems = 1.9 + 1.8 * Math.max(0, groups);
  const available = window.innerHeight - headerHeight - 12;
  // rootSize * (rows / 0.42 + fixedRems) = available  =>  solve for the root size.
  const rootByHeight = available / (rows / 0.42 + fixedRems);
  const rootSize = Math.max(7, Math.min(26, rootByHeight, window.innerWidth / 78));
  document.documentElement.style.fontSize = `${rootSize.toFixed(2)}px`;
  state.machineCount = machineCount;
  state.groupCount = groups;
}

function render(data) {
  if (data.dashboardBuild && data.dashboardBuild !== DASHBOARD_BUILD) {
    // A newer page is on the Server: load it once. Never loop when the reload still returns this build.
    let reloadedFor = null;
    try { reloadedFor = window.sessionStorage.getItem("meimad-dashboard-reload"); } catch (error) { reloadedFor = null; }
    if (reloadedFor !== data.dashboardBuild) {
      try { window.sessionStorage.setItem("meimad-dashboard-reload", data.dashboardBuild); } catch (error) { /* storage unavailable */ }
      window.location.reload();
      return;
    }
    state.hasSnapshot = true;
    byId("machine-grid").innerHTML = `<div class="empty-state">${escapeHtml(t.updateRequired)}</div>`;
    byId("machine-grid").setAttribute("aria-busy", "false");
    return;
  }
  if (Number(data.schemaVersion) < 2) {
    state.hasSnapshot = true;
    byId("machine-grid").innerHTML = `<div class="empty-state">${escapeHtml(t.updateRequired)}</div>`;
    byId("machine-grid").setAttribute("aria-busy", "false");
    return;
  }
  const machines = Array.isArray(data.machines) ? data.machines : [];
  state.hasSnapshot = true;
  state.machineIds = machines.map((machine) => machine.machineId).filter(Boolean);
  // The Server decides the polling interval (TvDashboard:RefreshAfterSeconds, 5-300 s).
  state.refreshSeconds = Math.max(5, Number(data.refreshAfterSeconds) || 60);
  const groups = machines.length ? renderGroups(machines) : { html: `<div class="empty-state">${escapeHtml(t.noMachines)}</div>`, groupCount: 0 };
  fitGrid(machines.length, groups.groupCount);
  byId("machine-grid").innerHTML = groups.html;
  byId("machine-grid").setAttribute("aria-busy", "false");
  subscribeToVisibleMachines();
}

function liveUrl() {
  const scheme = window.location.protocol === "https:" ? "wss:" : "ws:";
  return `${scheme}//${window.location.host}/api/v1/machines/live`;
}

function subscribeToVisibleMachines() {
  if (!state.socket || state.socket.readyState !== WebSocket.OPEN || state.machineIds.length === 0) return;
  state.socket.send(JSON.stringify({ type: "subscribe", machineIds: state.machineIds }));
}

function scheduleLiveRefresh() {
  clearTimeout(state.liveRefreshTimer);
  state.liveRefreshTimer = setTimeout(refresh, 150);
}

function connectLive() {
  clearTimeout(state.reconnectTimer);
  if (state.socket && [WebSocket.OPEN, WebSocket.CONNECTING].indexOf(state.socket.readyState) !== -1) return;
  let socket;
  try {
    socket = new WebSocket(liveUrl());
  } catch (error) {
    state.reconnectTimer = setTimeout(connectLive, 5000);
    return;
  }
  state.socket = socket;
  socket.addEventListener("open", () => {
    setServerStatus("connected", t.liveConnected);
    subscribeToVisibleMachines();
    refresh();
  });
  socket.addEventListener("message", (event) => {
    try {
      const message = JSON.parse(event.data);
      if (["MachineSnapshotUpdated", "MachineConnectionChanged", "BenchStateChanged"].indexOf(message.type) !== -1) {
        scheduleLiveRefresh();
      }
    } catch (error) { /* Ignore malformed server messages and retain last-known-good content. */ }
  });
  socket.addEventListener("close", () => {
    setServerStatus("disconnected", t.liveLost);
    state.reconnectTimer = setTimeout(connectLive, 2000);
  });
  socket.addEventListener("error", () => socket.close());
}

function refresh() {
  clearTimeout(state.timer);
  setServerStatus("connecting", state.hasSnapshot ? t.refreshing : t.connecting);
  const headers = state.etag ? { "If-None-Match": state.etag } : {};
  return fetch("/api/v1/tv-dashboard", { headers: headers, cache: "no-cache" })
    .then((response) => {
      if (response.status === 304) {
        setServerStatus("connected", t.connected);
        return null;
      }
      if (!response.ok) throw new Error(`Request failed: ${response.status}`);
      state.etag = response.headers.get("ETag");
      return response.json().then((data) => {
        render(data);
        setServerStatus("connected", t.connected);
      });
    })
    .catch(() => {
      setServerStatus("disconnected", t.disconnected);
      if (!state.hasSnapshot) {
        byId("machine-grid").innerHTML = `<div class="empty-state">${escapeHtml(t.statusUnavailable)}</div>`;
        byId("machine-grid").setAttribute("aria-busy", "false");
      }
    })
    .then(() => {
      state.timer = setTimeout(refresh, state.refreshSeconds * 1000);
    });
}

// Full screen (the same as F11 in a desktop browser), with the vendor prefixes old TV browsers use.
function fullscreenElement() {
  return document.fullscreenElement || document.webkitFullscreenElement || document.mozFullScreenElement || null;
}

function updateFullscreenButton() {
  const button = byId("fullscreen-toggle");
  if (!button) return;
  const active = !!fullscreenElement();
  button.textContent = active ? t.exitFullscreen : t.fullscreen;
  button.title = active ? t.exitFullscreen : t.fullscreen;
  button.setAttribute("aria-pressed", active ? "true" : "false");
}

function showFullscreenHint() {
  const button = byId("fullscreen-toggle");
  if (!button) return;
  clearTimeout(state.hintTimer);
  button.textContent = t.fullscreenUnavailable;
  button.title = t.fullscreenUnavailable;
  state.hintTimer = setTimeout(updateFullscreenButton, 6000);
}

function toggleFullscreen() {
  const root = document.documentElement;
  if (fullscreenElement()) {
    const exit = document.exitFullscreen || document.webkitExitFullscreen || document.mozCancelFullScreen;
    if (exit) exit.call(document);
    return;
  }
  const request = root.requestFullscreen || root.webkitRequestFullscreen || root.mozRequestFullScreen;
  if (!request) {
    showFullscreenHint();
    return;
  }
  try {
    const result = request.call(root);
    if (result && typeof result.then === "function") result.then(null, showFullscreenHint);
  } catch (error) {
    showFullscreenHint();
  }
}

function onFullscreenChange() {
  updateFullscreenButton();
  fitGrid(state.machineCount);
}

byId("fullscreen-toggle").addEventListener("click", toggleFullscreen);
document.addEventListener("fullscreenchange", onFullscreenChange);
document.addEventListener("webkitfullscreenchange", onFullscreenChange);
window.addEventListener("resize", () => fitGrid(state.machineCount));
applyLanguage();
fitGrid(0, 0);
refresh().then(connectLive, connectLive);
