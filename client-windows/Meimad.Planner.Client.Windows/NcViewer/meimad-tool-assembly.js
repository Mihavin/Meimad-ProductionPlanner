"use strict";

// Meimad Planner: the Tool Room's measured tools in the NC viewer (owner decision 2026-09-29).
// After the Tool Room measures a tool, the viewer shows it exactly as the Tool Room has it:
//  - the tool table dialog gets a "Tool Room" section with the Tool Room's row of every measured
//    tool (identifier, offset, measured length and diameter, shape, holder and components);
//  - the 3D view draws a measured mill tool from the Tool Room's drawing (holder, extension,
//    collet, shank and cutter at their sizes, gauge line at the top) instead of the schematic
//    cutter of the vendored viewer, whose tool group is hidden while ours is shown.
// Tools the Tool Room has not measured keep the viewer's own tool. The vendored files stay
// unmodified: the table is added next to the upstream one and the tool follows the wrapper's
// tool-pose hook (meimad-viewer3d.js).
(() => {
  const meimad = window.fanucDesktop && window.fanucDesktop.meimad;
  if (!meimad || typeof meimad.toolAssemblies !== "function") return;

  let assemblies = new Map(); // T number -> Tool Room assembly
  let source = "";
  let loading = null;

  function load() {
    loading = meimad.toolAssemblies().then((result) => {
      assemblies = new Map((result?.tools || []).map((tool) => [Number(tool.number), tool]));
      source = result?.source || "";
      renderTable();
      redrawTool();
    }).catch((error) => console.error("Meimad: the Tool Room tools could not be read.", error));
    return loading;
  }

  // ----- tool table dialog --------------------------------------------------------------------

  const number = (value) => (value === null || value === undefined || !Number.isFinite(Number(value)) ? "" : String(Math.round(Number(value) * 1000) / 1000));

  function cell(row, text) {
    const td = document.createElement("td");
    td.textContent = text;
    row.appendChild(td);
  }

  function renderTable() {
    const wrap = document.querySelector(".tool-editor-table-wrap");
    if (!wrap) return;
    let section = document.getElementById("meimadToolRoomTools");
    if (assemblies.size === 0) {
      section?.remove();
      return;
    }
    if (!section) {
      section = document.createElement("section");
      section.id = "meimadToolRoomTools";
      section.className = "meimad-tool-room";
      wrap.insertAdjacentElement("afterend", section);
    }
    const programTools = new Set([...document.querySelectorAll("#toolEditorRows tr")]
      .map((row) => Number(row.dataset.toolNumber))
      .filter((value) => Number.isFinite(value) && value > 0));
    section.replaceChildren();
    const heading = document.createElement("h3");
    heading.textContent = "Tool Room: measured tools";
    const note = document.createElement("p");
    note.className = "meimad-tool-room-note";
    note.textContent = "Shown exactly as measured in the Tool Room, with holder and components. The 3D view draws these tools as assembled.";
    const origin = document.createElement("p");
    origin.className = "meimad-tool-room-note";
    origin.setAttribute("translate", "no");
    origin.textContent = source;
    const table = document.createElement("table");
    table.className = "tool-editor-table meimad-tool-room-table";
    const head = document.createElement("thead");
    const headRow = document.createElement("tr");
    for (const label of ["Tool", "Identifier", "Offset", "Measured length", "Measured diameter", "Type", "Holder and components", "Notes"]) {
      const th = document.createElement("th");
      th.textContent = label;
      headRow.appendChild(th);
    }
    head.appendChild(headRow);
    const body = document.createElement("tbody");
    for (const tool of [...assemblies.values()].sort((left, right) => left.number - right.number)) {
      const row = document.createElement("tr");
      if (programTools.size > 0 && !programTools.has(tool.number)) row.className = "meimad-tool-room-unused";
      cell(row, `T${tool.number}`);
      cell(row, tool.identifier + (tool.description ? ` - ${tool.description}` : ""));
      cell(row, tool.offsetNumber ?? "");
      cell(row, number(tool.measuredLength));
      cell(row, number(tool.measuredDiameter));
      cell(row, String(tool.shapeType || "").replace(/_/g, " ").toLowerCase());
      cell(row, tool.componentsText || "");
      cell(row, tool.notes || "");
      body.appendChild(row);
    }
    table.append(head, body);
    section.append(heading, note, origin, table);
  }

  // The dialog's rows are rebuilt every time it opens: the Tool Room section follows them.
  const rows = document.getElementById("toolEditorRows");
  if (rows) new MutationObserver(() => renderTable()).observe(rows, { childList: true });

  // ----- 3D tool --------------------------------------------------------------------------------

  let hooks = window.meimadViewer3d;
  let model = null;
  let lastPlayback = null;
  let lastPose = null;
  let drawn = null;

  const CUTTER_KINDS = new Set(["CUTTER", "BALL", "POINT", "CONE", "DISC", "INSERT", "BLADE", "DOVETAIL", "SPHERE"]);

  function near(a, b) {
    return a && b && Math.abs(a.x - b.x) < 1e-6 && Math.abs((a.y || 0) - (b.y || 0)) < 1e-6 && Math.abs(a.z - b.z) < 1e-6;
  }

  // The T number of the tool at the pose: the playing segment, else the segment ending at the tip.
  function activeTool(pose) {
    const segments = model?.segments || [];
    const playback = typeof window.cncPreviewState === "function" ? window.cncPreviewState().playback : "idle";
    if (lastPlayback && playback !== "idle" && playback !== "complete") {
      const index = Math.max(-1, Number(lastPlayback.completedIndex)) + 1;
      if (segments[index]) return Number(segments[index].tool);
    }
    for (let index = segments.length - 1; index >= 0; index -= 1) {
      if (near(segments[index].end, pose.tip)) return Number(segments[index].tool);
    }
    return null;
  }

  function isUpstreamTool(object) {
    if (!object.isGroup || object.userData.meimadToolAssembly) return false;
    let found = false;
    object.traverse((child) => {
      if (child.isMesh && child.material?.color?.getHex?.() === 0xffa657) found = true;
    });
    return found;
  }

  function removeDrawn() {
    if (!drawn) return;
    drawn.parent?.remove(drawn);
    drawn.traverse((child) => {
      child.geometry?.dispose?.();
      child.material?.dispose?.();
    });
    drawn = null;
  }

  function scaleOf() {
    const units = String(model?.units || model?.segments?.[0]?.units || "").toLowerCase();
    return units.includes("in") || units === "g20" || units === "20" ? 1 / 25.4 : 1;
  }

  function buildAssembly(THREE, tool, scale) {
    const group = new THREE.Group();
    group.userData.meimadToolAssembly = true;
    const cutter = new THREE.MeshBasicMaterial({ color: 0xffa657, transparent: true, opacity: 0.6, depthWrite: false });
    const holder = new THREE.MeshBasicMaterial({ color: 0x8b949e, transparent: true, opacity: 0.45, depthWrite: false });
    const unknown = new THREE.MeshBasicMaterial({ color: 0x8b949e, transparent: true, opacity: 0.18, depthWrite: false });
    const total = Number(tool.totalLength) || 0;
    for (const segment of tool.segments || []) {
      const height = Math.max(0, Number(segment.height) || 0) * scale;
      const radius = Math.max(0, Number(segment.diameter) || 0) / 2 * scale;
      if (height <= 0) continue;
      // Local +Y runs from the tip (0) up to the gauge line, as in the vendored viewer.
      const bottom = (total - (Number(segment.top) || 0) - (Number(segment.height) || 0)) * scale;
      const material = segment.kind === "GAP" ? unknown : segment.isDefault ? unknown : CUTTER_KINDS.has(segment.kind) ? cutter : holder;
      let mesh;
      switch (segment.kind) {
        case "GAP":
          mesh = new THREE.Mesh(new THREE.CylinderGeometry(0.3 * scale, 0.3 * scale, height, 8), material);
          mesh.position.y = bottom + height / 2;
          break;
        case "BALL":
          mesh = new THREE.Mesh(new THREE.SphereGeometry(radius, 24, 12, 0, Math.PI * 2, Math.PI / 2, Math.PI / 2), material);
          mesh.position.y = bottom + radius;
          break;
        case "SPHERE":
          mesh = new THREE.Mesh(new THREE.SphereGeometry(radius, 24, 16), material);
          mesh.position.y = bottom + height / 2;
          break;
        case "POINT":
        case "CONE":
          mesh = new THREE.Mesh(new THREE.ConeGeometry(radius, height, 32), material);
          mesh.rotation.x = Math.PI;
          mesh.position.y = bottom + height / 2;
          break;
        case "TAPER": {
          // The adaptor taper above the gauge line: small end at the top (toward the pull stud).
          const top = Math.max(0, Number(segment.topDiameter) || 0) / 2 * scale;
          mesh = new THREE.Mesh(new THREE.CylinderGeometry(top, radius, height, 32), holder);
          mesh.position.y = bottom + height / 2;
          break;
        }
        case "DOVETAIL":
          mesh = new THREE.Mesh(new THREE.CylinderGeometry(radius * 0.5, radius, height, 32), material);
          mesh.position.y = bottom + height / 2;
          break;
        case "INSERT":
        case "BLADE":
          mesh = new THREE.Mesh(new THREE.BoxGeometry(radius * 2, height, Math.max(radius * 0.4, 1 * scale)), material);
          mesh.position.y = bottom + height / 2;
          break;
        default: // HOLDER, CYLINDER, CUTTER, DISC
          mesh = new THREE.Mesh(new THREE.CylinderGeometry(radius, radius, height, 32), material);
          mesh.position.y = bottom + height / 2;
          break;
      }
      group.add(mesh);
    }
    const tip = new THREE.Points(
      new THREE.BufferGeometry().setAttribute("position", new THREE.Float32BufferAttribute([0, 0, 0], 3)),
      new THREE.PointsMaterial({ color: 0xffd60a, size: 6, sizeAttenuation: false, depthTest: false }));
    tip.renderOrder = 9;
    group.add(tip);
    return group;
  }

  function redrawTool() {
    removeDrawn();
    if (!hooks?.captured || !lastPose || !model || model.kind === "lathe" || assemblies.size === 0) return;
    const upstreamTool = hooks.contentRoot.children.find(isUpstreamTool);
    if (!upstreamTool) return; // the tool is not shown
    const tool = assemblies.get(activeTool(lastPose));
    upstreamTool.visible = !tool;
    if (!tool) return;
    drawn = buildAssembly(hooks.THREE, tool, scaleOf());
    drawn.position.copy(upstreamTool.position);
    drawn.quaternion.copy(upstreamTool.quaternion);
    hooks.contentRoot.add(drawn);
    hooks.upstream.render();
  }

  function attach(value) {
    hooks = value;
    if (!hooks?.captured) return;
    hooks.on("model", (value) => { model = value; });
    hooks.on("playback", (value) => { lastPlayback = value; });
    hooks.on("toolPose", (pose) => {
      lastPose = pose || null;
      redrawTool();
    });
  }

  if (hooks) attach(hooks);
  window.addEventListener("meimad:viewer3d", (event) => attach(event.detail));
  if (window.fanucDesktop.onRender) window.fanucDesktop.onRender(() => { if (!loading) load(); });
  load();
})();
