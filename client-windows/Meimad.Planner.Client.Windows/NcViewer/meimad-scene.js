"use strict";

// Meimad Planner: the "Machine & setup" card. It draws
//   - the Machine's STL models (Setup → Machines → NC viewer 3D models) on their kinematic nodes,
//     so they move with the axes in the Machine view;
//   - the Operation's Cimatron fixtures and rest material, placed through the Operation's Work CS
//     into the program's coordinates (meimad-scene-core.js has the math);
//   - which renderer draws the 3D view (GPU or software).
// Files imported here with "Import from Cimatron" are shown in this window only; the Operation's
// Cimatron setup (Case → Operations) keeps them for everyone.
(() => {
  const desktop = window.fanucDesktop;
  const meimad = desktop && desktop.meimad;
  const core = window.MeimadScene;
  const elements = {
    card: document.getElementById("meimadSceneCard"),
    renderer: document.getElementById("meimadSceneRenderer"),
    workCs: document.getElementById("meimadSceneWorkCs"),
    machine: document.getElementById("meimadSceneMachine"),
    fixtures: document.getElementById("meimadSceneFixtures"),
    rest: document.getElementById("meimadSceneRest"),
    items: document.getElementById("meimadSceneItems"),
    importButton: document.getElementById("meimadSceneImport"),
    status: document.getElementById("meimadSceneStatus")
  };
  if (!meimad || !meimad.scene || !core || !elements.card) return;

  const FIXTURE_COLOR = 0x7c8fa6;
  const REST_COLOR = 0xd6a65a;
  const MESH_TAG = "meimadSceneMesh";

  let hooks = null;
  let model = null;
  let scene = { machineModels: {}, workCs: null, items: [], operationTitle: null };
  const meshes = new Map(); // mesh url -> Promise<Float32Array>
  const loaded = new Map(); // mesh url -> { positions, triangles } | { error }
  let fixtureGroup = null;
  let lights = null;

  function status(text, kind) {
    elements.status.textContent = text || "";
    elements.status.dataset.kind = kind || "";
  }

  function message(error) {
    return error && error.message ? error.message : String(error);
  }

  // ----- meshes -------------------------------------------------------------------------------

  function fetchMesh(url) {
    if (!meshes.has(url)) {
      const promise = fetch(url, { cache: "no-store" })
        .then((response) => {
          if (!response.ok) {
            return response.text().then((text) => {
              throw new Error(text || `HTTP ${response.status}`);
            });
          }
          return response.arrayBuffer();
        })
        .then((buffer) => {
          const positions = window.MeimadMaterial.readStl(buffer);
          loaded.set(url, { positions, triangles: Math.floor(positions.length / 9) });
          return positions;
        })
        .catch((error) => {
          loaded.set(url, { error: message(error) });
          throw error;
        });
      meshes.set(url, promise);
    }
    return meshes.get(url);
  }

  function ensureLights() {
    if (!hooks || lights) return;
    const existing = hooks.scene.getObjectByName("meimad-lights");
    if (existing) {
      lights = existing;
      return;
    }
    const THREE = hooks.THREE;
    lights = new THREE.Group();
    lights.name = "meimad-lights";
    const hemisphere = new THREE.HemisphereLight(0xffffff, 0x2a3038, 0.9);
    const key = new THREE.DirectionalLight(0xffffff, 0.75);
    key.position.set(1, -0.6, 1.4);
    lights.add(hemisphere, key);
    hooks.scene.add(lights);
  }

  function meshObject(positions, color, opacity) {
    const THREE = hooks.THREE;
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
    geometry.computeVertexNormals();
    const transparent = opacity < 0.999;
    const material = new THREE.MeshStandardMaterial({
      color,
      roughness: 0.7,
      metalness: 0.2,
      side: THREE.DoubleSide,
      transparent,
      opacity,
      depthWrite: !transparent
    });
    const mesh = new THREE.Mesh(geometry, material);
    mesh.userData[MESH_TAG] = true;
    mesh.renderOrder = transparent ? 2 : 0;
    return mesh;
  }

  function disposeTree(object) {
    object.traverse((child) => {
      if (child.geometry) child.geometry.dispose();
      if (child.material) (Array.isArray(child.material) ? child.material : [child.material]).forEach((value) => value.dispose());
    });
  }

  function scaleOf() {
    const scale = model && model.view && model.view.unitScale;
    return Number.isFinite(scale) && scale > 0 ? scale : 1;
  }

  function colorOf(text, fallback) {
    const match = /^#?([0-9a-f]{6})$/i.exec(String(text || ""));
    return match ? parseInt(match[1], 16) : fallback;
  }

  // ----- machine models -----------------------------------------------------------------------

  function machineModelsForModel() {
    const id = model && model.machineDefinition && model.machineDefinition.id;
    return (id && scene.machineModels && scene.machineModels[id]) || [];
  }

  function nodeGroups() {
    const groups = new Map();
    for (const child of (hooks && hooks.machineRoot && hooks.machineRoot.children) || []) {
      const node = child.userData && child.userData.node;
      if (node && node.id) groups.set(node.id, child);
    }
    return groups;
  }

  async function drawMachineModels() {
    if (!hooks || !hooks.captured || !model) return;
    const models = machineModelsForModel();
    const groups = nodeGroups();
    // The upstream viewer rebuilds the node groups for every model; ours are added again.
    for (const group of groups.values()) {
      for (const child of [...group.children]) {
        if (child.userData[MESH_TAG]) {
          group.remove(child);
          disposeTree(child);
        } else {
          child.visible = true;
        }
      }
    }
    if (!models.length || !elements.machine.checked) {
      hooks.upstream.render();
      return;
    }
    ensureLights();
    const requested = model;
    const missing = [];
    for (const entry of models) {
      const group = groups.get(entry.node);
      if (!group) {
        missing.push(entry.node);
        continue;
      }
      try {
        const positions = await fetchMesh(entry.meshUrl);
        if (model !== requested) return;
        const placed = core.placeOnNode(positions, entry, scaleOf());
        const opacity = Number.isFinite(entry.opacity) ? entry.opacity : 1;
        group.add(meshObject(placed, colorOf(entry.color, 0x9aa4ae), opacity));
        // The node's placeholder box makes way for the real model.
        for (const child of group.children) if (!child.userData[MESH_TAG]) child.visible = false;
      } catch (error) {
        status(`Machine model ${entry.name} could not be loaded: ${message(error)}`, "error");
      }
    }
    if (missing.length) {
      status(`The machine definition has no node ${missing.join(", ")}; check the Machine's NC viewer 3D models in Setup.`, "warning");
    }
    hooks.upstream.render();
  }

  // ----- fixtures and rest material -----------------------------------------------------------

  function removeFixtures() {
    if (fixtureGroup && hooks) {
      hooks.contentRoot.remove(fixtureGroup);
      disposeTree(fixtureGroup);
    }
    fixtureGroup = null;
  }

  async function drawFixtures() {
    if (!hooks || !hooks.captured) return;
    removeFixtures();
    if (!model || model.kind === "lathe") {
      hooks.upstream.render();
      return;
    }
    const wanted = scene.items.filter((item) =>
      (item.kind === "fixture" && elements.fixtures.checked) || (item.kind === "stock" && elements.rest.checked));
    if (!wanted.length) {
      hooks.upstream.render();
      return;
    }
    ensureLights();
    const THREE = hooks.THREE;
    const group = new THREE.Group();
    group.name = "meimad-fixtures";
    fixtureGroup = group;
    hooks.contentRoot.add(group);
    const requested = model;
    const workCs = scene.workCs;
    const shift = core.offsetShift(model, workCs && workCs.workOffset);
    for (const item of wanted) {
      try {
        const positions = await fetchMesh(item.meshUrl);
        if (model !== requested || fixtureGroup !== group) return;
        const placed = core.modelToDrawing(positions, workCs, { scale: scaleOf(), shift });
        group.add(item.kind === "stock"
          ? meshObject(placed, REST_COLOR, 0.35)
          : meshObject(placed, FIXTURE_COLOR, 0.9));
      } catch (error) {
        status(`${item.name} could not be loaded: ${message(error)}`, "error");
      }
      renderItems();
    }
    hooks.upstream.render();
  }

  // ----- card ---------------------------------------------------------------------------------

  function kindLabel(kind) {
    return kind === "stock" ? "Rest material" : "Fixture";
  }

  function renderItems() {
    elements.items.replaceChildren();
    const models = machineModelsForModel();
    for (const entry of models) {
      const item = document.createElement("li");
      const label = document.createElement("span");
      label.textContent = "Machine model";
      const name = document.createElement("strong");
      name.setAttribute("translate", "no");
      name.textContent = ` ${entry.name} → ${entry.node}`;
      item.append(label, name, detailOf(entry.meshUrl));
      elements.items.appendChild(item);
    }
    for (const sceneItem of scene.items) {
      const item = document.createElement("li");
      const label = document.createElement("span");
      label.textContent = kindLabel(sceneItem.kind) + (sceneItem.imported ? " (this window)" : "");
      const name = document.createElement("strong");
      name.setAttribute("translate", "no");
      name.textContent = ` ${sceneItem.name}`;
      item.append(label, name, detailOf(sceneItem.meshUrl));
      if (sceneItem.kind === "stock" && /\.stl$/i.test(sceneItem.path || "") && window.meimadStock && window.meimadStock.useRestMaterial) {
        const use = document.createElement("button");
        use.type = "button";
        use.className = "meimad-pane-button";
        use.textContent = "Use as stock";
        use.title = "Cut the simulation from this rest material";
        use.addEventListener("click", () => window.meimadStock.useRestMaterial({
          path: sceneItem.path,
          name: sceneItem.name,
          workCs: scene.workCs
        }));
        item.appendChild(use);
      }
      elements.items.appendChild(item);
    }
    if (!models.length && !scene.items.length) {
      const item = document.createElement("li");
      item.className = "meimad-note";
      item.textContent = "No machine models, fixtures or rest material for this program.";
      elements.items.appendChild(item);
    }
  }

  function detailOf(url) {
    const detail = document.createElement("span");
    detail.className = "meimad-scene-detail";
    detail.setAttribute("translate", "no");
    const state = loaded.get(url);
    detail.textContent = !state ? " · …" : state.error ? " · ✖" : ` · ${state.triangles.toLocaleString("en-US")} ▲`;
    if (state && state.error) detail.title = state.error;
    return detail;
  }

  function renderWorkCs() {
    const workCs = scene.workCs;
    elements.workCs.textContent = workCs
      ? `Work CS (${workCs.source === "manual" ? "entered" : "Cimatron"}): ${core.describeWorkCs(workCs)}`
      : scene.items.length
        ? "No Work CS: the files are drawn at their own coordinates. Enter the Cimatron Work CS in Case → Operations → Cimatron setup."
        : "";
  }

  function rendererInfo() {
    const renderer = hooks && hooks.renderer;
    if (!renderer || typeof renderer.getContext !== "function") return;
    try {
      const gl = renderer.getContext();
      const info = gl.getExtension("WEBGL_debug_renderer_info");
      const name = info ? gl.getParameter(info.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER);
      const software = /swiftshader|llvmpipe|software|microsoft basic render/i.test(String(name));
      elements.renderer.textContent = software
        ? "3D drawing: software (no GPU acceleration). Update the graphics driver for faster drawing."
        : "3D drawing: GPU accelerated";
      elements.renderer.title = String(name || "");
      elements.renderer.dataset.kind = software ? "warning" : "success";
    } catch {
      elements.renderer.textContent = "";
    }
  }

  async function loadScene() {
    try {
      const result = await meimad.scene.load();
      scene = {
        machineModels: (result && result.machineModels) || {},
        workCs: (result && result.workCs) || null,
        items: (result && result.items) || [],
        operationTitle: result && result.operationTitle
      };
      if (result && result.note) status(result.note, "warning");
    } catch (error) {
      status(`The machine and setup models could not be read: ${message(error)}`, "error");
    }
    renderWorkCs();
    renderItems();
    redraw();
  }

  function redraw() {
    drawMachineModels();
    drawFixtures();
  }

  async function importCimatron() {
    try {
      const result = await meimad.scene.importCimatron();
      if (!result || result.canceled) return;
      const added = (result.items || []).map((item) => Object.assign({}, item, { imported: true }));
      const known = new Set(scene.items.map((item) => item.path));
      scene.items = scene.items.concat(added.filter((item) => !known.has(item.path)));
      status(`${added.length} Cimatron file${added.length === 1 ? "" : "s"} shown in this window. To keep them for the Operation, add them in Case → Operations → Cimatron setup.`, "success");
      renderWorkCs();
      renderItems();
      drawFixtures();
    } catch (error) {
      status(message(error), "error");
    }
  }

  function attach(value) {
    if (!value || hooks === value) return;
    hooks = value;
    if (!hooks.captured) {
      status("The 3D scene could not be captured; machine models and fixtures are unavailable.", "error");
      return;
    }
    hooks.on("modelApplied", (next) => {
      model = next || null;
      renderItems();
      redraw();
      rendererInfo();
    });
    rendererInfo();
  }

  elements.machine.addEventListener("change", drawMachineModels);
  elements.fixtures.addEventListener("change", drawFixtures);
  elements.rest.addEventListener("change", drawFixtures);
  elements.importButton.addEventListener("click", importCimatron);
  if (window.meimadViewer3d) attach(window.meimadViewer3d);
  window.addEventListener("meimad:viewer3d", (event) => attach(event.detail));
  loadScene();
})();
