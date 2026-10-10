"use strict";

// Meimad Planner: the Axes card and the axis readout on the 3D view. The current machine axis
// values follow the tool pose (selected row, single step and playback); the travel list shows the
// range every axis reaches over the whole program and checks it against the machine definition's
// travel limits. The calculation is in meimad-axes-core.js; this file only draws it.
(() => {
  const core = window.MeimadAxes;
  const kinematics = window.CncKinematics;
  const elements = {
    card: document.getElementById("meimadAxesCard"),
    frame: document.getElementById("meimadAxesFrame"),
    now: document.getElementById("meimadAxesNow"),
    rows: document.getElementById("meimadAxesRows"),
    summary: document.getElementById("meimadAxesSummary"),
    note: document.getElementById("meimadAxesNote"),
    rapids: document.getElementById("meimadAxesRapids"),
    overlayToggle: document.getElementById("meimadAxesOverlayToggle"),
    overlay: document.getElementById("meimadAxesOverlay")
  };
  if (!core || !elements.card) return;

  const OVERLAY_KEY = "meimad.axes.overlay";
  let hooks = null;
  let model = null;
  let calculator = null;
  let lastPose = null;
  let poseTimer = 0;
  let rangeTimer = 0;
  let axisOrder = [];

  const decimals = (axis) => (axis.type === "rotary" ? 3 : model && model.meta && model.meta.units === "inch" ? 4 : 3);

  function readOverlayPreference() {
    try {
      return window.localStorage.getItem(OVERLAY_KEY) !== "off";
    } catch {
      return true;
    }
  }

  function writeOverlayPreference(on) {
    try {
      window.localStorage.setItem(OVERLAY_KEY, on ? "on" : "off");
    } catch {
      // The readout still follows the checkbox for this window.
    }
  }

  function selectProgramLine(line) {
    if (!Number.isFinite(line)) return;
    if (typeof window.selectLine === "function") window.selectLine(line, undefined, true);
  }

  // ----- current position ---------------------------------------------------------------------

  function latheMilling() {
    const annotation = window.meimadLatheAnnotation;
    const lathe = window.MeimadLatheAxes;
    if (!model || model.kind !== "lathe" || !annotation || !lathe || !(annotation.usesC || annotation.usesY)) return null;
    const active = window.meimadActiveSegment ? window.meimadActiveSegment.current() : null;
    const values = active ? lathe.at(annotation, active.index, active.fraction) : { C: 0, Y: 0 };
    const result = {};
    if (annotation.usesY) result.Y = values.Y;
    if (annotation.usesC) result.C = values.C;
    return result;
  }

  function renderPose() {
    poseTimer = 0;
    let joints = model && calculator && lastPose ? core.poseJoints(model, kinematics, lastPose, calculator) : undefined;
    // A lathe's milling axes (C spindle position, Y) come from the program rows (meimad-lathe-axes-core.js).
    const milling = latheMilling();
    if (joints && milling) joints = Object.assign({}, joints, milling);
    const axes = axisOrder.length ? axisOrder : [];
    elements.now.replaceChildren();
    const overlayParts = [];
    for (const axis of axes) {
      const value = joints ? joints[axis.axis] : undefined;
      if (axis.type === "rotary" && !Number.isFinite(value)) continue;
      const cell = document.createElement("div");
      cell.className = "meimad-axes-value";
      const name = document.createElement("span");
      name.className = "meimad-axes-name";
      name.setAttribute("translate", "no");
      name.textContent = axis.label;
      const number = document.createElement("span");
      number.className = "meimad-axes-number";
      number.setAttribute("translate", "no");
      number.textContent = core.format(value, decimals(axis));
      const outside = Number.isFinite(value) && (
        (axis.limitMin !== null && value < axis.limitMin - 1e-6) ||
        (axis.limitMax !== null && value > axis.limitMax + 1e-6)) && !axis.wraps;
      if (outside) {
        cell.classList.add("outside");
        cell.title = "Outside the machine travel";
        number.textContent += " ⚠";
      }
      cell.append(name, number);
      elements.now.appendChild(cell);
      overlayParts.push(`${axis.label} ${core.format(value, decimals(axis))}${outside ? " ⚠" : ""}`);
    }
    if (!axes.length) {
      const empty = document.createElement("p");
      empty.className = "meimad-note";
      empty.textContent = "Open a program to see the axis positions.";
      elements.now.appendChild(empty);
    }
    const showOverlay = elements.overlayToggle.checked && overlayParts.length > 0 && Boolean(joints);
    elements.overlay.hidden = !showOverlay;
    if (showOverlay) elements.overlay.textContent = overlayParts.join("   ");
  }

  function schedulePose(pose) {
    lastPose = pose || null;
    // The tool pose changes every frame while playing; the readout redraws at most ~20 times a second.
    if (!poseTimer) poseTimer = window.setTimeout(renderPose, 50);
  }

  // ----- travel ranges ------------------------------------------------------------------------

  function limitText(row) {
    if (row.status === "no-limit") return row.type === "rotary" ? "Continuous" : "No limit defined";
    const low = row.limitMin === null ? "−∞" : core.format(row.limitMin, row.type === "rotary" ? 1 : 3);
    const high = row.limitMax === null ? "+∞" : core.format(row.limitMax, row.type === "rotary" ? 1 : 3);
    return `${low} … ${high}`;
  }

  function statusText(row) {
    switch (row.status) {
      case "outside": return "Outside travel";
      case "no-limit": return "Not checked";
      case "not-used": return "Not moved";
      default: return "Within travel";
    }
  }

  function renderRanges() {
    rangeTimer = 0;
    elements.rows.replaceChildren();
    if (!model) {
      elements.summary.className = "meimad-validation";
      elements.summary.textContent = "";
      elements.note.textContent = "";
      return;
    }
    const result = core.travelRanges(model, kinematics, { includeRapids: elements.rapids.checked });
    if (result.kind === "lathe" && window.meimadLatheAnnotation && window.MeimadLatheAxes) {
      const milling = window.MeimadLatheAxes.ranges(window.meimadLatheAnnotation, model.segments);
      for (const axis of ["Y", "C"]) {
        const range = milling[axis];
        if (!range) continue;
        result.rows.push({
          axis, label: axis, type: axis === "C" ? "rotary" : "linear",
          min: range.min, max: range.max, minLine: range.minLine, maxLine: range.maxLine,
          limitMin: null, limitMax: null, status: "no-limit", overBy: 0, overLine: null
        });
      }
    }
    axisOrder = result.kind === "lathe"
      ? result.rows.map((row) => ({ axis: row.axis, label: row.label, type: row.type, limitMin: null, limitMax: null, wraps: false }))
      : core.machineAxes(model);
    if (!axisOrder.length) axisOrder = result.rows.map((row) => ({ axis: row.axis, label: row.label, type: row.type, limitMin: row.limitMin, limitMax: row.limitMax, wraps: false }));
    elements.frame.textContent = result.kind === "lathe"
      ? "Program coordinates (X as diameter). The lathe definition has no machine travel to check."
      : "Machine coordinates of the spindle gauge point, from the machine definition's kinematics and the program's work offsets.";
    for (const row of result.rows) {
      const item = document.createElement("li");
      item.className = `meimad-axes-row ${row.status}`;
      const name = document.createElement("strong");
      name.setAttribute("translate", "no");
      name.textContent = row.label;
      const range = document.createElement("span");
      range.className = "meimad-axes-range";
      range.setAttribute("translate", "no");
      const digits = row.type === "rotary" ? 3 : decimals(row);
      range.textContent = row.status === "not-used" ? "—" : `${core.format(row.min, digits)} … ${core.format(row.max, digits)}`;
      const limits = document.createElement("span");
      limits.className = "meimad-axes-limits";
      limits.textContent = limitText(row);
      const status = document.createElement("span");
      status.className = "meimad-axes-status";
      // The state is in the text (and an icon); colour only repeats it.
      const icon = row.status === "outside" ? "✖ " : row.status === "inside" ? "✔ " : "• ";
      status.textContent = icon + statusText(row);
      item.append(name, range, limits, status);
      if (row.status === "outside") {
        const detail = document.createElement("span");
        detail.className = "meimad-axes-detail";
        detail.textContent = `Over by ${core.format(row.overBy, digits)} at row ${row.overLine}`;
        item.appendChild(detail);
      }
      const line = row.status === "outside" ? row.overLine : row.minLine;
      if (Number.isFinite(line)) {
        item.tabIndex = 0;
        item.title = `Open row ${line}`;
        item.addEventListener("click", () => selectProgramLine(line));
        item.addEventListener("keydown", (event) => {
          if (event.key === "Enter" || event.key === " ") {
            event.preventDefault();
            selectProgramLine(line);
          }
        });
      }
      elements.rows.appendChild(item);
    }
    elements.summary.className = `meimad-validation ${result.kind === "lathe" ? "neutral" : result.outside ? "invalid" : "valid"}`;
    elements.summary.textContent = result.kind === "lathe"
      ? "Travel is not checked for lathes."
      : result.outside
        ? `✖ ${result.outside} axis${result.outside === 1 ? "" : "es"} outside the machine travel`
        : "✔ The program stays within the machine travel";
    elements.note.textContent = result.positionsUnknown
      ? `Work offsets ${result.defaultedWorkOffsets.join(", ")} have no values, so the machine positions assume a placeholder offset. Enter the real offsets under Home offsets or "Initial # vars" before trusting the travel check.`
      : "";
    renderPose();
  }

  function scheduleRanges() {
    if (rangeTimer) window.clearTimeout(rangeTimer);
    // A large program takes a moment; let the toolpath draw first.
    rangeTimer = window.setTimeout(renderRanges, 30);
  }

  // ----- wiring -------------------------------------------------------------------------------

  function attach(value) {
    if (!value || hooks === value) return;
    hooks = value;
    hooks.on("modelApplied", (next) => {
      model = next || null;
      calculator = model ? core.createCalculator(model, kinematics) : null;
      lastPose = null;
      scheduleRanges();
    });
    hooks.on("toolPose", (pose) => schedulePose(pose));
  }

  elements.overlayToggle.checked = readOverlayPreference();
  elements.overlayToggle.addEventListener("change", () => {
    writeOverlayPreference(elements.overlayToggle.checked);
    renderPose();
  });
  elements.rapids.addEventListener("change", scheduleRanges);
  if (window.meimadViewer3d) attach(window.meimadViewer3d);
  window.addEventListener("meimad:viewer3d", (event) => attach(event.detail));
  renderPose();
})();
