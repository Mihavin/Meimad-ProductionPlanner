// 3D toolpath viewer shared by the VS Code webview and the desktop app.
//
// The module receives THREE and the CncKinematics helper from the host page
// (no bare imports, so it loads from any URL the page's CSP allows). Toolpath
// lines are drawn with an instanced screen-space line shader: each layer is a
// single draw call, widths are in pixels, rapids are dashed, and picking uses
// an ID render pass so hovering stays fast on large CAM programs.
//
// Coordinates: mill models are X/Y/Z part coordinates (Z up). Lathe models
// arrive as programmed X diameter / Z and are drawn as X radius (vertical) and
// Z along the spindle axis, in the Y = 0 plane.

export const SEGMENT_STYLES = {
  rapid: { color: "#53a7ff", width: 1.2, dashed: true, alpha: 0.7, label: "G0 rapid" },
  feed: { color: "#f2cc60", width: 2, label: "G1 feed" },
  "arc-cw": { color: "#ff7b72", width: 2, label: "G2 clockwise arc" },
  "arc-ccw": { color: "#7ee787", width: 2, label: "G3 counter-clockwise arc" },
  g71: { color: "#d2a8ff", width: 2, label: "G71 rough pass" },
  "g71-contour": { color: "#b78cff", width: 2, label: "G71 contour follow" },
  "g71-finish": { color: "#ff9f43", width: 2, label: "G71 rough-finish contour" },
  g72: { color: "#7ee787", width: 2, label: "G72 facing rough pass" },
  "g72-finish": { color: "#ffa657", width: 2, label: "G72 rough-finish" },
  g73: { color: "#56d4dd", width: 2, label: "G73 copied profile" },
  g75: { color: "#f778ba", width: 2, label: "G75 groove peck" },
  thread: { color: "#2dd4bf", width: 2, label: "G76 thread cut" },
  "thread-chamfer": { color: "#22b8cf", width: 2, label: "G76 thread chamfer" },
  g30: { color: "#a371f7", width: 1.4, dashed: true, alpha: 0.8, label: "G30 return" },
  home: { color: "#a371f7", width: 1.4, dashed: true, alpha: 0.8, label: "G28/G53 machine move" },
  "tool-change": { color: "#8b949e", width: 1.4, dashed: true, alpha: 0.8, label: "M06 tool change retract" },
  "cycle-feed": { color: "#d2a8ff", width: 2.2, label: "Canned-cycle feed" },
  "cycle-retract": { color: "#a5d6ff", width: 1.6, label: "Canned-cycle feed retract" },
  pocket: { color: "#ffa657", width: 2, label: "G12/G13/G150 pocket" },
  engrave: { color: "#f778ba", width: 2, label: "G47 engraving" },
  probe: { color: "#8b949e", width: 1.6, label: "Probe move" },
  compensated: { color: "#ff66c4", width: 2.2, label: "Cutter/nose compensation centre" },
  "compensation-interference": { color: "#ff3b30", width: 3.4, label: "Compensation interference" },
  "polyarc-outside": { color: "#49d6ff", width: 2.5, dashed: true, label: "Polyarc outside" },
  "polyarc-internal": { color: "#ffb84d", width: 2.2, dashed: true, label: "Polyarc internal" },
  active: { color: "#ffffff", width: 3.6, label: "Active playback move" },
  selected: { color: "#ffffff", width: 7, alpha: 0.5, label: "Selected NC row" }
};

const RETURN_KINDS = new Set(["g30", "home", "tool-change"]);
const RAPID_LAYER_KINDS = new Set(["rapid", "g30", "home", "tool-change"]);
const DEG = Math.PI / 180;

const LINE_VERTEX = `
  attribute vec3 instanceStart;
  attribute vec3 instanceEnd;
  attribute vec4 instanceColor;
  attribute vec4 instanceStyle;
  attribute float instancePick;
  uniform vec2 resolution;
  uniform float pixelRatio;
  uniform float minWidth;
  varying vec4 vColor;
  varying float vDistance;
  varying float vDashed;
  varying float vPick;
  void main() {
    vec4 clipStart = projectionMatrix * modelViewMatrix * vec4(instanceStart, 1.0);
    vec4 clipEnd = projectionMatrix * modelViewMatrix * vec4(instanceEnd, 1.0);
    vec2 screenStart = (clipStart.xy / clipStart.w * 0.5 + 0.5) * resolution;
    vec2 screenEnd = (clipEnd.xy / clipEnd.w * 0.5 + 0.5) * resolution;
    vec2 delta = screenEnd - screenStart;
    float len = length(delta);
    vec2 dir = len > 1e-4 ? delta / len : vec2(1.0, 0.0);
    vec2 normal = vec2(-dir.y, dir.x);
    float width = max(instanceStyle.x * 0.1 * pixelRatio, minWidth * pixelRatio);
    vec4 clip = mix(clipStart, clipEnd, position.x);
    vec2 offset = normal * position.y * width * 0.5 + dir * (position.x * 2.0 - 1.0) * width * 0.5;
    clip.xy += offset / resolution * 2.0 * clip.w;
    vDistance = position.x * len / pixelRatio;
    vColor = instanceColor;
    vDashed = instanceStyle.y;
    vPick = instancePick;
    if (instanceStyle.z < 0.5) {
      clip = vec4(2.0, 2.0, 2.0, 1.0);
    }
    gl_Position = clip;
  }
`;

const LINE_FRAGMENT = `
  uniform float opacity;
  uniform float dashSize;
  uniform float gapSize;
  varying vec4 vColor;
  varying float vDistance;
  varying float vDashed;
  void main() {
    if (vDashed > 0.5 && mod(vDistance, dashSize + gapSize) > dashSize) discard;
    gl_FragColor = vec4(vColor.rgb, vColor.a * opacity);
  }
`;

const PICK_FRAGMENT = `
  varying float vPick;
  void main() {
    float id = floor(vPick + 0.5);
    float r = mod(id, 256.0);
    float g = mod(floor(id / 256.0), 256.0);
    float b = floor(id / 65536.0);
    gl_FragColor = vec4(r / 255.0, g / 255.0, b / 255.0, 1.0);
  }
`;

function hexToRgb(hex) {
  const value = Number.parseInt(String(hex).replace("#", ""), 16);
  return [(value >> 16) & 255, (value >> 8) & 255, value & 255];
}

function emptyBounds() {
  return { minX: Infinity, maxX: -Infinity, minY: Infinity, maxY: -Infinity, minZ: Infinity, maxZ: -Infinity };
}

function extendBounds(bounds, point) {
  if (point.x < bounds.minX) bounds.minX = point.x;
  if (point.x > bounds.maxX) bounds.maxX = point.x;
  if (point.y < bounds.minY) bounds.minY = point.y;
  if (point.y > bounds.maxY) bounds.maxY = point.y;
  if (point.z < bounds.minZ) bounds.minZ = point.z;
  if (point.z > bounds.maxZ) bounds.maxZ = point.z;
}

function niceStep(target) {
  if (!(target > 0)) return 1;
  const power = 10 ** Math.floor(Math.log10(target));
  const normalized = target / power;
  if (normalized < 2) return power;
  if (normalized < 5) return power * 2;
  return power * 5;
}

function decimalsFor(step) {
  if (step >= 1) return 0;
  return Math.min(4, Math.ceil(-Math.log10(step)));
}

export function createToolpathViewer({ THREE, kinematics, canvas, overlay, onRender }) {
  let renderer;
  try {
    renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: false, preserveDrawingBuffer: false });
  } catch (error) {
    throw new Error(`WebGL is not available (${error.message})`);
  }
  renderer.setClearColor(0x0d1117, 1);
  renderer.setPixelRatio(window.devicePixelRatio || 1);
  const overlayContext = overlay ? overlay.getContext("2d") : undefined;

  const scene = new THREE.Scene();
  const camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.001, 1000);
  const contentRoot = new THREE.Group();
  contentRoot.matrixAutoUpdate = false;
  const machineRoot = new THREE.Group();
  const gridRoot = new THREE.Group();
  scene.add(gridRoot, contentRoot, machineRoot);
  const pickScene = new THREE.Scene();
  const pickRoot = new THREE.Group();
  pickRoot.matrixAutoUpdate = false;
  pickScene.add(pickRoot);

  const resolution = new THREE.Vector2(1, 1);
  const materials = [];
  const makeLineMaterial = ({ depthTest = true, opacity = 1, pick = false, minWidth = 0 } = {}) => {
    const material = new THREE.ShaderMaterial({
      uniforms: {
        resolution: { value: resolution },
        pixelRatio: { value: renderer.getPixelRatio() },
        minWidth: { value: minWidth },
        opacity: { value: opacity },
        dashSize: { value: 6 },
        gapSize: { value: 4 }
      },
      vertexShader: LINE_VERTEX,
      fragmentShader: pick ? PICK_FRAGMENT : LINE_FRAGMENT,
      transparent: !pick,
      depthTest,
      depthWrite: !pick ? depthTest : true,
      blending: pick ? THREE.NoBlending : THREE.NormalBlending
    });
    materials.push(material);
    return material;
  };
  const baseQuad = new THREE.BufferGeometry();
  baseQuad.setAttribute("position", new THREE.Float32BufferAttribute([0, -1, 0, 1, -1, 0, 1, 1, 0, 0, 1, 0], 3));
  baseQuad.setIndex([0, 1, 2, 0, 2, 3]);

  const state = {
    model: undefined,
    kind: "mill",
    filters: {},
    selectedLine: undefined,
    selectedExecutionIndex: undefined,
    playback: undefined,
    frame: "part",
    view: "iso",
    target: new THREE.Vector3(),
    azimuth: 45 * DEG,
    elevation: 35.264 * DEG,
    viewHeight: 100,
    sceneRadius: 100,
    up: new THREE.Vector3(0, 0, 1),
    front: new THREE.Vector3(0, -1, 0),
    toolPose: undefined,
    showMachine: true,
    showTool: true,
    pickDirty: true,
    renderQueued: false,
    lastPickTarget: undefined
  };
  const layers = {};
  let pickTargets = [];
  let pickTarget;
  let chain;
  let machineNodes = new Map();
  let unitScale = 1;
  let toolGroup;
  let stockGroup;
  let noseGroup;
  let axesGroup;

  // ----- helpers -----------------------------------------------------------------

  const toScene = (point) => {
    if (state.kind === "lathe") {
      const radius = Number.isFinite(point.r) ? point.r : point.x / 2;
      return { x: radius, y: 0, z: point.z };
    }
    return point;
  };

  function disposeObject(object) {
    object.traverse?.((child) => {
      child.geometry?.dispose?.();
      if (child.material && !materials.includes(child.material)) {
        (Array.isArray(child.material) ? child.material : [child.material]).forEach((material) => material.dispose?.());
      }
    });
  }

  function clearGroup(group) {
    for (const child of [...group.children]) {
      group.remove(child);
      disposeObject(child);
    }
  }

  // ----- line layers ---------------------------------------------------------------

  function createLayer(name, { renderOrder, depthTest = true, pickable = true, opacity = 1 }) {
    const material = makeLineMaterial({ depthTest, opacity });
    const pickMaterial = pickable ? makeLineMaterial({ pick: true, minWidth: 9 }) : undefined;
    layers[name] = {
      name,
      material,
      pickMaterial,
      mesh: undefined,
      pickMesh: undefined,
      renderOrder,
      countBefore: undefined,
      instanceCount: 0
    };
  }

  createLayer("rapids", { renderOrder: 2 });
  createLayer("cuts", { renderOrder: 3 });
  createLayer("compensated", { renderOrder: 4 });
  createLayer("polyarc", { renderOrder: 1, pickable: false });
  createLayer("selection", { renderOrder: 6, depthTest: false, pickable: false });
  createLayer("active", { renderOrder: 7, depthTest: false, pickable: false });
  createLayer("nose", { renderOrder: 8, depthTest: false, pickable: false });

  // Builds instanced line data for a list of polylines. Consecutive polylines
  // that share an end point form one strip; gaps insert hidden break instances.
  function buildLayer(name, entries, executionCount) {
    const layer = layers[name];
    if (layer.mesh) {
      contentRoot.remove(layer.mesh);
      layer.mesh.geometry.dispose();
      layer.mesh = undefined;
    }
    if (layer.pickMesh) {
      pickRoot.remove(layer.pickMesh);
      layer.pickMesh.geometry.dispose();
      layer.pickMesh = undefined;
    }
    let instanceTotal = 0;
    let previousEnd;
    for (const entry of entries) {
      const count = entry.points.length;
      if (count < 2) continue;
      const first = entry.points[0];
      if (!previousEnd || previousEnd.x !== first.x || previousEnd.y !== first.y || previousEnd.z !== first.z) {
        instanceTotal += previousEnd ? 1 : 0;
      }
      instanceTotal += count - 1;
      previousEnd = entry.points[count - 1];
    }
    layer.countBefore = executionCount !== undefined ? new Int32Array(executionCount + 2) : undefined;
    if (!instanceTotal) {
      layer.instanceCount = 0;
      if (layer.countBefore) layer.countBefore.fill(0);
      return;
    }
    const positions = new Float32Array((instanceTotal + 1) * 3);
    const colors = new Uint8Array(instanceTotal * 4);
    const styles = new Uint8Array(instanceTotal * 4);
    const picks = new Float32Array(instanceTotal);
    let vertex = 0;
    let instance = 0;
    previousEnd = undefined;
    let executionCursor = 0;
    const markExecution = (executionIndex) => {
      if (!layer.countBefore || !Number.isFinite(executionIndex)) return;
      while (executionCursor <= executionIndex && executionCursor < layer.countBefore.length) {
        layer.countBefore[executionCursor] = instance;
        executionCursor += 1;
      }
    };
    const writeVertex = (point) => {
      const scenePoint = toScene(point);
      positions[vertex * 3] = scenePoint.x;
      positions[vertex * 3 + 1] = scenePoint.y;
      positions[vertex * 3 + 2] = scenePoint.z;
      vertex += 1;
    };
    for (const entry of entries) {
      const points = entry.points;
      if (points.length < 2) continue;
      markExecution(entry.executionIndex);
      const style = entry.style;
      const rgb = hexToRgb(style.color);
      const alpha = Math.round((style.alpha ?? 0.96) * 255);
      const width = Math.round((style.width || 2) * 10);
      const first = points[0];
      if (!previousEnd) {
        writeVertex(first);
      } else if (previousEnd.x !== first.x || previousEnd.y !== first.y || previousEnd.z !== first.z) {
        // Hidden break instance from the previous end to this start.
        styles[instance * 4 + 2] = 0;
        instance += 1;
        writeVertex(first);
      }
      for (let index = 1; index < points.length; index += 1) {
        writeVertex(points[index]);
        colors.set([rgb[0], rgb[1], rgb[2], alpha], instance * 4);
        styles.set([width, style.dashed ? 1 : 0, 1, 0], instance * 4);
        picks[instance] = entry.pickId || 0;
        instance += 1;
      }
      previousEnd = points[points.length - 1];
    }
    if (layer.countBefore) {
      while (executionCursor < layer.countBefore.length) {
        layer.countBefore[executionCursor] = instance;
        executionCursor += 1;
      }
    }
    const geometry = new THREE.InstancedBufferGeometry();
    geometry.index = baseQuad.index;
    geometry.setAttribute("position", baseQuad.getAttribute("position"));
    const buffer = new THREE.InstancedInterleavedBuffer(positions, 3, 1);
    geometry.setAttribute("instanceStart", new THREE.InterleavedBufferAttribute(buffer, 3, 0));
    geometry.setAttribute("instanceEnd", new THREE.InterleavedBufferAttribute(buffer, 3, 3));
    geometry.setAttribute("instanceColor", new THREE.InstancedBufferAttribute(colors, 4, true));
    geometry.setAttribute("instanceStyle", new THREE.InstancedBufferAttribute(styles, 4, false));
    geometry.setAttribute("instancePick", new THREE.InstancedBufferAttribute(picks, 1, false));
    geometry.instanceCount = instance;
    geometry.boundingSphere = new THREE.Sphere(new THREE.Vector3(), Infinity);
    layer.instanceCount = instance;
    const mesh = new THREE.Mesh(geometry, layer.material);
    mesh.frustumCulled = false;
    mesh.renderOrder = layer.renderOrder;
    layer.mesh = mesh;
    contentRoot.add(mesh);
    if (layer.pickMaterial) {
      const pickMesh = new THREE.Mesh(geometry, layer.pickMaterial);
      pickMesh.frustumCulled = false;
      layer.pickMesh = pickMesh;
      pickRoot.add(pickMesh);
    }
  }

  function applyPlaybackRange() {
    const playback = state.playback;
    for (const name of ["rapids", "cuts", "compensated"]) {
      const layer = layers[name];
      if (!layer.mesh) continue;
      let count = layer.instanceCount;
      if (playback && !playback.complete && layer.countBefore) {
        const completed = Math.max(-1, Math.min(playback.completedIndex, layer.countBefore.length - 2));
        count = layer.countBefore[completed + 1];
      }
      layer.mesh.geometry.instanceCount = count;
    }
    state.pickDirty = true;
  }

  // ----- model --------------------------------------------------------------------

  function segmentVisible(segment) {
    const filters = state.filters;
    if (!filters.showNonCutting && segment.nonCutting) return false;
    if (filters.tool !== undefined && filters.tool !== "all" && segment.tool !== Number(filters.tool)) return false;
    if (RETURN_KINDS.has(segment.kind)) return Boolean(filters.showReturns);
    if (segment.kind === "rapid" && !filters.showRapids) return false;
    return true;
  }

  function styleFor(segment) {
    if (segment.compensationInterference && segment.kind === "compensated") {
      return SEGMENT_STYLES["compensation-interference"];
    }
    if (segment.compensationInterference && state.kind === "lathe") {
      return SEGMENT_STYLES["compensation-interference"];
    }
    return SEGMENT_STYLES[segment.kind] || SEGMENT_STYLES.feed;
  }

  function rebuildGeometry() {
    const model = state.model;
    pickTargets = [];
    if (!model) return;
    const executionCount = model.segments.length;
    const rapids = [];
    const cuts = [];
    for (const segment of model.segments) {
      if (!segmentVisible(segment)) continue;
      pickTargets.push({ segment, layer: "base" });
      const entry = {
        points: segment.points,
        style: styleFor(segment),
        executionIndex: segment.executionIndex,
        pickId: pickTargets.length
      };
      (RAPID_LAYER_KINDS.has(segment.kind) ? rapids : cuts).push(entry);
    }
    buildLayer("rapids", rapids, executionCount);
    buildLayer("cuts", cuts, executionCount);
    const compensated = [];
    if (state.filters.showCompensated) {
      for (const segment of model.compensatedSegments || []) {
        if (!segmentVisible({ ...segment, kind: "feed" })) continue;
        pickTargets.push({ segment, layer: "compensated" });
        compensated.push({
          points: segment.points,
          style: styleFor(segment),
          executionIndex: segment.executionIndex,
          pickId: pickTargets.length
        });
      }
    }
    buildLayer("compensated", compensated, executionCount);
    const polyarc = [];
    if (state.filters.showPolyarc) {
      for (const segment of state.polyarcSegments || []) {
        polyarc.push({ points: segment.points, style: SEGMENT_STYLES[segment.kind === "internal" ? "polyarc-internal" : "polyarc-outside"] });
      }
    }
    buildLayer("polyarc", polyarc);
    rebuildSelection();
    applyPlaybackRange();
    rebuildGrid();
  }

  function rebuildSelection() {
    const model = state.model;
    const entries = [];
    if (model && Number.isFinite(state.selectedLine)) {
      const candidates = [
        ...model.segments.filter((segment) => segment.line === state.selectedLine && segmentVisible(segment)),
        ...(state.filters.showCompensated
          ? (model.compensatedSegments || []).filter((segment) => segment.line === state.selectedLine)
          : [])
      ];
      const chosen = Number.isFinite(state.selectedExecutionIndex)
        ? candidates.filter((segment) => segment.executionIndex === state.selectedExecutionIndex)
        : candidates;
      for (const segment of chosen.length ? chosen : candidates) {
        entries.push({ points: segment.points, style: SEGMENT_STYLES.selected });
      }
    }
    buildLayer("selection", entries);
    requestRender();
  }

  // ----- stock, tool and machine ---------------------------------------------------

  function rebuildStock() {
    if (stockGroup) {
      contentRoot.remove(stockGroup);
      disposeObject(stockGroup);
      stockGroup = undefined;
    }
    const stock = state.model?.stock;
    if (!stock || state.kind !== "lathe") return;
    stockGroup = new THREE.Group();
    const outer = stock.od / 2;
    const inner = Math.max(0, stock.id / 2);
    const profile = [
      new THREE.Vector2(inner, 0),
      new THREE.Vector2(outer, 0),
      new THREE.Vector2(outer, stock.length),
      new THREE.Vector2(inner, stock.length)
    ];
    const geometry = new THREE.LatheGeometry(profile, 48);
    const material = new THREE.MeshBasicMaterial({
      color: 0x6e7681,
      transparent: true,
      opacity: 0.14,
      depthWrite: false,
      side: THREE.DoubleSide
    });
    const mesh = new THREE.Mesh(geometry, material);
    // LatheGeometry revolves around +Y; the spindle axis is Z.
    mesh.rotation.x = Math.PI / 2;
    mesh.position.z = stock.frontZ - stock.length;
    mesh.renderOrder = 0;
    const edges = new THREE.LineSegments(
      new THREE.EdgesGeometry(geometry, 30),
      new THREE.LineBasicMaterial({ color: 0x8b949e, transparent: true, opacity: 0.35 })
    );
    edges.rotation.x = Math.PI / 2;
    edges.position.z = stock.frontZ - stock.length;
    stockGroup.add(mesh, edges);
    contentRoot.add(stockGroup);
  }

  function buildMachine() {
    clearGroup(machineRoot);
    machineNodes = new Map();
    chain = undefined;
    const definition = state.model?.machineDefinition;
    if (!definition?.kinematics?.nodes?.length || !kinematics) return;
    unitScale = state.model?.view?.unitScale || 1;
    const nodes = definition.kinematics.nodes.map((node) => ({
      ...node,
      origin: Array.isArray(node.origin) ? node.origin.map((value) => value * unitScale) : undefined,
      pivot: Array.isArray(node.pivot) ? node.pivot.map((value) => value * unitScale) : undefined
    }));
    try {
      chain = kinematics.buildChain({ nodes });
    } catch {
      chain = undefined;
      return;
    }
    if (state.kind !== "mill") return;
    const palette = [0x30363d, 0x3d444d, 0x484f58, 0x57606a];
    nodes.forEach((node, index) => {
      const group = new THREE.Group();
      group.matrixAutoUpdate = false;
      const geometry = node.geometry;
      if (geometry && geometry.shape) {
        let mesh;
        const center = (geometry.center || [0, 0, 0]).map((value) => value * unitScale);
        const material = new THREE.MeshBasicMaterial({
          color: node.workpieceMount ? 0x2f81f7 : palette[index % palette.length],
          transparent: true,
          opacity: node.workpieceMount ? 0.22 : 0.28,
          depthWrite: false
        });
        if (geometry.shape === "box") {
          const size = (geometry.size || [1, 1, 1]).map((value) => value * unitScale);
          mesh = new THREE.Mesh(new THREE.BoxGeometry(size[0], size[1], size[2]), material);
        } else if (geometry.shape === "cylinder") {
          const radius = (geometry.radius || 1) * unitScale;
          const height = (geometry.height || 1) * unitScale;
          mesh = new THREE.Mesh(new THREE.CylinderGeometry(radius, radius, height, 48), material);
          if ((geometry.axis || "z") === "z") mesh.rotation.x = Math.PI / 2;
          else if (geometry.axis === "x") mesh.rotation.z = Math.PI / 2;
        }
        if (mesh) {
          mesh.position.set(center[0], center[1], center[2]);
          const edges = new THREE.LineSegments(
            new THREE.EdgesGeometry(mesh.geometry, 25),
            new THREE.LineBasicMaterial({ color: 0x8b949e, transparent: true, opacity: 0.45 })
          );
          edges.position.copy(mesh.position);
          edges.rotation.copy(mesh.rotation);
          group.add(mesh, edges);
        }
      }
      // A future external model (node.model) is loaded into this group.
      group.userData.node = node;
      machineNodes.set(node.id, group);
      machineRoot.add(group);
    });
    // Travel envelope of the tool mount.
    const axes = definition.axes || {};
    if (["X", "Y", "Z"].every((axis) => Number.isFinite(axes[axis]?.min) && Number.isFinite(axes[axis]?.max))) {
      const min = ["X", "Y", "Z"].map((axis) => axes[axis].min * unitScale);
      const max = ["X", "Y", "Z"].map((axis) => axes[axis].max * unitScale);
      const box = new THREE.Box3(new THREE.Vector3(...min), new THREE.Vector3(...max));
      const helper = new THREE.Box3Helper(box, 0x30363d);
      helper.userData.envelope = true;
      machineRoot.add(helper);
    }
  }

  function currentRotary() {
    const pose = state.toolPose;
    if (pose?.rotary) return pose.rotary;
    return { A: 0, B: 0, C: 0 };
  }

  function partFrameMatrix(rotary) {
    const frame = state.model?.frame;
    if (!chain || !frame) return undefined;
    const mount = kinematics.workpieceTransform(chain, rotary);
    return kinematics.multiply(mount, kinematics.translation(frame.x, frame.y, frame.z));
  }

  function setMatrixFromRowMajor(target, rows) {
    target.set(
      rows[0], rows[1], rows[2], rows[3],
      rows[4], rows[5], rows[6], rows[7],
      rows[8], rows[9], rows[10], rows[11],
      rows[12], rows[13], rows[14], rows[15]
    );
  }

  function updateFrames() {
    const machineView = state.frame === "machine" && state.kind === "mill" && chain && state.model?.frame;
    machineRoot.visible = Boolean(machineView && state.showMachine);
    if (!machineView) {
      contentRoot.matrix.identity();
      pickRoot.matrix.identity();
      contentRoot.matrixWorldNeedsUpdate = true;
      pickRoot.matrixWorldNeedsUpdate = true;
      return;
    }
    const rotary = currentRotary();
    const part = partFrameMatrix(rotary);
    setMatrixFromRowMajor(contentRoot.matrix, part);
    pickRoot.matrix.copy(contentRoot.matrix);
    contentRoot.matrixWorldNeedsUpdate = true;
    pickRoot.matrixWorldNeedsUpdate = true;
    // Spindle joints from the tool tip in machine coordinates.
    const pose = state.toolPose;
    let joints = { X: 0, Y: 0, Z: 0, ...rotary };
    if (pose?.tip) {
      const tipWorld = kinematics.transformPoint(part, pose.tip);
      joints = { ...joints, X: tipWorld.x, Y: tipWorld.y, Z: tipWorld.z + (pose.length || 0) };
    }
    const world = kinematics.evaluateChain(chain, joints);
    for (const [id, group] of machineNodes) {
      const matrix = world.get(id);
      if (matrix) {
        setMatrixFromRowMajor(group.matrix, matrix);
        group.matrixWorldNeedsUpdate = true;
      }
    }
  }

  function rebuildTool() {
    if (toolGroup) {
      contentRoot.remove(toolGroup);
      disposeObject(toolGroup);
      toolGroup = undefined;
    }
    if (noseGroup) {
      contentRoot.remove(noseGroup);
      disposeObject(noseGroup);
      noseGroup = undefined;
    }
    buildLayer("nose", []);
    const pose = state.toolPose;
    if (!pose || !state.showTool) return;
    if (state.kind === "lathe") {
      const center = toScene(pose.center);
      const entries = [];
      if (pose.radius > 0) {
        const ring = [];
        for (let index = 0; index <= 48; index += 1) {
          const angle = (index / 48) * Math.PI * 2;
          ring.push({ x: (center.x + Math.cos(angle) * pose.radius) * 2, z: center.z + Math.sin(angle) * pose.radius });
        }
        entries.push({ points: ring, style: { color: "#ff3344", width: 3 } });
      }
      buildLayer("nose", entries);
      const nose = toScene(pose.nosePoint);
      noseGroup = new THREE.Group();
      const dot = new THREE.Points(
        new THREE.BufferGeometry().setAttribute("position", new THREE.Float32BufferAttribute([nose.x, nose.y, nose.z], 3)),
        new THREE.PointsMaterial({ color: 0xffd60a, size: 7, sizeAttenuation: false, depthTest: false })
      );
      dot.renderOrder = 9;
      noseGroup.add(dot);
      contentRoot.add(noseGroup);
      return;
    }
    // Mill cutter: flutes, optional ball/drill point, and a holder stub.
    const radius = Math.max(pose.radius || 0, state.sceneRadius * 0.004);
    const flute = Math.max(radius * 6, state.sceneRadius * 0.03);
    const holder = Math.min(Math.max(pose.length || flute * 2, flute * 1.5), flute * 4);
    toolGroup = new THREE.Group();
    const cutterMaterial = new THREE.MeshBasicMaterial({ color: 0xffa657, transparent: true, opacity: 0.55, depthWrite: false });
    const holderMaterial = new THREE.MeshBasicMaterial({ color: 0x8b949e, transparent: true, opacity: 0.35, depthWrite: false });
    const type = pose.type || "";
    const pointed = /drill|spot|center|chamfer|engraver/.test(type);
    const ball = type === "ball-mill";
    let tipLength = 0;
    if (pointed) {
      tipLength = radius * 1.2;
      const cone = new THREE.Mesh(new THREE.ConeGeometry(radius, tipLength, 24), cutterMaterial);
      cone.rotation.x = Math.PI;
      cone.position.y = tipLength / 2;
      toolGroup.add(cone);
    } else if (ball) {
      tipLength = radius;
      const sphere = new THREE.Mesh(new THREE.SphereGeometry(radius, 24, 12, 0, Math.PI * 2, Math.PI / 2, Math.PI / 2), cutterMaterial);
      sphere.position.y = radius;
      toolGroup.add(sphere);
    }
    const body = new THREE.Mesh(new THREE.CylinderGeometry(radius, radius, flute - tipLength, 24), cutterMaterial);
    body.position.y = tipLength + (flute - tipLength) / 2;
    const shank = new THREE.Mesh(new THREE.CylinderGeometry(radius * 1.6, radius * 1.6, holder, 24), holderMaterial);
    shank.position.y = flute + holder / 2;
    toolGroup.add(body, shank);
    const tipDot = new THREE.Points(
      new THREE.BufferGeometry().setAttribute("position", new THREE.Float32BufferAttribute([0, 0, 0], 3)),
      new THREE.PointsMaterial({ color: 0xffd60a, size: 6, sizeAttenuation: false, depthTest: false })
    );
    tipDot.renderOrder = 9;
    toolGroup.add(tipDot);
    const axis = pose.axis || { x: 0, y: 0, z: 1 };
    toolGroup.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), new THREE.Vector3(axis.x, axis.y, axis.z).normalize());
    toolGroup.position.set(pose.tip.x, pose.tip.y, pose.tip.z);
    contentRoot.add(toolGroup);
  }

  // ----- grid and axes ------------------------------------------------------------

  let gridLabels = [];
  function rebuildGrid() {
    clearGroup(gridRoot);
    gridLabels = [];
    const bounds = contentBounds(true);
    if (!bounds) return;
    const lathe = state.kind === "lathe";
    const span = Math.max(bounds.maxX - bounds.minX, bounds.maxZ - bounds.minZ, lathe ? 0 : bounds.maxY - bounds.minY, 1e-6);
    const step = niceStep(span / 8);
    const positions = [];
    const majorPositions = [];
    const push = (list, a, b) => list.push(a.x, a.y, a.z, b.x, b.y, b.z);
    if (lathe) {
      // X radius (vertical) and Z (horizontal) in the Y = 0 plane.
      const zMin = Math.floor(bounds.minZ / step) * step - step;
      const zMax = Math.ceil(bounds.maxZ / step) * step + step;
      const xMin = Math.min(0, Math.floor(bounds.minX / step) * step);
      const xMax = Math.ceil(bounds.maxX / step) * step + step;
      for (let z = zMin; z <= zMax + step * 1e-6; z += step) {
        push(Math.abs(z) < step * 1e-6 ? majorPositions : positions, { x: xMin, y: 0, z }, { x: xMax, y: 0, z });
        gridLabels.push({ point: { x: xMin, y: 0, z }, text: z.toFixed(decimalsFor(step)), anchor: "bottom" });
      }
      for (let x = xMin; x <= xMax + step * 1e-6; x += step) {
        push(Math.abs(x) < step * 1e-6 ? majorPositions : positions, { x, y: 0, z: zMin }, { x, y: 0, z: zMax });
        gridLabels.push({ point: { x, y: 0, z: zMin }, text: `D${(x * 2).toFixed(decimalsFor(step))}`, anchor: "left" });
      }
    } else {
      const xMin = Math.floor(bounds.minX / step) * step - step;
      const xMax = Math.ceil(bounds.maxX / step) * step + step;
      const yMin = Math.floor(bounds.minY / step) * step - step;
      const yMax = Math.ceil(bounds.maxY / step) * step + step;
      const z = Math.min(0, bounds.minZ);
      for (let x = xMin; x <= xMax + step * 1e-6; x += step) {
        push(Math.abs(x) < step * 1e-6 ? majorPositions : positions, { x, y: yMin, z }, { x, y: yMax, z });
        gridLabels.push({ point: { x, y: yMin, z }, text: x.toFixed(decimalsFor(step)), anchor: "bottom" });
      }
      for (let y = yMin; y <= yMax + step * 1e-6; y += step) {
        push(Math.abs(y) < step * 1e-6 ? majorPositions : positions, { x: xMin, y, z }, { x: xMax, y, z });
        gridLabels.push({ point: { x: xMin, y, z }, text: y.toFixed(decimalsFor(step)), anchor: "left" });
      }
    }
    const minor = new THREE.LineSegments(
      new THREE.BufferGeometry().setAttribute("position", new THREE.Float32BufferAttribute(positions, 3)),
      new THREE.LineBasicMaterial({ color: 0x252c35, depthWrite: false })
    );
    const major = new THREE.LineSegments(
      new THREE.BufferGeometry().setAttribute("position", new THREE.Float32BufferAttribute(majorPositions, 3)),
      new THREE.LineBasicMaterial({ color: 0x56606d, depthWrite: false })
    );
    minor.renderOrder = -2;
    major.renderOrder = -1;
    gridRoot.add(minor, major);
    // Part zero axes.
    const axisLength = step * 1.5;
    const axisLines = [
      [{ x: 0, y: 0, z: 0 }, { x: axisLength, y: 0, z: 0 }, 0xff7b72],
      [{ x: 0, y: 0, z: 0 }, { x: 0, y: axisLength, z: 0 }, 0x7ee787],
      [{ x: 0, y: 0, z: 0 }, { x: 0, y: 0, z: axisLength }, 0x58a6ff]
    ];
    if (axesGroup) {
      contentRoot.remove(axesGroup);
      disposeObject(axesGroup);
    }
    axesGroup = new THREE.Group();
    for (const [a, b, color] of axisLines) {
      if (lathe && b.y) continue;
      axesGroup.add(new THREE.Line(
        new THREE.BufferGeometry().setAttribute("position", new THREE.Float32BufferAttribute([a.x, a.y, a.z, b.x, b.y, b.z], 3)),
        new THREE.LineBasicMaterial({ color, depthTest: false })
      ));
    }
    contentRoot.add(axesGroup);
    // The grid follows the part frame in the machine view.
    gridRoot.matrixAutoUpdate = false;
    gridRoot.matrix.copy(contentRoot.matrix);
    gridRoot.matrixWorldNeedsUpdate = true;
  }

  // ----- camera --------------------------------------------------------------------

  function contentBounds(includeRapids = false) {
    const model = state.model;
    if (!model) return undefined;
    const bounds = emptyBounds();
    const cutBounds = emptyBounds();
    const add = (target, point) => extendBounds(target, toScene(point));
    for (const segment of model.segments) {
      if (!segmentVisible(segment) || RETURN_KINDS.has(segment.kind) && !includeRapids) continue;
      for (const point of segment.points) {
        add(bounds, point);
        if (!RAPID_LAYER_KINDS.has(segment.kind)) add(cutBounds, point);
      }
    }
    if (state.filters.showCompensated) {
      for (const segment of model.compensatedSegments || []) segment.points.forEach((point) => add(cutBounds, point));
    }
    if (state.filters.showPolyarc) {
      for (const segment of state.polyarcSegments || []) segment.points.forEach((point) => add(cutBounds, point));
    }
    if (state.kind === "lathe" && model.stock) {
      add(cutBounds, { x: model.stock.od, z: model.stock.frontZ });
      add(cutBounds, { x: model.stock.id, z: model.stock.frontZ - model.stock.length });
    }
    // Mills fit to the cutting moves; lathes (where rapids frame the part) to everything.
    const useCut = Number.isFinite(cutBounds.minX) && (state.kind === "mill" || !Number.isFinite(bounds.minX));
    if (useCut) return cutBounds;
    if (Number.isFinite(bounds.minX)) {
      if (Number.isFinite(cutBounds.minX)) {
        for (const key of Object.keys(cutBounds)) {
          bounds[key] = key.startsWith("min") ? Math.min(bounds[key], cutBounds[key]) : Math.max(bounds[key], cutBounds[key]);
        }
      }
      return bounds;
    }
    return Number.isFinite(cutBounds.minX) ? cutBounds : undefined;
  }

  function viewBasis() {
    const up = state.up;
    const front = state.front;
    const side = new THREE.Vector3().crossVectors(up, front);
    const horizontal = front.clone().multiplyScalar(Math.cos(state.azimuth))
      .add(side.clone().multiplyScalar(Math.sin(state.azimuth)));
    const eye = horizontal.clone().multiplyScalar(Math.cos(state.elevation))
      .add(up.clone().multiplyScalar(Math.sin(state.elevation)))
      .normalize();
    const right = new THREE.Vector3().crossVectors(up, horizontal).normalize();
    const forward = eye.clone().negate();
    const cameraUp = new THREE.Vector3().crossVectors(right, forward).normalize();
    return { eye, right, cameraUp };
  }

  function updateCamera() {
    const width = Math.max(1, canvas.clientWidth);
    const height = Math.max(1, canvas.clientHeight);
    const aspect = width / height;
    const halfHeight = state.viewHeight / 2;
    camera.left = -halfHeight * aspect;
    camera.right = halfHeight * aspect;
    camera.top = halfHeight;
    camera.bottom = -halfHeight;
    const { eye, cameraUp } = viewBasis();
    const distance = Math.max(state.sceneRadius * 6, state.viewHeight * 4, 1);
    let target = state.target.clone();
    if (state.frame === "machine" && state.kind === "mill") {
      target = state.target.clone();
    }
    camera.position.copy(target).addScaledVector(eye, distance);
    camera.up.copy(cameraUp);
    camera.lookAt(target);
    camera.near = 0;
    camera.far = distance * 2 + state.sceneRadius * 8;
    camera.updateProjectionMatrix();
    camera.updateMatrixWorld();
    state.pickDirty = true;
  }

  function setViewPreset(name) {
    const lathe = state.kind === "lathe";
    state.view = name;
    const presets = lathe
      ? {
        lathe: [0, 0],
        iso: [35, 28],
        top: [0, 89.999],
        front: [0, 0],
        right: [90, 0]
      }
      : {
        iso: [45, 35.264],
        top: [0, 89.999],
        front: [0, 0],
        right: [90, 0],
        left: [-90, 0],
        back: [180, 0],
        bottom: [0, -89.999]
      };
    const [azimuth, elevation] = presets[name] || presets[lathe ? "lathe" : "iso"];
    state.azimuth = azimuth * DEG;
    state.elevation = elevation * DEG;
    fit();
  }

  function fit() {
    const width = Math.max(1, canvas.clientWidth);
    const height = Math.max(1, canvas.clientHeight);
    let bounds;
    if (state.frame === "machine" && state.kind === "mill" && machineRoot.children.length) {
      const box = new THREE.Box3().setFromObject(machineRoot);
      bounds = {
        minX: box.min.x, maxX: box.max.x, minY: box.min.y, maxY: box.max.y, minZ: box.min.z, maxZ: box.max.z
      };
    } else {
      bounds = contentBounds(false);
    }
    if (!bounds) {
      bounds = { minX: -1, maxX: 1, minY: -1, maxY: 1, minZ: -1, maxZ: 1 };
    }
    const center = new THREE.Vector3(
      (bounds.minX + bounds.maxX) / 2,
      (bounds.minY + bounds.maxY) / 2,
      (bounds.minZ + bounds.maxZ) / 2
    );
    const { right, cameraUp } = viewBasis();
    const corners = [];
    for (const x of [bounds.minX, bounds.maxX]) {
      for (const y of [bounds.minY, bounds.maxY]) {
        for (const z of [bounds.minZ, bounds.maxZ]) corners.push(new THREE.Vector3(x, y, z));
      }
    }
    let spanX = 0;
    let spanY = 0;
    for (const corner of corners) {
      const offset = corner.clone().sub(center);
      spanX = Math.max(spanX, Math.abs(offset.dot(right)));
      spanY = Math.max(spanY, Math.abs(offset.dot(cameraUp)));
    }
    const aspect = width / height;
    const margin = 1.15;
    state.viewHeight = Math.max(2 * spanY * margin, (2 * spanX * margin) / aspect, 1e-3);
    state.sceneRadius = Math.max(corners[0].distanceTo(corners[7]) / 2, 1e-3);
    state.target.copy(center);
    if (state.frame === "machine") {
      state.target.copy(center);
    }
    updateCamera();
    rebuildTool();
    requestRender();
  }

  // ----- rendering ------------------------------------------------------------------

  function resize() {
    const width = Math.max(1, canvas.clientWidth);
    const height = Math.max(1, canvas.clientHeight);
    renderer.setPixelRatio(window.devicePixelRatio || 1);
    renderer.setSize(width, height, false);
    resolution.set(width * renderer.getPixelRatio(), height * renderer.getPixelRatio());
    for (const material of materials) {
      material.uniforms.pixelRatio.value = renderer.getPixelRatio();
    }
    if (overlay) {
      overlay.width = Math.round(width * (window.devicePixelRatio || 1));
      overlay.height = Math.round(height * (window.devicePixelRatio || 1));
    }
    if (pickTarget) {
      pickTarget.dispose();
      pickTarget = undefined;
    }
    updateCamera();
    requestRender();
  }

  function requestRender() {
    state.pickDirty = true;
    if (state.renderQueued) return;
    state.renderQueued = true;
    window.requestAnimationFrame(() => {
      state.renderQueued = false;
      render();
    });
  }

  function projectToScreen(point, matrix) {
    const vector = new THREE.Vector3(point.x, point.y, point.z);
    if (matrix) vector.applyMatrix4(matrix);
    vector.project(camera);
    return {
      x: (vector.x * 0.5 + 0.5) * canvas.clientWidth,
      y: (-vector.y * 0.5 + 0.5) * canvas.clientHeight,
      visible: vector.z >= -1 && vector.z <= 1
    };
  }

  function drawOverlay() {
    if (!overlayContext) return;
    const ratio = window.devicePixelRatio || 1;
    const width = canvas.clientWidth;
    const height = canvas.clientHeight;
    overlayContext.setTransform(ratio, 0, 0, ratio, 0, 0);
    overlayContext.clearRect(0, 0, width, height);
    overlayContext.font = "10px Consolas, monospace";
    overlayContext.fillStyle = "#6e7681";
    // Grid labels, thinned so they never overlap.
    const matrix = gridRoot.matrix;
    let lastBottom = -Infinity;
    let lastLeft = Infinity;
    const bottomLabels = gridLabels.filter((label) => label.anchor === "bottom")
      .map((label) => ({ ...label, screen: projectToScreen(label.point, matrix) }))
      .sort((a, b) => a.screen.x - b.screen.x);
    for (const label of bottomLabels) {
      if (!label.screen.visible || label.screen.x < 0 || label.screen.x > width - 20) continue;
      if (label.screen.x - lastBottom < 42) continue;
      overlayContext.fillText(label.text, label.screen.x + 3, Math.min(height - 6, Math.max(12, label.screen.y + 12)));
      lastBottom = label.screen.x;
    }
    const leftLabels = gridLabels.filter((label) => label.anchor === "left")
      .map((label) => ({ ...label, screen: projectToScreen(label.point, matrix) }))
      .sort((a, b) => b.screen.y - a.screen.y);
    for (const label of leftLabels) {
      if (!label.screen.visible || label.screen.y < 12 || label.screen.y > height) continue;
      if (lastLeft - label.screen.y < 16) continue;
      overlayContext.fillText(label.text, Math.max(4, Math.min(width - 60, label.screen.x - 8 - overlayContext.measureText(label.text).width)), label.screen.y - 3);
      lastLeft = label.screen.y;
    }
    // Axis triad.
    const origin = { x: 44, y: height - 44 };
    const { right, cameraUp } = viewBasis();
    const partRotation = new THREE.Matrix4().extractRotation(contentRoot.matrix);
    const axes = state.kind === "lathe"
      ? [["X", new THREE.Vector3(1, 0, 0), "#ff7b72"], ["Z", new THREE.Vector3(0, 0, 1), "#58a6ff"]]
      : [["X", new THREE.Vector3(1, 0, 0), "#ff7b72"], ["Y", new THREE.Vector3(0, 1, 0), "#7ee787"], ["Z", new THREE.Vector3(0, 0, 1), "#58a6ff"]];
    overlayContext.lineWidth = 2;
    overlayContext.font = "bold 11px Consolas, monospace";
    for (const [label, vector, color] of axes) {
      const direction = vector.clone().applyMatrix4(partRotation);
      const x = direction.dot(right) * 26;
      const y = -direction.dot(cameraUp) * 26;
      overlayContext.strokeStyle = color;
      overlayContext.beginPath();
      overlayContext.moveTo(origin.x, origin.y);
      overlayContext.lineTo(origin.x + x, origin.y + y);
      overlayContext.stroke();
      overlayContext.fillStyle = color;
      overlayContext.fillText(label, origin.x + x * 1.25 - 4, origin.y + y * 1.25 + 4);
    }
    if (state.kind === "lathe") {
      overlayContext.fillStyle = "#6e7681";
      overlayContext.font = "10px Consolas, monospace";
      overlayContext.fillText("X drawn at radius scale; grid labels are diameters", 12, 16);
    }
  }

  function render() {
    updateFrames();
    gridRoot.matrix.copy(contentRoot.matrix);
    gridRoot.matrixWorldNeedsUpdate = true;
    renderer.setRenderTarget(null);
    renderer.render(scene, camera);
    drawOverlay();
    if (onRender) onRender();
  }

  function ensurePickBuffer() {
    const width = Math.max(1, Math.round(canvas.clientWidth * renderer.getPixelRatio()));
    const height = Math.max(1, Math.round(canvas.clientHeight * renderer.getPixelRatio()));
    if (!pickTarget || pickTarget.width !== width || pickTarget.height !== height) {
      pickTarget?.dispose();
      pickTarget = new THREE.WebGLRenderTarget(width, height, {
        type: THREE.UnsignedByteType,
        format: THREE.RGBAFormat,
        depthBuffer: true
      });
      state.pickDirty = true;
    }
    if (!state.pickDirty) return;
    updateFrames();
    pickRoot.matrix.copy(contentRoot.matrix);
    pickRoot.matrixWorldNeedsUpdate = true;
    const clearColor = new THREE.Color();
    renderer.getClearColor(clearColor);
    const clearAlpha = renderer.getClearAlpha();
    renderer.setRenderTarget(pickTarget);
    renderer.setClearColor(0x000000, 1);
    renderer.clear();
    renderer.render(pickScene, camera);
    renderer.setRenderTarget(null);
    renderer.setClearColor(clearColor, clearAlpha);
    state.pickDirty = false;
  }

  function pick(clientX, clientY, radius = 5) {
    if (!state.model || !pickTargets.length) return undefined;
    ensurePickBuffer();
    const rectangle = canvas.getBoundingClientRect();
    const ratio = renderer.getPixelRatio();
    const x = Math.round((clientX - rectangle.left) * ratio);
    const y = Math.round((rectangle.bottom - clientY) * ratio);
    const size = radius * 2 + 1;
    const left = Math.max(0, x - radius);
    const bottom = Math.max(0, y - radius);
    const width = Math.min(size, pickTarget.width - left);
    const height = Math.min(size, pickTarget.height - bottom);
    if (width <= 0 || height <= 0) return undefined;
    const pixels = new Uint8Array(width * height * 4);
    renderer.readRenderTargetPixels(pickTarget, left, bottom, width, height, pixels);
    let best;
    let bestDistance = Infinity;
    for (let row = 0; row < height; row += 1) {
      for (let column = 0; column < width; column += 1) {
        const offset = (row * width + column) * 4;
        const id = pixels[offset] + pixels[offset + 1] * 256 + pixels[offset + 2] * 65536;
        if (!id) continue;
        const distance = Math.hypot(left + column - x, bottom + row - y);
        if (distance < bestDistance) {
          bestDistance = distance;
          best = id;
        }
      }
    }
    return best ? pickTargets[best - 1] : undefined;
  }

  // ----- interaction ----------------------------------------------------------------

  let drag;
  function pointerDown(event) {
    const pan = event.button === 1 || event.button === 2 || event.shiftKey;
    drag = {
      pointerId: event.pointerId,
      x: event.clientX,
      y: event.clientY,
      startX: event.clientX,
      startY: event.clientY,
      mode: pan ? "pan" : "orbit",
      moved: false
    };
    canvas.setPointerCapture(event.pointerId);
  }

  function pointerMove(event) {
    if (!drag || drag.pointerId !== event.pointerId) return false;
    const dx = event.clientX - drag.x;
    const dy = event.clientY - drag.y;
    drag.x = event.clientX;
    drag.y = event.clientY;
    if (Math.hypot(event.clientX - drag.startX, event.clientY - drag.startY) > 3) drag.moved = true;
    if (!drag.moved) return true;
    if (drag.mode === "orbit") {
      state.azimuth -= dx * 0.008;
      state.elevation = Math.max(-89.999 * DEG, Math.min(89.999 * DEG, state.elevation + dy * 0.008));
      state.view = "custom";
    } else {
      const { right, cameraUp } = viewBasis();
      const scale = state.viewHeight / Math.max(1, canvas.clientHeight);
      state.target.addScaledVector(right, -dx * scale).addScaledVector(cameraUp, dy * scale);
    }
    updateCamera();
    requestRender();
    return true;
  }

  function pointerUp(event) {
    if (!drag || drag.pointerId !== event.pointerId) return { click: false };
    const click = !drag.moved && event.button === 0;
    try {
      canvas.releasePointerCapture(event.pointerId);
    } catch {
      // The pointer may already be released.
    }
    drag = undefined;
    return { click };
  }

  function wheel(event) {
    const rectangle = canvas.getBoundingClientRect();
    const px = event.clientX - rectangle.left - rectangle.width / 2;
    const py = event.clientY - rectangle.top - rectangle.height / 2;
    const scaleBefore = state.viewHeight / Math.max(1, rectangle.height);
    const factor = Math.exp(event.deltaY * 0.0012);
    state.viewHeight = Math.min(1e7, Math.max(1e-4, state.viewHeight * factor));
    const scaleAfter = state.viewHeight / Math.max(1, rectangle.height);
    const { right, cameraUp } = viewBasis();
    // Keep the point under the cursor fixed.
    state.target.addScaledVector(right, px * (scaleBefore - scaleAfter));
    state.target.addScaledVector(cameraUp, -py * (scaleBefore - scaleAfter));
    updateCamera();
    requestRender();
  }

  // ----- public API -------------------------------------------------------------------

  function setModel(model, { polyarcSegments, preserveView = false } = {}) {
    const previousKind = state.kind;
    const previousMachine = state.model?.machineDefinition?.id;
    state.model = model;
    state.kind = model?.kind === "lathe" ? "lathe" : "mill";
    state.polyarcSegments = polyarcSegments || [];
    if (state.kind === "lathe") {
      state.up = new THREE.Vector3(1, 0, 0);
      state.front = new THREE.Vector3(0, 1, 0);
    } else {
      state.up = new THREE.Vector3(0, 0, 1);
      state.front = new THREE.Vector3(0, -1, 0);
    }
    if (state.kind !== "mill") state.frame = "part";
    if (previousMachine !== model?.machineDefinition?.id || previousKind !== state.kind || !chain) {
      buildMachine();
    }
    rebuildGeometry();
    rebuildStock();
    rebuildTool();
    if (!preserveView || previousKind !== state.kind) {
      setViewPreset(model?.view?.defaultView || (state.kind === "lathe" ? "lathe" : "iso"));
    } else {
      requestRender();
    }
  }

  function setFilters(filters) {
    state.filters = { ...filters };
    rebuildGeometry();
    requestRender();
  }

  function setSelection(line, executionIndex) {
    state.selectedLine = line;
    state.selectedExecutionIndex = executionIndex;
    rebuildSelection();
  }

  // playback: undefined (show all), or { completedIndex, complete, active:
  // { points } } with the partial active move in model coordinates.
  function setPlayback(playback) {
    state.playback = playback;
    applyPlaybackRange();
    const active = playback?.active?.points?.length >= 2
      ? [{ points: playback.active.points, style: SEGMENT_STYLES.active }]
      : [];
    buildLayer("active", active);
    requestRender();
  }

  function setToolPose(pose) {
    state.toolPose = pose;
    if (pose && state.kind === "mill" && kinematics && chain) {
      const rotary = pose.rotary || { A: 0, B: 0, C: 0 };
      pose.axis = kinematics.toolAxisInWorkpiece(chain, rotary);
    }
    rebuildTool();
    requestRender();
  }

  function setFrame(frame) {
    state.frame = frame === "machine" && state.kind === "mill" ? "machine" : "part";
    fit();
  }

  function setOptions({ showTool, showMachine } = {}) {
    if (showTool !== undefined) state.showTool = showTool;
    if (showMachine !== undefined) state.showMachine = showMachine;
    rebuildTool();
    requestRender();
  }

  function dispose() {
    for (const layer of Object.values(layers)) {
      layer.mesh?.geometry.dispose();
    }
    materials.forEach((material) => material.dispose());
    pickTarget?.dispose();
    renderer.dispose();
  }

  const observer = typeof ResizeObserver === "function" ? new ResizeObserver(() => resize()) : undefined;
  observer?.observe(canvas);
  resize();

  return {
    dispose,
    fit,
    pick,
    pointerDown,
    pointerMove,
    pointerUp,
    render: requestRender,
    resize,
    setFilters,
    setFrame,
    setModel,
    setOptions,
    setPlayback,
    setSelection,
    setToolPose,
    setView: setViewPreset,
    wheel,
    get frame() {
      return state.frame;
    },
    get view() {
      return state.view;
    },
    get kind() {
      return state.kind;
    }
  };
}
