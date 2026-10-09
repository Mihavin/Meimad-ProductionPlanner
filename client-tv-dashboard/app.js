"use strict";

// Written for the browsers of factory TVs (Samsung Tizen ships Chromium 47-94): ES2015 only.
// No optional chaining, nullish coalescing, async/await, replaceAll, padStart, destructuring,
// default parameters, URL search-parameter objects or promise finalizers; the layout is sized here instead
// of with CSS custom properties, clamp() or grid.
const DASHBOARD_BUILD = "0.1.37";
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
    fullscreenUnavailable: "Full screen is not available in this browser; use the browser's own full-screen mode"
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
    fullscreenUnavailable: "מסך מלא אינו זמין בדפדפן זה; השתמשו במצב המסך המלא של הדפדפן"
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
    fullscreenUnavailable: "Полноэкранный режим недоступен в этом браузере; используйте полноэкранный режим самого браузера"
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
  machineIds: []
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
}

function progressStatus(progress) {
  return t[progress.statusCode] || t.waiting;
}

function clampPercent(value) {
  return Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : null;
}

function progressLabel(progress) {
  const percent = clampPercent(progress.completionPercent);
  if (progress.phase === "setup") {
    const setupPercent = Number.isFinite(progress.setupPercent) ? progress.setupPercent : percent === null ? 0 : percent;
    return `${t.setup} ${setupPercent}%`;
  }
  if (progress.phase === "completed") {
    return `${t.part} ${progress.currentPart || progress.plannedParts}/${progress.plannedParts} | 100% ${t.ofWorkOrder}`;
  }
  if (progress.phase === "production" && Number.isFinite(progress.currentPart) && progress.plannedParts && percent !== null) {
    return `${t.part} ${progress.currentPart}/${progress.plannedParts} | ${percent}% ${t.ofWorkOrder}`;
  }
  return t.progressUnavailable;
}

function cycleAverageLabel(progress) {
  if (!Number.isFinite(progress.averageCycleSeconds) || !(progress.averageCycleSampleCount >= 1)) return "";
  const total = Math.max(0, Math.round(progress.averageCycleSeconds));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  const time = hours > 0
    ? `${hours}:${pad2(minutes)}:${pad2(seconds)}`
    : `${minutes}:${pad2(seconds)}`;
  return `${t.averageCycle} ${time} (${progress.averageCycleSampleCount})`;
}

function renderMachine(machine) {
  const number = escapeHtml(machine.number);
  const name = escapeHtml(machine.name);
  const online = !!(machine.connection && machine.connection.online === true);
  const connectionState = online ? t.online : t.offline;
  // The CNC connection: a coloured dot plus the state written next to the Machine number.
  const connection = `<span class="machine-connection connection-${online ? "online" : "offline"}" title="${escapeHtml(connectionState)}"><span class="connection-dot" aria-hidden="true"></span><span class="connection-text">${escapeHtml(connectionState)}</span></span>`;
  const identity = `<div class="machine-identity"><div class="machine-number" title="${number}">${number}${connection}</div><div class="machine-name" title="${name}">${name}</div></div>`;
  if (!machine.current) {
    return `<article class="machine-row idle" aria-label="${number} ${name}">
      ${identity}
      <div class="operation empty">${escapeHtml(t.noCurrentOperation)}</div></article>`;
  }

  const operation = machine.current;
  const progress = operation.progress || {};
  const percent = clampPercent(progress.completionPercent);
  const average = cycleAverageLabel(progress);
  return `<article class="machine-row" aria-label="${number} ${name}">
    ${identity}
    <div class="operation">
      <div class="operation-title"><strong>${escapeHtml(operation.partNumber)}</strong> <span class="work-order">${escapeHtml(t.workOrder)} ${escapeHtml(operation.batchNumber)}</span> <span class="job-op">OP${escapeHtml(operation.operationNumber)}</span></div>
      <div class="operation-name">${escapeHtml(operation.operationName)}</div>
    </div>
    <div class="execution status-${escapeHtml(progress.statusCode || "waiting")}">
      <div class="execution-line"><span class="status-label">${escapeHtml(progressStatus(progress))}</span><span class="completion-label">${escapeHtml(progressLabel(progress))}</span></div>
      ${average ? `<div class="cycle-average">${escapeHtml(average)}</div>` : ""}
      <div class="progress-track"><span style="width:${percent === null ? 0 : percent}%"></span></div>
    </div>
  </article>`;
}

// One band per Machine fills the viewport: the root font size scales the band text with the
// band height (rem units in the stylesheet) and never grows past what the width allows.
function fitGrid(machineCount) {
  const header = document.querySelector(".topbar");
  const headerHeight = header ? header.offsetHeight : 0;
  const rows = Math.max(1, machineCount);
  const rowHeight = Math.max(24, (window.innerHeight - headerHeight) / rows);
  const rootSize = Math.max(7, Math.min(22, rowHeight * 0.19, window.innerWidth / 90));
  document.documentElement.style.fontSize = `${rootSize.toFixed(2)}px`;
  state.machineCount = machineCount;
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
  fitGrid(machines.length);
  byId("machine-grid").innerHTML = machines.length
    ? machines.map(renderMachine).join("")
    : `<div class="empty-state">${escapeHtml(t.noMachines)}</div>`;
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
fitGrid(0);
refresh().then(connectLive, connectLive);
