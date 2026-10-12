"use strict";

// Meimad Planner stock and material removal for the NC viewer page. Loaded after preview.js.
// - Stock definition (box, cylinder or STL, relative to a work offset), saved next to the NC
//   program in the Case Working Folder and reloaded with it.
// - Material removal in a worker: milling on the 3D voxel stock (meimad-voxel-worker.js, with
//   collision checks, the finished-part comparison and a journal; the earlier column model in
//   meimad-material-worker.js stays selectable), turning on the radius profile. The stock follows
//   the playback position, or shows the finished part while playback is idle; ON/OFF and resolution.
// - Verification card: collision rules, the events with their NC rows (markers in the 3D view;
//   a "stop" rule pauses the playback), gouges and material left outside the finished part.
// - Playback from the selected NC row, single-tool playback that skips the other tools' moves,
//   and a progress bar for both the cutting computation and the playback.
// - Export of the machined stock as STL into the next Operation's Stock folder.
(() => {
  const desktop = window.fanucDesktop;
  const meimad = desktop && desktop.meimad;
  if (!meimad || !meimad.stock) return;
  const byId = (id) => document.getElementById(id);
  const elements = {
    toggle: byId("meimadMaterialToggle"),
    resolution: byId("meimadResolution"),
    fromRow: byId("meimadFromRow"),
    progress: byId("meimadProgress"),
    progressBar: byId("meimadProgressBar"),
    progressText: byId("meimadProgressText"),
    card: byId("meimadStockCard"),
    type: byId("meimadStockType"),
    offset: byId("meimadStockOffset"),
    boxFields: byId("meimadStockBox"),
    cylinderFields: byId("meimadStockCylinder"),
    stlFields: byId("meimadStockStl"),
    stlPath: byId("meimadStockStlPath"),
    stlBrowse: byId("meimadStockStlBrowse"),
    candidates: byId("meimadStockCandidates"),
    fromToolpath: byId("meimadStockFromToolpath"),
    apply: byId("meimadStockApply"),
    save: byId("meimadStockSave"),
    exportNext: byId("meimadStockExportNext"),
    exportAs: byId("meimadStockExportAs"),
    note: byId("meimadStockNote"),
    reference: byId("meimadStockReference"),
    runButton: byId("runButton"),
    pauseButton: byId("pauseButton"),
    toolFilter: byId("toolFilter"),
    engine: byId("meimadEngine"),
    backend: byId("meimadBackend"),
    collisions: byId("meimadCollisions"),
    policyRows: byId("meimadPolicyRows"),
    verifyStats: byId("meimadVerifyStats"),
    events: byId("meimadEvents"),
    partSource: byId("meimadPartSource"),
    partPath: byId("meimadPartPath"),
    partBrowse: byId("meimadPartBrowse"),
    deviation: byId("meimadDeviation"),
    deviationResult: byId("meimadDeviationResult"),
    journalCopy: byId("meimadJournalCopy")
  };
  if (!elements.card || !elements.toggle) return;
  const field = (name) => elements.card.querySelector(`[data-stock="${name}"]`);
  const number = (name, fallback) => {
    const input = field(name);
    const value = input ? Number(input.value) : NaN;
    return Number.isFinite(value) ? value : fallback;
  };
  const setNumber = (name, value) => {
    const input = field(name);
    if (input && Number.isFinite(value)) input.value = String(Number(value.toFixed(4)));
  };

  let hooks = window.meimadViewer3d;
  let model;
  let timeline = [];           // { index, tool, line, startSeconds, endSeconds, duration }
  let compact = [];            // segments for the worker
  let stlPositions;            // Float32Array of the loaded STL
  let stlName = "";
  // Rest material exported by Cimatron is in part-model coordinates; its Work CS (the Operation's
  // Cimatron setup) turns it into program coordinates when it is loaded. Null for an STL that is
  // already in program coordinates.
  let stlWorkCs = null;
  let hostState = { stock: null, candidates: [], canSave: false, nextOperationNumber: null };
  let definition = { type: "none", workOffset: "G54", box: {}, cylinder: {}, stl: { path: "", offset: { x: 0, y: 0, z: 0 } } };
  let worker;
  let workerReady = false;
  let mesh;
  let outline;
  let lights;
  let filterTool = "all";
  let lastTarget = { index: -2, fraction: 0 };
  let workerBusy = false;
  let playbackDone = 0;
  let seeking = false;
  let tiltedMoves = 0;
  let boxBeforeLathe = false;
  // 3D stock: the surface arrives in render chunks; events, totals and the finished part.
  let voxelGroup;
  const chunkMeshes = new Map();
  let chunkMaterial;
  let markerGroup;
  let events = [];
  let lastStats = null;
  let workerEngine = "";
  let partPositions = null;   // finished part in program coordinates
  let partSourceText = "";
  let userPartPath = "";
  let sceneFixtures = [];
  let assemblies = {};
  let axisLimitMoves = 0;
  let deviationAsked = false;

  // Collision rules: the checks of meimad-voxel-sim.js and their default severity.
  const CHECKS = [
    ["holderStock", "Holder or shank in the stock", "error"],
    ["toolFixture", "Tool in a fixture", "stop"],
    ["holderFixture", "Holder in a fixture", "stop"],
    ["holderPart", "Holder in the finished part", "warning"],
    ["rapidStock", "Rapid through material", "error"],
    ["gouge", "Cut into the finished part", "error"],
    ["axisLimit", "Axis travel limit", "error"]
  ];
  const SEVERITIES = [["allowed", "Off"], ["warning", "Warning"], ["error", "Error"], ["stop", "Stop playback"]];
  const STORE = { backend: "meimad.ncViewer.backend", engine: "meimad.ncViewer.engine", policy: "meimad.ncViewer.collisionPolicy", collisions: "meimad.ncViewer.collisions" };
  const readStore = (key) => {
    try { return window.localStorage?.getItem(key) ?? null; } catch { return null; }
  };
  const writeStore = (key, value) => {
    try { window.localStorage?.setItem(key, value); } catch { /* not remembered */ }
  };

  const editor = () => document.querySelector(".CodeMirror")?.CodeMirror;
  const message = (error) => (error && error.message) || String(error || "Unknown error");
  const status = (text, level) => {
    const state = byId("statusState");
    const text_ = byId("statusMessage");
    if (!state || !text_) return;
    const labels = { ready: "Ready", success: "Done", working: "Working", warning: "Review", error: "Error" };
    state.className = `status-state ${level || "ready"}`;
    state.querySelector("span").textContent = labels[level] || "Ready";
    text_.textContent = text;
  };

  // ----- definition <-> form -----------------------------------------------------------------

  function readForm() {
    const type = elements.type.value;
    definition = {
      type,
      workOffset: elements.offset.value || "G54",
      box: {
        minX: number("boxMinX", -50), maxX: number("boxMaxX", 50),
        minY: number("boxMinY", -50), maxY: number("boxMaxY", 50),
        minZ: number("boxMinZ", -20), maxZ: number("boxMaxZ", 0)
      },
      cylinder: {
        diameter: number("cylDiameter", 60), innerDiameter: number("cylInner", 0),
        centerX: number("cylCenterX", 0), centerY: number("cylCenterY", 0),
        minZ: number("cylMinZ", -40), maxZ: number("cylMaxZ", 0), length: number("cylLength", 40), frontZ: number("cylFrontZ", 0)
      },
      stl: { path: elements.stlPath.value.trim(), name: stlName, offset: { x: number("stlX", 0), y: number("stlY", 0), z: number("stlZ", 0) } }
    };
    if (stlWorkCs) definition.stl.workCs = stlWorkCs;
    return definition;
  }

  function writeForm() {
    elements.type.value = definition.type || "none";
    elements.offset.value = definition.workOffset || "G54";
    const box = definition.box || {};
    setNumber("boxMinX", box.minX); setNumber("boxMaxX", box.maxX);
    setNumber("boxMinY", box.minY); setNumber("boxMaxY", box.maxY);
    setNumber("boxMinZ", box.minZ); setNumber("boxMaxZ", box.maxZ);
    const cylinder = definition.cylinder || {};
    setNumber("cylDiameter", cylinder.diameter); setNumber("cylInner", cylinder.innerDiameter);
    setNumber("cylCenterX", cylinder.centerX); setNumber("cylCenterY", cylinder.centerY);
    setNumber("cylMinZ", cylinder.minZ); setNumber("cylMaxZ", cylinder.maxZ);
    setNumber("cylLength", cylinder.length); setNumber("cylFrontZ", cylinder.frontZ);
    elements.stlPath.value = definition.stl?.path || "";
    stlWorkCs = definition.stl?.workCs && typeof definition.stl.workCs === "object" ? definition.stl.workCs : null;
    setNumber("stlX", definition.stl?.offset?.x ?? 0);
    setNumber("stlY", definition.stl?.offset?.y ?? 0);
    setNumber("stlZ", definition.stl?.offset?.z ?? 0);
    refreshTypeFields();
  }

  function refreshTypeFields() {
    const lathe = model?.kind === "lathe";
    const type = elements.type.value;
    elements.boxFields.hidden = type !== "box" || lathe;
    elements.cylinderFields.hidden = type !== "cylinder";
    elements.stlFields.hidden = type !== "stl";
    for (const row of elements.cylinderFields.querySelectorAll("[data-for]")) {
      row.hidden = !String(row.dataset.for).split(" ").includes(lathe ? "lathe" : "mill");
    }
    elements.offset.parentElement.hidden = lathe;
    for (const option of elements.type.options) {
      if (option.value === "box") option.hidden = lathe;
    }
    // A lathe has no box stock. The page first shows a blank lathe program, so a box chosen meanwhile
    // comes back with the milling program instead of staying a cylinder.
    if (lathe && type === "box") {
      elements.type.value = "cylinder";
      boxBeforeLathe = true;
    } else if (!lathe && model && boxBeforeLathe && type === "cylinder") {
      elements.type.value = "box";
      boxBeforeLathe = false;
      elements.boxFields.hidden = false;
      elements.cylinderFields.hidden = true;
    } else if (!lathe && model) {
      boxBeforeLathe = false;
    }
  }

  function fillWorkOffsets() {
    const current = elements.offset.value || "G54";
    elements.offset.replaceChildren();
    const labels = (model?.workOffsetTable?.rows || []).map((row) => row.label).filter((label) => label !== "EXT");
    for (const label of labels.length ? labels : ["G54", "G55", "G56", "G57", "G58", "G59"]) {
      const option = document.createElement("option");
      option.value = label;
      option.textContent = label;
      elements.offset.appendChild(option);
    }
    elements.offset.value = [...elements.offset.options].some((option) => option.value === current) ? current : "G54";
  }

  // Work offset labels as the program writes them ("G54.1 P1", "G54.1P1", "G154 P1", "G110", "G15 H1").
  const sameOffset = (a, b) => String(a || "").toUpperCase().replace(/\s+/g, "") === String(b || "").toUpperCase().replace(/\s+/g, "");

  // A work offset's machine position on one axis: the value saved under Home offsets, the program's
  // initial variable, else the machine default (MRZP). `known` is false for the default.
  function offsetValue(label, axis) {
    const table = model?.workOffsetTable;
    const row = (table?.rows || []).find((candidate) => sameOffset(candidate.label, label));
    const entry = row?.values?.[axis];
    if (Number.isFinite(entry?.saved)) return { value: entry.saved, known: true };
    if (Number.isFinite(entry?.initial)) return { value: entry.initial, known: true };
    const fallback = table?.defaults?.[axis];
    return { value: Number.isFinite(fallback) ? fallback : 0, known: false, row: Boolean(row) };
  }

  // The shift from the drawing frame (the first work offset the program selects; every toolpath point
  // is relative to it) to the work offset the stock is measured from.
  function offsetShift() {
    if (!model || model.kind === "lathe") return { x: 0, y: 0, z: 0 };
    const frameLabel = model.frame?.label;
    const target = elements.offset.value || "G54";
    if (!model.workOffsetTable || !frameLabel || sameOffset(target, frameLabel)) return { x: 0, y: 0, z: 0 };
    const axis = (name) => offsetValue(target, name).value - offsetValue(frameLabel, name).value;
    return { x: axis("X"), y: axis("Y"), z: axis("Z") };
  }

  // Says what the stock is measured from, so a stock placed on an offset without values is not mistaken
  // for one placed on the real fixture.
  function describeReference() {
    if (!elements.reference) return;
    if (!model || model.kind === "lathe") {
      elements.reference.textContent = "";
      return;
    }
    const target = elements.offset.value || "G54";
    const frameLabel = model.frame?.label || "G54";
    if (sameOffset(target, frameLabel)) {
      elements.reference.textContent = `Measured from ${target}, the program zero (the work offset the toolpath is drawn in).`;
      return;
    }
    const unknown = ["X", "Y", "Z"].filter((axis) => !offsetValue(target, axis).known || !offsetValue(frameLabel, axis).known);
    const shift = offsetShift();
    const fixed = (value) => (Math.abs(value) < 5e-4 ? "0" : value.toFixed(3).replace(/\.?0+$/, ""));
    elements.reference.textContent = unknown.length
      ? `⚠ ${target} or ${frameLabel} has no values here (${unknown.join(", ")}), so ${target} is placed by the machine default. Enter both under Home offsets, or measure the stock from ${frameLabel}.`
      : `Measured from ${target}: X${fixed(shift.x)} Y${fixed(shift.y)} Z${fixed(shift.z)} from the program zero ${frameLabel}.`;
  }

  function cutBounds() {
    const bounds = { minX: Infinity, maxX: -Infinity, minY: Infinity, maxY: -Infinity, minZ: Infinity, maxZ: -Infinity };
    for (const segment of model?.segments || []) {
      if (!isCutting(segment)) continue;
      for (const point of segment.points || []) {
        const x = model.kind === "lathe" ? (Number.isFinite(point.r) ? point.r : point.x / 2) : point.x;
        if (x < bounds.minX) bounds.minX = x;
        if (x > bounds.maxX) bounds.maxX = x;
        if (Number.isFinite(point.y)) {
          if (point.y < bounds.minY) bounds.minY = point.y;
          if (point.y > bounds.maxY) bounds.maxY = point.y;
        }
        if (point.z < bounds.minZ) bounds.minZ = point.z;
        if (point.z > bounds.maxZ) bounds.maxZ = point.z;
      }
    }
    return Number.isFinite(bounds.minX) ? bounds : undefined;
  }

  function stockFromToolpath() {
    const bounds = cutBounds();
    if (!bounds) {
      status("The program has no cutting moves to size the stock from.", "warning");
      return;
    }
    const margin = Math.max(1, (bounds.maxX - bounds.minX) * 0.05);
    // The toolpath is in the program zero; the fields are measured from the chosen offset.
    const shift = offsetShift();
    for (const [low, high, axis] of [["minX", "maxX", "x"], ["minY", "maxY", "y"], ["minZ", "maxZ", "z"]]) {
      if (Number.isFinite(bounds[low])) bounds[low] -= shift[axis];
      if (Number.isFinite(bounds[high])) bounds[high] -= shift[axis];
    }
    if (model.kind === "lathe") {
      const od = Math.ceil((bounds.maxX + margin) * 2);
      const frontZ = Math.max(0, bounds.maxZ);
      elements.type.value = "cylinder";
      setNumber("cylDiameter", od);
      setNumber("cylInner", 0);
      setNumber("cylLength", Math.ceil(frontZ - bounds.minZ + margin));
      setNumber("cylFrontZ", frontZ);
    } else {
      elements.type.value = "box";
      setNumber("boxMinX", Math.floor(bounds.minX - margin));
      setNumber("boxMaxX", Math.ceil(bounds.maxX + margin));
      setNumber("boxMinY", Math.floor((Number.isFinite(bounds.minY) ? bounds.minY : 0) - margin));
      setNumber("boxMaxY", Math.ceil((Number.isFinite(bounds.maxY) ? bounds.maxY : 0) + margin));
      setNumber("boxMinZ", Math.floor(bounds.minZ - 1));
      // The part top: Z0 of the chosen offset, or the highest cut when the program cuts above it.
      const top = -shift.z;
      setNumber("boxMaxZ", bounds.maxZ <= top ? Math.round(top * 1000) / 1000 : Math.ceil(bounds.maxZ));
    }
    refreshTypeFields();
    applyStock();
  }

  // ----- model -------------------------------------------------------------------------------

  const RETURN_KINDS = new Set(["g30", "home", "tool-change", "rapid"]);
  function isCutting(segment) {
    return !RETURN_KINDS.has(segment.kind) && !segment.nonCutting && Array.isArray(segment.points) && segment.points.length > 0;
  }

  function buildChain() {
    const definition_ = model?.machineDefinition;
    const kinematics = window.CncKinematics;
    if (!definition_?.kinematics?.nodes?.length || !kinematics || model.kind !== "mill") return undefined;
    const scale = model.view?.unitScale || 1;
    try {
      return kinematics.buildChain({
        nodes: definition_.kinematics.nodes.map((node) => ({
          ...node,
          origin: Array.isArray(node.origin) ? node.origin.map((value) => value * scale) : undefined,
          pivot: Array.isArray(node.pivot) ? node.pivot.map((value) => value * scale) : undefined
        }))
      });
    } catch {
      return undefined;
    }
  }

  // Machine axes outside their travel at the end of each move (linear axes only when the work offsets
  // have real values, so machine positions are known).
  function travelChecker() {
    const axes = window.MeimadAxes;
    if (!model || model.kind === "lathe" || !axes?.createCalculator || !axes.machineAxes) return null;
    const calculator = axes.createCalculator(model, window.CncKinematics);
    const known = !(model.meta?.defaultedWorkOffsets || []).length;
    const limits = axes.machineAxes(model).filter((axis) => !axis.wraps && (axis.limitMin !== null || axis.limitMax !== null)
      && (axis.type === "rotary" || known));
    if (!limits.length) return null;
    return (segment) => {
      const points = segment.points || [];
      if (!points.length) return undefined;
      const rotary = segment.rotary || {};
      const joints = calculator.joints(points[points.length - 1], { A: rotary.a1 || 0, B: rotary.b1 || 0, C: rotary.c1 || 0 }, segment.toolLength);
      if (!joints) return undefined;
      const outside = [];
      for (const axis of limits) {
        const value = joints[axis.axis];
        if (!Number.isFinite(value)) continue;
        if ((axis.limitMin !== null && value < axis.limitMin - 1e-3) || (axis.limitMax !== null && value > axis.limitMax + 1e-3)) {
          outside.push({ axis: axis.label || axis.axis, value, min: axis.limitMin, max: axis.limitMax });
        }
      }
      return outside.length ? outside : undefined;
    };
  }

  function compactSegments() {
    const chain = buildChain();
    const travel = travelChecker();
    axisLimitMoves = 0;
    tiltedMoves = 0;
    let elapsed = 0;
    timeline = [];
    compact = (model.segments || []).map((segment, index) => {
      if (!Number.isFinite(segment.executionIndex)) segment.executionIndex = index;
      const duration = Number.isFinite(segment.estimatedSeconds) ? Math.max(0, segment.estimatedSeconds) : 0;
      timeline.push({ index, tool: segment.tool, line: segment.line, startSeconds: elapsed, endSeconds: elapsed + duration, duration });
      elapsed += duration;
      // The tool axis in the part's frame (the frame the points are in): one vector for a fixed
      // rotary position, one per point when the rotaries move during the segment. The points of
      // such a move are sampled uniformly over the rotary sweep, so the angles interpolate by index.
      let axis;
      let axes;
      const rotary = segment.rotary;
      if (chain && rotary && (rotary.a0 || rotary.b0 || rotary.c0 || rotary.a1 || rotary.b1 || rotary.c1)) {
        try {
          const toolAxis = (t) => window.CncKinematics.toolAxisInWorkpiece(chain, {
            A: (rotary.a0 || 0) + ((rotary.a1 || 0) - (rotary.a0 || 0)) * t,
            B: (rotary.b0 || 0) + ((rotary.b1 || 0) - (rotary.b0 || 0)) * t,
            C: (rotary.c0 || 0) + ((rotary.c1 || 0) - (rotary.c0 || 0)) * t
          });
          const turns = ["a", "b", "c"].some((name) => Math.abs((rotary[`${name}1`] || 0) - (rotary[`${name}0`] || 0)) > 1e-6);
          const count = segment.points?.length || 0;
          if (turns && count > 1) axes = segment.points.map((_, pointIndex) => toolAxis(pointIndex / (count - 1)));
          else axis = toolAxis(1);
          const tilted = axes ? axes.some((value) => value.z < 0.9999) : axis.z < 0.9999;
          if (tilted && isCutting(segment)) tiltedMoves += 1;
        } catch {
          axis = undefined;
          axes = undefined;
        }
      }
      const axisLimit = travel ? travel(segment) : undefined;
      if (axisLimit) axisLimitMoves += 1;
      return {
        tool: segment.tool,
        cutting: isCutting(segment),
        rapid: segment.kind === "rapid",
        nonCutting: Boolean(segment.nonCutting),
        line: segment.line,
        sourceUnit: segment.sourceUnit,
        sourceLine: segment.sourceLine,
        axisLimit,
        points: (segment.points || []).map((point) => model.kind === "lathe"
          ? { x: point.x, z: point.z, r: Number.isFinite(point.r) ? point.r : point.x / 2 }
          : { x: point.x, y: point.y ?? 0, z: point.z }),
        toolRadius: segment.toolRadius,
        toolType: segment.toolType,
        toolCornerRadius: segment.toolCornerRadius,
        toolTip: segment.toolTip,
        toolLength: Number.isFinite(segment.toolLength) && segment.toolLength > 0 ? segment.toolLength : undefined,
        axis,
        axes
      };
    });
  }

  // An empty Resolution field: a mill grid sized from the stock and the smallest cutting tool, so narrow
  // slots and small tools show; a lathe keeps the engine's own automatic cell.
  function autoResolution(stock, stl) {
    if (!model || model.kind === "lathe" || !window.MeimadMaterial?.autoMillResolution) return undefined;
    const extent = stl ? window.MeimadMaterial.bounds(stl) : stock;
    if (!extent || !Number.isFinite(extent.maxX)) return undefined;
    const span = Math.max(extent.maxX - extent.minX, extent.maxY - extent.minY);
    const tools = toolsByNumber();
    let smallest = Infinity;
    for (const segment of compact) {
      if (!segment.cutting) continue;
      const definition = tools[segment.tool] || {};
      const radius = Number.isFinite(segment.toolRadius) && segment.toolRadius > 0
        ? segment.toolRadius
        : Number.isFinite(definition.diameter) ? definition.diameter / 2 : 0;
      const cutter = window.MeimadMaterial.coneBody({ radius, type: segment.toolType || definition.type }, definition.description);
      if (cutter.radius >= 0.05 && cutter.radius < smallest) smallest = cutter.radius;
    }
    return window.MeimadMaterial.autoMillResolution(span, Number.isFinite(smallest) ? smallest : undefined);
  }

  // Mill tools whose cut differs from the program's tool data: cone tools cut with the body size their
  // name gives (CAM posts write the tip diameter), and cutting tools with no usable diameter.
  function toolNotes() {
    const sized = [];
    const missing = [];
    if (!model || model.kind === "lathe" || !window.MeimadMaterial?.coneBody) return { sized, missing };
    const tools = toolsByNumber();
    const seen = new Set();
    for (const segment of compact) {
      if (!segment.cutting || seen.has(segment.tool)) continue;
      seen.add(segment.tool);
      const definition = tools[segment.tool] || {};
      const radius = Number.isFinite(segment.toolRadius) && segment.toolRadius > 0
        ? segment.toolRadius
        : Number.isFinite(definition.diameter) ? definition.diameter / 2 : 0;
      const cutter = window.MeimadMaterial.coneBody({ radius, type: segment.toolType || definition.type }, definition.description);
      if (cutter.bodyFromName) sized.push(`T${segment.tool} Ø${(cutter.radius * 2).toFixed(1)}`);
      else if (!(cutter.radius >= 0.05)) missing.push(`T${segment.tool}`);
    }
    return { sized, missing };
  }

  function toolsByNumber() {
    const tools = {};
    for (const tool of model?.toolDefinitions || []) {
      tools[tool.number] = {
        diameter: tool.diameter, cornerRadius: tool.cornerRadius, type: tool.type, tip: tool.tip, width: tool.width, length: tool.length,
        pointAngle: tool.pointAngle, taperAngle: tool.taperAngle, description: tool.description
      };
    }
    return tools;
  }

  // ----- worker --------------------------------------------------------------------------------

  function stopWorker() {
    if (worker) {
      worker.terminate();
      worker = undefined;
    }
    workerReady = false;
    workerBusy = false;
  }

  function stockForWorker() {
    const shift = offsetShift();
    if (model.kind === "lathe") {
      const cylinder = definition.cylinder || {};
      return { od: cylinder.diameter, id: cylinder.innerDiameter || 0, length: cylinder.length, frontZ: cylinder.frontZ ?? 0 };
    }
    if (definition.type === "cylinder") {
      const cylinder = definition.cylinder || {};
      const radius = (cylinder.diameter || 0) / 2;
      return {
        minX: cylinder.centerX + shift.x - radius, maxX: cylinder.centerX + shift.x + radius,
        minY: cylinder.centerY + shift.y - radius, maxY: cylinder.centerY + shift.y + radius,
        minZ: cylinder.minZ + shift.z, maxZ: cylinder.maxZ + shift.z,
        cylinder: { cx: cylinder.centerX + shift.x, cy: cylinder.centerY + shift.y, radius, innerRadius: (cylinder.innerDiameter || 0) / 2 }
      };
    }
    if (definition.type === "stl") {
      const offset = definition.stl?.offset || { x: 0, y: 0, z: 0 };
      return { offset: { x: offset.x + shift.x, y: offset.y + shift.y, z: offset.z + shift.z } };
    }
    const box = definition.box || {};
    return {
      minX: box.minX + shift.x, maxX: box.maxX + shift.x,
      minY: box.minY + shift.y, maxY: box.maxY + shift.y,
      minZ: box.minZ + shift.z, maxZ: box.maxZ + shift.z
    };
  }

  function validDefinition() {
    if (!model) return false;
    if (definition.type === "none") return false;
    if (model.kind === "lathe") return definition.type !== "stl" && definition.cylinder?.diameter > 0 && definition.cylinder?.length > 0;
    if (definition.type === "stl") return Boolean(stlPositions && stlPositions.length >= 9);
    if (definition.type === "cylinder") return definition.cylinder?.diameter > 0 && definition.cylinder.maxZ > definition.cylinder.minZ;
    return definition.box?.maxX > definition.box?.minX && definition.box?.maxY > definition.box?.minY && definition.box?.maxZ > definition.box?.minZ;
  }

  function startWorker() {
    stopWorker();
    if (!elements.toggle.checked || !validDefinition()) {
      showProgress(false);
      return;
    }
    const voxel = model.kind !== "lathe" && engineChoice() === "voxel";
    try {
      worker = new Worker(voxel ? "./meimad-voxel-worker.js" : "./meimad-material-worker.js");
    } catch (error) {
      status(`Material removal is unavailable: ${message(error)}`, "error");
      return;
    }
    worker.onmessage = onWorkerMessage;
    worker.onerror = (event) => status(`Material removal stopped: ${event.message || "worker error"}`, "error");
    const stl = definition.type === "stl" && model.kind !== "lathe" ? stlPositions : undefined;
    const stockDefinition = stockForWorker();
    workerEngine = voxel ? "voxel" : "column";
    events = [];
    lastStats = null;
    deviationAsked = false;
    renderEvents();
    renderStats();
    elements.deviationResult.textContent = "";
    if (voxel) {
      const resolution = Number(elements.resolution.value) || autoResolution(stockDefinition, stl);
      const shift = stockDefinition.offset || { x: 0, y: 0, z: 0 };
      const stock = stl ? { positions: shifted(stl, shift) } : stockDefinition;
      worker.postMessage({
        type: "init",
        stock,
        resolution,
        tools: toolsByNumber(),
        assemblies,
        motions: compact,
        fixtures: sceneFixtures.map((fixture) => ({ name: fixture.name, positions: fixture.positions })),
        target: partPositions,
        policy: currentPolicy(),
        collisions: elements.collisions.checked,
        extent: extentOf([...sceneFixtures.map((fixture) => fixture.positions), partPositions]),
        filterTool,
        deviation: elements.deviation.checked,
        backend: elements.backend && elements.backend.value === "cpu" ? "cpu" : "auto"
      });
      lastTarget = { index: -2, fraction: 0 };
      showProgress(true);
      setProgress(0, compact.length, "Preparing the stock…");
      return;
    }
    worker.postMessage({
      type: "init",
      kind: model.kind,
      stock: stockDefinition,
      resolution: Number(elements.resolution.value) || autoResolution(stockDefinition, stl),
      tools: toolsByNumber(),
      segments: compact,
      filterTool: filterTool,
      stl: stl ? new Float32Array(stl) : undefined
    });
    lastTarget = { index: -2, fraction: 0 };
    showProgress(true);
    setProgress(0, compact.length, "Preparing the stock…");
  }

  function onWorkerMessage(event) {
    const data = event.data || {};
    if (data.type === "ready" && workerEngine === "voxel") {
      workerReady = true;
      const notes = Object.entries(data.notes || {}).filter(([, list]) => list && list.length)
        .map(([tool, list]) => `T${tool}: ${list.join("; ")}`);
      elements.note.textContent = `3D stock: ${Number(data.resolution).toFixed(3)} mm voxels, computed on ${data.backend || "CPU"} · grey: stock faces, cyan to violet: cut depth` +
        (tiltedMoves ? ` · ${tiltedMoves} tilted-axis cutting move(s)` : "") +
        (notes.length ? ` · Estimated tool geometry: ${notes.join(" · ")}` : "");
      followPlayback();
      return;
    }
    if (data.type === "chunks") {
      applyChunks(data);
      return;
    }
    if (data.type === "backend") {
      status(`Material removal continues on the ${data.note || "CPU"}.`, "warning");
      return;
    }
    if (data.type === "events") {
      receiveEvents(data);
      return;
    }
    if (data.type === "stats") {
      lastStats = data.stats;
      renderStats();
      window.dispatchEvent(new CustomEvent("meimad:verification", { detail: { events, stats: lastStats } }));
      if (!workerBusy && elements.deviation.checked && partPositions && !deviationAsked && lastStats.cursor >= compact.length - 1) {
        deviationAsked = true;
        worker.postMessage({ type: "deviation", limitMm: 10 });
      }
      return;
    }
    if (data.type === "deviation") {
      renderDeviation(data.result);
      return;
    }
    if (data.type === "journal") {
      copyJournal(data.rows);
      return;
    }
    if (data.type === "ready") {
      workerReady = true;
      const notes = toolNotes();
      elements.note.textContent = `Stock grid: ${data.cells.toLocaleString()} cells at ${Number(data.resolution).toFixed(3)} mm` +
        (model && model.kind !== "lathe" ? " · grey: untouched stock faces, cyan to violet: deeper cuts" : "") +
        (tiltedMoves ? ` · ${tiltedMoves} tilted-axis cutting move(s)` : "") +
        (notes.sized.length ? ` · Cut with the body size from the tool name: ${notes.sized.join(", ")}` : "");
      if (notes.missing.length) {
        status(`No diameter for ${notes.missing.join(", ")}: their paths remove almost no material. Enter the diameter in the tool table.`, "warning");
      }
      followPlayback();
      return;
    }
    if (data.type === "progress") {
      workerBusy = Boolean(data.busy);
      setProgress(data.done, data.total, workerBusy ? "Cutting…" : undefined);
      return;
    }
    if (data.type === "surface") {
      updateMesh(data.positions, data.kind, data.cut);
      return;
    }
    if (data.type === "exported") {
      exportResolve?.(data.stl);
      exportResolve = undefined;
      return;
    }
    if (data.type === "error") status(`Material removal error: ${data.message}`, "error");
  }

  let exportResolve;
  function exportStl() {
    return new Promise((resolve, reject) => {
      if (!worker || !workerReady) {
        reject(new Error("Turn material removal on and let the cut finish before exporting the stock."));
        return;
      }
      exportResolve = resolve;
      worker.postMessage({ type: "export", header: `Meimad Planner machined stock ${byId("editorDocumentName")?.textContent || ""}` });
      setTimeout(() => {
        if (exportResolve === resolve) {
          exportResolve = undefined;
          reject(new Error("The machined stock could not be exported in time."));
        }
      }, 30000);
    });
  }

  // ----- mesh ------------------------------------------------------------------------------------

  // The shared camera-following rig (meimad-lighting.js), so cut floors and walls read by their shading.
  function ensureLights() {
    if (hooks && window.MeimadLighting) lights = window.MeimadLighting.ensure(hooks);
  }

  function updateMesh(positions, kind, cut) {
    if (!hooks) return;
    const THREE = hooks.THREE;
    ensureLights();
    if (!mesh) {
      const geometry = new THREE.BufferGeometry();
      // Faceted, with a color per vertex (depthColors) so cut floors and walls stand apart from the top.
      const material = new THREE.MeshStandardMaterial({
        color: 0xffffff, vertexColors: true, flatShading: true, roughness: 0.82, metalness: 0.05, side: THREE.DoubleSide
      });
      mesh = new THREE.Mesh(geometry, material);
      mesh.renderOrder = 1;
      mesh.name = "meimad-stock-columns";
      mesh.frustumCulled = false;
      hooks.contentRoot.add(mesh);
    }
    mesh.geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
    mesh.geometry.setAttribute("color", new THREE.BufferAttribute(depthColors(positions, kind, cut), 3));
    mesh.geometry.computeVertexNormals();
    mesh.geometry.computeBoundingSphere();
    mesh.visible = true;
    mesh.userData.kind = kind;
    removeOutline();
    hooks.upstream.render();
  }

  // Vertex colors are linear in three.js; the palette below is written as on-screen (sRGB) values.
  const toLinear = (value) => (value <= 0.04045 ? value / 12.92 : Math.pow((value + 0.055) / 1.055, 2.4));

  // Mill: the stock's own faces (the blank, or the previous Operation's rest material) grey; faces this
  // program cut from light cyan (shallow) to violet (deep) by their depth below the nearest face of
  // the stock's box (the top, a side or the bottom), so a pocket cut from the side by a tilted tool
  // reads like one cut from above. `cut` (one flag per triangle) marks the cut faces; without it a face
  // counts as cut once it lies below the box. Lathe: one light steel color (the shape reads from the
  // faceted shading).
  function depthColors(positions, kind, cut) {
    const colors = new Float32Array(positions.length);
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity, minZ = Infinity, maxZ = -Infinity;
    for (let index = 0; index + 2 < positions.length; index += 3) {
      const x = positions[index];
      const y = positions[index + 1];
      const z = positions[index + 2];
      if (x < minX) minX = x;
      if (x > maxX) maxX = x;
      if (y < minY) minY = y;
      if (y > maxY) maxY = y;
      if (z < minZ) minZ = z;
      if (z > maxZ) maxZ = z;
    }
    const depthOf = (x, y, z) => Math.min(x - minX, maxX - x, y - minY, maxY - y, z - minZ, maxZ - z);
    // The deepest a cut can reach: the middle of the box's thinnest direction.
    const range = Math.max(1e-6, Math.min(maxX - minX, maxY - minY, maxZ - minZ) / 2);
    const untouched = Math.max(0.02, Math.max(maxX - minX, maxY - minY, maxZ - minZ) * 0.002);
    const stockR = toLinear(0.70), stockG = toLinear(0.72), stockB = toLinear(0.75);
    const flags = cut && cut.length * 9 === positions.length ? cut : null;
    for (let index = 0; index + 2 < positions.length; index += 3) {
      let r = stockR, g = stockG, b = stockB;
      const isCut = flags ? flags[Math.floor(index / 9)] === 1 : true;
      if (kind !== "lathe" && isCut) {
        const depth = depthOf(positions[index], positions[index + 1], positions[index + 2]);
        if (flags || depth > untouched) {
          const t = Math.min(1, Math.max(0, depth) / range);
          // HSL hue 190 deg (cyan) to 275 deg (violet), slightly lighter when shallow. The lightness
          // stays high so the lighting, not the color, shows floors, walls and fillets apart.
          const hue = (190 + 85 * t) / 360;
          const lightness = 0.74 - 0.1 * t;
          const saturation = 0.42;
          const q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
          const p = 2 * lightness - q;
          const channel = (h) => {
            const x = h < 0 ? h + 1 : h > 1 ? h - 1 : h;
            if (x < 1 / 6) return p + (q - p) * 6 * x;
            if (x < 1 / 2) return q;
            if (x < 2 / 3) return p + (q - p) * (2 / 3 - x) * 6;
            return p;
          };
          r = toLinear(channel(hue + 1 / 3));
          g = toLinear(channel(hue));
          b = toLinear(channel(hue - 1 / 3));
        }
      }
      colors[index] = r;
      colors[index + 1] = g;
      colors[index + 2] = b;
    }
    return colors;
  }

  function removeMesh() {
    clearChunks();
    clearMarkers();
    if (mesh && hooks) {
      hooks.contentRoot.remove(mesh);
      mesh.geometry.dispose();
      mesh.material.dispose();
      mesh = undefined;
      hooks.upstream.render();
    }
  }

  function removeOutline() {
    if (outline && hooks) {
      hooks.contentRoot.remove(outline);
      outline.traverse?.((child) => { child.geometry?.dispose?.(); child.material?.dispose?.(); });
      outline = undefined;
    }
  }

  // Material removal off: the stock as a transparent outline so its placement can be checked.
  function showOutline() {
    removeOutline();
    if (!hooks || !model || !validDefinition() || definition.type === "stl") return;
    const THREE = hooks.THREE;
    const stock = stockForWorker();
    outline = new THREE.Group();
    const material = new THREE.MeshBasicMaterial({ color: 0x6e7681, transparent: true, opacity: 0.14, depthWrite: false, side: THREE.DoubleSide });
    const edgeMaterial = new THREE.LineBasicMaterial({ color: 0x8b949e, transparent: true, opacity: 0.4 });
    let geometry;
    if (model.kind === "lathe") {
      const outer = stock.od / 2;
      const inner = Math.max(0, stock.id / 2);
      geometry = new THREE.LatheGeometry([
        new THREE.Vector2(inner, 0), new THREE.Vector2(outer, 0),
        new THREE.Vector2(outer, stock.length), new THREE.Vector2(inner, stock.length)
      ], 48);
      const solid = new THREE.Mesh(geometry, material);
      solid.rotation.x = Math.PI / 2;
      solid.position.z = stock.frontZ - stock.length;
      const edges = new THREE.LineSegments(new THREE.EdgesGeometry(geometry, 30), edgeMaterial);
      edges.rotation.copy(solid.rotation);
      edges.position.copy(solid.position);
      outline.add(solid, edges);
    } else if (stock.cylinder) {
      const height = stock.maxZ - stock.minZ;
      geometry = new THREE.CylinderGeometry(stock.cylinder.radius, stock.cylinder.radius, height, 48);
      const solid = new THREE.Mesh(geometry, material);
      solid.rotation.x = Math.PI / 2;
      solid.position.set(stock.cylinder.cx, stock.cylinder.cy, stock.minZ + height / 2);
      const edges = new THREE.LineSegments(new THREE.EdgesGeometry(geometry, 30), edgeMaterial);
      edges.rotation.copy(solid.rotation);
      edges.position.copy(solid.position);
      outline.add(solid, edges);
    } else {
      geometry = new THREE.BoxGeometry(stock.maxX - stock.minX, stock.maxY - stock.minY, stock.maxZ - stock.minZ);
      const solid = new THREE.Mesh(geometry, material);
      solid.position.set((stock.minX + stock.maxX) / 2, (stock.minY + stock.maxY) / 2, (stock.minZ + stock.maxZ) / 2);
      const edges = new THREE.LineSegments(new THREE.EdgesGeometry(geometry), edgeMaterial);
      edges.position.copy(solid.position);
      outline.add(solid, edges);
    }
    hooks.contentRoot.add(outline);
    hooks.upstream.render();
  }

  // ----- 3D stock surface, events and the finished part -------------------------------------------

  function engineChoice() {
    return elements.engine && elements.engine.value === "column" ? "column" : "voxel";
  }

  function shifted(positions, shift) {
    if (!shift || (!shift.x && !shift.y && !shift.z)) return new Float32Array(positions);
    const out = new Float32Array(positions.length);
    for (let index = 0; index + 2 < positions.length; index += 3) {
      out[index] = positions[index] + shift.x;
      out[index + 1] = positions[index + 1] + shift.y;
      out[index + 2] = positions[index + 2] + shift.z;
    }
    return out;
  }

  function extentOf(list) {
    let box = null;
    for (const positions of list) {
      if (!positions || positions.length < 9) continue;
      const b = window.MeimadVoxel ? window.MeimadVoxel.bounds(positions) : null;
      if (!b) continue;
      box = box ? window.MeimadVoxel.union(box, b) : b;
    }
    return box;
  }

  function ensureChunkGroup() {
    if (!hooks) return null;
    const THREE = hooks.THREE;
    ensureLights();
    if (!voxelGroup) {
      voxelGroup = new THREE.Group();
      voxelGroup.name = "meimad-stock-3d";
      chunkMaterial = new THREE.MeshStandardMaterial({ color: 0xffffff, vertexColors: true, roughness: 0.82, metalness: 0.05 });
    }
    if (voxelGroup.parent !== hooks.contentRoot) hooks.contentRoot.add(voxelGroup);
    return voxelGroup;
  }

  function clearChunks() {
    for (const chunk of chunkMeshes.values()) {
      chunk.parent?.remove(chunk);
      chunk.geometry.dispose();
    }
    chunkMeshes.clear();
  }

  function applyChunks(data) {
    const group = ensureChunkGroup();
    if (!group) return;
    const THREE = hooks.THREE;
    if (data.reset) clearChunks();
    for (const key of data.empty || []) {
      const old = chunkMeshes.get(key);
      if (!old) continue;
      group.remove(old);
      old.geometry.dispose();
      chunkMeshes.delete(key);
    }
    for (const part of data.meshes || []) {
      const geometry = new THREE.BufferGeometry();
      geometry.setAttribute("position", new THREE.BufferAttribute(part.positions, 3));
      geometry.setAttribute("normal", new THREE.BufferAttribute(part.normals, 3));
      geometry.setAttribute("color", new THREE.BufferAttribute(part.colors, 3));
      geometry.setIndex(new THREE.BufferAttribute(part.index, 1));
      geometry.computeBoundingSphere();
      const old = chunkMeshes.get(part.key);
      if (old) {
        old.geometry.dispose();
        old.geometry = geometry;
      } else {
        const chunk = new THREE.Mesh(geometry, chunkMaterial);
        chunk.renderOrder = 1;
        chunk.userData.meimadStockChunk = part.key;
        group.add(chunk);
        chunkMeshes.set(part.key, chunk);
      }
    }
    if ((data.meshes || []).length || (data.empty || []).length || data.reset) {
      group.visible = true;
      removeOutline();
      hooks.upstream.render();
    }
  }

  function clearMarkers() {
    if (markerGroup) {
      markerGroup.parent?.remove(markerGroup);
      markerGroup.traverse((child) => { child.geometry?.dispose?.(); child.material?.dispose?.(); });
      markerGroup = undefined;
    }
  }

  // Red markers where a collision or gouge starts (a sphere and a short line along the tool axis).
  function drawMarkers() {
    clearMarkers();
    if (!hooks || !events.length) {
      hooks?.upstream.render();
      return;
    }
    const THREE = hooks.THREE;
    markerGroup = new THREE.Group();
    markerGroup.name = "meimad-collision-markers";
    const span = definition.box && Number.isFinite(definition.box.maxX) ? Math.max(definition.box.maxX - definition.box.minX, 20) : 100;
    const size = Math.max(1.5, span / 120);
    const sphere = new THREE.SphereGeometry(size, 16, 12);
    for (const event of events) {
      if (!event.position) continue;
      const color = event.severity === "warning" ? 0xffb020 : 0xff2a2a;
      const marker = new THREE.Mesh(sphere, new THREE.MeshBasicMaterial({ color, depthTest: false, transparent: true, opacity: 0.85 }));
      marker.position.set(event.position.x, event.position.y, event.position.z);
      marker.renderOrder = 9;
      marker.userData.meimadEvent = event.id;
      markerGroup.add(marker);
    }
    markerGroup.visible = window.meimadSceneFlags ? window.meimadSceneFlags.collisionMarkers() : true;
    hooks.contentRoot.add(markerGroup);
    hooks.upstream.render();
  }

  const SEVERITY_TEXT = { stop: "⏹ Stop", error: "⛔ Error", warning: "⚠ Warning" };
  const TYPE_TEXT = {
    HolderCollision: "Holder collision", FixtureCollision: "Fixture collision", RapidInMaterial: "Rapid in material",
    Gouge: "Gouge", AxisLimit: "Axis limit", MachineCollision: "Machine collision"
  };

  function receiveEvents(data) {
    const incoming = data.events || [];
    const fresh = data.replace ? incoming.filter((event) => !events.some((known) => known.id === event.id)) : incoming;
    events = data.replace ? incoming : events.concat(incoming);
    renderEvents();
    drawMarkers();
    if (fresh.some((event) => event.severity === "stop") && playbackState() === "running" && elements.pauseButton && !elements.pauseButton.disabled) {
      const stop = fresh.find((event) => event.severity === "stop");
      elements.pauseButton.click();
      status(`Playback stopped: ${TYPE_TEXT[stop.type] || stop.type} at row ${stop.line ?? "?"} (T${stop.tool}). ${stop.message}`, "error");
    }
    window.dispatchEvent(new CustomEvent("meimad:verification", { detail: { events, stats: lastStats } }));
  }

  function renderEvents() {
    if (!elements.events) return;
    elements.events.replaceChildren();
    for (const event of events.slice(0, 500)) {
      const item = document.createElement("li");
      item.className = `meimad-event severity-${event.severity}`;
      item.tabIndex = 0;
      const row = event.sourceUnit ? `${event.sourceUnit} row ${event.sourceLine ?? "?"}` : `row ${event.line ?? "?"}`;
      const span = event.lastIndex > event.executionIndex ? ` (${event.lastIndex - event.executionIndex + 1} moves)` : "";
      const estimated = event.estimatedGeometry ? " · estimated tool geometry" : "";
      item.textContent = `${SEVERITY_TEXT[event.severity] || event.severity} · ${TYPE_TEXT[event.type] || event.type} · ${row} · T${event.tool}${span} — ${event.message}${estimated}`;
      item.title = "Go to this move";
      const go = () => goToEvent(event);
      item.addEventListener("click", go);
      item.addEventListener("keydown", (key) => { if (key.key === "Enter") go(); });
      elements.events.appendChild(item);
    }
    if (events.length > 500) {
      const more = document.createElement("li");
      more.className = "meimad-event";
      more.textContent = `… ${events.length - 500} more`;
      elements.events.appendChild(more);
    }
  }

  function goToEvent(event) {
    const entry = timeline[event.executionIndex];
    if (entry) window.cncPreviewSeek?.(entry.startSeconds + entry.duration * 0.5);
    if (!event.sourceUnit && Number.isFinite(event.line)) {
      const cm = editor();
      if (cm) {
        cm.setCursor({ line: event.line - 1, ch: 0 });
        cm.scrollIntoView({ line: event.line - 1, ch: 0 }, 120);
      }
    }
  }

  function renderStats() {
    if (!elements.verifyStats) return;
    if (!lastStats || workerEngine !== "voxel") {
      elements.verifyStats.textContent = workerEngine === "column"
        ? "Collision checks and the finished-part comparison need the 3D stock."
        : axisLimitMoves ? `${axisLimitMoves} move(s) outside the machine travel.` : "";
      return;
    }
    const volume = (value) => `${Math.round(value).toLocaleString()} mm³`;
    const parts = [
      `Removed ${volume(lastStats.removedVolume)}`,
      `${lastStats.collisions} collision${lastStats.collisions === 1 ? "" : "s"}`,
      `${lastStats.gouges} gouge${lastStats.gouges === 1 ? "" : "s"}`
    ];
    if (lastStats.axisLimits) parts.push(`${lastStats.axisLimits} axis limit${lastStats.axisLimits === 1 ? "" : "s"}`);
    if (!elements.collisions.checked) parts.push("collision checks off");
    elements.verifyStats.textContent = parts.join(" · ");
  }

  function renderDeviation(result) {
    if (!result) {
      elements.deviationResult.textContent = "No finished part to compare with.";
      return;
    }
    const mm = (value) => `${Number(value).toFixed(2)} mm`;
    const volume = (value) => `${Math.round(value).toLocaleString()} mm³`;
    const gouge = result.gougeVoxelCount
      ? `⛔ Gouges ${volume(result.gougeVolume)}, deepest ≈ ${mm(result.maxGouge)}${result.firstGougeRow ? `, first at row ${result.firstGougeRow}` : ""}`
      : "✔ No gouges";
    const left = result.remainingVoxelCount
      ? `Left outside the part ${volume(result.remainingVolume)}, thickest ≈ ${result.remainingCapped ? "over " : ""}${mm(result.maxRemainingStock)}`
      : "✔ No material left outside the part";
    elements.deviationResult.textContent = `${gouge} · ${left}`;
  }

  // The finished part: the Operation's Cimatron part model, else an STL chosen here.
  async function refreshSceneGeometry() {
    if (!window.meimadSceneGeometry || !model || model.kind === "lathe") return;
    const placed = await window.meimadSceneGeometry.placed(["fixture", "part", "finished-part"]);
    sceneFixtures = placed.filter((item) => item.kind === "fixture");
    const part = placed.find((item) => item.kind === "part");
    if (part && !userPartPath) {
      partPositions = part.positions;
      partSourceText = `Finished part: ${part.name} (Operation setup)`;
    }
    renderPartSource();
  }

  function renderPartSource() {
    elements.partSource.textContent = partPositions
      ? partSourceText
      : "No finished part: link the part model in the Operation's Cimatron setup, or choose its STL.";
    elements.deviation.disabled = !partPositions;
  }

  async function loadPart(path) {
    try {
      const result = await meimad.stock.readStl(path);
      const bytes = Uint8Array.from(atob(result.base64), (character) => character.charCodeAt(0));
      partPositions = window.MeimadMaterial.readStl(bytes.buffer);
      userPartPath = path;
      elements.partPath.value = path;
      partSourceText = `Finished part: ${result.name || path} (${Math.floor(partPositions.length / 9).toLocaleString()} triangles)`;
      renderPartSource();
      return true;
    } catch (error) {
      status(`The finished part STL could not be read: ${message(error)}`, "error");
      return false;
    }
  }

  function currentPolicy() {
    const policy = {};
    for (const select of elements.policyRows?.querySelectorAll("select[data-check]") || []) policy[select.dataset.check] = select.value;
    return policy;
  }

  function buildPolicyRows() {
    if (!elements.policyRows) return;
    let stored = {};
    try { stored = JSON.parse(readStore(STORE.policy) || "{}") || {}; } catch { stored = {}; }
    const fromMachine = (model?.machineDefinition?.simulation?.collisionPolicy) || {};
    elements.policyRows.replaceChildren();
    for (const [check, label, fallback] of CHECKS) {
      const row = document.createElement("label");
      row.textContent = label;
      const select = document.createElement("select");
      select.dataset.check = check;
      for (const [value, text] of SEVERITIES) {
        const option = document.createElement("option");
        option.value = value;
        option.textContent = text;
        select.appendChild(option);
      }
      select.value = stored[check] || fromMachine[check] || fallback;
      select.addEventListener("change", () => {
        writeStore(STORE.policy, JSON.stringify(currentPolicy()));
        if (elements.toggle.checked) applyStock();
      });
      row.appendChild(select);
      elements.policyRows.appendChild(row);
    }
  }

  function copyJournal(rows) {
    const header = "index,row,program,program row,tool,removed mm3,sweeps,cut,collision,gouge,rapid in material,axis limit";
    const lines = rows.map((row) => [row.index, row.line ?? "", row.sourceUnit ?? "", row.sourceLine ?? "", row.tool,
      row.removedVolume.toFixed(3), row.sweeps, row.cut ? 1 : 0, row.collision ? 1 : 0, row.gouge ? 1 : 0, row.rapidInMaterial ? 1 : 0, row.axisLimit ? 1 : 0].join(","));
    const text = [header, ...lines].join("\n");
    const done = () => status(`Journal of ${rows.length.toLocaleString()} moves copied (CSV).`, "success");
    if (navigator.clipboard?.writeText) navigator.clipboard.writeText(text).then(done, (error) => status(message(error), "error"));
    else status("The clipboard is not available here.", "warning");
  }

  // ----- playback following ----------------------------------------------------------------------

  function playbackState() {
    return typeof window.cncPreviewState === "function" ? window.cncPreviewState().playback : "idle";
  }

  function polylineLength(points) {
    let total = 0;
    for (let index = 1; index < (points?.length || 0); index += 1) {
      const a = points[index - 1];
      const b = points[index];
      total += model.kind === "lathe"
        ? Math.hypot(b.z - a.z, (b.x - a.x) / 2)
        : Math.hypot(b.x - a.x, (b.y || 0) - (a.y || 0), b.z - a.z);
    }
    return total;
  }

  let lastPlayback;
  function followPlayback(playback = lastPlayback) {
    if (!worker || !workerReady) return;
    lastPlayback = playback;
    let target;
    const state = playbackState();
    if (!playback || state === "idle" || state === "complete") {
      target = { index: compact.length - 1, fraction: 1 };
    } else {
      const completed = Math.max(-1, playback.completedIndex);
      const activeIndex = completed + 1;
      let fraction = 0;
      if (playback.active?.points && compact[activeIndex]) {
        const full = polylineLength(model.segments[activeIndex]?.points);
        fraction = full > 0 ? Math.min(1, polylineLength(playback.active.points) / full) : 1;
      }
      target = activeIndex < compact.length ? { index: activeIndex, fraction } : { index: compact.length - 1, fraction: 1 };
      playbackDone = activeIndex;
    }
    if (target.index === lastTarget.index && Math.abs(target.fraction - lastTarget.fraction) < 0.02) return;
    lastTarget = target;
    worker.postMessage({ type: "cutTo", index: target.index, fraction: target.fraction });
  }

  // Single tool: while running, jump over the moves of the other tools.
  function skipOtherTools(playback) {
    if (filterTool === "all" || !playback || playbackState() !== "running" || seeking) return;
    const activeIndex = Math.max(-1, playback.completedIndex) + 1;
    const active = compact[activeIndex];
    if (!active || active.tool === Number(filterTool)) return;
    const next = timeline.find((entry) => entry.index > activeIndex && compact[entry.index].tool === Number(filterTool));
    seeking = true;
    window.cncPreviewSeek?.(next ? next.startSeconds : timeline[timeline.length - 1]?.endSeconds || 0);
    window.requestAnimationFrame(() => {
      seeking = false;
      if (next) elements.runButton?.click();
    });
  }

  // Run from the selected NC row: seek to the row's first move, then let the upstream Run continue.
  function runFromSelectedRow(event) {
    if (!elements.fromRow.checked || !timeline.length) return;
    const state = playbackState();
    if (state !== "idle" && state !== "complete") return;
    const cursor = editor()?.getCursor();
    const line = cursor ? cursor.line + 1 : 1;
    const entry = timeline.find((candidate) => candidate.line >= line
      && (filterTool === "all" || compact[candidate.index].tool === Number(filterTool)));
    if (!entry || entry.startSeconds <= 0) return;
    window.cncPreviewSeek?.(entry.startSeconds);
  }

  // ----- progress ------------------------------------------------------------------------------------

  function showProgress(visible) {
    if (elements.progress) elements.progress.hidden = !visible;
  }

  function setProgress(done, total, label) {
    if (!elements.progress) return;
    const percent = total > 0 ? Math.round(Math.min(1, done / total) * 100) : 0;
    elements.progressBar.style.width = `${percent}%`;
    elements.progressText.textContent = label
      ? `${label} ${percent}% (${done.toLocaleString()} / ${total.toLocaleString()} moves)`
      : `Material removal ${percent}% (${done.toLocaleString()} / ${total.toLocaleString()} moves)`;
    elements.progress.classList.toggle("busy", Boolean(label));
  }

  // ----- stock persistence ---------------------------------------------------------------------------

  async function loadFromHost() {
    try {
      hostState = await meimad.stock.load();
    } catch (error) {
      hostState = { stock: null, candidates: [], canSave: false, nextOperationNumber: null };
      status(message(error), "warning");
    }
    if (hostState.stock) definition = { ...definition, ...hostState.stock };
    fillWorkOffsets();
    writeForm();
    fillCandidates();
    elements.save.disabled = !hostState.canSave;
    elements.save.title = hostState.canSave
      ? `Saves the stock definition next to the NC program (${hostState.savePath || "revision folder"})`
      : "The program has no folder yet: save it in the Case Working Folder to keep the stock with it.";
    elements.exportNext.disabled = !hostState.nextOperationNumber;
    elements.exportNext.textContent = hostState.nextOperationNumber
      ? `Export machined stock to OP${String(hostState.nextOperationNumber).padStart(2, "0")}`
      : "Export machined stock (no next Operation)";
    if (definition.type === "stl" && definition.stl?.path) await loadStl(definition.stl.path, false, stlWorkCs);
    applyStock();
  }

  function fillCandidates() {
    elements.candidates.replaceChildren();
    const none = document.createElement("option");
    none.value = "";
    none.textContent = (hostState.candidates || []).length ? "Choose a machined stock from the previous Operation…" : "No STL in this Operation's Stock folder";
    elements.candidates.appendChild(none);
    for (const candidate of hostState.candidates || []) {
      const option = document.createElement("option");
      option.value = candidate.path;
      option.textContent = candidate.workCs ? `${candidate.name} (Cimatron rest material)` : candidate.name;
      elements.candidates.appendChild(option);
    }
    elements.candidates.disabled = !(hostState.candidates || []).length;
  }

  async function loadStl(path, announce, workCs) {
    try {
      const result = await meimad.stock.readStl(path);
      const bytes = Uint8Array.from(atob(result.base64), (character) => character.charCodeAt(0));
      const raw = window.MeimadMaterial.readStl(bytes.buffer);
      stlWorkCs = workCs || null;
      stlPositions = stlWorkCs && window.MeimadScene ? window.MeimadScene.modelToDrawing(raw, stlWorkCs) : raw;
      stlName = result.name || "";
      elements.stlPath.value = path;
      if (announce) {
        const placed = stlWorkCs ? " Placed through the Operation's Cimatron Work CS." : "";
        status(`Stock STL loaded: ${stlName} (${Math.floor(stlPositions.length / 9).toLocaleString()} triangles).${placed}`, "success");
      }
      return true;
    } catch (error) {
      stlPositions = undefined;
      status(`The stock STL could not be read: ${message(error)}`, "error");
      return false;
    }
  }

  function applyStock() {
    readForm();
    refreshTypeFields();
    describeReference();
    if (!model) return;
    if (elements.toggle.checked) {
      removeOutline();
      startWorker();
    } else {
      stopWorker();
      removeMesh();
      showProgress(false);
      showOutline();
    }
  }

  async function saveStock() {
    readForm();
    try {
      const result = await meimad.stock.save(definition);
      status(`Stock definition saved: ${result.path}`, "success");
    } catch (error) {
      status(message(error), "error");
    }
  }

  async function exportMachined(toNextOperation) {
    try {
      const stl = await exportStl();
      const bytes = new Uint8Array(stl);
      let binary = "";
      for (let index = 0; index < bytes.length; index += 0x8000) {
        binary += String.fromCharCode.apply(null, bytes.subarray(index, index + 0x8000));
      }
      const result = await meimad.stock.exportStl(btoa(binary), { toNextOperation });
      if (result?.canceled) status("Export canceled.", "ready");
      else status(`Machined stock exported: ${result.path}`, "success");
    } catch (error) {
      status(message(error), "error");
    }
  }

  // ----- wiring ----------------------------------------------------------------------------------------

  function attachHooks() {
    hooks = window.meimadViewer3d;
    if (!hooks) return;
    if (!hooks.captured) {
      elements.toggle.disabled = true;
      elements.note.textContent = "Material removal is unavailable: the 3D scene could not be captured.";
      return;
    }
    hooks.on("model", (next) => {
      model = next;
      buildPolicyRows();
      refreshSceneGeometry();
      compactSegments();
      fillWorkOffsets();
      refreshTypeFields();
      lastTarget = { index: -2, fraction: 0 };
      applyStock();
    });
    hooks.on("playback", (playback) => {
      skipOtherTools(playback);
      followPlayback(playback);
      if (playback && playbackState() === "running") {
        const done = Math.max(0, Math.max(-1, playback.completedIndex) + 1);
        if (!elements.toggle.checked) {
          showProgress(true);
          setProgress(done, compact.length);
        }
      }
    });
    hooks.on("filters", (filters) => {
      const previous = filterTool;
      filterTool = filters?.tool ?? "all";
      if (previous !== filterTool && worker) {
        worker.postMessage({ type: "filter", tool: filterTool });
        lastTarget = { index: -2, fraction: 0 };
        followPlayback();
      }
    });
  }

  if (window.meimadViewer3d) attachHooks();
  else window.addEventListener("meimad:viewer3d", attachHooks, { once: true });

  // Verification wiring.
  if (elements.engine) {
    elements.engine.value = readStore(STORE.engine) === "column" ? "column" : "voxel";
    elements.engine.addEventListener("change", () => {
      writeStore(STORE.engine, elements.engine.value);
      if (elements.toggle.checked) applyStock();
    });
  }
  if (elements.backend) {
    elements.backend.value = readStore(STORE.backend) === "cpu" ? "cpu" : "auto";
    elements.backend.addEventListener("change", () => {
      writeStore(STORE.backend, elements.backend.value);
      if (elements.toggle.checked) applyStock();
    });
  }
  if (elements.collisions) {
    elements.collisions.checked = readStore(STORE.collisions) !== "off";
    elements.collisions.addEventListener("change", () => {
      writeStore(STORE.collisions, elements.collisions.checked ? "on" : "off");
      if (elements.toggle.checked) applyStock();
    });
  }
  buildPolicyRows();
  elements.deviation?.addEventListener("change", () => {
    if (!worker || workerEngine !== "voxel") return;
    worker.postMessage({ type: "view", deviation: elements.deviation.checked });
    if (elements.deviation.checked && partPositions) {
      deviationAsked = true;
      worker.postMessage({ type: "deviation", limitMm: 10 });
    } else {
      elements.deviationResult.textContent = "";
    }
  });
  elements.partBrowse?.addEventListener("click", async () => {
    try {
      const result = await meimad.stock.chooseStl();
      if (result && !result.canceled && result.path && await loadPart(result.path) && elements.toggle.checked) applyStock();
    } catch (error) {
      status(message(error), "error");
    }
  });
  elements.partPath?.addEventListener("change", async () => {
    const path = elements.partPath.value.trim();
    if (!path) {
      userPartPath = "";
      partPositions = null;
      await refreshSceneGeometry();
    } else if (await loadPart(path) && elements.toggle.checked) applyStock();
  });
  elements.journalCopy?.addEventListener("click", () => {
    if (!worker || workerEngine !== "voxel") {
      status("The journal needs material removal on the 3D stock.", "warning");
      return;
    }
    worker.postMessage({ type: "journal" });
  });
  window.addEventListener("meimad:scene-loaded", async () => {
    await refreshSceneGeometry();
    if (elements.toggle.checked && workerEngine === "voxel" && (sceneFixtures.length || partPositions)) applyStock();
  });
  if (typeof meimad.toolAssemblies === "function") {
    meimad.toolAssemblies().then((result) => {
      assemblies = Object.fromEntries((result?.tools || []).map((tool) => [Number(tool.number), tool]));
      if (Object.keys(assemblies).length && elements.toggle.checked && workerEngine === "voxel") applyStock();
    }).catch(() => { assemblies = {}; });
  }
  renderPartSource();

  elements.toggle.addEventListener("change", applyStock);
  elements.resolution.addEventListener("change", () => { if (elements.toggle.checked) applyStock(); });
  elements.type.addEventListener("change", refreshTypeFields);
  elements.offset.addEventListener("change", describeReference);
  elements.apply.addEventListener("click", applyStock);
  elements.save.addEventListener("click", saveStock);
  elements.fromToolpath.addEventListener("click", stockFromToolpath);
  elements.exportNext.addEventListener("click", () => exportMachined(true));
  elements.exportAs.addEventListener("click", () => exportMachined(false));
  elements.stlBrowse.addEventListener("click", async () => {
    try {
      const result = await meimad.stock.chooseStl();
      if (result && !result.canceled && result.path && await loadStl(result.path, true, null)) {
        elements.type.value = "stl";
        refreshTypeFields();
        applyStock();
      }
    } catch (error) {
      status(message(error), "error");
    }
  });
  // A typed path is an STL in program coordinates; only a Cimatron candidate carries a Work CS.
  elements.stlPath.addEventListener("change", () => { stlWorkCs = null; });
  elements.candidates.addEventListener("change", async () => {
    const path = elements.candidates.value;
    const candidate = (hostState.candidates || []).find((value) => value.path === path);
    if (candidate?.workCs?.workOffset) selectOffset(candidate.workCs.workOffset);
    if (path && await loadStl(path, true, candidate?.workCs || null)) {
      elements.type.value = "stl";
      refreshTypeFields();
      applyStock();
    }
  });
  elements.runButton?.addEventListener("click", runFromSelectedRow, true);

  function selectOffset(label) {
    const value = String(label || "").trim().toUpperCase();
    if ([...elements.offset.options].some((option) => option.value === value)) elements.offset.value = value;
  }

  // meimad-scene.js: "Use as stock" on a Cimatron rest material of the Operation.
  window.meimadStock = Object.freeze({
    /** The applied stock definition (the lathe Machine view puts the spindle face behind it). */
    definition: () => definition,
    async useRestMaterial(item) {
      if (!item?.path) return false;
      if (item.workCs?.workOffset) selectOffset(item.workCs.workOffset);
      if (!await loadStl(item.path, true, item.workCs || null)) return false;
      elements.type.value = "stl";
      refreshTypeFields();
      elements.card.scrollIntoView({ block: "nearest" });
      applyStock();
      return true;
    }
  });

  window.meimadSimulationState = () => ({
    engine: workerEngine,
    events: events.length,
    stats: lastStats,
    stockType: definition.type,
    materialRemoval: elements.toggle.checked,
    workerReady,
    segments: compact.length,
    tiltedMoves,
    lastTarget
  });

  loadFromHost();
})();
