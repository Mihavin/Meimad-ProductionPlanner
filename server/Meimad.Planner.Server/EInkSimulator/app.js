"use strict";

const state = {
  deviceId: localStorage.getItem("meimad-eink-device-id") || "",
  token: localStorage.getItem("meimad-eink-device-token") || "",
  versionEtag: null,
  manifest: null,
  pollTimer: null,
  imageUrl: null,
  realStatus: null,
  localRevision: 1
};

const byId = (id) => document.getElementById(id);
const escapeHtml = (value) => String(value ?? "")
  .replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;")
  .replaceAll('"', "&quot;").replaceAll("'", "&#039;");

function log(message) {
  byId("protocol-log").textContent = `${new Date().toLocaleTimeString()}  ${message}\n${byId("protocol-log").textContent}`.trim();
}

function headers(extra = {}) {
  return { ...extra, "Authorization": `Bearer ${state.token}` };
}

async function getJson(path, extraHeaders = {}) {
  const response = await fetch(path, { method: "GET", headers: headers(extraHeaders), cache: "no-cache" });
  if (!response.ok && response.status !== 304) {
    let message = `HTTP ${response.status}`;
    try { message = (await response.json()).error?.message || message; } catch { /* safe fallback */ }
    throw new Error(message);
  }
  return response.status === 304 ? { response, value: null } : { response, value: await response.json() };
}

async function postJson(path, body) {
  const response = await fetch(path, {
    method: "POST",
    headers: headers({ "Content-Type": "application/json" }),
    body: JSON.stringify(body)
  });
  if (!response.ok) {
    let message = `HTTP ${response.status}`;
    try { message = (await response.json()).error?.message || message; } catch { /* safe fallback */ }
    throw new Error(message);
  }
  return response.json();
}

function devicePath(suffix) {
  return `/api/v1/eink/devices/${encodeURIComponent(state.deviceId)}${suffix}`;
}

async function checkVersion(force) {
  const conditional = !force && state.versionEtag ? { "If-None-Match": state.versionEtag } : {};
  log("GET version (small change check)");
  const result = await getJson(devicePath("/version"), conditional);
  if (result.response.status === 304) {
    log("304 unchanged — no package or screen transfer needed");
    return false;
  }
  state.versionEtag = result.response.headers.get("ETag");
  byId("revision").textContent = `Revision ${result.value.machineScreenRevision}`;
  log(`Version changed: ${result.value.machineScreenRevision}`);
  return true;
}

async function loadAll(force = false) {
  clearTimeout(state.pollTimer);
  state.deviceId = byId("device-id").value.trim();
  state.token = byId("device-token").value.trim();
  if (!state.deviceId || !state.token) {
    setConnection("Device ID and token are required.", true);
    return;
  }
  localStorage.setItem("meimad-eink-device-id", state.deviceId);
  localStorage.setItem("meimad-eink-device-token", state.token);
  setConnection("Checking version…", false);
  try {
    const changed = await checkVersion(force);
    if (changed || force) {
      const [screen, time] = await Promise.all([
        getJson(devicePath("/machine-screen")),
        getJson(devicePath("/time-config"))
      ]);
      renderScreen(screen.value);
      renderTime(time.value);
      await loadManifest(screen.value.package);
      await loadPhysicalStatus();
    }
    setConnection("Connected • authorized read-only device", false, true);
  } catch (error) {
    setConnection(error.message, true);
    log(`ERROR ${error.message}`);
  } finally {
    schedulePoll();
  }
}

async function loadPhysicalStatus() {
  try {
    const result = await getJson(`/api/tablets/${encodeURIComponent(state.deviceId)}/status`);
    state.realStatus = result.value?.status || null;
    byId("send-to-qc").disabled = state.realStatus !== "IN_SETUP_RUN";
    log(`GET physical status - ${state.realStatus || "UNKNOWN"}`);
  } catch (error) {
    state.realStatus = null;
    byId("send-to-qc").disabled = true;
    log(`Physical status unavailable - ${error.message}`);
  }
}

function renderScreen(screen) {
  const machine = screen.machine;
  byId("machine-number").textContent = machine?.number || "UNASSIGNED";
  byId("machine-type").textContent = machine?.processType || "NO MACHINE";
  byId("last-update").textContent = `Updated ${new Date(screen.generatedAt).toLocaleString()}`;
  byId("revision").textContent = `Revision ${screen.machineScreenRevision}`;
  const status = byId("status-block");
  status.className = `status-block status-${screen.status.code}`;
  status.innerHTML = `<span class="status-symbol">${escapeHtml(screen.status.icon)}</span><strong>${escapeHtml(screen.status.label)}</strong>`;
  byId("current-job").innerHTML = screen.current
    ? `<div class="job-part">${escapeHtml(screen.current.partNumber)}</div>
       <div class="job-operation">${escapeHtml(screen.current.batchNumber)} • OP${escapeHtml(screen.current.operationNumber)} • ${escapeHtml(screen.current.operationName)}</div>
       <div class="job-detail">Quantity ${escapeHtml(screen.current.quantity)} • ${escapeHtml(screen.current.status)}${screen.current.projectedFinish ? ` • Finish ${escapeHtml(new Date(screen.current.projectedFinish).toLocaleString())}` : ""}</div>`
    : `<div class="empty-content">No current job.</div>`;
  byId("next-jobs").innerHTML = screen.next.length
    ? screen.next.map(job => `<li><strong>${escapeHtml(job.partNumber)} • ${escapeHtml(job.batchNumber)}</strong><span>OP${escapeHtml(job.operationNumber)} • ${escapeHtml(job.operationName)}</span></li>`).join("")
    : "<li>No next jobs</li>";
  const conflictStrip = byId("conflict-strip");
  conflictStrip.className = `conflict-strip${screen.conflicts.length ? " has-conflict" : ""}`;
  conflictStrip.textContent = screen.conflicts.length
    ? `▲ ${screen.conflicts[0].severity.toUpperCase()} • ${screen.conflicts[0].message}${screen.conflicts.length > 1 ? ` • +${screen.conflicts.length - 1} more` : ""}`
    : "✓ NO CALCULATED CONFLICTS";
  log("GET machine-screen — structured display data rendered");
}

function renderTime(config) {
  const days = config.workdays.map(day => day.slice(0, 3).toUpperCase()).join(" ");
  const window = config.shiftWindows[0];
  byId("time-config").textContent = `${days} • ${window.startsAtLocal}–${window.endsAtLocal} • check every ${config.pollIntervalSeconds}s`;
  state.pollSeconds = config.pollIntervalSeconds;
  log("GET time-config — automatic check window loaded");
}

async function loadManifest(packageLink) {
  state.manifest = null;
  if (!packageLink) {
    byId("package-id").textContent = "No package assigned";
    byId("package-revision").textContent = "—";
    byId("tool-cart").textContent = "—";
    byId("package-context").textContent = "No official work metadata.";
    byId("package-files").innerHTML = '<div class="empty-content">No official package for the current job.</div>';
    return;
  }
  log("GET package-manifest");
  const result = await getJson(devicePath("/package-manifest"));
  state.manifest = result.value;
  byId("package-id").textContent = result.value.packageId;
  byId("package-revision").textContent = result.value.revision;
  byId("tool-cart").textContent = result.value.toolCartId || "—";
  const metadata = result.value.metadata;
  byId("package-context").textContent = metadata
    ? `${metadata.machine.number} • ${metadata.part.partNumber} ${metadata.part.revision || ""} • ${metadata.batch.batchNumber} • OP${metadata.operation.operationNumber} ${metadata.operation.name}`
    : "Legacy package metadata unavailable";
  byId("package-files").innerHTML = result.value.files.length
    ? result.value.files.map(file => `<div class="file-row"><strong>${escapeHtml(file.logicalPath)}</strong><span>${escapeHtml(file.assetType)} • ${escapeHtml(file.mediaType)} • ${escapeHtml(file.byteLength)} bytes</span><button type="button" data-file-id="${escapeHtml(file.fileId)}">VIEW</button></div>`).join("")
    : '<div class="empty-content">Published manifest contains no files.</div>';
  document.querySelectorAll("[data-file-id]").forEach(button => {
    button.addEventListener("click", () => loadFile(button.dataset.fileId));
  });
}

async function loadFile(fileId) {
  const file = state.manifest?.files.find(value => value.fileId === fileId);
  if (!file) return;
  showPage("file");
  byId("file-name").textContent = `${file.logicalPath} • revision ${state.manifest.revision}`;
  byId("file-verification").textContent = "VERIFYING SHA-256…";
  byId("file-content").textContent = "Downloading authorized read-only file…";
  byId("file-content").hidden = false;
  byId("file-image").hidden = true;
  log(`GET ${file.downloadPath}`);
  try {
    const response = await fetch(file.downloadPath, { method: "GET", headers: headers(), cache: "no-cache" });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const bytes = await response.arrayBuffer();
    const digest = [...new Uint8Array(await crypto.subtle.digest("SHA-256", bytes))]
      .map(value => value.toString(16).padStart(2, "0")).join("");
    if (digest !== file.checksum.value) throw new Error("Downloaded checksum mismatch");
    byId("file-verification").textContent = "✓ SHA-256 VERIFIED • READ-ONLY";
    if (file.mediaType.startsWith("image/")) {
      if (state.imageUrl) URL.revokeObjectURL(state.imageUrl);
      state.imageUrl = URL.createObjectURL(new Blob([bytes], { type: file.mediaType }));
      byId("file-image").src = state.imageUrl;
      byId("file-image").hidden = false;
      byId("file-content").hidden = true;
    } else {
      byId("file-content").textContent = new TextDecoder("utf-8", { fatal: false }).decode(bytes);
    }
    log(`File checksum verified: ${digest}`);
  } catch (error) {
    byId("file-verification").textContent = "▲ FILE REJECTED";
    byId("file-content").textContent = error.message;
    log(`FILE REJECTED ${error.message}`);
  }
}

function setConnection(message, error, connected = false) {
  const element = byId("connection-state");
  element.textContent = message;
  element.className = `connection-state${error ? " error" : connected ? " connected" : ""}`;
}

function showPage(page) {
  document.querySelectorAll(".screen-page").forEach(element => element.classList.toggle("active-page", element.id === `${page}-page`));
  document.querySelectorAll(".tab").forEach(element => element.classList.toggle("active", element.dataset.page === page));
  byId("screen-title").textContent = page === "machine" ? "MACHINE PAGE" : page === "package" ? "SETUP PACKAGE" : "NC / TEXT VIEWER";
}

function ensureBatteryLabel() {
  let element = byId("battery-state");
  if (!element) {
    element = document.createElement("span");
    element.id = "battery-state";
    document.querySelector(".screen-meta").prepend(element);
  }
  return element;
}

function applyLocalScenario() {
  const workflow = byId("scenario-status").value;
  const verification = byId("scenario-verification").value;
  const offline = byId("scenario-offline").checked;
  const lowBattery = byId("scenario-low-battery").checked;
  const status = byId("status-block");
  const verificationBlock = byId("verification-block");
  const battery = ensureBatteryLabel();

  status.className = `status-block status-${workflow.toLowerCase()}`;
  status.innerHTML = `<span class="status-symbol">${workflow === "BLOCKED" ? "!" : "■"}</span><strong>${escapeHtml(workflow.replaceAll("_", " "))}</strong>`;
  verificationBlock.hidden = verification === "none";
  verificationBlock.className = `verification-block${verification === "failed" || verification === "expired" ? " failure" : ""}`;
  verificationBlock.textContent = verification === "code" ? "SETUP RESPONSE CODE: 042731"
    : verification === "failed" ? "VERIFICATION FAILED - CNC START REMAINS BLOCKED"
    : verification === "expired" ? "VERIFICATION EXPIRED - RUN OFFSET LOADER AGAIN" : "";
  battery.textContent = lowBattery ? "LOW BATTERY - REPLACE 3 AA" : "BATTERY OK";
  battery.className = lowBattery ? "battery-low" : "";
  setConnection(offline ? "OFFLINE - showing last-known-good content" : `Local ${workflow} scenario`, offline, !offline);
  byId("scenario-state").textContent = `LOCAL ONLY | ${workflow} | ${offline ? "SERVER OFFLINE" : "SERVER AVAILABLE"} | ${lowBattery ? "LOW BATTERY" : "BATTERY OK"}`;
  log(`LOCAL scenario ${workflow}${offline ? " offline" : ""}${lowBattery ? " low-battery" : ""}`);
}

async function sendToQc() {
  if (state.realStatus !== "IN_SETUP_RUN") return;
  byId("send-to-qc").disabled = true;
  try {
    const result = await postJson(`/api/tablets/${encodeURIComponent(state.deviceId)}/events`, { event_type: "SEND_TO_QC" });
    log(`POST SEND_TO_QC accepted${result.duplicate ? " (idempotent retry)" : ""}`);
    setConnection("SEND_TO_QC accepted; refreshing authoritative status", false, true);
    await loadPhysicalStatus();
  } catch (error) {
    setConnection(`SEND_TO_QC uncertain/rejected: ${error.message}`, true);
    log(`SEND_TO_QC ERROR ${error.message}`);
  }
}

function schedulePoll() {
  clearTimeout(state.pollTimer);
  const seconds = Math.max(30, Number(state.pollSeconds) || 300);
  state.pollTimer = setTimeout(() => loadAll(false), seconds * 1000);
}

byId("device-id").value = state.deviceId;
byId("device-token").value = state.token;
byId("connect").addEventListener("click", () => loadAll(true));
byId("apply-scenario").addEventListener("click", applyLocalScenario);
byId("change-revision").addEventListener("click", () => {
  state.localRevision += 1;
  byId("revision").textContent = `Revision LOCAL-${state.localRevision}`;
  byId("scenario-state").textContent = "LOCAL ONLY | NEW PACKAGE REVISION AVAILABLE | review before clearing local marks";
  log(`LOCAL revision changed to ${state.localRevision}`);
});
byId("send-to-qc").addEventListener("click", sendToQc);
document.querySelectorAll(".tab").forEach(tab => tab.addEventListener("click", () => showPage(tab.dataset.page)));
if (state.deviceId && state.token) loadAll(false);
