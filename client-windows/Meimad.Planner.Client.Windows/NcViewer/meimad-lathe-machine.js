"use strict";

// Meimad Planner: the Machine frame for lathes. The vendored viewer draws a lathe program as its X-Z
// turning profile and offers the Machine frame for mills only, so this script adds a Frame choice for
// lathes and draws the lathe's kinematic chain itself: bed, Z carriage, X slide, the Y slide of a
// Y-axis lathe and the turret follow the tool; the main spindle turns with C. Machine 3D models
// (meimad-scene.js) attach to these node groups. Milling axes come from meimad-lathe-axes-core.js.
// Scene axes for a lathe: x = radius (up), z = spindle axis, y = depth; the chain origin (spindle
// face) sits at meimadLatheAxes.spindleFaceZ on the program's Z.
(() => {
  const core = window.MeimadLatheAxes;
  const kinematics = window.CncKinematics;
  const label = document.getElementById("meimadLatheFrameLabel");
  const select = document.getElementById("meimadLatheFrame");
  const note = document.getElementById("meimadLatheNote");
  if (!core || !kinematics || !label || !select) return;

  let hooks = null;
  let model = null;
  let chain = null;
  let root = null;
  let definitionId = null;
  let annotation = null;
  let lastPose = null;
  const groups = new Map();

  const lathe = () => Boolean(model && model.kind === "lathe");
  const machineFrame = () => lathe() && select.value === "machine";

  function scaleOf() {
    const scale = model && model.view && model.view.unitScale;
    return Number.isFinite(scale) && scale > 0 ? scale : 1;
  }

  function sourceLines() {
    const editor = document.querySelector(".CodeMirror");
    const text = editor && editor.CodeMirror ? editor.CodeMirror.getValue() : "";
    return text ? text.split(/\r?\n/) : [];
  }

  function stockCylinder() {
    const definition = window.meimadStock && typeof window.meimadStock.definition === "function" ? window.meimadStock.definition() : null;
    if (!definition || definition.type !== "cylinder" || !definition.cylinder) return null;
    return { frontZ: Number(definition.cylinder.frontZ), length: Number(definition.cylinder.length) };
  }

  function dispose(object) {
    object.traverse((child) => {
      if (child.geometry) child.geometry.dispose();
      if (child.material) (Array.isArray(child.material) ? child.material : [child.material]).forEach((material) => material.dispose());
    });
  }

  function clear() {
    if (root && hooks) {
      hooks.scene.remove(root);
      dispose(root);
    }
    root = null;
    chain = null;
    groups.clear();
  }

  function placeholderMesh(THREE, geometry, workpiece) {
    const material = new THREE.MeshBasicMaterial({
      color: workpiece ? 0x2f81f7 : 0x484f58,
      transparent: true,
      opacity: workpiece ? 0.3 : 0.24,
      depthWrite: false
    });
    let mesh;
    if (geometry.shape === "box") {
      const size = geometry.size || [1, 1, 1];
      mesh = new THREE.Mesh(new THREE.BoxGeometry(size[0], size[1], size[2]), material);
    } else if (geometry.shape === "cylinder") {
      mesh = new THREE.Mesh(new THREE.CylinderGeometry(geometry.radius || 1, geometry.radius || 1, geometry.height || 1, 48), material);
      if ((geometry.axis || "z") === "z") mesh.rotation.x = Math.PI / 2;
      else if (geometry.axis === "x") mesh.rotation.z = Math.PI / 2;
    }
    if (!mesh) return null;
    const center = geometry.center || [0, 0, 0];
    mesh.position.set(center[0], center[1], center[2]);
    const edges = new THREE.LineSegments(
      new THREE.EdgesGeometry(mesh.geometry, 25),
      new THREE.LineBasicMaterial({ color: 0x8b949e, transparent: true, opacity: 0.45 })
    );
    edges.position.copy(mesh.position);
    edges.rotation.copy(mesh.rotation);
    const group = new THREE.Group();
    group.add(mesh, edges);
    return group;
  }

  function build() {
    clear();
    const definition = model && model.machineDefinition;
    definitionId = definition ? definition.id : null;
    if (!hooks || !lathe() || !definition) return;
    const nodes = core.prepareNodes(definition, scaleOf());
    try {
      chain = kinematics.buildChain({ nodes });
    } catch (error) {
      chain = null;
      if (note) note.textContent = "The lathe definition has no usable kinematic chain, so the Machine view is unavailable.";
      return;
    }
    const THREE = hooks.THREE;
    root = new THREE.Group();
    root.name = "meimad-lathe-machine";
    root.matrixAutoUpdate = false;
    for (const node of nodes) {
      const group = new THREE.Group();
      group.matrixAutoUpdate = false;
      group.userData.node = node;
      if (node.geometry) {
        const shape = placeholderMesh(THREE, node.geometry, node.workpieceMount);
        if (shape) group.add(shape);
      }
      groups.set(node.id, group);
      root.add(group);
    }
    hooks.scene.add(root);
    window.dispatchEvent(new CustomEvent("meimad:lathe-machine"));
  }

  function axesNow() {
    const active = window.meimadActiveSegment ? window.meimadActiveSegment.current() : null;
    return active && annotation ? core.at(annotation, active.index, active.fraction) : { C: 0, Y: 0 };
  }

  /** The machine joints of the current tool position: X as diameter, Z from the spindle face. */
  function joints() {
    const point = lastPose && (lastPose.nosePoint || lastPose.center);
    const faceZ = core.spindleFaceZ(model, stockCylinder());
    const milling = axesNow();
    if (!point || !Number.isFinite(point.z)) return { X: 0, Y: milling.Y, Z: 0, C: milling.C, faceZ };
    const diameter = Number.isFinite(point.r) ? point.r * 2 : point.x;
    return { X: diameter, Y: milling.Y, Z: point.z - faceZ, C: milling.C, faceZ };
  }

  function pose() {
    if (!root || !chain) return;
    root.visible = machineFrame();
    if (!root.visible) {
      hooks.upstream.render();
      return;
    }
    const values = joints();
    root.matrix.makeTranslation(0, 0, values.faceZ);
    root.matrixWorldNeedsUpdate = true;
    const world = kinematics.evaluateChain(chain, { X: values.X, Y: values.Y, Z: values.Z, C: values.C });
    for (const [id, group] of groups) {
      const matrix = world.get(id);
      if (!matrix) continue;
      group.matrix.set(
        matrix[0], matrix[1], matrix[2], matrix[3],
        matrix[4], matrix[5], matrix[6], matrix[7],
        matrix[8], matrix[9], matrix[10], matrix[11],
        matrix[12], matrix[13], matrix[14], matrix[15]);
      group.matrixWorldNeedsUpdate = true;
    }
    hooks.upstream.render();
  }

  function refreshUi() {
    label.hidden = !lathe();
    if (!note) return;
    if (!lathe()) {
      note.textContent = "";
      return;
    }
    const parts = [];
    if (annotation && (annotation.usesC || annotation.usesY)) {
      parts.push(`Milling axes in the program: ${[annotation.usesC ? "C" : null, annotation.usesY ? "Y" : null].filter(Boolean).join(" and ")}; the Machine view turns the spindle and moves the turret with them. Live-tool cuts are drawn as their X-Z profile only.`);
    }
    if (annotation && annotation.polar) parts.push("Polar interpolation (G112/G12.1) is not interpreted: its X/C words are not machine positions.");
    if (annotation && annotation.cylindrical) parts.push("Cylindrical interpolation (G107/G7.1) is not interpreted.");
    note.textContent = parts.join(" ");
  }

  function attach(value) {
    if (!value || hooks === value || !value.captured) return;
    hooks = value;
    hooks.on("modelApplied", (next) => {
      const previousKind = model && model.kind;
      model = next || null;
      annotation = lathe() ? core.annotate(model.segments, sourceLines()) : null;
      window.meimadLatheAnnotation = annotation;
      if (!lathe()) clear();
      else if (!root || previousKind !== "lathe" || definitionId !== (model.machineDefinition && model.machineDefinition.id)) build();
      refreshUi();
      pose();
    });
    hooks.on("toolPose", (next) => {
      lastPose = next || null;
      if (machineFrame()) pose();
    });
  }

  select.addEventListener("change", () => {
    pose();
    window.dispatchEvent(new CustomEvent("meimad:lathe-frame", { detail: select.value }));
  });
  if (window.meimadActiveSegment) window.meimadActiveSegment.on(() => { if (machineFrame()) pose(); });

  window.meimadLatheMachine = Object.freeze({
    /** Node id -> group, for Machine 3D models (meimad-scene.js). */
    nodeGroups: () => new Map(groups),
    isMachineFrame: machineFrame,
    joints
  });
  if (window.meimadViewer3d) attach(window.meimadViewer3d);
  window.addEventListener("meimad:viewer3d", (event) => attach(event.detail));
})();
