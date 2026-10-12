// Meimad Planner wrapper around the vendored 3D viewer (media/viewer3d.js). preview.js imports
// this module instead (meimad-bridge.js points document.body.dataset.viewerModule here). It keeps
// the upstream viewer unmodified: the THREE namespace handed to it records the scene, the content
// group, the camera and the renderer it creates, and the returned API is wrapped so the Meimad
// simulation (meimad-simulation.js) learns about every model, playback, tool pose and filter
// change and selection (meimad-playback.js follows the selected row into a called program).
// window.meimadViewer3d exposes the captured objects and an event hook.
import { createToolpathViewer as createUpstreamViewer, SEGMENT_STYLES } from "../media/viewer3d.js";

// Segment kinds of the Okuma OSP executor (meimad-okuma.js). An Okuma cycle is drawn and listed
// in the legend under its own name; the FANUC cycle kinds of the upstream table (G71 rough pass,
// G76 thread cut, ...) are never used for an Okuma program.
Object.assign(SEGMENT_STYLES, {
  "lap-rough": { color: "#d2a8ff", width: 2, label: "LAP rough pass (G85/G86)" },
  "lap-contour": { color: "#b78cff", width: 2, label: "LAP rough contour follow" },
  "lap-finish": { color: "#ff9f43", width: 2, label: "LAP finish contour (G87)" },
  "osp-thread": { color: "#2dd4bf", width: 2, label: "Thread cut (G31-G35, G71/G72, G88)" },
  "osp-groove": { color: "#f778ba", width: 2, label: "Groove / drill peck (G73/G74)" }
});

export { SEGMENT_STYLES };

export function createToolpathViewer(options) {
  const THREE = options.THREE;
  const captured = { scenes: [], groups: [], cameras: [], renderers: [], vectors: [] };
  // The first three vectors the upstream viewer makes are its camera target, up and front (its state
  // object); "Follow tool" (meimad-display.js) moves the target.
  let capturingVectors = true;
  // The Path selector (meimad-path-visibility-core.js): the upstream line materials get a pick-id
  // range test whose inputs every line material shares, so a mode or playback change is a uniform
  // update, not a geometry rebuild.
  const Path = typeof window !== "undefined" ? window.MeimadPathVisibility : undefined;
  const CLASS_WIDTH = 4096;
  let classTexture = null;
  try {
    classTexture = new THREE.DataTexture(new Uint8Array(CLASS_WIDTH).fill(255), CLASS_WIDTH, 1, THREE.RedFormat, THREE.UnsignedByteType);
    classTexture.magFilter = THREE.NearestFilter;
    classTexture.minFilter = THREE.NearestFilter;
    classTexture.needsUpdate = true;
  } catch {
    classTexture = null;
  }
  const pathUniforms = {
    meimadPathMode: { value: 0 },
    meimadFilter: { value: 0 },
    meimadClass: { value: classTexture },
    meimadBaseCount: { value: 0 },
    meimadBaseThreshold: { value: 1 },
    meimadCompensatedThreshold: { value: 1 },
    meimadDim: { value: 1 }
  };
  let pathPatched = 0;
  // The module namespace is not writable, and an object derived from it cannot shadow its
  // exports either (assignment delegates to the namespace), so the constructors are
  // overridden on a plain copy of the namespace. If that ever fails the upstream viewer runs
  // unwrapped and the simulation stays off.
  let capturing = THREE;
  try {
    capturing = {
      ...THREE,
      ...(Path && THREE.ShaderMaterial ? {
        ShaderMaterial: class extends THREE.ShaderMaterial {
          constructor(parameters, ...rest) {
            const patched = parameters ? Path.patchLineShader(parameters.vertexShader) : null;
            super(patched ? { ...parameters, vertexShader: patched, uniforms: { ...parameters.uniforms, ...pathUniforms } } : parameters, ...rest);
            if (patched) pathPatched += 1;
          }
        }
      } : {}),
      Scene: class extends THREE.Scene {
        constructor(...args) { super(...args); captured.scenes.push(this); }
      },
      Group: class extends THREE.Group {
        constructor(...args) { super(...args); captured.groups.push(this); }
      },
      OrthographicCamera: class extends THREE.OrthographicCamera {
        constructor(...args) { super(...args); captured.cameras.push(this); }
      },
      ...(THREE.Vector3 ? {
        Vector3: class extends THREE.Vector3 {
          constructor(...args) {
            super(...args);
            if (capturingVectors && captured.vectors.length < 3) captured.vectors.push(this);
          }
        }
      } : {}),
      // The toolpath view asks for the discrete (high-performance) GPU on dual-GPU PCs.
      // An alpha canvas, so the 3D view background (meimad-background.js) can show through.
      WebGLRenderer: class extends THREE.WebGLRenderer {
        constructor(parameters = {}, ...rest) {
          super({ powerPreference: "high-performance", ...parameters, alpha: true }, ...rest);
          captured.renderers.push(this);
          this.meimadTransparentBackground = false;
          // Meimad scripts adjust the scene just before it is drawn, after the upstream viewer has set
          // its own frames (meimad-machine-frame.js moves the part with a table that travels in X/Y).
          // With a Meimad background the on-screen draw clears transparent; the picking pass (a render
          // target) keeps the viewer's own clear color. three.js assigns render as an instance
          // property in its constructor, which would shadow a class method, so it is wrapped here.
          const baseRender = this.render;
          this.render = (scene, camera) => {
            emit("beforeRender", scene);
            const transparent = this.meimadTransparentBackground && this.getRenderTarget() === null;
            const alpha = transparent ? this.getClearAlpha() : 1;
            if (transparent) this.setClearAlpha(0);
            try {
              baseRender.call(this, scene, camera);
            } finally {
              if (transparent) this.setClearAlpha(alpha);
            }
          };
        }
      }
    };
  } catch (error) {
    console.error("Meimad: the 3D scene could not be captured; material removal is unavailable.", error);
    capturing = THREE;
  }
  const listeners = new Map();
  const emit = (name, ...args) => {
    for (const listener of [...(listeners.get(name) || [])]) {
      try {
        listener(...args);
      } catch (error) {
        console.error(error);
      }
    }
  };
  let upstream;
  try {
    upstream = createUpstreamViewer({
    ...options,
    THREE: capturing,
    onRender: () => {
      emit("render");
      if (typeof options.onRender === "function") options.onRender();
    }
  });
  } finally {
    capturingVectors = false;
  }
  const [vectorTarget, vectorUp, vectorFront] = captured.vectors;
  const cameraTarget = vectorTarget && vectorUp && vectorFront
    && vectorUp.x === 0 && vectorUp.y === 0 && vectorUp.z === 1 && vectorFront.x === 0 && vectorFront.y === -1 && vectorFront.z === 0
    ? vectorTarget
    : null;
  // Creation order in viewer3d.js: scene, camera, contentRoot, machineRoot, gridRoot, pickScene, pickRoot.
  const hooks = {
    THREE,
    captured: capturing !== THREE && captured.scenes.length > 0 && captured.groups.length > 0,
    scene: captured.scenes[0],
    contentRoot: captured.groups[0],
    machineRoot: captured.groups[1],
    gridRoot: captured.groups[2],
    pickRoot: captured.groups[3],
    camera: captured.cameras[0],
    renderer: captured.renderers[0],
    cameraTarget,
    upstream,
    on(name, listener) {
      if (!listeners.has(name)) listeners.set(name, new Set());
      listeners.get(name).add(listener);
      return () => listeners.get(name)?.delete(listener);
    },
    /** Path selector: "played", "all", "remaining", "dim" or "hidden". */
    setPathMode(mode) {
      if (!Path) return;
      pathMode = Path.normalizeMode(mode);
      storePathMode(pathMode);
      applyPath();
      // Marks the pick buffer dirty (hidden moves cannot be picked) and redraws.
      upstream.setPlayback(upstreamPlayback(pathPlayback));
    },
    get pathMode() { return pathMode; },
    get pathControl() { return pathActive(); }
  };
  window.meimadViewer3d = hooks;

  let pathMode = Path ? Path.normalizeMode(readPathMode()) : "played";
  let pathModel;
  let pathFilters = {};
  let pathPlayback;
  let pathLayout = null;
  let pathSelection;          // execution index of the selected row's move
  let pathOperations = null;  // CAM operation per segment
  let classKey = "";
  // Without a patched shader (an upstream change) the vendored playback range stays in charge.
  const pathActive = () => Boolean(Path && pathPatched);
  function refreshPathLayout() {
    pathLayout = pathActive() ? Path.index(pathModel, pathFilters) : null;
    classKey = "";
  }

  // The NC program's text in the editor (operation header rows).
  function programText() {
    try {
      return document.querySelector(".CodeMirror")?.CodeMirror?.getValue() || "";
    } catch {
      return "";
    }
  }

  // Which moves the filter modes keep, uploaded to the class texture when it changes.
  function refreshClasses() {
    if (!pathLayout || !classTexture || !Path.FILTERS.has(pathMode)) return;
    const segments = pathModel?.segments || [];
    const running = pathPlayback && !pathPlayback.complete && Number.isFinite(pathPlayback.completedIndex);
    let key;
    let keep;
    if (pathMode === "tool") {
      const active = running ? segments[Math.max(0, Math.min(segments.length - 1, pathPlayback.completedIndex + 1))] : null;
      key = `tool|${active ? active.tool : "all"}`;
      keep = active ? (segment) => segment && segment.tool === active.tool : () => true;
    } else if (pathMode === "rowTool") {
      const selected = Number.isFinite(pathSelection) ? segments[pathSelection] : null;
      key = `row|${selected ? selected.tool : "all"}`;
      keep = selected ? (segment) => segment && segment.tool === selected.tool : () => true;
    } else {
      if (!pathOperations) pathOperations = Path.operations(segments, Path.headerLines(programText()));
      const operation = Number.isFinite(pathSelection) ? pathOperations[pathSelection] : undefined;
      key = `op|${operation ?? "all"}`;
      keep = operation !== undefined ? (segment, index) => pathOperations[index] === operation : () => true;
    }
    if (key === classKey) return;
    classKey = key;
    const flags = Path.classes(pathLayout, segments, keep);
    const height = Math.max(1, Math.ceil(flags.length / CLASS_WIDTH));
    const data = new Uint8Array(CLASS_WIDTH * height);
    data.set(flags);
    if (classTexture.image.height !== height) {
      const next = new THREE.DataTexture(data, CLASS_WIDTH, height, THREE.RedFormat, THREE.UnsignedByteType);
      next.magFilter = THREE.NearestFilter;
      next.minFilter = THREE.NearestFilter;
      next.needsUpdate = true;
      classTexture.dispose();
      classTexture = next;
      pathUniforms.meimadClass.value = next;
    } else {
      classTexture.image.data = data;
      classTexture.needsUpdate = true;
    }
  }
  function applyPath() {
    if (!pathActive()) return;
    const values = Path.uniforms(pathMode, pathLayout, pathPlayback);
    pathUniforms.meimadPathMode.value = values.mode;
    pathUniforms.meimadBaseCount.value = values.baseCount;
    pathUniforms.meimadBaseThreshold.value = values.baseThreshold;
    pathUniforms.meimadCompensatedThreshold.value = values.compensatedThreshold;
    pathUniforms.meimadDim.value = values.dim;
    pathUniforms.meimadFilter.value = values.filter && classTexture ? 1 : 0;
    refreshClasses();
  }
  // The first move of a selected main-program row.
  function selectionExecution(line) {
    if (!Number.isFinite(line)) return undefined;
    const segments = pathModel?.segments || [];
    for (let index = 0; index < segments.length; index += 1) {
      if (segments[index].line === line && !segments[index].sourceUnit) return index;
    }
    return undefined;
  }

  // The shader decides what is drawn, so the upstream viewer always keeps every instance.
  const upstreamPlayback = (playback) => (pathActive() && playback && !playback.complete ? { ...playback, complete: true } : playback);
  bindPathControl(pathActive(), pathMode);
  const wrapper = {
    dispose: () => upstream.dispose(),
    fit: () => upstream.fit(),
    pick: (...args) => upstream.pick(...args),
    pointerDown: (event) => upstream.pointerDown(event),
    pointerMove: (event) => upstream.pointerMove(event),
    pointerUp: (event) => upstream.pointerUp(event),
    render: () => upstream.render(),
    resize: () => upstream.resize(),
    setFilters(filters) {
      upstream.setFilters(filters);
      pathFilters = filters || {};
      refreshPathLayout();
      applyPath();
      emit("filters", filters);
    },
    setFrame(frame) {
      upstream.setFrame(frame);
      emit("frame", frame);
    },
    setModel(model, modelOptions) {
      emit("model", model, modelOptions);
      pathModel = model;
      pathOperations = null;
      pathSelection = undefined;
      refreshPathLayout();
      applyPath();
      upstream.setModel(model, modelOptions);
      emit("modelApplied", model);
    },
    setOptions: (viewOptions) => upstream.setOptions(viewOptions),
    setPlayback(playback) {
      pathPlayback = playback;
      applyPath();
      upstream.setPlayback(upstreamPlayback(playback));
      emit("playback", playback);
    },
    setSelection(line, executionIndex) {
      pathSelection = Number.isFinite(executionIndex) ? executionIndex : selectionExecution(line);
      if (Path && Path.FILTERS.has(pathMode)) {
        applyPath();
        upstream.setPlayback(upstreamPlayback(pathPlayback));
      }
      upstream.setSelection(line, executionIndex);
      emit("selection", line, executionIndex);
    },
    setToolPose(pose) {
      upstream.setToolPose(pose);
      emit("toolPose", pose);
    },
    setView: (view) => upstream.setView(view),
    wheel: (event) => upstream.wheel(event),
    get frame() { return upstream.frame; },
    get view() { return upstream.view; },
    get kind() { return upstream.kind; }
  };
  window.dispatchEvent(new CustomEvent("meimad:viewer3d", { detail: hooks }));
  return wrapper;
}

const PATH_MODE_KEY = "meimad.ncViewer.pathMode";

// This PC's last Path choice (a per-viewer convenience; storage may be unavailable).
function readPathMode() {
  try {
    return window.localStorage?.getItem(PATH_MODE_KEY) || undefined;
  } catch {
    return undefined;
  }
}

function storePathMode(mode) {
  try {
    window.localStorage?.setItem(PATH_MODE_KEY, mode);
  } catch {
    // Not remembered; the choice still applies.
  }
}

// The Path selector next to Tool. It follows the current viewer (the page may create a new one).
function bindPathControl(active, mode) {
  const select = typeof document !== "undefined" ? document.getElementById("meimadPathMode") : null;
  if (!select) return;
  select.disabled = !active;
  select.value = mode;
  if (select.dataset.meimadBound) return;
  select.dataset.meimadBound = "1";
  select.addEventListener("change", () => window.meimadViewer3d?.setPathMode?.(select.value));
}
