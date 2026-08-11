"use strict";

const state = {
  etag: null,
  refreshSeconds: 15,
  freshness: "unknown",
  nextRefreshAt: Date.now(),
  timer: null,
  hasSnapshot: false
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

function formatLocal(value, options) {
  if (!value) return "—";
  return new Intl.DateTimeFormat(undefined, options).format(new Date(value));
}

function formatFinish(value) {
  return value ? `Finish ${formatLocal(value, { weekday: "short", hour: "2-digit", minute: "2-digit" })}` : "Finish not calculated";
}

function renderJob(job, emptyText) {
  if (!job) return `<div class="empty-value">${escapeHtml(emptyText)}</div>`;
  const urgency = job.urgent
    ? `<span class="urgent-tag">▲ URGENT • DUE ${escapeHtml(job.workFinishDate)}</span>`
    : "";
  return `
    <div class="job-part">${escapeHtml(job.partNumber)}</div>
    <div class="job-line">${escapeHtml(job.batchNumber)} • OP${escapeHtml(job.operationNumber)} • ${escapeHtml(job.operationName)}</div>
    <div class="job-meta">${escapeHtml(formatFinish(job.projectedFinish))}</div>
    ${urgency}`;
}

function renderConflicts(conflicts) {
  if (!conflicts.length) return `<div class="ok-text">✓ No calculated conflicts</div>`;
  const visible = conflicts.slice(0, 2).map((conflict) => `
    <div class="conflict-item ${conflict.severity === "blocking" ? "blocking" : ""}">
      <strong>${escapeHtml(conflict.severity)} • ${escapeHtml(conflict.code)}</strong>
      <span>${escapeHtml(conflict.message)}</span>
    </div>`).join("");
  const remainder = conflicts.length > 2
    ? `<div class="more-conflicts">+${conflicts.length - 2} more conflict${conflicts.length - 2 === 1 ? "" : "s"}</div>`
    : "";
  return `<div class="conflict-list">${visible}${remainder}</div>`;
}

function renderDowntime(downtime) {
  if (!downtime) return `<div class="ok-text">✓ Available</div>`;
  const heading = downtime.isCurrent
    ? `<div class="downtime-current">● DOWNTIME NOW</div>`
    : `<div class="downtime-upcoming">● UPCOMING</div>`;
  return `${heading}
    <div class="downtime-reason">${escapeHtml(downtime.reason)}</div>
    <div class="downtime-time">${escapeHtml(formatLocal(downtime.startsAt, { weekday: "short", hour: "2-digit", minute: "2-digit" }))} → ${escapeHtml(formatLocal(downtime.endsAt, { weekday: "short", hour: "2-digit", minute: "2-digit" }))}</div>`;
}

function renderMachine(machine) {
  const statusClass = ["current", "setup", "conflict", "downtime"].includes(machine.status.code)
    ? machine.status.code
    : "idle";
  return `<article class="machine-row status-${statusClass}" aria-label="Machine ${escapeHtml(machine.number)}">
    <div class="cell machine-cell">
      <div class="machine-number">${escapeHtml(machine.number)}</div>
      <div class="machine-name">${escapeHtml(machine.name)}</div>
      <div class="process-type">${escapeHtml(machine.processType)}</div>
    </div>
    <div class="cell status-cell">
      <div class="cell-label">Status</div>
      <div class="status-pill"><span class="status-icon" aria-hidden="true">${escapeHtml(machine.status.icon)}</span><span>${escapeHtml(machine.status.label)}</span></div>
    </div>
    <div class="cell current-cell">
      <div class="cell-label">Current job</div>
      ${renderJob(machine.current, "No current job")}
    </div>
    <div class="cell next-cell">
      <div class="cell-label">Next job</div>
      ${renderJob(machine.next, "No next job")}
    </div>
    <div class="cell conflicts-cell">
      <div class="cell-label">Conflicts • ${machine.conflicts.length}</div>
      ${renderConflicts(machine.conflicts)}
    </div>
    <div class="cell downtime-cell">
      <div class="cell-label">Downtime</div>
      ${renderDowntime(machine.downtime)}
    </div>
  </article>`;
}

function renderUrgent(batches) {
  const section = byId("urgent-section");
  section.hidden = batches.length === 0;
  byId("urgent-list").innerHTML = batches.slice(0, 8).map((batch) => `
    <div class="urgent-batch ${batch.isOverdue ? "overdue" : ""}">
      <strong>${escapeHtml(batch.partNumber)} • ${escapeHtml(batch.batchNumber)}</strong>
      <span>${batch.isOverdue ? "OVERDUE" : "DUE"} ${escapeHtml(batch.workFinishDate)}${batch.machineNumber ? ` • ${escapeHtml(batch.machineNumber)}` : " • UNASSIGNED"}</span>
    </div>`).join("");
}

function render(data) {
  state.hasSnapshot = true;
  state.refreshSeconds = Math.max(5, Number(data.refreshAfterSeconds) || 15);
  state.freshness = String(data.freshness || "unknown");
  byId("machine-count").textContent = data.summary.machineCount;
  byId("conflict-count").textContent = data.summary.criticalConflictCount;
  byId("urgent-count").textContent = data.summary.urgentBatchCount;
  byId("downtime-count").textContent = data.summary.downtimeMachineCount;
  byId("machine-list").innerHTML = data.machines.length
    ? data.machines.map(renderMachine).join("")
    : `<div class="empty-state">No display-enabled Machines are configured.</div>`;
  renderUrgent(data.urgentBatches);
  byId("generated-at").textContent = `Snapshot ${formatLocal(data.generatedAt, { year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit" })}`;
  byId("connection-banner").hidden = true;
}

async function refresh() {
  clearTimeout(state.timer);
  byId("refresh-state").textContent = "Refreshing…";
  try {
    const headers = state.etag ? { "If-None-Match": state.etag } : {};
    const response = await fetch("/api/v1/tv-dashboard", { headers, cache: "no-cache" });
    if (response.status === 304) {
      byId("connection-banner").hidden = true;
    } else if (response.ok) {
      state.etag = response.headers.get("ETag");
      render(await response.json());
    } else {
      throw new Error(`Server returned ${response.status}`);
    }
  } catch (error) {
    byId("connection-banner").hidden = false;
    if (!state.hasSnapshot) {
      byId("machine-list").innerHTML = `<div class="empty-state">Server data is unavailable. Retrying automatically…</div>`;
    }
  } finally {
    state.nextRefreshAt = Date.now() + state.refreshSeconds * 1000;
    state.timer = setTimeout(refresh, state.refreshSeconds * 1000);
  }
}

function updateClock() {
  const now = new Date();
  byId("clock").textContent = new Intl.DateTimeFormat(undefined, {
    hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false
  }).format(now);
  byId("date").textContent = new Intl.DateTimeFormat(undefined, {
    weekday: "long", year: "numeric", month: "long", day: "numeric"
  }).format(now);
  const remaining = Math.max(0, Math.ceil((state.nextRefreshAt - Date.now()) / 1000));
  byId("refresh-state").textContent = state.hasSnapshot
    ? `${state.freshness.toUpperCase()} • READ-ONLY • refresh in ${remaining}s`
    : "Connecting…";
}

setInterval(updateClock, 1000);
updateClock();
refresh();
