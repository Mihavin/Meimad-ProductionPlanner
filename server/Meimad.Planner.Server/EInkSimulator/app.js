"use strict";

const HOLD_MILLISECONDS = 1200;
const TOOLS_PER_PAGE = 3;
const FIRMWARE_VERSION = "0.1.0-mvp";

// Exact classic 5x7 GLCD glyphs used by TFT_eSPI font 1. Each character is
// five vertical columns plus the firmware renderer's one-column spacing cell.
// Source lineage: Adafruit_GFX/TFT_eSPI classic bitmap font (BSD licensed).
const GLCD_FONT = new Uint8Array([
  0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x5F,0x00,0x00,
  0x00,0x07,0x00,0x07,0x00, 0x14,0x7F,0x14,0x7F,0x14,
  0x24,0x2A,0x7F,0x2A,0x12, 0x23,0x13,0x08,0x64,0x62,
  0x36,0x49,0x56,0x20,0x50, 0x00,0x08,0x07,0x03,0x00,
  0x00,0x1C,0x22,0x41,0x00, 0x00,0x41,0x22,0x1C,0x00,
  0x2A,0x1C,0x7F,0x1C,0x2A, 0x08,0x08,0x3E,0x08,0x08,
  0x00,0x80,0x70,0x30,0x00, 0x08,0x08,0x08,0x08,0x08,
  0x00,0x00,0x60,0x60,0x00, 0x20,0x10,0x08,0x04,0x02,
  0x3E,0x51,0x49,0x45,0x3E, 0x00,0x42,0x7F,0x40,0x00,
  0x72,0x49,0x49,0x49,0x46, 0x21,0x41,0x49,0x4D,0x33,
  0x18,0x14,0x12,0x7F,0x10, 0x27,0x45,0x45,0x45,0x39,
  0x3C,0x4A,0x49,0x49,0x31, 0x41,0x21,0x11,0x09,0x07,
  0x36,0x49,0x49,0x49,0x36, 0x46,0x49,0x49,0x29,0x1E,
  0x00,0x00,0x14,0x00,0x00, 0x00,0x40,0x34,0x00,0x00,
  0x00,0x08,0x14,0x22,0x41, 0x14,0x14,0x14,0x14,0x14,
  0x00,0x41,0x22,0x14,0x08, 0x02,0x01,0x59,0x09,0x06,
  0x3E,0x41,0x5D,0x59,0x4E, 0x7C,0x12,0x11,0x12,0x7C,
  0x7F,0x49,0x49,0x49,0x36, 0x3E,0x41,0x41,0x41,0x22,
  0x7F,0x41,0x41,0x41,0x3E, 0x7F,0x49,0x49,0x49,0x41,
  0x7F,0x09,0x09,0x09,0x01, 0x3E,0x41,0x41,0x51,0x73,
  0x7F,0x08,0x08,0x08,0x7F, 0x00,0x41,0x7F,0x41,0x00,
  0x20,0x40,0x41,0x3F,0x01, 0x7F,0x08,0x14,0x22,0x41,
  0x7F,0x40,0x40,0x40,0x40, 0x7F,0x02,0x1C,0x02,0x7F,
  0x7F,0x04,0x08,0x10,0x7F, 0x3E,0x41,0x41,0x41,0x3E,
  0x7F,0x09,0x09,0x09,0x06, 0x3E,0x41,0x51,0x21,0x5E,
  0x7F,0x09,0x19,0x29,0x46, 0x26,0x49,0x49,0x49,0x32,
  0x03,0x01,0x7F,0x01,0x03, 0x3F,0x40,0x40,0x40,0x3F,
  0x1F,0x20,0x40,0x20,0x1F, 0x3F,0x40,0x38,0x40,0x3F,
  0x63,0x14,0x08,0x14,0x63, 0x03,0x04,0x78,0x04,0x03,
  0x61,0x59,0x49,0x4D,0x43, 0x00,0x7F,0x41,0x41,0x41,
  0x02,0x04,0x08,0x10,0x20, 0x00,0x41,0x41,0x41,0x7F,
  0x04,0x02,0x01,0x02,0x04, 0x40,0x40,0x40,0x40,0x40,
  0x00,0x03,0x07,0x08,0x00, 0x20,0x54,0x54,0x78,0x40,
  0x7F,0x28,0x44,0x44,0x38, 0x38,0x44,0x44,0x44,0x28,
  0x38,0x44,0x44,0x28,0x7F, 0x38,0x54,0x54,0x54,0x18,
  0x00,0x08,0x7E,0x09,0x02, 0x18,0xA4,0xA4,0x9C,0x78,
  0x7F,0x08,0x04,0x04,0x78, 0x00,0x44,0x7D,0x40,0x00,
  0x20,0x40,0x40,0x3D,0x00, 0x7F,0x10,0x28,0x44,0x00,
  0x00,0x41,0x7F,0x40,0x00, 0x7C,0x04,0x78,0x04,0x78,
  0x7C,0x08,0x04,0x04,0x78, 0x38,0x44,0x44,0x44,0x38,
  0xFC,0x18,0x24,0x24,0x18, 0x18,0x24,0x24,0x18,0xFC,
  0x7C,0x08,0x04,0x04,0x08, 0x48,0x54,0x54,0x54,0x24,
  0x04,0x04,0x3F,0x44,0x24, 0x3C,0x40,0x40,0x20,0x7C,
  0x1C,0x20,0x40,0x20,0x1C, 0x3C,0x40,0x30,0x40,0x3C,
  0x44,0x28,0x10,0x28,0x44, 0x4C,0x90,0x90,0x90,0x7C,
  0x44,0x64,0x54,0x4C,0x44, 0x00,0x08,0x36,0x41,0x00,
  0x00,0x00,0x77,0x00,0x00, 0x00,0x41,0x36,0x08,0x00,
  0x02,0x01,0x02,0x04,0x02
]);

const state = {
  hardwareId: localStorage.getItem("meimad-tablet-hardware-id") || "",
  token: sessionStorage.getItem("meimad-tablet-device-token") || "",
  wifiSsid: localStorage.getItem("meimad-tablet-wifi-ssid") || "",
  tabletId: "",
  status: null,
  screenModel: null,
  toolPage: 0,
  showingService: false,
  localFixture: false,
  lastSuccessfulContact: "UNAVAILABLE",
  lastHttpResult: "NOT CONNECTED",
  lastRefreshDuration: "NOT RECORDED"
};

const byId = id => document.getElementById(id);
const escapeHtml = value => String(value ?? "")
  .replaceAll("&", "&amp;")
  .replaceAll("<", "&lt;")
  .replaceAll(">", "&gt;")
  .replaceAll('"', "&quot;")
  .replaceAll("'", "&#039;");

function panelContext() {
  const context = byId("panel-canvas").getContext("2d", { alpha: false });
  context.imageSmoothingEnabled = false;
  return context;
}

function bitmapTextWidth(value, size) {
  return String(value ?? "").length * 6 * size;
}

function fitBitmapText(value, maximumWidth, size) {
  const text = String(value ?? "");
  if (bitmapTextWidth(text, size) <= maximumWidth) return text;
  let fitted = text;
  while (fitted.length > 0 && bitmapTextWidth(`${fitted}...`, size) > maximumWidth) {
    fitted = fitted.slice(0, -1);
  }
  return `${fitted}...`;
}

function drawBitmapText(context, value, x, y, size) {
  const text = String(value ?? "");
  context.fillStyle = "#000";
  for (let index = 0; index < text.length; index += 1) {
    let code = text.charCodeAt(index);
    if (code < 32 || code > 126) code = 63;
    const glyphOffset = (code - 32) * 5;
    for (let column = 0; column < 5; column += 1) {
      const bits = GLCD_FONT[glyphOffset + column];
      for (let row = 0; row < 8; row += 1) {
        if ((bits & (1 << row)) !== 0) {
          context.fillRect(x + (index * 6 + column) * size, y + row * size, size, size);
        }
      }
    }
  }
}

function drawBitmapRight(context, value, right, y, size) {
  drawBitmapText(context, value, right - bitmapTextWidth(value, size), y, size);
}

function drawHorizontal(context, x, y, width) {
  context.fillRect(x, y, width, 1);
}

function drawVertical(context, x, y, height) {
  context.fillRect(x, y, 1, height);
}

function drawOutline(context, x, y, width, height) {
  drawHorizontal(context, x, y, width);
  drawHorizontal(context, x, y + height - 1, width);
  drawVertical(context, x, y, height);
  drawVertical(context, x + width - 1, y, height);
}

function log(message) {
  const existing = byId("protocol-log").textContent === "No requests made."
    ? ""
    : byId("protocol-log").textContent;
  byId("protocol-log").textContent = `${new Date().toLocaleTimeString()}  ${message}\n${existing}`.trim();
}

function setAction(message) {
  byId("action-state").textContent = message;
}

function setConnection(message, error = false) {
  const element = byId("connection-state");
  element.textContent = message;
  element.className = `connection-state${error ? " error" : ""}`;
}

function batteryVoltage() {
  const value = Number.parseFloat(byId("battery-voltage").value);
  return Number.isFinite(value) && value > 0 ? value : null;
}

function requestHeaders(extra = {}) {
  const headers = { ...extra, Authorization: `Bearer ${state.token}`, "X-Meimad-Firmware-Version": FIRMWARE_VERSION };
  const voltage = batteryVoltage();
  if (voltage !== null) headers["X-Meimad-Battery-Voltage"] = voltage.toFixed(3);
  return headers;
}

async function getJson(path) {
  const response = await fetch(path, { method: "GET", headers: requestHeaders(), cache: "no-cache" });
  if (!response.ok) throw new Error(await readError(response));
  return response.json();
}

async function postJson(path, body) {
  const response = await fetch(path, {
    method: "POST",
    headers: requestHeaders({ "Content-Type": "application/json" }),
    body: JSON.stringify(body)
  });
  if (!response.ok) throw new Error(await readError(response));
  return response.json();
}

async function readError(response) {
  try {
    const body = await response.json();
    return body?.error?.message || `HTTP ${response.status}`;
  } catch {
    return `HTTP ${response.status}`;
  }
}

function readBenchConfiguration() {
  state.hardwareId = byId("hardware-id").value.trim();
  state.token = byId("device-token").value.trim();
  state.wifiSsid = byId("wifi-ssid").value.trim();
  if (!state.hardwareId || !state.token) throw new Error("Hardware MAC and device token are required.");
  localStorage.setItem("meimad-tablet-hardware-id", state.hardwareId);
  localStorage.setItem("meimad-tablet-wifi-ssid", state.wifiSsid);
  sessionStorage.setItem("meimad-tablet-device-token", state.token);
}

async function registrationPing() {
  log("GET /api/tablet/ping (physical firmware registration)");
  const result = await getJson(`/api/tablet/ping?hardwareId=${encodeURIComponent(state.hardwareId)}`);
  if (result?.status !== "ok" || !result?.tabletId) throw new Error("Malformed tablet registration response.");
  state.tabletId = String(result.tabletId);
  return state.tabletId;
}

async function requestStatus(reason) {
  if (!state.tabletId) throw new Error("Tablet is not registered.");
  log(`GET /api/tablets/${state.tabletId}/status (${reason})`);
  const value = await getJson(`/api/tablets/${encodeURIComponent(state.tabletId)}/status`);
  state.status = value;
  state.lastSuccessfulContact = new Date().toISOString().replace("T", " ").replace(/\.\d{3}Z$/, "Z");
  state.lastHttpResult = "STATUS HTTP 200";
  return value;
}

async function bootOrRefresh(reason = "external-reset") {
  const started = performance.now();
  state.localFixture = false;
  byId("scenario-state").textContent = "Live Server mode";
  try {
    readBenchConfiguration();
    setConnection("Connecting...");
    await registrationPing();
    const status = await requestStatus(reason);
    renderProduction(status);
    state.lastRefreshDuration = `${Math.max(1, Math.round(performance.now() - started))} ms (browser)`;
    setConnection(`Connected / tablet ${state.tabletId}`);
    setAction(`${reason === "physical-refresh-button" ? "D1 REFRESH" : "RESET / BOOT"}: Server status displayed.`);
  } catch (error) {
    state.lastHttpResult = error.message;
    handleContactFailure(error.message);
  }
}

function handleContactFailure(message) {
  setConnection(message, true);
  log(`CONTACT FAILED: ${message}`);
  setAction("Server contact failed. The E-Ink panel retains its last-known screen.");
  if (state.screenModel?.status === "IN_SETUP" && state.screenModel?.verification?.state === "WAITING_FOR_OPERATOR") {
    renderProduction(makeUnavailableModel());
    setAction("Server contact failed while a response code could be visible. Code cleared; setup remains blocked.");
  }
}

function statusText(status) {
  const labels = {
    READY_FOR_SETUP: "READY FOR SETUP",
    IN_SETUP: "IN SETUP",
    IN_SETUP_RUN: "IN SETUP RUN",
    IN_QC: "IN QUALITY CONTROL",
    READY_FOR_PRODUCTION: "READY FOR PRODUCTION",
    IN_PRODUCTION: "IN PRODUCTION",
    BLOCKED: "BLOCKED",
    UNKNOWN: "STATUS UNKNOWN"
  };
  return labels[status] || "STATUS UNKNOWN";
}

function verificationStateText(verification) {
  const labels = {
    WAITING_FOR_OPERATOR: "ENTER RESPONSE CODE",
    EXPIRED: "CODE EXPIRED",
    INVALIDATED: "SETUP CHANGED",
    UNAVAILABLE: "CODE UNAVAILABLE"
  };
  return labels[verification?.state] || "VERIFICATION UNAVAILABLE";
}

function normalizeModel(value) {
  return {
    tabletId: value?.tablet_id || state.tabletId || "UNREGISTERED",
    machine: value?.machine || { name: "MACHINE", number: "NOT CONFIRMED" },
    part: value?.part || { number: "NO PART", name: "NO ACTIVE RUN" },
    operation: value?.operation || { number: 0, name: "NO OPERATION" },
    status: String(value?.status || "UNKNOWN"),
    verification: value?.verification || null,
    diagnostics: value?.diagnostics || null,
    revision: value?.revision ?? "UNAVAILABLE",
    notice: value?.notice || "",
    tools: Array.isArray(value?.tools) ? value.tools.slice(0, 12) : [],
    lowBattery: value?.lowBattery ?? ((batteryVoltage() ?? 4.5) <= 3.30)
  };
}

function clearPanel(context) {
  context.fillStyle = "#fff";
  context.fillRect(0, 0, 800, 480);
  context.fillStyle = "#000";
}

function drawProductionCanvas(model) {
  const context = panelContext();
  clearPanel(context);
  const left = 24;
  const right = 776;
  const contentWidth = right - left;
  const machine = `${model.machine.name || "MACHINE"}  -  ${model.machine.number || "NOT CONFIRMED"}`;
  const tablet = String(model.tabletId).startsWith("T") ? String(model.tabletId) : `T${model.tabletId}`;

  drawBitmapText(context, fitBitmapText(machine, 610, 4), left, 18, 4);
  drawBitmapRight(context, tablet, right, 14, 2);
  if (model.lowBattery) drawBitmapRight(context, "LOW BATTERY", right, 46, 1);
  else if (state.localFixture) drawBitmapRight(context, "LAYOUT DEMO", right, 46, 1);
  drawHorizontal(context, left, 68, contentWidth);

  drawBitmapText(context, "PART", left, 82, 1);
  drawBitmapText(context, fitBitmapText(model.part.number || "NO PART", 340, 3), left, 102, 3);
  drawBitmapText(context, fitBitmapText(model.part.name || "NO ACTIVE RUN", 340, 2), left, 142, 2);
  drawVertical(context, 400, 80, 102);
  drawBitmapText(context, "OPERATION", 424, 82, 1);
  drawBitmapText(context, `OP${model.operation.number ?? 0}`, 424, 102, 3);
  drawBitmapText(context, fitBitmapText(model.operation.name || "NO OPERATION", 350, 2), 424, 142, 2);

  if (model.status === "IN_SETUP") {
    drawOutline(context, left, 190, contentWidth, 270);
    drawOutline(context, left + 1, 191, contentWidth - 2, 268);
    drawBitmapText(context, "SETUP VERIFICATION", 40, 206, 1);
    drawBitmapText(context, fitBitmapText(verificationStateText(model.verification), 690, 3), 40, 236, 3);
    const waiting = model.verification?.state === "WAITING_FOR_OPERATOR"
      && /^[0-9]{4,6}$/.test(String(model.verification?.response_code || ""));
    if (waiting) {
      const code = String(model.verification.response_code);
      drawBitmapText(context, code, left + (contentWidth - bitmapTextWidth(code, 7)) / 2, 292, 7);
    } else {
      drawBitmapText(context, "SETUP BLOCKED", 40, 306, 4);
    }
    drawHorizontal(context, 40, 388, 720);
    const instruction = waiting ? "TYPE THIS CODE AT THE CNC" : "PRESS REFRESH - DO NOT START";
    drawBitmapText(context, fitBitmapText(instruction, 700, 2), 40, 410, 2);
    return;
  }

  drawOutline(context, left, 190, contentWidth, 110);
  drawOutline(context, left + 1, 191, contentWidth - 2, 108);
  drawBitmapText(context, "STATUS", 40, 204, 1);
  if (model.notice) {
    const notice = fitBitmapText(model.notice, 520, 2);
    drawBitmapRight(context, notice, right - 16, 202, 2);
  }
  drawBitmapText(context, fitBitmapText(statusText(model.status), 710, 4), 40, 238, 4);

  const tools = model.tools || [];
  const pages = Math.max(1, Math.ceil(tools.length / TOOLS_PER_PAGE));
  const page = Math.max(0, Math.min(state.toolPage, pages - 1));
  drawBitmapText(context, "TOOLS", left, 316, 2);
  drawBitmapRight(context, `TOOLS ${page + 1} / ${pages}`, right, 316, 2);
  drawBitmapText(context, "TOOL", 34, 344, 1);
  drawBitmapText(context, "DESCRIPTION", 126, 344, 1);
  drawBitmapText(context, "OFFSET", 666, 344, 1);
  drawHorizontal(context, left, 360, contentWidth);
  drawVertical(context, 112, 338, 136);
  drawVertical(context, 650, 338, 136);

  const shown = tools.slice(page * TOOLS_PER_PAGE, (page + 1) * TOOLS_PER_PAGE);
  if (shown.length === 0) {
    drawBitmapText(context, "NO TOOL DATA AVAILABLE", 126, 378, 2);
    return;
  }
  shown.forEach((tool, row) => {
    const y = 370 + row * 38;
    drawBitmapText(context, fitBitmapText(tool.tool, 68, 2), 34, y, 2);
    drawBitmapText(context, fitBitmapText(tool.description, 500, 2), 126, y, 2);
    drawBitmapText(context, fitBitmapText(tool.offset, 92, 2), 666, y, 2);
    if (row < TOOLS_PER_PAGE - 1) drawHorizontal(context, left, y + 28, contentWidth);
  });
}

function drawServiceCanvas(leftFields, rightFields) {
  const context = panelContext();
  clearPanel(context);
  drawBitmapText(context, "TABLET SERVICE / DEBUG", 20, 14, 3);
  drawBitmapText(context, "HOLD D1 / REFRESH 1.2s TO OPEN", 568, 22, 1);
  drawHorizontal(context, 20, 50, 760);
  drawVertical(context, 398, 58, 402);
  const drawField = (label, value, x, y) => {
    drawBitmapText(context, label, x, y, 1);
    drawBitmapText(context, fitBitmapText(value || "UNAVAILABLE", 350, 2), x, y + 12, 2);
  };
  leftFields.forEach((entry, index) => drawField(entry[0], entry[1], 20, 60 + index * 46));
  rightFields.forEach((entry, index) => drawField(entry[0], entry[1], 420, 60 + index * 46));
}

function renderProduction(value) {
  const model = normalizeModel(value);
  state.screenModel = model;
  state.showingService = false;
  state.toolPage = Math.min(state.toolPage, Math.max(0, Math.ceil(model.tools.length / TOOLS_PER_PAGE) - 1));
  byId("production-screen").hidden = false;
  byId("service-screen").hidden = true;
  byId("machine-heading").textContent = `${model.machine.name || "MACHINE"}  -  ${model.machine.number || "NOT CONFIRMED"}`;
  byId("tablet-label").textContent = String(model.tabletId).startsWith("T") ? model.tabletId : `T${model.tabletId}`;
  byId("battery-warning").hidden = !model.lowBattery;
  byId("part-number").textContent = model.part.number || "NO PART";
  byId("part-name").textContent = model.part.name || "NO ACTIVE RUN";
  byId("operation-number").textContent = `OP${model.operation.number ?? 0}`;
  byId("operation-name").textContent = model.operation.name || "NO OPERATION";

  const verificationVisible = model.status === "IN_SETUP";
  byId("normal-region").hidden = verificationVisible;
  byId("verification-region").hidden = !verificationVisible;
  if (verificationVisible) renderVerification(model.verification);
  else renderNormalRegion(model);
  drawProductionCanvas(model);
  byId("eink-screen").setAttribute(
    "aria-label",
    `${model.machine.name || "Machine"} ${model.machine.number || ""}; part ${model.part.number || "none"}; operation ${model.operation.number ?? 0}; status ${statusText(model.status)}`);
}

function renderNormalRegion(model) {
  byId("workflow-status").textContent = statusText(model.status);
  byId("status-notice").textContent = model.notice;
  renderToolPage();
}

function renderVerification(verification) {
  const waiting = verification?.state === "WAITING_FOR_OPERATOR"
    && /^[0-9]{4,6}$/.test(String(verification?.response_code || ""));
  byId("verification-state").textContent = verificationStateText(verification);
  byId("verification-code").hidden = !waiting;
  byId("verification-code").textContent = waiting ? verification.response_code : "";
  byId("verification-blocked").hidden = waiting;
  byId("verification-instruction").textContent = waiting
    ? "TYPE THIS CODE AT THE CNC"
    : "PRESS REFRESH - DO NOT START";
}

function renderToolPage() {
  const tools = state.screenModel?.tools || [];
  const pages = Math.max(1, Math.ceil(tools.length / TOOLS_PER_PAGE));
  state.toolPage = Math.max(0, Math.min(state.toolPage, pages - 1));
  byId("tool-page-label").textContent = `TOOLS ${state.toolPage + 1} / ${pages}`;
  const shown = tools.slice(state.toolPage * TOOLS_PER_PAGE, (state.toolPage + 1) * TOOLS_PER_PAGE);
  byId("tool-rows").innerHTML = shown.length
    ? shown.map(tool => `<div class="tool-row"><span>${escapeHtml(tool.tool)}</span><span>${escapeHtml(tool.description)}</span><span>${escapeHtml(tool.offset)}</span></div>`).join("")
    : '<div class="no-tools">NO TOOL DATA AVAILABLE</div>';
}

function changeToolPage(direction) {
  if (state.showingService) {
    setAction(`${direction < 0 ? "D2" : "D4"} ignored: the retained Service/Debug screen remains visible.`);
    return;
  }
  const pages = Math.max(1, Math.ceil((state.screenModel?.tools?.length || 0) / TOOLS_PER_PAGE));
  const previous = state.toolPage;
  state.toolPage = Math.max(0, Math.min(state.toolPage + direction, pages - 1));
  if (state.toolPage === previous) {
    setAction(`${direction < 0 ? "D2 PREVIOUS" : "D4 NEXT"}: already at tool-page boundary ${state.toolPage + 1} / ${pages}.`);
  } else {
    renderToolPage();
    drawProductionCanvas(state.screenModel);
    setAction(`${direction < 0 ? "D2 PREVIOUS" : "D4 NEXT"}: tool page ${state.toolPage + 1} / ${pages}.`);
  }
}

function serviceField(label, value) {
  return `<div class="service-field"><span>${escapeHtml(label)}</span><strong>${escapeHtml(value || "UNAVAILABLE")}</strong></div>`;
}

async function showServiceScreen() {
  const started = performance.now();
  try {
    readBenchConfiguration();
    await registrationPing();
    await requestStatus("service-screen");
  } catch (error) {
    state.lastHttpResult = error.message;
    log(`SERVICE REFRESH FAILED: ${error.message}`);
  }
  const model = state.status ? normalizeModel(state.status) : (state.screenModel || normalizeModel(null));
  const voltage = batteryVoltage();
  const left = [
    ["TABLET ID", model.tabletId],
    ["HARDWARE MAC", state.hardwareId],
    ["FIRMWARE", FIRMWARE_VERSION],
    ["MACHINE BINDING", `${model.machine.number || ""} - ${model.machine.name || ""}`],
    ["WI-FI SSID", state.wifiSsid],
    ["IP / RSSI", "BROWSER / UNAVAILABLE"],
    ["BATTERY", voltage === null ? "UNAVAILABLE" : `${voltage.toFixed(3)} V`],
    ["WAKE REASON", "physical-button-ext1"]
  ];
  const right = [
    ["SERVER", location.origin],
    ["LAST SUCCESSFUL CONTACT", state.lastSuccessfulContact],
    ["LAST HTTP RESULT", state.lastHttpResult],
    ["WORKFLOW STATE", model.status],
    ["CURRENT REVISION", model.revision],
    ["LAST PANEL REFRESH", state.lastRefreshDuration],
    ["LAST CNC VERIFICATION", model.diagnostics?.verification_result || "NOT REPORTED"],
    ["PROTECTED MACRO VERSION", model.diagnostics?.protected_macro_version ?? "NOT REPORTED"]
  ];
  byId("service-columns").innerHTML = `<div class="service-column">${left.map(entry => serviceField(entry[0], entry[1])).join("")}</div><div class="service-column">${right.map(entry => serviceField(entry[0], entry[1])).join("")}</div>`;
  drawServiceCanvas(left, right);
  byId("eink-screen").setAttribute("aria-label", "Tablet Service and Debug screen");
  byId("production-screen").hidden = true;
  byId("service-screen").hidden = false;
  state.showingService = true;
  state.lastRefreshDuration = `${Math.max(1, Math.round(performance.now() - started))} ms (browser)`;
  setAction("D1 held 1.2 seconds: Service/Debug screen displayed after bounded Server contact.");
}

async function sendToQc() {
  try {
    readBenchConfiguration();
    await registrationPing();
    const current = await requestStatus("before-SEND_TO_QC");
    if (current?.status !== "IN_SETUP_RUN") {
      renderProduction(current);
      setAction(`D4 held 1.2 seconds: SEND_TO_QC ignored because Server status is ${current?.status || "UNKNOWN"}.`);
      return;
    }
    log(`POST /api/tablets/${state.tabletId}/events { event_type: SEND_TO_QC }`);
    await postJson(`/api/tablets/${encodeURIComponent(state.tabletId)}/events`, { event_type: "SEND_TO_QC" });
    const refreshed = await requestStatus("after-SEND_TO_QC");
    refreshed.notice = refreshed.status === "IN_QC" ? "SEND TO QC ACCEPTED" : "QC ACCEPTED - REFRESH PENDING";
    renderProduction(refreshed);
    setAction("D4 held 1.2 seconds: one scoped SEND_TO_QC was submitted, then status was refreshed.");
  } catch (error) {
    state.lastHttpResult = error.message;
    handleContactFailure(`SEND_TO_QC uncertain/rejected: ${error.message}`);
  }
}

function makeUnavailableModel() {
  return {
    tablet_id: state.tabletId || "UNREGISTERED",
    machine: { name: "MACHINE", number: "NOT CONFIRMED" },
    part: { number: "LAST CODE CLEARED", name: "SERVER CONTACT FAILED" },
    operation: { number: 0, name: "REFRESH REQUIRED" },
    status: "IN_SETUP",
    verification: { required: true, state: "UNAVAILABLE" },
    revision: "UNAVAILABLE"
  };
}

function applyLocalFixture() {
  const workflow = byId("scenario-status").value;
  const verificationState = byId("scenario-verification").value;
  const lowBattery = byId("scenario-low-battery").checked;
  const offline = byId("scenario-offline").checked;
  const tools = [
    { tool: "T01", description: "D10 End Mill", offset: "H01" },
    { tool: "T02", description: "D6 Ball Mill", offset: "H02" },
    { tool: "T03", description: "Probe", offset: "H99" },
    { tool: "T04", description: "D20 Face Mill", offset: "H04" },
    { tool: "T05", description: "D4 Drill", offset: "H05" },
    { tool: "T06", description: "D8 Reamer", offset: "H06" },
    { tool: "T07", description: "Chamfer Mill", offset: "H07" }
  ];
  const fixture = {
    tablet_id: state.tabletId || "3041",
    machine: { name: "DMG MORI", number: "M10" },
    part: { number: "P-12345", name: "Housing" },
    operation: { number: 30, name: "Finish Milling" },
    status: workflow,
    verification: workflow === "IN_SETUP" ? {
      required: true,
      state: verificationState,
      response_code: verificationState === "WAITING_FOR_OPERATOR" ? "0388" : null
    } : null,
    diagnostics: { verification_result: "LOCAL FIXTURE", protected_macro_version: 5 },
    revision: "DEMO",
    tools,
    lowBattery
  };
  state.localFixture = true;
  state.toolPage = 0;
  renderProduction(fixture);
  setConnection(offline ? "OFFLINE / retained local fixture" : "Local fixture / no Server mutation", offline);
  byId("scenario-state").textContent = `LOCAL ONLY / ${workflow} / ${lowBattery ? "LOW BATTERY" : "BATTERY OK"}`;
  setAction("Local firmware-layout fixture applied. No request was sent to the Server.");
  log(`LOCAL FIXTURE ${workflow}${offline ? " OFFLINE" : ""}${lowBattery ? " LOW BATTERY" : ""}`);
}

function bindHoldButton(element, shortAction, longAction) {
  let timer = null;
  let longTriggered = false;
  const start = event => {
    if (event.type === "keydown" && event.repeat) return;
    if (event.type === "keydown" && event.key !== " " && event.key !== "Enter") return;
    event.preventDefault();
    longTriggered = false;
    element.classList.add("pressing");
    timer = window.setTimeout(() => {
      longTriggered = true;
      timer = null;
      element.classList.remove("pressing");
      void longAction();
    }, HOLD_MILLISECONDS);
  };
  const finish = event => {
    if (event.type === "keyup" && event.key !== " " && event.key !== "Enter") return;
    if (timer !== null) window.clearTimeout(timer);
    timer = null;
    element.classList.remove("pressing");
    if (!longTriggered) void shortAction();
  };
  const cancel = () => {
    if (timer !== null) window.clearTimeout(timer);
    timer = null;
    element.classList.remove("pressing");
  };
  element.addEventListener("pointerdown", start);
  element.addEventListener("pointerup", finish);
  element.addEventListener("pointercancel", cancel);
  element.addEventListener("pointerleave", event => { if (event.buttons === 0) cancel(); });
  element.addEventListener("keydown", start);
  element.addEventListener("keyup", finish);
}

byId("hardware-id").value = state.hardwareId;
byId("device-token").value = state.token;
byId("wifi-ssid").value = state.wifiSsid;
byId("connect").addEventListener("click", () => void bootOrRefresh("external-reset"));
byId("apply-scenario").addEventListener("click", applyLocalFixture);
bindHoldButton(byId("button-d1"), () => bootOrRefresh("physical-refresh-button"), showServiceScreen);
bindHoldButton(byId("button-d4"), () => changeToolPage(1), sendToQc);
byId("button-d2").addEventListener("click", () => changeToolPage(-1));
byId("button-reset").addEventListener("click", () => void bootOrRefresh("external-reset"));

renderProduction(makeUnavailableModel());
