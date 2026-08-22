"use strict";

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

function renderMachine(machine) {
  const number = escapeHtml(machine.number);
  const name = escapeHtml(machine.name);
  const preview = machine.current?.previewUrl
    ? `<img class="job-preview" src="${escapeHtml(machine.current.previewUrl)}" alt="" loading="lazy" onerror="this.hidden=true">`
    : `<span class="job-preview placeholder" aria-hidden="true"></span>`;
  if (!machine.current) {
    return `<article class="machine-row idle" aria-label="${number} ${name}">
      <div class="machine-number">${number}</div><div class="machine-name">${name}</div>${preview}
      <div class="operation empty">No current operation</div></article>`;
  }

  const operation = machine.current;
  const progress = operation.progress || {};
  const percent = Number.isFinite(progress.completionPercent)
    ? Math.max(0, Math.min(100, progress.completionPercent)) : null;
  const progressStyle = percent === null ? "" : ` style="--progress:${percent}%"`;
  return `<article class="machine-row" aria-label="${number} ${name}">
    <div class="machine-number">${number}</div><div class="machine-name">${name}</div>${preview}
    <div class="operation">
      <div class="operation-title"><strong>${escapeHtml(operation.partNumber)}</strong> <span class="batch">Batch ${escapeHtml(operation.batchNumber)}</span> <span class="job-op">OP${escapeHtml(operation.operationNumber)}</span></div>
      <div class="operation-name">${escapeHtml(operation.operationName)}</div>
    </div>
    <div class="execution status-${escapeHtml(progress.statusCode || "waiting")}">
      <div class="execution-line"><span class="status-label">${escapeHtml(progress.statusLabel || "Waiting")}</span><span class="completion-label">${escapeHtml(progress.completionLabel || "Progress unavailable")}</span></div>
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
  const machines = Array.isArray(data.machines) ? data.machines : [];
  state.hasSnapshot = true;
  state.machineCount = machines.length;
  state.machineIds = machines.map((machine) => machine.machineId).filter(Boolean);
  state.refreshSeconds = Math.max(60, Number(data.refreshAfterSeconds) || 60);
  byId("machine-grid").innerHTML = machines.length
    ? machines.map(renderMachine).join("")
    : `<div class="empty-state">No display-enabled machines</div>`;
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
    setServerStatus("connected", "Connected — live updates active");
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
    setServerStatus("disconnected", "Live connection lost; showing stale status while reconnecting");
    state.reconnectTimer = setTimeout(connectLive, 2000);
  });
  socket.addEventListener("error", () => socket.close());
}

async function refresh() {
  clearTimeout(state.timer);
  setServerStatus("connecting", state.hasSnapshot ? "Refreshing machine status" : "Connecting");
  try {
    const headers = state.etag ? { "If-None-Match": state.etag } : {};
    const response = await fetch("/api/v1/tv-dashboard", { headers, cache: "no-cache" });
    if (response.status === 304) {
      setServerStatus("connected", "Connected");
    } else if (response.ok) {
      state.etag = response.headers.get("ETag");
      render(await response.json());
      setServerStatus("connected", "Connected");
    } else {
      throw new Error(`Request failed: ${response.status}`);
    }
  } catch (error) {
    setServerStatus("disconnected", "Disconnected; showing last received machine status");
    if (!state.hasSnapshot) {
      byId("machine-grid").innerHTML = `<div class="empty-state">Machine status unavailable</div>`;
      byId("machine-grid").setAttribute("aria-busy", "false");
    }
  } finally {
    state.timer = setTimeout(refresh, state.refreshSeconds * 1000);
  }
}

window.addEventListener("resize", () => fitGrid(state.machineCount));
refresh().finally(connectLive);
