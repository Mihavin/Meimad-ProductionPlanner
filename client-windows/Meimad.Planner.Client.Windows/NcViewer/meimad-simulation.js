"use strict";

// Meimad Planner stock and material removal for the NC viewer page. Loaded after preview.js.
// - Stock definition (box, cylinder or STL, relative to a work offset), saved next to the NC
//   program in the Case Working Folder and reloaded with it.
// - Material removal in a worker (meimad-material-worker.js): the stock follows the playback
//   position, or shows the finished part while playback is idle; ON/OFF and resolution.
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
    runButton: byId("runButton"),
    toolFilter: byId("toolFilter")
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
    if (lathe && type === "box") elements.type.value = "cylinder";
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

  // The shift from the drawing frame (the program's own work offset) to the chosen offset.
  function offsetShift() {
    if (!model || model.kind === "lathe") return { x: 0, y: 0, z: 0 };
    const table = model.workOffsetTable;
    const frameLabel = String(model.frame?.label || "").match(/G5\d(?:\.\d+ ?P\d+)?/)?.[0];
    const value = (label, axis) => {
      const row = (table?.rows || []).find((candidate) => candidate.label === label);
      const entry = row?.values?.[axis];
      const number_ = Number.isFinite(entry?.saved) ? entry.saved : Number.isFinite(entry?.initial) ? entry.initial : table?.defaults?.[axis];
      return Number.isFinite(number_) ? number_ : 0;
    };
    const target = elements.offset.value || "G54";
    if (!table || !frameLabel || target === frameLabel) return { x: 0, y: 0, z: 0 };
    return { x: value(target, "X") - value(frameLabel, "X"), y: value(target, "Y") - value(frameLabel, "Y"), z: value(target, "Z") - value(frameLabel, "Z") };
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
      setNumber("boxMaxZ", Math.max(0, Math.ceil(bounds.maxZ)) === 0 && bounds.maxZ <= 0 ? 0 : Math.ceil(bounds.maxZ));
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

  function compactSegments() {
    const chain = buildChain();
    tiltedMoves = 0;
    let elapsed = 0;
    timeline = [];
    compact = (model.segments || []).map((segment, index) => {
      if (!Number.isFinite(segment.executionIndex)) segment.executionIndex = index;
      const duration = Number.isFinite(segment.estimatedSeconds) ? Math.max(0, segment.estimatedSeconds) : 0;
      timeline.push({ index, tool: segment.tool, line: segment.line, startSeconds: elapsed, endSeconds: elapsed + duration, duration });
      elapsed += duration;
      let axis;
      if (chain && segment.rotary && (segment.rotary.a1 || segment.rotary.b1 || segment.rotary.c1 || segment.rotary.a0 || segment.rotary.b0 || segment.rotary.c0)) {
        try {
          axis = window.CncKinematics.toolAxisInWorkpiece(chain, { A: segment.rotary.a1 || 0, B: segment.rotary.b1 || 0, C: segment.rotary.c1 || 0 });
          if (axis && axis.z < 0.985 && isCutting(segment)) tiltedMoves += 1;
        } catch {
          axis = undefined;
        }
      }
      return {
        tool: segment.tool,
        cutting: isCutting(segment),
        points: (segment.points || []).map((point) => model.kind === "lathe"
          ? { x: point.x, z: point.z, r: Number.isFinite(point.r) ? point.r : point.x / 2 }
          : { x: point.x, y: point.y ?? 0, z: point.z }),
        toolRadius: segment.toolRadius,
        toolType: segment.toolType,
        toolCornerRadius: segment.toolCornerRadius,
        toolTip: segment.toolTip,
        axis
      };
    });
  }

  function toolsByNumber() {
    const tools = {};
    for (const tool of model?.toolDefinitions || []) {
      tools[tool.number] = { diameter: tool.diameter, cornerRadius: tool.cornerRadius, type: tool.type, tip: tool.tip, width: tool.width };
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
    try {
      worker = new Worker("./meimad-material-worker.js");
    } catch (error) {
      status(`Material removal is unavailable: ${message(error)}`, "error");
      return;
    }
    worker.onmessage = onWorkerMessage;
    worker.onerror = (event) => status(`Material removal stopped: ${event.message || "worker error"}`, "error");
    const stl = definition.type === "stl" && model.kind !== "lathe" ? stlPositions : undefined;
    worker.postMessage({
      type: "init",
      kind: model.kind,
      stock: stockForWorker(),
      resolution: Number(elements.resolution.value) || undefined,
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
    if (data.type === "ready") {
      workerReady = true;
      elements.note.textContent = `Stock grid: ${data.cells.toLocaleString()} cells at ${Number(data.resolution).toFixed(3)} mm` +
        (tiltedMoves ? ` · ${tiltedMoves} tilted-axis cutting move(s) are not simulated (2.5D)` : "");
      followPlayback();
      return;
    }
    if (data.type === "progress") {
      workerBusy = Boolean(data.busy);
      setProgress(data.done, data.total, workerBusy ? "Cutting…" : undefined);
      return;
    }
    if (data.type === "surface") {
      updateMesh(data.positions, data.kind);
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

  function ensureLights() {
    if (lights || !hooks) return;
    const THREE = hooks.THREE;
    lights = new THREE.Group();
    const hemisphere = new THREE.HemisphereLight(0xffffff, 0x2a3038, 0.9);
    const key = new THREE.DirectionalLight(0xffffff, 0.75);
    key.position.set(1, -0.6, 1.4);
    lights.add(hemisphere, key);
    hooks.scene.add(lights);
  }

  function updateMesh(positions, kind) {
    if (!hooks) return;
    const THREE = hooks.THREE;
    ensureLights();
    if (!mesh) {
      const geometry = new THREE.BufferGeometry();
      const material = new THREE.MeshStandardMaterial({ color: 0xb9c2cc, roughness: 0.75, metalness: 0.15, side: THREE.DoubleSide });
      mesh = new THREE.Mesh(geometry, material);
      mesh.renderOrder = 1;
      mesh.frustumCulled = false;
      hooks.contentRoot.add(mesh);
    }
    mesh.geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
    mesh.geometry.computeVertexNormals();
    mesh.geometry.computeBoundingSphere();
    mesh.visible = true;
    mesh.userData.kind = kind;
    removeOutline();
    hooks.upstream.render();
  }

  function removeMesh() {
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
    if (definition.type === "stl" && definition.stl?.path) await loadStl(definition.stl.path, false);
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
      option.textContent = candidate.name;
      elements.candidates.appendChild(option);
    }
    elements.candidates.disabled = !(hostState.candidates || []).length;
  }

  async function loadStl(path, announce) {
    try {
      const result = await meimad.stock.readStl(path);
      const bytes = Uint8Array.from(atob(result.base64), (character) => character.charCodeAt(0));
      stlPositions = window.MeimadMaterial.readStl(bytes.buffer);
      stlName = result.name || "";
      elements.stlPath.value = path;
      if (announce) status(`Stock STL loaded: ${stlName} (${Math.floor(stlPositions.length / 9).toLocaleString()} triangles).`, "success");
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

  elements.toggle.addEventListener("change", applyStock);
  elements.resolution.addEventListener("change", () => { if (elements.toggle.checked) applyStock(); });
  elements.type.addEventListener("change", refreshTypeFields);
  elements.apply.addEventListener("click", applyStock);
  elements.save.addEventListener("click", saveStock);
  elements.fromToolpath.addEventListener("click", stockFromToolpath);
  elements.exportNext.addEventListener("click", () => exportMachined(true));
  elements.exportAs.addEventListener("click", () => exportMachined(false));
  elements.stlBrowse.addEventListener("click", async () => {
    try {
      const result = await meimad.stock.chooseStl();
      if (result && !result.canceled && result.path && await loadStl(result.path, true)) {
        elements.type.value = "stl";
        refreshTypeFields();
        applyStock();
      }
    } catch (error) {
      status(message(error), "error");
    }
  });
  elements.candidates.addEventListener("change", async () => {
    const path = elements.candidates.value;
    if (path && await loadStl(path, true)) {
      elements.type.value = "stl";
      refreshTypeFields();
      applyStock();
    }
  });
  elements.runButton?.addEventListener("click", runFromSelectedRow, true);

  window.meimadSimulationState = () => ({
    stockType: definition.type,
    materialRemoval: elements.toggle.checked,
    workerReady,
    segments: compact.length,
    tiltedMoves,
    lastTarget
  });

  loadFromHost();
})();
