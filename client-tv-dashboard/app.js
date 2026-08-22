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

function renderMachine(machine) {
  const number = escapeHtml(machine.number);
  const name = escapeHtml(machine.name);
  const job = (value, prefix) => value
    ? `<div class="job ${prefix}"><span class="job-prefix">${prefix === "current" ? "Current" : prefix === "next" ? "Next" : "After"}:</span> ${escapeHtml(value.partNumber)} <span class="job-op">OP${escapeHtml(value.operationNumber)}</span> <span class="job-name">${escapeHtml(value.operationName)}</span></div>`
    : `<div class="job ${prefix} empty">${prefix === "current" ? "No current work" : "—"}</div>`;
  const preview = machine.current?.previewUrl
    ? `<img class="job-preview" src="${escapeHtml(machine.current.previewUrl)}" alt="" loading="lazy" onerror="this.hidden=true">`
    : `<span class="job-preview placeholder" aria-hidden="true"></span>`;
  return `<article class="machine-row" aria-label="${number} ${name}">
    <div class="machine-number">${number}</div>
    <div class="machine-name">${name}</div>
    ${preview}
    ${job(machine.current, "current")}
    ${job(machine.next, "next")}
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

  grid.style.setProperty("--machine-count", String(machineCount));
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
