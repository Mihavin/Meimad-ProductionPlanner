"use strict";

const DASHBOARD_BUILD = "0.1.33";
const translations = {
  en: {
    machineStatus: "Machine status", language: "Language", waitingForStatus: "Waiting for machine status",
    noMachines: "No display-enabled machines", updateRequired: "Dashboard update required",
    statusUnavailable: "Machine status unavailable", noImage: "No image", noPicture: "No picture available",
    pictureUnavailable: "Picture unavailable", partPicture: "part picture", online: "Online", offline: "Offline",
    noCurrentOperation: "No current operation", batch: "Batch", started: "Started", paused: "Paused",
    completed: "Completed", waiting: "Waiting", setup: "Setup", part: "Part", ofBatch: "of Batch",
    progressUnavailable: "Progress unavailable", connecting: "Connecting", refreshing: "Refreshing machine status",
    connected: "Connected", liveConnected: "Connected — live updates active",
    liveLost: "Live connection lost; showing stale status while reconnecting",
    disconnected: "Disconnected; showing last received machine status"
  },
  he: {
    machineStatus: "מצב מכונות", language: "שפה", waitingForStatus: "ממתין לנתוני המכונות",
    noMachines: "אין מכונות המוגדרות להצגה", updateRequired: "נדרש עדכון ללוח המכונות",
    statusUnavailable: "נתוני המכונות אינם זמינים", noImage: "אין תמונה", noPicture: "אין תמונה זמינה",
    pictureUnavailable: "התמונה אינה זמינה", partPicture: "תמונת חלק", online: "מחובר", offline: "לא מחובר",
    noCurrentOperation: "אין פעולה נוכחית", batch: "אצווה", started: "בתהליך", paused: "מושהית",
    completed: "הושלמה", waiting: "ממתינה", setup: "הכנה", part: "חלק", ofBatch: "מהאצווה",
    progressUnavailable: "נתוני התקדמות אינם זמינים", connecting: "מתחבר", refreshing: "מרענן את מצב המכונות",
    connected: "מחובר", liveConnected: "מחובר — עדכונים חיים פעילים",
    liveLost: "החיבור החי נותק; מוצגים נתונים אחרונים בזמן החיבור מחדש",
    disconnected: "מנותק; מוצגים נתוני המכונות האחרונים"
  },
  ru: {
    machineStatus: "Состояние станков", language: "Язык", waitingForStatus: "Ожидание данных о станках",
    noMachines: "Нет станков, включённых для отображения", updateRequired: "Требуется обновление панели",
    statusUnavailable: "Данные о станках недоступны", noImage: "Нет изображения", noPicture: "Изображение отсутствует",
    pictureUnavailable: "Изображение недоступно", partPicture: "изображение детали", online: "В сети", offline: "Не в сети",
    noCurrentOperation: "Нет текущей операции", batch: "Партия", started: "Запущена", paused: "Приостановлена",
    completed: "Завершена", waiting: "Ожидание", setup: "Наладка", part: "Деталь", ofBatch: "партии",
    progressUnavailable: "Данные о ходе недоступны", connecting: "Подключение", refreshing: "Обновление состояния станков",
    connected: "Подключено", liveConnected: "Подключено — оперативные обновления активны",
    liveLost: "Связь потеряна; показаны последние данные, выполняется переподключение",
    disconnected: "Отключено; показаны последние полученные данные"
  }
};

function resolveLanguage() {
  const requested = new URLSearchParams(window.location.search).get("lang")?.toLowerCase();
  if (requested && translations[requested]) return requested;
  const browserLanguage = (navigator.languages?.[0] || navigator.language || "en").toLowerCase();
  if (browserLanguage.startsWith("he")) return "he";
  if (browserLanguage.startsWith("ru")) return "ru";
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
  hasSnapshot: false,
  machineCount: 0,
  machineIds: []
};
const byId = (id) => document.getElementById(id);

function escapeHtml(value) {
  return String(value ?? "").replaceAll("&", "&amp;").replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;").replaceAll('"', "&quot;").replaceAll("'", "&#039;");
}

function setServerStatus(status, description) {
  const indicator = byId("server-status");
  indicator.className = `server-status server-status-${status}`;
  indicator.setAttribute("aria-label", description);
  indicator.title = description;
}

function applyLanguage() {
  document.documentElement.lang = language;
  document.documentElement.dir = language === "he" ? "rtl" : "ltr";
  document.title = `Meimad — ${t.machineStatus}`;
  byId("page-title").textContent = t.machineStatus;
  byId("language-nav").setAttribute("aria-label", t.language);
  document.querySelectorAll("[data-language]").forEach((link) => {
    const active = link.dataset.language === language;
    link.classList.toggle("active", active);
    if (active) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  });
  byId("machine-grid").innerHTML = `<div class="empty-state">${escapeHtml(t.waitingForStatus)}</div>`;
  setServerStatus("connecting", t.connecting);
}

function previewUrl(value) {
  if (!value) return null;
  try {
    const url = new URL(value, `${window.location.origin}/`);
    return url.origin === window.location.origin ? url.href : null;
  } catch {
    return null;
  }
}

function renderPreview(job) {
  const url = previewUrl(job?.previewUrl);
  const label = escapeHtml(job?.partNumber || "part");
  if (!url) {
    return `<div class="preview-frame"><span class="job-preview placeholder" aria-label="${escapeHtml(t.noPicture)}">${escapeHtml(t.noImage)}</span></div>`;
  }
  return `<div class="preview-frame"><img class="job-preview" src="${escapeHtml(url)}" alt="${label} ${escapeHtml(t.partPicture)}" loading="eager" decoding="async" onerror="this.hidden=true;this.nextElementSibling.hidden=false"><span class="job-preview placeholder" aria-label="${escapeHtml(t.pictureUnavailable)}" hidden>${escapeHtml(t.noImage)}</span></div>`;
}

function progressStatus(progress) {
  return t[progress.statusCode] || t.waiting;
}

function progressLabel(progress) {
  const percent = Number.isFinite(progress.completionPercent)
    ? Math.max(0, Math.min(100, progress.completionPercent)) : null;
  if (progress.phase === "setup") {
    return `${t.setup} ${Number.isFinite(progress.setupPercent) ? progress.setupPercent : percent || 0}%`;
  }
  if (progress.phase === "completed") {
    return `${t.part} ${progress.currentPart || progress.plannedParts}/${progress.plannedParts} | 100% ${t.ofBatch}`;
  }
  if (progress.phase === "production" && progress.currentPart && progress.plannedParts && percent !== null) {
    return `${t.part} ${progress.currentPart}/${progress.plannedParts} | ${percent}% ${t.ofBatch}`;
  }
  return t.progressUnavailable;
}

function renderMachine(machine) {
  const number = escapeHtml(machine.number);
  const name = escapeHtml(machine.name);
  const preview = renderPreview(machine.current);
  const online = machine.connection?.online === true;
  const connectionState = online ? t.online : t.offline;
  const connection = `<div class="machine-connection connection-${online ? "online" : "offline"}" role="img" aria-label="${escapeHtml(connectionState)}" title="${escapeHtml(connectionState)}"><span aria-hidden="true"></span></div>`;
  const machineState = String(machine.machineStatus || "").trim();
  const telemetry = `<div class="machine-telemetry${machineState ? "" : " unavailable"}" title="MTConnect machine state">MT: ${escapeHtml(machineState || "—")}</div>`;
  if (!machine.current) {
    return `<article class="machine-row idle" aria-label="${number} ${name}">
      <div class="machine-number">${number}</div><div class="machine-name" title="${name}">${name}</div>${connection}${telemetry}${preview}
      <div class="operation empty">${escapeHtml(t.noCurrentOperation)}</div></article>`;
  }

  const operation = machine.current;
  const progress = operation.progress || {};
  const percent = Number.isFinite(progress.completionPercent)
    ? Math.max(0, Math.min(100, progress.completionPercent)) : null;
  const progressStyle = percent === null ? "" : ` style="--progress:${percent}%"`;
  return `<article class="machine-row" aria-label="${number} ${name}">
    <div class="machine-number" title="${number}">${number}</div><div class="machine-name" title="${name}">${name}</div>${connection}${telemetry}${preview}
    <div class="operation">
      <div class="operation-title"><strong>${escapeHtml(operation.partNumber)}</strong> <span class="batch">${escapeHtml(t.batch)} ${escapeHtml(operation.batchNumber)}</span> <span class="job-op">OP${escapeHtml(operation.operationNumber)}</span></div>
      <div class="operation-name">${escapeHtml(operation.operationName)}</div>
    </div>
    <div class="execution status-${escapeHtml(progress.statusCode || "waiting")}">
      <div class="execution-line"><span class="status-label">${escapeHtml(progressStatus(progress))}</span><span class="completion-label">${escapeHtml(progressLabel(progress))}</span></div>
      <div class="progress-track"${progressStyle}><span></span></div>
    </div>
  </article>`;
}

function fitGrid(machineCount) {
  const grid = byId("machine-grid");
  if (machineCount < 1) {
    grid.style.setProperty("--machine-count", "1");
    return;
  }
  grid.style.setProperty("--machine-count", String(machineCount));
}

function render(data) {
  if (data.dashboardBuild && data.dashboardBuild !== DASHBOARD_BUILD) {
    window.location.reload();
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
  state.machineCount = machines.length;
  state.machineIds = machines.map((machine) => machine.machineId).filter(Boolean);
  state.refreshSeconds = Math.max(60, Number(data.refreshAfterSeconds) || 60);
  byId("machine-grid").innerHTML = machines.length
    ? machines.map(renderMachine).join("")
    : `<div class="empty-state">${escapeHtml(t.noMachines)}</div>`;
  byId("machine-grid").setAttribute("aria-busy", "false");
  fitGrid(machines.length);
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
  if (state.socket && [WebSocket.OPEN, WebSocket.CONNECTING].includes(state.socket.readyState)) return;
  const socket = new WebSocket(liveUrl());
  state.socket = socket;
  socket.addEventListener("open", () => {
    setServerStatus("connected", t.liveConnected);
    subscribeToVisibleMachines();
    refresh();
  });
  socket.addEventListener("message", (event) => {
    try {
      const message = JSON.parse(event.data);
      if (["MachineSnapshotUpdated", "MachineConnectionChanged", "BenchStateChanged"].includes(message.type)) {
        scheduleLiveRefresh();
      }
    } catch { /* Ignore malformed server messages and retain last-known-good content. */ }
  });
  socket.addEventListener("close", () => {
    setServerStatus("disconnected", t.liveLost);
    state.reconnectTimer = setTimeout(connectLive, 2000);
  });
  socket.addEventListener("error", () => socket.close());
}

async function refresh() {
  clearTimeout(state.timer);
  setServerStatus("connecting", state.hasSnapshot ? t.refreshing : t.connecting);
  try {
    const headers = state.etag ? { "If-None-Match": state.etag } : {};
    const response = await fetch("/api/v1/tv-dashboard", { headers, cache: "no-cache" });
    if (response.status === 304) {
      setServerStatus("connected", t.connected);
    } else if (response.ok) {
      state.etag = response.headers.get("ETag");
      render(await response.json());
      setServerStatus("connected", t.connected);
    } else {
      throw new Error(`Request failed: ${response.status}`);
    }
  } catch (error) {
    setServerStatus("disconnected", t.disconnected);
    if (!state.hasSnapshot) {
      byId("machine-grid").innerHTML = `<div class="empty-state">${escapeHtml(t.statusUnavailable)}</div>`;
      byId("machine-grid").setAttribute("aria-busy", "false");
    }
  } finally {
    state.timer = setTimeout(refresh, state.refreshSeconds * 1000);
  }
}

window.addEventListener("resize", () => fitGrid(state.machineCount));
applyLanguage();
refresh().finally(connectLive);
