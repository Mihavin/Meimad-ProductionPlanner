"use strict";

const state = {
  etag: null,
  refreshSeconds: 15,
  timer: null,
  hasSnapshot: false,
  machineCount: 0
};

const byId = (id) => document.getElementById(id);

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function setServerStatus(status, description) {
  const indicator = byId("server-status");
  indicator.className = `server-status server-status-${status}`;
  indicator.setAttribute("aria-label", description);
  indicator.title = description;
}

function statusClass(code) {
  return ["current", "setup", "conflict", "downtime"].includes(code)
    ? code
    : "idle";
}

function renderMachine(machine) {
  const code = statusClass(String(machine.status?.code ?? "idle"));
  const number = escapeHtml(machine.number);
  const name = escapeHtml(machine.name);
  const label = escapeHtml(machine.status?.label ?? "Unknown");
  return `<article class="machine-card status-${code}" aria-label="${number} ${name}: ${label}">
    <div class="machine-identity">
      <span class="machine-number">${number}</span>
      <span class="machine-name">${name}</span>
    </div>
    <div class="machine-status">${label}</div>
  </article>`;
}

function fitGrid(machineCount) {
  const grid = byId("machine-grid");
  if (machineCount < 1) {
    grid.style.setProperty("--grid-columns", "1");
    grid.style.setProperty("--grid-rows", "1");
    grid.style.setProperty("--density", "1");
    return;
  }

  const width = Math.max(1, grid.clientWidth);
  const height = Math.max(1, grid.clientHeight);
  const preferredAspect = 1.65;
  let best = null;

  for (let columns = 1; columns <= machineCount; columns += 1) {
    const rows = Math.ceil(machineCount / columns);
    const cellAspect = (width / columns) / (height / rows);
    const emptyCells = (columns * rows) - machineCount;
    const score = Math.abs(Math.log(cellAspect / preferredAspect)) + (emptyCells * 0.025);
    if (!best || score < best.score) best = { columns, rows, score };
  }

  const cellWidth = width / best.columns;
  const cellHeight = height / best.rows;
  const density = Math.max(.48, Math.min(1.2, cellWidth / 360, cellHeight / 170));
  grid.style.setProperty("--grid-columns", String(best.columns));
  grid.style.setProperty("--grid-rows", String(best.rows));
  grid.style.setProperty("--density", density.toFixed(3));
}

function render(data) {
  const machines = Array.isArray(data.machines) ? data.machines : [];
  state.hasSnapshot = true;
  state.machineCount = machines.length;
  state.refreshSeconds = Math.max(5, Number(data.refreshAfterSeconds) || 15);
  byId("machine-grid").innerHTML = machines.length
    ? machines.map(renderMachine).join("")
    : `<div class="empty-state">No display-enabled machines</div>`;
  byId("machine-grid").setAttribute("aria-busy", "false");
  fitGrid(machines.length);
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
refresh();
