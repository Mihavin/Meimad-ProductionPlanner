// Meimad Planner wrapper around the vendored 3D viewer (media/viewer3d.js). preview.js imports
// this module instead (meimad-bridge.js points document.body.dataset.viewerModule here). It keeps
// the upstream viewer unmodified: the THREE namespace handed to it records the scene, the content
// group, the camera and the renderer it creates, and the returned API is wrapped so the Meimad
// simulation (meimad-simulation.js) learns about every model, playback, tool pose and filter
// change. window.meimadViewer3d exposes the captured objects and an event hook.
import { createToolpathViewer as createUpstreamViewer, SEGMENT_STYLES } from "../media/viewer3d.js";

export { SEGMENT_STYLES };

export function createToolpathViewer(options) {
  const THREE = options.THREE;
  const captured = { scenes: [], groups: [], cameras: [], renderers: [] };
  // The module namespace is not writable, and an object derived from it cannot shadow its
  // exports either (assignment delegates to the namespace), so the four constructors are
  // overridden on a plain copy of the namespace. If that ever fails the upstream viewer runs
  // unwrapped and the simulation stays off.
  let capturing = THREE;
  try {
    capturing = {
      ...THREE,
      Scene: class extends THREE.Scene {
        constructor(...args) { super(...args); captured.scenes.push(this); }
      },
      Group: class extends THREE.Group {
        constructor(...args) { super(...args); captured.groups.push(this); }
      },
      OrthographicCamera: class extends THREE.OrthographicCamera {
        constructor(...args) { super(...args); captured.cameras.push(this); }
      },
      WebGLRenderer: class extends THREE.WebGLRenderer {
        constructor(...args) { super(...args); captured.renderers.push(this); }
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
  const upstream = createUpstreamViewer({
    ...options,
    THREE: capturing,
    onRender: () => {
      emit("render");
      if (typeof options.onRender === "function") options.onRender();
    }
  });
  // Creation order in viewer3d.js: scene, camera, contentRoot, machineRoot, gridRoot, pickScene, pickRoot.
  const hooks = {
    THREE,
    captured: capturing !== THREE && captured.scenes.length > 0 && captured.groups.length > 0,
    scene: captured.scenes[0],
    contentRoot: captured.groups[0],
    machineRoot: captured.groups[1],
    camera: captured.cameras[0],
    renderer: captured.renderers[0],
    upstream,
    on(name, listener) {
      if (!listeners.has(name)) listeners.set(name, new Set());
      listeners.get(name).add(listener);
      return () => listeners.get(name)?.delete(listener);
    }
  };
  window.meimadViewer3d = hooks;
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
      emit("filters", filters);
    },
    setFrame(frame) {
      upstream.setFrame(frame);
      emit("frame", frame);
    },
    setModel(model, modelOptions) {
      emit("model", model, modelOptions);
      upstream.setModel(model, modelOptions);
      emit("modelApplied", model);
    },
    setOptions: (viewOptions) => upstream.setOptions(viewOptions),
    setPlayback(playback) {
      upstream.setPlayback(playback);
      emit("playback", playback);
    },
    setSelection: (line, executionIndex) => upstream.setSelection(line, executionIndex),
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
