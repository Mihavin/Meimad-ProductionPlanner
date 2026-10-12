"use strict";

// Meimad Planner: the Display card of the NC viewer (nc-viewer visualization spec).
//   - Machined stock, collision markers and the Tool Room tool's holder can be shown or hidden;
//   - View: Shaded, Wireframe or X-ray (see-through) for the stock, fixtures, finished part and machine;
//   - Follow tool keeps the tool in the middle of the view while it moves (T: F is Fit in the 3D
//     view), Fit all frames everything (Home).
// Visibility changes only switch scene objects on and off: no material simulation or toolpath
// rebuild runs again. Other display switches stay where they were (Tool and Rapids in the toolbar,
// machine models, fixtures, rest material and finished part in the Machine & setup card).
(() => {
  const byId = (id) => document.getElementById(id);
  const elements = {
    machined: byId("meimadShowMachined"),
    markers: byId("meimadShowMarkers"),
    holder: byId("meimadShowHolder"),
    viewMode: byId("meimadViewMode"),
    follow: byId("meimadFollowTool"),
    fitAll: byId("meimadFitAll"),
    canvas: byId("toolpathCanvas")
  };
  if (!elements.viewMode) return;

  const STORE = "meimad.ncViewer.display";
  let stored = {};
  try { stored = JSON.parse(window.localStorage?.getItem(STORE) || "{}") || {}; } catch { stored = {}; }
  const save = () => {
    try {
      window.localStorage?.setItem(STORE, JSON.stringify({
        machined: elements.machined?.checked, markers: elements.markers?.checked, holder: elements.holder?.checked,
        viewMode: elements.viewMode.value
      }));
    } catch {
      // Not remembered.
    }
  };
  if (elements.machined && stored.machined === false) elements.machined.checked = false;
  if (elements.markers && stored.markers === false) elements.markers.checked = false;
  if (elements.holder && stored.holder === false) elements.holder.checked = false;
  if (["shaded", "wireframe", "xray"].includes(stored.viewMode)) elements.viewMode.value = stored.viewMode;

  let hooks = null;
  let pose = null;
  let following = false;
  let model = null;

  // Shaded, wireframe or X-ray for every lit mesh (stock, fixtures, finished part, machine models);
  // the toolpath, markers and the tool keep their own look.
  function applyViewMode(root, mode) {
    if (!root) return;
    root.traverse((object) => {
      if (!object.isMesh) return;
      const materials = Array.isArray(object.material) ? object.material : [object.material];
      for (const material of materials) {
        if (!material || !(material.isMeshStandardMaterial || material.isMeshLambertMaterial || material.isMeshPhongMaterial)) continue;
        if (material.userData.meimadViewMode === mode) continue;
        if (!material.userData.meimadOriginal) {
          material.userData.meimadOriginal = { transparent: material.transparent, opacity: material.opacity, depthWrite: material.depthWrite, wireframe: material.wireframe };
        }
        const original = material.userData.meimadOriginal;
        material.wireframe = mode === "wireframe" ? true : original.wireframe;
        material.transparent = mode === "xray" ? true : original.transparent;
        material.opacity = mode === "xray" ? Math.min(original.opacity, 0.28) : original.opacity;
        material.depthWrite = mode === "xray" ? false : original.depthWrite;
        material.needsUpdate = true;
        material.userData.meimadViewMode = mode;
      }
    });
  }

  function beforeRender(scene) {
    if (!hooks || scene !== hooks.scene) return;
    const root = hooks.contentRoot;
    for (const child of root.children) {
      if (child.name === "meimad-stock-3d" || child.name === "meimad-stock-columns") child.visible = elements.machined ? elements.machined.checked : true;
      if (child.name === "meimad-collision-markers") child.visible = elements.markers ? elements.markers.checked : true;
      if (child.isGroup && child.userData && child.userData.meimadToolAssembly) {
        for (const part of child.children) if (part.userData && part.userData.meimadHolder) part.visible = elements.holder ? elements.holder.checked : true;
      }
    }
    const mode = elements.viewMode.value;
    applyViewMode(root, mode);
    applyViewMode(hooks.machineRoot, mode);
    if (following && pose && pose.tip && hooks.cameraTarget && model && model.kind !== "lathe") {
      const THREE = hooks.THREE;
      const tip = new THREE.Vector3(pose.tip.x, pose.tip.y, pose.tip.z).applyMatrix4(root.matrix);
      const delta = tip.clone().sub(hooks.cameraTarget);
      if (delta.lengthSq() > 1e-12) {
        hooks.cameraTarget.copy(tip);
        hooks.camera.position.add(delta);
        hooks.camera.updateMatrixWorld();
      }
    }
  }

  function render() {
    hooks?.upstream.render();
  }

  function setFollowing(value) {
    following = Boolean(value) && Boolean(hooks && hooks.cameraTarget);
    if (elements.follow) {
      elements.follow.setAttribute("aria-pressed", following ? "true" : "false");
      elements.follow.classList.toggle("active", following);
    }
    window.dispatchEvent(new CustomEvent("meimad:follow", { detail: { following } }));
    render();
  }

  function attach(value) {
    if (!value || hooks === value || !value.captured) return;
    hooks = value;
    hooks.on("beforeRender", beforeRender);
    hooks.on("toolPose", (next) => {
      pose = next || null;
      if (following) render();
    });
    hooks.on("modelApplied", (next) => { model = next || null; });
    if (elements.follow) {
      elements.follow.disabled = !hooks.cameraTarget;
      if (!hooks.cameraTarget) elements.follow.title = "Follow tool is unavailable: the 3D view's camera could not be reached.";
    }
  }

  for (const input of [elements.machined, elements.markers, elements.holder, elements.viewMode]) {
    input?.addEventListener("change", () => {
      save();
      render();
    });
  }
  elements.follow?.addEventListener("click", () => setFollowing(!following));
  elements.fitAll?.addEventListener("click", () => {
    setFollowing(false);
    hooks?.upstream.fit();
  });
  window.addEventListener("keydown", (event) => {
    if (event.target && /^(INPUT|SELECT|TEXTAREA)$/.test(event.target.tagName)) return;
    if (event.target && event.target.closest && event.target.closest(".CodeMirror")) return;
    if (event.key === "t" || event.key === "T") {
      if (!elements.follow?.disabled) setFollowing(!following);
    } else if (event.key === "Home") {
      setFollowing(false);
      hooks?.upstream.fit();
    }
  });

  window.meimadSceneFlags = Object.freeze({
    collisionMarkers: () => (elements.markers ? elements.markers.checked : true),
    machinedStock: () => (elements.machined ? elements.machined.checked : true),
    viewMode: () => elements.viewMode.value,
    following: () => following,
    setFollowing
  });

  if (window.meimadViewer3d) attach(window.meimadViewer3d);
  window.addEventListener("meimad:viewer3d", (event) => attach(event.detail));
})();
