"use strict";

// Preview page logic shared by the VS Code webview and the desktop app. The
// 3D drawing lives in viewer3d.js (an ES module loaded with three.js); this
// script owns the toolbar, sidebar, playback timeline and host messaging.

const vscode = acquireVsCodeApi();
const canvas = document.getElementById("toolpathCanvas");
const overlayCanvas = document.getElementById("toolpathOverlay");
const viewerStatus = document.getElementById("viewerStatus");
const tooltip = document.getElementById("tooltip");
const fitButton = document.getElementById("fitButton");
const reloadParameters = document.getElementById("reloadParameters");
const runButton = document.getElementById("runButton");
const pauseButton = document.getElementById("pauseButton");
const restartButton = document.getElementById("restartButton");
const speedSlider = document.getElementById("speedSlider");
const speedValue = document.getElementById("speedValue");
const playbackHud = document.getElementById("playbackHud");
const showRapids = document.getElementById("showRapids");
const showG30 = document.getElementById("showG30");
const showG30Text = document.getElementById("showG30Text");
const showCompensated = document.getElementById("showCompensated");
const showPolyarc = document.getElementById("showPolyarc");
const showPolyarcLabel = document.getElementById("showPolyarcLabel");
const showExtruder = document.getElementById("showExtruder");
const showTool = document.getElementById("showTool");
const toolFilter = document.getElementById("toolFilter");
const machineSelect = document.getElementById("machineSelect");
const frameSelect = document.getElementById("frameSelect");
const frameLabel = document.getElementById("frameLabel");
const viewButtons = [...document.querySelectorAll("[data-view]")];
const g30XInput = document.getElementById("g30X");
const g30ZInput = document.getElementById("g30Z");
const g30XLabel = document.getElementById("g30XLabel");
const g30ZLabel = document.getElementById("g30ZLabel");
const initialMacrosInput = document.getElementById("initialMacros");
const eyebrow = document.getElementById("previewEyebrow");

let model = {
  kind: "lathe",
  segments: [],
  compensatedSegments: [],
  errors: [],
  tools: [],
  toolDefinitions: [],
  toolTable: {},
  warnings: [],
  meta: {},
  statsRows: [],
  machineRows: [],
  stock: undefined
};
let polyarcDisplaySegments = [];
let selectedLine = 1;
let selectedExecutionIndex;
let playbackTimeline = [];
let compensatedSegmentsByExecution = new Map();
let playbackLine;
let playbackFrame;
let viewer;
let segmentStyles = {};
let hoverFrame;
let lastHoverEvent;
let playback = {
  state: "idle",
  elapsedSeconds: 0,
  totalSeconds: 0,
  speed: Number(speedSlider.value) || 10,
  lastTimestamp: undefined
};

// ----- 3D viewer bootstrap ------------------------------------------------------

function moduleUrl(name, fallback) {
  const value = document.body.dataset[name];
  return value || new URL(fallback, document.baseURI).href;
}

function setViewerStatus(message, isError = false) {
  if (!viewerStatus) return;
  viewerStatus.textContent = message || "";
  viewerStatus.hidden = !message;
  viewerStatus.classList.toggle("error", isError);
}

async function loadViewer() {
  setViewerStatus("Loading 3D view…");
  try {
    const [THREE, viewerModule] = await Promise.all([
      import(moduleUrl("threeModule", "../node_modules/three/build/three.module.js")),
      import(moduleUrl("viewerModule", "../media/viewer3d.js"))
    ]);
    segmentStyles = viewerModule.SEGMENT_STYLES;
    viewer = viewerModule.createToolpathViewer({
      THREE,
      kinematics: window.CncKinematics,
      canvas,
      overlay: overlayCanvas
    });
    setViewerStatus("");
    applyModelToViewer({ preserveView: false });
    populateLegend();
  } catch (error) {
    console.error(error);
    setViewerStatus(`The 3D view could not start: ${error.message}`, true);
  }
}

function currentFilters() {
  return {
    tool: toolFilter.value,
    showRapids: showRapids.checked,
    showReturns: showG30.checked,
    showCompensated: showCompensated.checked,
    showPolyarc: showPolyarc.checked && !showPolyarc.disabled,
    showNonCutting: showExtruder.checked
  };
}

function applyModelToViewer({ preserveView }) {
  if (!viewer) return;
  viewer.setFilters(currentFilters());
  viewer.setModel(model, { polyarcSegments: polyarcDisplaySegments, preserveView });
  viewer.setOptions({ showTool: showTool.checked });
  viewer.setSelection(selectedLine, selectedExecutionIndex);
  if (model.kind === "mill" && frameSelect.value === "machine") {
    viewer.setFrame("machine");
  }
  updateToolPose();
  updateViewButtons();
}

function refreshFilters() {
  if (!viewer) return;
  viewer.setFilters(currentFilters());
  viewer.setSelection(selectedLine, selectedExecutionIndex);
  syncViewerPlayback();
  viewer.fit();
}

// ----- geometry helpers -----------------------------------------------------------

function pointDistance(first, second) {
  if (model.kind === "lathe") {
    return Math.hypot(second.z - first.z, (second.x - first.x) / 2);
  }
  return Math.hypot(second.x - first.x, second.y - first.y, second.z - first.z);
}

function interpolatePoint(first, second, fraction) {
  const point = {
    x: first.x + (second.x - first.x) * fraction,
    z: first.z + (second.z - first.z) * fraction
  };
  if (Number.isFinite(first.y) || Number.isFinite(second.y)) {
    point.y = (first.y || 0) + ((second.y || 0) - (first.y || 0)) * fraction;
  }
  return point;
}

function partialSegment(segment, fraction) {
  const clamped = Math.max(0, Math.min(1, fraction));
  if (clamped >= 1 || segment.points.length < 2) {
    return segment;
  }
  let total = 0;
  for (let index = 1; index < segment.points.length; index += 1) {
    total += pointDistance(segment.points[index - 1], segment.points[index]);
  }
  if (total <= 1e-12) {
    return { ...segment, points: [segment.start, segment.start], end: segment.start };
  }
  const target = total * clamped;
  const points = [segment.points[0]];
  let travelled = 0;
  for (let index = 1; index < segment.points.length; index += 1) {
    const previous = segment.points[index - 1];
    const point = segment.points[index];
    const distance = pointDistance(previous, point);
    if (travelled + distance < target - 1e-12) {
      points.push(point);
      travelled += distance;
      continue;
    }
    points.push(interpolatePoint(previous, point, distance > 1e-12 ? (target - travelled) / distance : 0));
    break;
  }
  return { ...segment, points, end: points[points.length - 1] };
}

function sampledPolyarcPoints(segment) {
  if (segment.type !== "arc") return [segment.start, segment.end];
  const sweep = Number(segment.sweepRadians);
  const radius = Number(segment.radius);
  if (!Number.isFinite(sweep) || !Number.isFinite(radius) || radius <= 0) {
    return [segment.start, segment.end];
  }
  const startAngle = Math.atan2(segment.start.r - segment.center.r, segment.start.z - segment.center.z);
  const divisions = Math.max(2, Math.min(256, Math.ceil(Math.abs(sweep) / (Math.PI / 48))));
  const points = [];
  for (let index = 0; index <= divisions; index += 1) {
    if (index === divisions) {
      points.push(segment.end);
      continue;
    }
    const angle = startAngle + sweep * index / divisions;
    const radial = segment.center.r + radius * Math.sin(angle);
    points.push({ z: segment.center.z + radius * Math.cos(angle), r: radial, x: radial * 2 });
  }
  return points;
}

function rebuildPolyarcDisplaySegments() {
  const overlay = model.polyarcOverlay;
  polyarcDisplaySegments = model.kind === "lathe"
    ? (overlay?.profiles || []).flatMap((profile) =>
      [...(profile.endCaps || []), ...(profile.segments || [])].map((segment) => ({
        kind: profile.kind === "internal" ? "internal" : "outside",
        points: sampledPolyarcPoints(segment)
      })))
    : [];
  const available = polyarcDisplaySegments.length > 0;
  showPolyarc.disabled = !available;
  showPolyarcLabel.hidden = model.kind !== "lathe";
  showPolyarcLabel.title = available
    ? `${overlay.fileName} · ${overlay.profileCount} profiles · ${overlay.segmentCount} segments` +
      (overlay.repairedArcCount ? ` · ${overlay.repairedArcCount} invalid arc rendered as lines` : "")
    : overlay?.error || "No project polyarc file loaded";
  showPolyarc.dataset.source = available ? overlay.fileName : "";
  showPolyarc.dataset.profileCount = available ? String(overlay.profileCount) : "0";
}

// ----- lathe tool nose (imaginary tip convention matches the parser) ---------------

function toolTipDirection(tip) {
  return {
    1: { z: 1, r: 1 },
    2: { z: -1, r: 1 },
    3: { z: -1, r: -1 },
    4: { z: 1, r: -1 },
    5: { z: 1, r: 0 },
    6: { z: 0, r: 1 },
    7: { z: -1, r: 0 },
    8: { z: 0, r: -1 }
  }[tip];
}

function imaginaryToolCenter(nosePoint, radius, tip) {
  const direction = toolTipDirection(tip);
  if (!direction || !Number.isFinite(radius) || radius <= 0) return { ...nosePoint };
  return { z: nosePoint.z - direction.z * radius, x: nosePoint.x - direction.r * radius * 2 };
}

function imaginaryNosePoint(center, radius, tip) {
  const direction = toolTipDirection(tip);
  if (!direction || !Number.isFinite(radius) || radius <= 0) return { ...center };
  return { z: center.z + direction.z * radius, x: center.x + direction.r * radius * 2 };
}

function compensatedToolCenter(segment, fraction) {
  const executionIndex = Number(segment?.executionIndex);
  if (!Number.isFinite(executionIndex)) return undefined;
  const candidates = compensatedSegmentsByExecution.get(executionIndex) || [];
  const compensated =
    candidates.find((candidate) => candidate.sourceKind === segment.kind) ||
    candidates.find((candidate) => candidate.compensationPhase === "cancel") ||
    candidates.find((candidate) => candidate.compensationPhase === "startup");
  return compensated ? partialSegment(compensated, fraction).end : undefined;
}

function latheToolPose(segment, point, fraction = 1) {
  if (!segment || !Number.isFinite(point?.x) || !Number.isFinite(point?.z)) return undefined;
  const radius = Number(segment.toolRadius);
  const tip = Number(segment.toolTip);
  const compensatedCenter = radius > 0 ? compensatedToolCenter(segment, fraction) : undefined;
  const center = compensatedCenter || imaginaryToolCenter(point, radius, tip);
  return {
    center,
    nosePoint: compensatedCenter ? imaginaryNosePoint(center, radius, tip) : point,
    radius: Number.isFinite(radius) && radius > 0 ? radius : 0
  };
}

function millToolPose(segment, point, fraction = 1) {
  if (!segment || !point) return undefined;
  const rotary = segment.rotary || {};
  const at = (start, end) => (Number(start) || 0) + ((Number(end) || 0) - (Number(start) || 0)) * fraction;
  return {
    tip: { x: point.x, y: point.y, z: point.z },
    rotary: { A: at(rotary.a0, rotary.a1), B: at(rotary.b0, rotary.b1), C: at(rotary.c0, rotary.c1) },
    radius: Number(segment.toolRadius) || 0,
    length: Number(segment.toolLength) || 0,
    type: segment.toolType
  };
}

function toolPose(segment, point, fraction) {
  return model.kind === "lathe" ? latheToolPose(segment, point, fraction) : millToolPose(segment, point, fraction);
}

function selectedToolPose() {
  const candidates = (model.segments || []).filter((segment) => segment.line === selectedLine);
  const segment = Number.isFinite(selectedExecutionIndex)
    ? candidates.find((candidate) => candidate.executionIndex === selectedExecutionIndex)
    : candidates[candidates.length - 1];
  return segment ? toolPose(segment, segment.end, 1) : undefined;
}

// ----- playback ---------------------------------------------------------------------

function playbackPosition() {
  if (playback.state === "idle") return undefined;
  let completedExecutionIndex = -1;
  for (const entry of playbackTimeline) {
    if (entry.endSeconds <= playback.elapsedSeconds + 1e-9) {
      completedExecutionIndex = entry.segment.executionIndex;
      continue;
    }
    const fraction = entry.duration > 0 ? (playback.elapsedSeconds - entry.startSeconds) / entry.duration : 1;
    return { entry, fraction, completedExecutionIndex };
  }
  return { entry: undefined, fraction: 1, completedExecutionIndex };
}

function activePose(position) {
  if (!position) return selectedToolPose();
  if (position.entry) {
    const segment = position.entry.segment;
    const motionFraction = segment.dwellAfterSeconds && position.entry.duration > 0
      ? Math.min(1, (position.fraction * position.entry.duration) / Math.max(1e-9, position.entry.duration - segment.dwellAfterSeconds))
      : position.fraction;
    return toolPose(segment, partialSegment(segment, motionFraction).end, motionFraction);
  }
  if (playback.state === "complete") {
    const segment = playbackTimeline[playbackTimeline.length - 1]?.segment;
    return segment ? toolPose(segment, segment.end, 1) : undefined;
  }
  return undefined;
}

function updateToolPose(position = playbackPosition()) {
  if (!viewer) return;
  viewer.setToolPose(activePose(position));
}

function syncViewerPlayback(position = playbackPosition()) {
  if (!viewer) return;
  if (!position || playback.state === "complete") {
    viewer.setPlayback(undefined);
  } else {
    let active;
    if (position.entry) {
      const segment = position.entry.segment;
      const dwell = segment.dwellAfterSeconds || 0;
      const motionFraction = dwell && position.entry.duration > 0
        ? Math.min(1, (position.fraction * position.entry.duration) / Math.max(1e-9, position.entry.duration - dwell))
        : position.fraction;
      active = { points: partialSegment(segment, motionFraction).points };
    }
    viewer.setPlayback({ completedIndex: position.completedExecutionIndex, complete: false, active });
  }
  updateToolPose(position);
}

function rebuildPlaybackTimeline() {
  let elapsed = 0;
  compensatedSegmentsByExecution = new Map();
  (model.compensatedSegments || []).forEach((segment) => {
    const executionIndex = Number(segment.executionIndex);
    if (!Number.isFinite(executionIndex)) return;
    const segments = compensatedSegmentsByExecution.get(executionIndex) || [];
    segments.push(segment);
    compensatedSegmentsByExecution.set(executionIndex, segments);
  });
  playbackTimeline = (model.segments || []).map((segment, executionIndex) => {
    if (!Number.isFinite(segment.executionIndex)) segment.executionIndex = executionIndex;
    const duration = Number.isFinite(segment.estimatedSeconds) ? Math.max(0, segment.estimatedSeconds) : 0;
    const entry = { segment, duration, startSeconds: elapsed, endSeconds: elapsed + duration };
    elapsed += duration;
    return entry;
  });
  playback.totalSeconds = elapsed;
}

function playbackBlockLabel(segment) {
  const match = String(segment?.raw || "").match(/\bN\d+\b/i);
  return match ? match[0].toUpperCase() : `row ${segment?.line || "-"}`;
}

function setPlaybackLine(line) {
  const next = Number.isFinite(line) ? line : undefined;
  if (next === playbackLine) return;
  playbackLine = next;
  updateStats();
  vscode.postMessage({ type: "playbackLine", line: next ?? null });
}

function formatDuration(seconds) {
  if (!Number.isFinite(seconds)) return "—";
  const total = Math.max(0, Math.round(seconds));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const remainder = total % 60;
  return hours
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`
    : `${minutes}:${String(remainder).padStart(2, "0")}`;
}

function formatPlaybackDuration(seconds) {
  if (!Number.isFinite(seconds)) return "0:00.0";
  const tenths = Math.max(0, Math.round(seconds * 10));
  const minutes = Math.floor(tenths / 600);
  const remainder = (tenths - minutes * 600) / 10;
  return `${minutes}:${remainder.toFixed(1).padStart(4, "0")}`;
}

function updatePlaybackUi(position = playbackPosition()) {
  const active = position?.entry?.segment;
  const stateLabel = playback.state === "running"
    ? "Running"
    : playback.state === "paused"
      ? "Paused"
      : playback.state === "complete" ? "Complete" : "Ready";
  let activeLabel = "";
  if (active) {
    activeLabel = ` | ${playbackBlockLabel(active)} | T${String(active.tool).padStart(2, "0")}`;
    if (model.kind === "mill" && active.rotary && (active.rotary.b1 || active.rotary.c1)) {
      activeLabel += ` | B${Number(active.rotary.b1).toFixed(2)} C${Number(active.rotary.c1).toFixed(2)}`;
    }
  }
  playbackHud.textContent =
    `${stateLabel} ${playback.speed}x | ` +
    `${formatPlaybackDuration(playback.elapsedSeconds)} / ${formatPlaybackDuration(playback.totalSeconds)}` +
    activeLabel;
  playbackHud.className = `playback-hud ${playback.state}`;
  runButton.disabled = playback.state === "running" || playbackTimeline.length === 0;
  pauseButton.disabled = playback.state !== "running";
  restartButton.disabled = playbackTimeline.length === 0;
  speedValue.value = `${playback.speed}x`;
  speedValue.textContent = `${playback.speed}x`;
}

function stopPlaybackFrame() {
  if (playbackFrame !== undefined) {
    window.cancelAnimationFrame(playbackFrame);
    playbackFrame = undefined;
  }
}

function finishPlayback() {
  stopPlaybackFrame();
  playback.state = "complete";
  playback.elapsedSeconds = playback.totalSeconds;
  playback.lastTimestamp = undefined;
  const last = playbackTimeline[playbackTimeline.length - 1]?.segment;
  setPlaybackLine(last?.line);
  updatePlaybackUi();
  syncViewerPlayback();
}

function advancePlayback(timestamp) {
  if (playback.state !== "running") return;
  if (!Number.isFinite(playback.lastTimestamp)) playback.lastTimestamp = timestamp;
  const realSeconds = Math.max(0, (timestamp - playback.lastTimestamp) / 1000);
  playback.lastTimestamp = timestamp;
  playback.elapsedSeconds = Math.min(playback.totalSeconds, playback.elapsedSeconds + realSeconds * playback.speed);
  const position = playbackPosition();
  setPlaybackLine(position?.entry?.segment.line);
  updatePlaybackUi(position);
  syncViewerPlayback(position);
  if (playback.elapsedSeconds >= playback.totalSeconds - 1e-9) {
    finishPlayback();
    return;
  }
  playbackFrame = window.requestAnimationFrame(advancePlayback);
}

function runPlayback(restart = false) {
  if (!playbackTimeline.length || playback.totalSeconds <= 0) {
    finishPlayback();
    return;
  }
  stopPlaybackFrame();
  if (restart || playback.state === "idle" || playback.state === "complete") {
    playback.elapsedSeconds = 0;
  }
  playback.state = "running";
  playback.lastTimestamp = window.performance.now();
  const position = playbackPosition();
  setPlaybackLine(position?.entry?.segment.line);
  updatePlaybackUi(position);
  syncViewerPlayback(position);
  playbackFrame = window.requestAnimationFrame(advancePlayback);
}

function pausePlayback() {
  if (playback.state !== "running") return;
  stopPlaybackFrame();
  playback.state = "paused";
  playback.lastTimestamp = undefined;
  updatePlaybackUi();
  syncViewerPlayback();
}

// Pauses playback at `seconds` of estimated machine time.
function seekPlayback(seconds) {
  if (!playbackTimeline.length) return;
  stopPlaybackFrame();
  playback.state = "paused";
  playback.lastTimestamp = undefined;
  playback.elapsedSeconds = Math.max(0, Math.min(playback.totalSeconds, Number(seconds) || 0));
  const position = playbackPosition();
  setPlaybackLine(position?.entry?.segment.line);
  updatePlaybackUi(position);
  syncViewerPlayback(position);
}

function resetPlayback() {
  stopPlaybackFrame();
  playback.state = "idle";
  playback.elapsedSeconds = 0;
  playback.lastTimestamp = undefined;
  setPlaybackLine(undefined);
  updatePlaybackUi();
  viewer?.setPlayback(undefined);
}

// ----- sidebar ---------------------------------------------------------------------

function appendRow(list, label, value, className) {
  const term = document.createElement("dt");
  const definition = document.createElement("dd");
  term.textContent = label;
  definition.textContent = value;
  if (className) {
    definition.className = className;
    definition.title = value;
  }
  list.append(term, definition);
}

function formatRowValue(value, format) {
  if (format === "duration") return formatDuration(Number(value));
  if (value === undefined || value === null || value === "") return "—";
  return String(value);
}

function updateStats() {
  const list = document.getElementById("stats");
  list.replaceChildren();
  for (const [label, value, format] of model.statsRows || []) {
    appendRow(list, label, formatRowValue(value, format));
  }
  appendRow(list, "Selected row", String(selectedLine));
  appendRow(list, "Playback row", playbackLine ? String(playbackLine) : "-");
}

// ----- home (work) offsets --------------------------------------------------------------

const workOffsetCard = document.getElementById("workOffsetCard");
const workOffsetHead = document.getElementById("workOffsetHead");
const workOffsetRows = document.getElementById("workOffsetRows");
const workOffsetSelect = document.getElementById("workOffsetSelect");
const workOffsetInputs = document.getElementById("workOffsetInputs");
const workOffsetNote = document.getElementById("workOffsetNote");
let workOffsetEditing = "G54";
let workOffsetDraft = false;

function formatOffset(value) {
  return Number.isFinite(value) ? String(Number(Number(value).toFixed(4))) : "";
}

function workOffsetRow(label) {
  return (model.workOffsetTable?.rows || []).find((row) => row.label === label);
}

// Saved values of every row, as the host stores them ({ G54: { X: ... } }).
function savedWorkOffsets() {
  const saved = {};
  for (const row of model.workOffsetTable?.rows || []) {
    const values = {};
    for (const [axis, value] of Object.entries(row.values)) {
      if (Number.isFinite(value.saved)) values[axis] = value.saved;
    }
    if (Object.keys(values).length) saved[row.label] = values;
  }
  return saved;
}

function populateWorkOffsets() {
  const table = model.workOffsetTable;
  if (!workOffsetCard) return;
  workOffsetCard.hidden = model.kind !== "mill" || !table;
  if (workOffsetCard.hidden) return;
  const axes = table.axes || [];

  // Overview: the value each offset starts with; saved values in bold,
  // placeholders dimmed, "→" marks a value the program changed (G10, macros).
  const headRow = document.createElement("tr");
  for (const title of ["", ...axes]) {
    const cell = document.createElement("th");
    cell.textContent = title;
    headRow.appendChild(cell);
  }
  workOffsetHead.replaceChildren(headRow);
  workOffsetRows.replaceChildren();
  for (const row of table.rows) {
    const tr = document.createElement("tr");
    tr.classList.toggle("used", row.used);
    tr.classList.toggle("editing", row.label === workOffsetEditing);
    tr.title = row.used ? `${row.label} is used by the program. Click to edit.` : `Click to edit ${row.label}.`;
    const label = document.createElement("td");
    label.textContent = row.label;
    tr.appendChild(label);
    for (const axis of axes) {
      const value = row.values[axis] || {};
      const cell = document.createElement("td");
      const start = Number.isFinite(value.initial) ? value.initial : table.defaults?.[axis];
      cell.textContent = formatOffset(start);
      if (Number.isFinite(value.saved)) cell.classList.add("saved");
      else if (!Number.isFinite(value.initial)) cell.classList.add("placeholder");
      if (Number.isFinite(value.final)) {
        cell.textContent += ` → ${formatOffset(value.final)}`;
        cell.classList.add("changed");
        cell.title = `The program sets ${row.label} ${axis} to ${formatOffset(value.final)}`;
      }
      tr.appendChild(cell);
    }
    tr.addEventListener("click", () => {
      workOffsetEditing = row.label;
      workOffsetDraft = false;
      populateWorkOffsets();
    });
    workOffsetRows.appendChild(tr);
  }

  if (!workOffsetRow(workOffsetEditing)) workOffsetEditing = table.rows.find((row) => row.used && row.label !== "EXT")?.label || "G54";
  workOffsetSelect.replaceChildren(...table.rows.map((row) => {
    const option = document.createElement("option");
    option.value = row.label;
    option.textContent = row.used ? `${row.label} (used)` : row.label;
    return option;
  }));
  workOffsetSelect.value = workOffsetEditing;
  // Keep what the user is typing when the preview refreshes.
  if (!workOffsetDraft || !workOffsetInputs.childElementCount) {
    const row = workOffsetRow(workOffsetEditing);
    workOffsetInputs.replaceChildren(...axes.map((axis) => {
      const value = row?.values[axis] || {};
      const field = document.createElement("label");
      field.textContent = axis;
      const input = document.createElement("input");
      input.type = "number";
      input.step = "any";
      input.dataset.axis = axis;
      input.value = formatOffset(value.saved);
      const start = Number.isFinite(value.initial) ? value.initial : table.defaults?.[axis];
      input.placeholder = formatOffset(start);
      input.addEventListener("input", () => {
        workOffsetDraft = true;
      });
      input.addEventListener("keydown", (event) => {
        if (event.key === "Enter") applyWorkOffsets();
      });
      field.appendChild(input);
      return field;
    }));
  }
  workOffsetNote.textContent = `Values in ${table.units}${Number.isFinite(table.defaults?.X) ? `; empty fields use the program's own value or the ${table.defaultLabel} (${["X", "Y", "Z"].map((axis) => formatOffset(table.defaults[axis])).join(", ")})` : ""}. Saved per machine.`;
}

function postWorkOffsets(offsets) {
  vscode.postMessage({
    type: "workOffsetsChanged",
    machine: model.machineDefinition?.id,
    offsets
  });
  workOffsetDraft = false;
}

function applyWorkOffsets() {
  const offsets = savedWorkOffsets();
  const values = {};
  for (const input of workOffsetInputs.querySelectorAll("input[data-axis]")) {
    const text = input.value.trim();
    if (text !== "" && Number.isFinite(Number(text))) values[input.dataset.axis] = Number(text);
  }
  if (Object.keys(values).length) offsets[workOffsetEditing] = values;
  else delete offsets[workOffsetEditing];
  postWorkOffsets(offsets);
}

workOffsetSelect?.addEventListener("change", () => {
  workOffsetEditing = workOffsetSelect.value;
  workOffsetDraft = false;
  populateWorkOffsets();
});
document.getElementById("workOffsetApply")?.addEventListener("click", applyWorkOffsets);
document.getElementById("workOffsetClear")?.addEventListener("click", () => {
  const offsets = savedWorkOffsets();
  delete offsets[workOffsetEditing];
  for (const input of workOffsetInputs.querySelectorAll("input[data-axis]")) input.value = "";
  postWorkOffsets(offsets);
});

function populateMachineParameters() {
  const list = document.getElementById("machineParameters");
  list.replaceChildren();
  for (const [label, value, format] of model.machineRows || []) {
    appendRow(list, label, formatRowValue(value), format === "path" ? "parameter-path" : undefined);
  }
  const selection = model.machineSelection;
  if (selection?.detected) {
    appendRow(list, "Machine choice", `Auto: ${selection.reason}`, "parameter-path");
  }
}

function displayToolType(type) {
  return String(type || "other").replace(/-/g, " ");
}

function toolDefinition(number) {
  return (model.toolDefinitions || []).find((definition) => definition.number === number);
}

function formatSize(value) {
  return Number.isFinite(value) ? String(Number(Number(value).toFixed(4))) : "-";
}

function populateToolTable() {
  const head = document.getElementById("toolTableHead");
  const body = document.getElementById("toolTableRows");
  const source = document.getElementById("toolTableSource");
  const mill = model.kind === "mill";
  const columns = mill
    ? ["Tool", "Dia", "CR", "Length", "Type", "Source", "Conf."]
    : ["Tool", "Radius", "Nose", "Type", "Size", "Hand", "Source", "Conf."];
  if (head) {
    const row = document.createElement("tr");
    for (const label of columns) {
      const cell = document.createElement("th");
      cell.textContent = label;
      row.appendChild(cell);
    }
    head.replaceChildren(row);
  }
  body.replaceChildren();
  (model.toolDefinitions || []).forEach((tool) => {
    const row = document.createElement("tr");
    if (model.tools.includes(tool.number)) row.className = "tool-used";
    const confidence = Number.isFinite(tool.confidence) ? `${Math.round(tool.confidence * 100)}%` : "-";
    const values = mill
      ? [
        `T${String(tool.number).padStart(2, "0")}`,
        formatSize(tool.diameter),
        formatSize(tool.cornerRadius),
        formatSize(tool.length),
        displayToolType(tool.type),
        tool.recognition || "heuristic",
        confidence
      ]
      : [
        `T${String(tool.number).padStart(2, "0")}`,
        Number(tool.cornerRadius).toFixed(3).replace(/\.?0+$/, ""),
        tool.tipKnown === false ? "?" : String(tool.tip),
        displayToolType(tool.type),
        Number.isFinite(tool.diameter) ? `D${tool.diameter}` : Number.isFinite(tool.width) ? `W${tool.width}` : "-",
        tool.hand || "unknown",
        tool.recognition || "heuristic",
        confidence
      ];
    values.forEach((value) => {
      const cell = document.createElement("td");
      cell.textContent = value;
      row.appendChild(cell);
    });
    row.title = [
      tool.description,
      Number.isFinite(tool.diameter) ? `Diameter: ${tool.diameter}` : "",
      Number.isFinite(tool.width) ? `Width: ${tool.width}` : "",
      tool.evidence ? `Evidence: ${tool.evidence}` : "",
      tool.locked ? "Locked: AI cannot replace this row" : ""
    ].filter(Boolean).join("\n");
    body.appendChild(row);
  });
  if (!(model.toolDefinitions || []).length) {
    const row = document.createElement("tr");
    const cell = document.createElement("td");
    cell.colSpan = columns.length;
    cell.textContent = "No tools defined";
    row.appendChild(cell);
    body.appendChild(row);
  }
  const table = model.toolTable || {};
  const aiStatus = table.aiStatus || {};
  source.textContent = `${aiStatus.message || "Heuristic scan"} | XML: ${table.sourcePath || table.name || "NC comments"}`;
  source.title = [aiStatus.message, table.sourcePath || table.name].filter(Boolean).join("\n");
}

function populateLegend() {
  const legend = document.getElementById("legend");
  if (!legend) return;
  legend.replaceChildren();
  const kinds = new Set((model.segments || []).map((segment) => segment.kind));
  if ((model.compensatedSegments || []).length) kinds.add("compensated");
  if ((model.compensationIssues || []).length) kinds.add("compensation-interference");
  if (polyarcDisplaySegments.some((segment) => segment.kind === "outside")) kinds.add("polyarc-outside");
  if (polyarcDisplaySegments.some((segment) => segment.kind === "internal")) kinds.add("polyarc-internal");
  kinds.add("active");
  kinds.add("selected");
  const order = Object.keys(segmentStyles);
  const entries = [...kinds].filter((kind) => segmentStyles[kind]).sort((a, b) => order.indexOf(a) - order.indexOf(b));
  for (const kind of entries) {
    const style = segmentStyles[kind];
    const row = document.createElement("div");
    row.className = "legend";
    const swatch = document.createElement("i");
    swatch.style.borderTopColor = style.color;
    swatch.style.borderTopStyle = style.dashed ? "dashed" : "solid";
    swatch.style.borderTopWidth = `${Math.min(4, Math.max(2, style.width || 2))}px`;
    row.append(swatch, document.createTextNode(style.label));
    legend.appendChild(row);
  }
  const extras = model.kind === "lathe"
    ? [["tool-nose", "Tool nose / nose point"], ["stock", "Stock"]]
    : [["mill-tool", "Cutter (flutes / holder) at the active move"]];
  for (const [className, label] of extras) {
    const row = document.createElement("div");
    row.className = "legend";
    const swatch = document.createElement("i");
    swatch.className = className;
    row.append(swatch, document.createTextNode(label));
    legend.appendChild(row);
  }
}

function populateMachineSelect() {
  const selection = model.machineSelection;
  if (!machineSelect || !selection) return;
  const current = selection.selection || "auto";
  machineSelect.replaceChildren();
  const detected = (selection.available || []).find((machine) => machine.id === selection.id);
  const auto = document.createElement("option");
  auto.value = "auto";
  auto.textContent = selection.detected ? `Auto (${detected?.name || selection.id})` : "Auto detect";
  machineSelect.appendChild(auto);
  for (const machine of selection.available || []) {
    const option = document.createElement("option");
    option.value = machine.id;
    option.textContent = `${machine.name} · ${machine.type}`;
    option.title = machine.control;
    machineSelect.appendChild(option);
  }
  machineSelect.value = current;
  machineSelect.title = selection.reason || "";
}

function applyMachineChrome() {
  const lathe = model.kind === "lathe";
  if (eyebrow) eyebrow.textContent = model.view?.eyebrow || (lathe ? "FANUC LATHE | G18 X-Z | DIAMETER X" : "MILL");
  g30XLabel.hidden = !lathe;
  g30ZLabel.hidden = !lathe;
  showG30Text.textContent = lathe ? "G30" : "Returns";
  showG30.parentElement.title = lathe
    ? "Show G30 second-reference returns"
    : "Show G28/G53 machine moves and M06 tool-change retracts";
  frameLabel.hidden = lathe;
  for (const button of viewButtons) {
    const views = String(button.dataset.for || "mill lathe").split(" ");
    button.hidden = !views.includes(lathe ? "lathe" : "mill");
  }
}

function updateViewButtons() {
  const active = viewer?.view;
  for (const button of viewButtons) {
    button.classList.toggle("active", button.dataset.view === active);
    button.setAttribute("aria-pressed", String(button.dataset.view === active));
  }
}

function populateSidebar() {
  updateStats();
  populateMachineParameters();
  populateWorkOffsets();
  populateToolTable();
  populateLegend();
  populateMachineSelect();
  applyMachineChrome();
  const errorCard = document.getElementById("errorCard");
  const errorList = document.getElementById("errors");
  errorList.replaceChildren();
  const errors = model.errors || [];
  errorCard.hidden = errors.length === 0;
  const issuesByMessage = new Map((model.compensationIssues || []).map((issue) => [issue.message, issue]));
  errors.forEach((error, index) => {
    const item = document.createElement("li");
    item.textContent = error;
    const issue = issuesByMessage.get(error) || (model.kind === "lathe" ? (model.compensationIssues || [])[index] : undefined);
    if (Number.isFinite(issue?.line)) {
      item.dataset.line = String(issue.line);
      item.tabIndex = 0;
      const selectIssue = () => selectLine(issue.line, undefined, true);
      item.addEventListener("click", selectIssue);
      item.addEventListener("keydown", (event) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          selectIssue();
        }
      });
      item.title = `Open row ${issue.line}`;
    }
    errorList.appendChild(item);
  });
  const macroList = document.getElementById("macroVariables");
  macroList.replaceChildren();
  const variables = Object.entries(model.macroVariables || {});
  if (!variables.length) {
    const term = document.createElement("dt");
    term.textContent = "None";
    macroList.appendChild(term);
  } else {
    variables.forEach(([id, value]) => {
      appendRow(macroList, `#${id}`, Number(value).toFixed(4).replace(/\.?0+$/, ""));
    });
  }
  const warningList = document.getElementById("warnings");
  warningList.replaceChildren();
  const notes = model.warnings.length ? model.warnings : ["No parser limitations were triggered by this program."];
  notes.forEach((warning) => {
    const item = document.createElement("li");
    item.textContent = warning;
    warningList.appendChild(item);
  });

  const currentSelection = toolFilter.value;
  toolFilter.replaceChildren();
  const all = document.createElement("option");
  all.value = "all";
  all.textContent = "All cutting tools";
  toolFilter.appendChild(all);
  model.tools.forEach((tool) => {
    const option = document.createElement("option");
    option.value = String(tool);
    const definition = toolDefinition(tool);
    const size = model.kind === "mill"
      ? Number.isFinite(definition?.diameter) ? ` Ø${definition.diameter}` : ""
      : Number.isFinite(definition?.cornerRadius) ? ` R${definition.cornerRadius}` : "";
    option.textContent =
      `T${String(tool).padStart(2, "0")}${size}${definition ? ` · ${displayToolType(definition.type)}` : ""}`;
    toolFilter.appendChild(option);
  });
  if ([...toolFilter.options].some((option) => option.value === currentSelection)) {
    toolFilter.value = currentSelection;
  }
}

// ----- selection and hover -------------------------------------------------------------

function selectLine(line, executionIndex, notifyHost) {
  selectedLine = line;
  selectedExecutionIndex = executionIndex;
  updateStats();
  viewer?.setSelection(selectedLine, selectedExecutionIndex);
  if (playback.state === "idle") updateToolPose();
  if (notifyHost) vscode.postMessage({ type: "selectLine", line });
}

function formatCoordinate(value) {
  return Number.isFinite(value) ? Number(value.toFixed(4)).toString() : "?";
}

function describeSegment(segment, layer) {
  const feed = Number.isFinite(segment.feed) && segment.feed > 0 && segment.kind !== "rapid"
    ? ` | F${segment.feed} ${segment.feedMode || ""}`.trimEnd()
    : "";
  const header = `Row ${segment.line} | T${String(segment.tool).padStart(2, "0")} | ${layer === "compensated" ? "tool centre (compensated)" : segment.kind}${feed}`;
  const details = [];
  if (model.kind === "lathe") {
    if (Number.isFinite(segment.toolRadius)) details.push(`nose R${segment.toolRadius}`);
    details.push(`tip ${segment.toolTip ?? 0}`, displayToolType(segment.toolType));
    if (segment.cycle === "G76") {
      details.push(
        `pass ${segment.cyclePass}/${segment.cyclePassCount}`,
        `depth ${Number(segment.threadDepth).toFixed(3)}`,
        `cut ${Number(segment.threadIncrement || 0).toFixed(3)} radial`,
        `lead ${segment.threadPitch}`
      );
    }
    const end = segment.end || {};
    details.push(`end X${formatCoordinate(end.x)} Z${formatCoordinate(end.z)}`);
  } else {
    const end = segment.end || {};
    details.push(`end X${formatCoordinate(end.x)} Y${formatCoordinate(end.y)} Z${formatCoordinate(end.z)}`);
    if (segment.rotary && (segment.rotary.b1 || segment.rotary.c1 || segment.rotary.b0 || segment.rotary.c0)) {
      details.push(`B${formatCoordinate(segment.rotary.b1)} C${formatCoordinate(segment.rotary.c1)}`);
    }
    if (segment.cycle) details.push(segment.cycle);
    if (Number.isFinite(segment.toolDiameter)) details.push(`Ø${segment.toolDiameter}`);
    if (segment.workOffset) details.push(segment.workOffset);
    if (segment.tcpc) details.push("G234 TCPC");
    if (segment.dwo) details.push("G254 DWO");
    if (segment.featureFrame) details.push("G268");
    if (Number.isFinite(segment.spindleSpeed)) details.push(`S${segment.spindleSpeed}`);
    if (segment.sourceUnit) details.push(`${segment.sourceUnit} row ${segment.sourceLine}`);
  }
  const interference = segment.compensationInterference ? " | COMPENSATION INTERFERENCE" : "";
  return `${header}${interference}\n${details.join(" | ")}\n${segment.raw || ""}`;
}

function processHover() {
  hoverFrame = undefined;
  const event = lastHoverEvent;
  if (!event || !viewer) return;
  const hit = viewer.pick(event.clientX, event.clientY);
  if (!hit) {
    tooltip.hidden = true;
    return;
  }
  const rectangle = canvas.getBoundingClientRect();
  const x = event.clientX - rectangle.left;
  const y = event.clientY - rectangle.top;
  tooltip.textContent = describeSegment(hit.segment, hit.layer);
  tooltip.style.left = `${Math.max(8, Math.min(x + 12, canvas.clientWidth - 320))}px`;
  tooltip.style.top = `${Math.max(8, y - 60)}px`;
  tooltip.hidden = false;
}

canvas.addEventListener("pointerdown", (event) => {
  viewer?.pointerDown(event);
  tooltip.hidden = true;
  canvas.classList.add("dragging");
});

canvas.addEventListener("pointermove", (event) => {
  if (viewer?.pointerMove(event)) {
    tooltip.hidden = true;
    return;
  }
  lastHoverEvent = event;
  if (hoverFrame === undefined) hoverFrame = window.requestAnimationFrame(processHover);
});

canvas.addEventListener("pointerup", (event) => {
  canvas.classList.remove("dragging");
  const result = viewer?.pointerUp(event);
  if (result?.click) {
    const hit = viewer.pick(event.clientX, event.clientY);
    if (hit) {
      selectLine(hit.segment.line, hit.layer === "base" ? hit.segment.executionIndex : undefined, true);
    }
  }
});

canvas.addEventListener("pointerleave", () => {
  tooltip.hidden = true;
});
canvas.addEventListener("contextmenu", (event) => event.preventDefault());
canvas.addEventListener("wheel", (event) => {
  event.preventDefault();
  viewer?.wheel(event);
}, { passive: false });
canvas.addEventListener("dblclick", () => viewer?.fit());

// ----- toolbar ---------------------------------------------------------------------------

function updateG30() {
  vscode.postMessage({ type: "g30Changed", x: Number(g30XInput.value), z: Number(g30ZInput.value) });
}

function updateMacros() {
  vscode.postMessage({ type: "macrosChanged", value: initialMacrosInput.value });
}

fitButton.addEventListener("click", () => viewer?.fit());
runButton.addEventListener("click", () => runPlayback(false));
pauseButton.addEventListener("click", pausePlayback);
restartButton.addEventListener("click", () => runPlayback(true));
speedSlider.addEventListener("input", () => {
  playback.speed = Number(speedSlider.value) || 1;
  updatePlaybackUi();
});
reloadParameters.addEventListener("click", () => vscode.postMessage({ type: "reloadParameters" }));
for (const control of [showRapids, showG30, showCompensated, showPolyarc, showExtruder, toolFilter]) {
  control.addEventListener("change", refreshFilters);
}
showTool.addEventListener("change", () => viewer?.setOptions({ showTool: showTool.checked }));
machineSelect?.addEventListener("change", () => {
  vscode.postMessage({ type: "machineChanged", machine: machineSelect.value });
});
frameSelect?.addEventListener("change", () => {
  viewer?.setFrame(frameSelect.value);
  updateViewButtons();
});
for (const button of viewButtons) {
  button.addEventListener("click", () => {
    viewer?.setView(button.dataset.view);
    updateViewButtons();
  });
}
g30XInput.addEventListener("change", updateG30);
g30ZInput.addEventListener("change", updateG30);
initialMacrosInput.addEventListener("change", updateMacros);

// Keyboard view shortcuts when the viewport has focus.
canvas.tabIndex = 0;
canvas.addEventListener("keydown", (event) => {
  const views = { 1: "iso", 2: "top", 3: "front", 4: "right", f: "fit" };
  const view = views[event.key.toLowerCase?.() || event.key];
  if (!view || !viewer) return;
  event.preventDefault();
  if (view === "fit") viewer.fit();
  else viewer.setView(model.kind === "lathe" && view === "front" ? "lathe" : view);
  updateViewButtons();
});

window.addEventListener("message", (event) => {
  if (!event.data) return;
  if (event.data.type === "selection") {
    if (event.data.line !== selectedLine) selectLine(event.data.line, undefined, false);
    return;
  }
  if (event.data.type !== "render") return;
  const previousKind = model.kind;
  const previousMachine = model.machineDefinition?.id;
  // Hosts send the model as a compact JSON string (media/model-transport.js).
  if (typeof event.data.packed === "string") {
    try {
      model = window.CncModelTransport.parse(event.data.packed);
    } catch (error) {
      setViewerStatus(`The preview data could not be read: ${error.message}`, true);
      return;
    }
  } else {
    model = event.data.payload;
  }
  model.statsRows ||= [];
  model.machineRows ||= [];
  model.kind ||= "lathe";
  rebuildPolyarcDisplaySegments();
  selectedLine = event.data.selectedLine || selectedLine;
  selectedExecutionIndex = undefined;
  rebuildPlaybackTimeline();
  resetPlayback();
  document.getElementById("documentName").textContent = event.data.name;
  if (document.activeElement !== g30XInput) g30XInput.value = event.data.settings.g30X ?? "";
  if (document.activeElement !== g30ZInput) g30ZInput.value = event.data.settings.g30Z ?? "";
  if (document.activeElement !== initialMacrosInput) {
    initialMacrosInput.value = event.data.settings.initialVariables || "";
  }
  if (model.kind !== "mill") frameSelect.value = "part";
  populateSidebar();
  const sameLayout = previousKind === model.kind && previousMachine === model.machineDefinition?.id &&
    event.data.preserveView !== false;
  applyModelToViewer({ preserveView: sameLayout });
});

// Read-only state for automated smoke tests.
window.cncPreviewState = () => ({
  kind: model.kind,
  machine: model.machineDefinition?.id,
  segments: (model.segments || []).length,
  viewerReady: Boolean(viewer),
  viewerStatus: viewerStatus?.hidden ? "" : viewerStatus?.textContent || "",
  view: viewer?.view,
  frame: viewer?.frame,
  playback: playback.state
});
window.cncPreviewSeek = seekPlayback;

updatePlaybackUi();
void loadViewer();
vscode.postMessage({ type: "ready" });
