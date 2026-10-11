// Meimad Planner Machine Designer 3D view. The window sends the definition being edited ("scene") and
// the jog readings ("pose"); this page draws every component at its pose (meimad-designer-core.js),
// its meshes (binary STL served by the window, placed with the scene-core convention) or its box /
// cylinder primitive, the tool and workpiece mounts and the selected component's joint. A mesh that
// cannot be loaded is listed as an error and its component keeps its primitive; nothing is drawn as if
// it were loaded. Clicking a component selects it in the window.
import * as THREE from "../node_modules/three/build/three.module.js";

const core = window.MeimadDesigner;
const placement = window.MeimadScene;
const material = window.MeimadMaterial;
const webview = window.chrome && window.chrome.webview;

const canvas = document.getElementById("view");
const elements = {
  axes: document.getElementById("axes"),
  calibration: document.getElementById("calibration"),
  problems: document.getElementById("problems"),
  showMeshes: document.getElementById("showMeshes"),
  showPrimitives: document.getElementById("showPrimitives")
};

const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, powerPreference: "high-performance" });
renderer.setPixelRatio(window.devicePixelRatio || 1);
const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(35, 1, 1, 200000);
camera.up.set(0, 0, 1);
const target = new THREE.Vector3(0, 0, 0);
let distance = 4000;
let azimuth = -Math.PI / 4;
let elevation = Math.PI / 6;

scene.add(new THREE.HemisphereLight(0xffffff, 0x404850, 1.6));
const key = new THREE.DirectionalLight(0xffffff, 1.8);
key.position.set(1, -0.6, 1.4);
scene.add(key);
const grid = new THREE.GridHelper(4000, 40, 0x556677, 0x334455);
grid.rotation.x = Math.PI / 2;
scene.add(grid);
scene.add(new THREE.AxesHelper(300));

const machineRoot = new THREE.Group();
scene.add(machineRoot);
const overlays = new THREE.Group();
scene.add(overlays);

let definition = { nodes: [], mounts: {}, axes: {} };
let axes = {};
let selected = null;
const groups = new Map();
const meshCache = new Map();
const problems = new Map();
let labels = {};

// Interface text in the Planner's language (the window sends the catalog entries the page uses).
const t = (text) => labels[text] || text;

function translatePage() {
  for (const element of document.querySelectorAll("[data-i18n]")) {
    if (element.dataset.i18nSource === undefined) element.dataset.i18nSource = element.textContent.trim();
    element.textContent = t(element.dataset.i18nSource);
  }
  for (const element of document.querySelectorAll("[data-i18n-title]")) {
    if (element.dataset.i18nTitle === "") element.dataset.i18nTitle = element.title;
    element.title = t(element.dataset.i18nTitle);
  }
}

function post(message) {
  if (webview) webview.postMessage(message);
}

function theme() {
  return document.documentElement.dataset.workbenchTheme === "light";
}

function applyTheme() {
  scene.background = new THREE.Color(theme() ? 0xedf0f1 : 0x0c1118);
}

// ----- meshes -----------------------------------------------------------------------------------

function loadMesh(url) {
  if (!meshCache.has(url)) {
    meshCache.set(url, fetch(url, { cache: "no-store" }).then(async (response) => {
      if (!response.ok) throw new Error((await response.text()) || `HTTP ${response.status}`);
      return material.readStl(await response.arrayBuffer());
    }));
  }
  return meshCache.get(url);
}

function xyz(value) {
  const v = Array.isArray(value) ? value : [0, 0, 0];
  return { x: Number(v[0]) || 0, y: Number(v[1]) || 0, z: Number(v[2]) || 0 };
}

function colorOf(text, fallback) {
  const match = /^#?([0-9a-f]{6})$/i.exec(String(text || ""));
  return match ? parseInt(match[1], 16) : fallback;
}

function surface(color, opacity, wireframe) {
  const transparent = opacity < 0.999;
  return new THREE.MeshStandardMaterial({
    color, roughness: 0.7, metalness: 0.2, side: THREE.DoubleSide,
    transparent, opacity, wireframe: Boolean(wireframe), depthWrite: !transparent
  });
}

function primitiveObject(primitive) {
  if (!primitive || !primitive.shape) return null;
  let geometry = null;
  if (primitive.shape === "box" && Array.isArray(primitive.size)) {
    geometry = new THREE.BoxGeometry(...primitive.size.map((value) => Math.max(1, Math.abs(Number(value) || 1))));
  } else if (primitive.shape === "cylinder") {
    const radius = Math.max(1, Number(primitive.radius) || 50);
    const height = Math.max(1, Number(primitive.height) || 100);
    geometry = new THREE.CylinderGeometry(radius, radius, height, 40);
    // three.js cylinders run along Y; the definition names the axis.
    if (primitive.axis === "z") geometry.rotateX(Math.PI / 2);
    else if (primitive.axis === "x") geometry.rotateZ(Math.PI / 2);
  }
  if (!geometry) return null;
  const mesh = new THREE.Mesh(geometry, surface(0x7f8c99, 0.55, false));
  const center = xyz(primitive.center);
  mesh.position.set(center.x, center.y, center.z);
  mesh.userData.primitive = true;
  return mesh;
}

function report(node, model, message) {
  const key = `${node.id}/${model.name}`;
  problems.set(key, `${node.label || node.id}: ${t("mesh not drawn")} ${model.name} — ${message}`);
  renderProblems();
  post({ type: "meshError", node: node.id, mesh: model.name, message });
}

function renderProblems() {
  elements.problems.replaceChildren(...[...problems.values()].map((text) => {
    const line = document.createElement("div");
    line.textContent = text;
    return line;
  }));
}

let generation = 0;

async function build() {
  const current = ++generation;
  for (const group of groups.values()) {
    machineRoot.remove(group);
    group.traverse((child) => {
      if (child.geometry) child.geometry.dispose();
      if (child.material) child.material.dispose();
    });
  }
  groups.clear();
  problems.clear();
  for (const node of definition.nodes || []) {
    const group = new THREE.Group();
    group.matrixAutoUpdate = false;
    group.userData.nodeId = node.id;
    const primitive = primitiveObject(node.primitive);
    if (primitive) group.add(primitive);
    groups.set(node.id, group);
    machineRoot.add(group);
  }
  pose();
  const pending = [];
  for (const node of definition.nodes || []) {
    const group = groups.get(node.id);
    for (const model of node.models || []) {
      if (model.visible === false) continue;
      if (!model.meshUrl) {
        report(node, model, model.problem || "the mesh file is not available");
        continue;
      }
      pending.push(loadMesh(model.meshUrl).then((positions) => {
        if (current !== generation) return;
        const placed = placement.placeOnNode(positions, {
          offsetMm: xyz(model.position), rotationDeg: xyz(model.rotationDeg), scale: Number(model.scale) || 1
        }, 1);
        const geometry = new THREE.BufferGeometry();
        geometry.setAttribute("position", new THREE.BufferAttribute(placed, 3));
        geometry.computeVertexNormals();
        const opacity = Number.isFinite(model.opacity) ? Math.max(0.05, Math.min(1, model.opacity)) : 1;
        const mesh = new THREE.Mesh(geometry, surface(colorOf(model.color, 0x9aa4ae), opacity, model.wireframe));
        mesh.userData.model = true;
        group.add(mesh);
        group.userData.hasMesh = true;
      }).catch((error) => {
        if (current === generation) report(node, model, error && error.message ? error.message : String(error));
      }));
    }
  }
  await Promise.allSettled(pending);
  if (current !== generation) return;
  visibility();
  highlight();
}

function visibility() {
  for (const group of groups.values()) {
    for (const child of group.children) {
      if (child.userData.model) child.visible = elements.showMeshes.checked;
      if (child.userData.primitive) {
        // A component's primitive makes way for its loaded mesh, as in the NC viewer.
        child.visible = elements.showPrimitives.checked && !(group.userData.hasMesh && elements.showMeshes.checked);
      }
    }
  }
}

function highlight() {
  for (const [id, group] of groups) {
    for (const child of group.children) {
      if (!child.material || !child.material.emissive) continue;
      child.material.emissive.setHex(id === selected ? 0x0b6e70 : 0x000000);
    }
  }
}

// ----- pose, mounts and joints -------------------------------------------------------------------

function apply(group, m) {
  group.matrix.set(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
  group.matrixWorldNeedsUpdate = true;
}

function pose() {
  const world = core.worldMatrices(definition.nodes || [], axes);
  for (const [id, group] of groups) if (world[id]) apply(group, world[id]);
  overlays.clear();
  const tool = core.mountPoint(world, definition.mounts && definition.mounts.tool);
  if (tool) {
    const cone = new THREE.Mesh(new THREE.ConeGeometry(18, 60, 24), new THREE.MeshBasicMaterial({ color: 0xf0b429 }));
    cone.rotation.x = -Math.PI / 2; // pointing down the tool axis (−Z)
    cone.position.set(tool[0], tool[1], tool[2] - 30);
    overlays.add(cone);
  }
  const workpiece = core.mountPoint(world, definition.mounts && definition.mounts.workpiece);
  if (workpiece) {
    const triad = new THREE.AxesHelper(160);
    const frame = world[definition.mounts.workpiece.node];
    triad.matrixAutoUpdate = false;
    triad.matrix.set(frame[0], frame[1], frame[2], workpiece[0], frame[4], frame[5], frame[6], workpiece[1], frame[8], frame[9], frame[10], workpiece[2], 0, 0, 0, 1);
    overlays.add(triad);
  }
  const line = selected ? core.jointLine(definition.nodes || [], world, selected) : null;
  if (line) {
    const direction = new THREE.Vector3(...line.direction).normalize();
    const origin = new THREE.Vector3(...line.origin);
    if (line.type === "rotary") {
      const points = [origin.clone().addScaledVector(direction, -600), origin.clone().addScaledVector(direction, 600)];
      overlays.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(points), new THREE.LineDashedMaterial({ color: 0xff7b72, dashSize: 30, gapSize: 15 })).computeLineDistances());
    }
    overlays.add(new THREE.ArrowHelper(direction, origin, 400, line.type === "rotary" ? 0xff7b72 : 0x6adee1, 60, 30));
  }
  const parts = Object.keys(axes).map((name) => `${name} ${Number(axes[name]).toFixed(3).replace(/\.?0+$/, "")}`);
  elements.axes.textContent = parts.length ? parts.join("   ") : t("No axes");
  render();
}

// ----- camera ------------------------------------------------------------------------------------

function placeCamera() {
  const cos = Math.cos(elevation);
  camera.position.set(
    target.x + distance * cos * Math.cos(azimuth),
    target.y + distance * cos * Math.sin(azimuth),
    target.z + distance * Math.sin(elevation));
  camera.lookAt(target);
  camera.near = Math.max(1, distance / 1000);
  camera.far = distance * 100;
  camera.updateProjectionMatrix();
}

function fit() {
  const box = new THREE.Box3().setFromObject(machineRoot);
  if (box.isEmpty()) box.set(new THREE.Vector3(-500, -500, -500), new THREE.Vector3(500, 500, 500));
  box.getCenter(target);
  const size = box.getSize(new THREE.Vector3()).length();
  distance = Math.max(200, size * 1.4);
  placeCamera();
  render();
}

function view(name) {
  const views = { iso: [-Math.PI / 4, Math.PI / 6], front: [-Math.PI / 2, 0], right: [0, 0], top: [-Math.PI / 2, Math.PI / 2 - 1e-3] };
  [azimuth, elevation] = views[name] || views.iso;
  placeCamera();
  render();
}

function resize() {
  const width = canvas.clientWidth || 1;
  const height = canvas.clientHeight || 1;
  renderer.setSize(width, height, false);
  camera.aspect = width / height;
  camera.updateProjectionMatrix();
  render();
}

let frame = 0;
function render() {
  if (frame) return;
  frame = requestAnimationFrame(() => {
    frame = 0;
    renderer.render(scene, camera);
  });
}

let drag = null;
canvas.addEventListener("pointerdown", (event) => {
  drag = { x: event.clientX, y: event.clientY, button: event.button, moved: false };
  canvas.setPointerCapture(event.pointerId);
});
canvas.addEventListener("pointermove", (event) => {
  if (!drag) return;
  const dx = event.clientX - drag.x;
  const dy = event.clientY - drag.y;
  if (Math.abs(dx) + Math.abs(dy) > 2) drag.moved = true;
  drag.x = event.clientX;
  drag.y = event.clientY;
  if (drag.button === 2 || event.shiftKey) {
    const right = new THREE.Vector3().setFromMatrixColumn(camera.matrix, 0);
    const up = new THREE.Vector3().setFromMatrixColumn(camera.matrix, 1);
    const scale = distance / Math.max(1, canvas.clientHeight) * 0.8;
    target.addScaledVector(right, -dx * scale).addScaledVector(up, dy * scale);
  } else {
    azimuth -= dx * 0.008;
    elevation = Math.max(-Math.PI / 2 + 1e-3, Math.min(Math.PI / 2 - 1e-3, elevation + dy * 0.008));
  }
  placeCamera();
  render();
});
canvas.addEventListener("pointerup", (event) => {
  if (drag && !drag.moved && drag.button === 0) pick(event);
  drag = null;
});
canvas.addEventListener("contextmenu", (event) => event.preventDefault());
canvas.addEventListener("wheel", (event) => {
  event.preventDefault();
  distance = Math.max(50, distance * Math.exp(event.deltaY * 0.001));
  placeCamera();
  render();
}, { passive: false });
window.addEventListener("keydown", (event) => {
  if (event.key === "f" || event.key === "F") fit();
});

function pick(event) {
  const rect = canvas.getBoundingClientRect();
  const pointer = new THREE.Vector2(
    ((event.clientX - rect.left) / rect.width) * 2 - 1,
    -((event.clientY - rect.top) / rect.height) * 2 + 1);
  const raycaster = new THREE.Raycaster();
  raycaster.setFromCamera(pointer, camera);
  const hit = raycaster.intersectObjects(machineRoot.children, true).find((value) => value.object.visible);
  let object = hit ? hit.object : null;
  while (object && !object.userData.nodeId) object = object.parent;
  if (object) post({ type: "select", id: object.userData.nodeId });
}

for (const button of document.querySelectorAll("[data-view]")) button.addEventListener("click", () => view(button.dataset.view));
document.getElementById("fit").addEventListener("click", fit);
elements.showMeshes.addEventListener("change", () => { visibility(); render(); });
elements.showPrimitives.addEventListener("change", () => { visibility(); render(); });
window.addEventListener("resize", resize);

// ----- messages from the window ------------------------------------------------------------------

let first = true;
if (webview) {
  webview.addEventListener("message", (event) => {
    const message = event.data || {};
    if (message.type === "scene") {
      definition = message;
      axes = message.axes || {};
      selected = message.selected || null;
      const calibration = message.calibration;
      elements.calibration.hidden = !calibration;
      elements.calibration.textContent = calibration ? t(calibration) : "";
      build().then(() => {
        if (first) {
          first = false;
          fit();
        } else {
          render();
        }
      });
    } else if (message.type === "pose") {
      axes = message.axes || {};
      pose();
    } else if (message.type === "select") {
      selected = message.id || null;
      highlight();
      pose();
    } else if (message.type === "labels") {
      labels = message.items || {};
      translatePage();
      pose();
    } else if (message.type === "theme") {
      applyTheme();
      render();
    }
  });
}

applyTheme();
resize();
placeCamera();
post({ type: "ready" });
