"use strict";

// Meimad Planner: the 3D stock simulation worker (milling). It owns the voxel simulator
// (meimad-voxel-sim.js), cuts the program's motions in execution order up to the position the page
// asks for, and streams the changed parts of the stock surface, the collision events and the totals.
// Going backwards restarts from the nearest checkpoint. The WebGPU backend (meimad-voxel-gpu.js)
// takes over the voxel classification when the GPU is available and has passed its self-check;
// the CPU engine is the reference and the fallback.
//   page -> worker  { type: "init", stock, resolution, tools, assemblies, motions, fixtures, target,
//                     policy, collisions, extent, filterTool, deviation, backend: "auto" | "cpu" }
//                   { type: "cutTo", index, fraction }
//                   { type: "filter", tool }
//                   { type: "view", deviation }          heat map of the finished-part comparison
//                   { type: "deviation", limitMm }       the comparison summary
//                   { type: "journal", from?, to? }
//                   { type: "export", header }
//   worker -> page  { type: "ready", resolution, blocks, backend, notes }
//                   { type: "backend", backend, note }   the GPU stopped; the CPU carries on
//                   { type: "progress", done, total, busy }
//                   { type: "chunks", meshes: [{ key, positions, normals, colors, index }], empty: [key], reset? }
//                   { type: "events", events: [...], total }
//                   { type: "stats", stats }
//                   { type: "deviation", result } / { type: "journal", rows } / { type: "exported", stl }
//                   { type: "error", message }
importScripts("./meimad-material.js", "./meimad-voxel-core.js", "./meimad-voxel-mesh.js", "./meimad-voxel-sim.js", "./meimad-voxel-gpu.js");

const V = self.MeimadVoxel;
const MESH = self.MeimadVoxelMesh;
const SIM = self.MeimadVoxelSim;

let options = null;
let sim = null;
let goal = { index: -1, fraction: 1 };
let scheduled = false;
let pending = new Set();     // render chunks to remesh
let sentEvents = 0;
let lastMeshAt = 0;
let view = { deviation: false };
let filterTool = "all";
let generation = 0;          // a rebuild abandons work still in flight
let running = false;
let gpu = null;              // the WebGPU backend of the current simulator
let gpuNote = "";            // why the CPU is used (no WebGPU, self-check failed, a GPU error)
let gpuCheck = null;         // the self-check, run once per worker
let backendChoice = "auto";

function motionsFor(filter) {
  if (filter === "all") return options.motions;
  // Single-tool playback simulates only that tool's moves.
  return options.motions.map((motion) => (Number(motion.tool) === Number(filter) ? motion : { ...motion, cutting: false, rapid: false }));
}

function build() {
  generation += 1;
  if (gpu) {
    try { gpu.destroy(); } catch { /* already lost */ }
    gpu = null;
  }
  sim = SIM.create({
    stock: options.stock,
    resolution: options.resolution,
    tools: options.tools,
    assemblies: options.assemblies,
    motions: motionsFor(filterTool),
    fixtures: options.fixtures,
    target: options.target,
    policy: options.policy,
    collisions: options.collisions,
    extent: options.extent,
    limitBlocks: options.limitBlocks
  });
  sentEvents = 0;
  pending = new Set(MESH.allChunks(sim.grid));
  self.postMessage({ type: "chunks", meshes: [], empty: [], reset: true });
}

function meshView() {
  return view.deviation && sim.target ? { deviation: true, target: sim.target } : null;
}

// Remeshes pending chunks for up to `budgetMs` and posts them.
function postChunks(force, budgetMs) {
  const now = Date.now();
  if (!force && now - lastMeshAt < 120) return;
  lastMeshAt = now;
  for (const key of MESH.chunksOfBlocks(sim.grid, V.takeDirty(sim.grid))) pending.add(key);
  if (!pending.size) return;
  const meshes = [];
  const empty = [];
  const transfer = [];
  const started = Date.now();
  for (const key of pending) {
    pending.delete(key);
    const mesh = MESH.meshChunk(sim.grid, key, meshView());
    if (mesh) {
      meshes.push(mesh);
      transfer.push(mesh.positions.buffer, mesh.normals.buffer, mesh.colors.buffer, mesh.index.buffer);
    } else {
      empty.push(key);
    }
    if (Date.now() - started > (budgetMs || 60)) break;
  }
  self.postMessage({ type: "chunks", meshes, empty }, transfer);
}

function postEvents() {
  if (sim.events.length === sentEvents) return;
  // Events may have been dropped by a rewind: always send the full list when it shrank.
  const all = sim.events.length < sentEvents;
  const list = all ? sim.events : sim.events.slice(sentEvents);
  self.postMessage({ type: "events", events: list.map((event) => ({ ...event })), total: sim.events.length, replace: all || sentEvents === 0 });
  sentEvents = sim.events.length;
}

function postStats(busy) {
  self.postMessage({ type: "progress", done: sim.cursor + 1, total: options.motions.length, busy });
  self.postMessage({ type: "stats", stats: sim.stats() });
}

// The GPU backend for the current simulator when WebGPU is there and agrees with the CPU engine.
async function prepareGpu() {
  if (backendChoice === "cpu") {
    gpuNote = "CPU (chosen)";
    return null;
  }
  if (!gpuCheck) gpuCheck = self.MeimadVoxelGpu.selfCheck({}).catch((error) => ({ ok: false, error: error.message }));
  const check = await gpuCheck;
  if (!check.ok) {
    gpuNote = `CPU (${check.error || `the GPU differed from the CPU engine by ${(check.mismatch * 100).toFixed(2)}%`})`;
    return null;
  }
  const made = await self.MeimadVoxelGpu.create(sim.grid, sim.target, {});
  if (made.error) {
    gpuNote = `CPU (${made.error})`;
    return null;
  }
  gpuNote = "";
  return made.backend;
}

async function work() {
  scheduled = false;
  if (!sim || running) return;
  running = true;
  const mine = generation;
  let result;
  try {
    if (gpu) {
      try {
        result = await sim.cutToGpu(goal.index, goal.fraction, 80, gpu);
      } catch (error) {
        if (mine !== generation) return;
        gpuNote = `CPU (the GPU stopped: ${(error && error.message) || error})`;
        try { gpu.destroy(); } catch { /* lost */ }
        gpu = null;
        self.postMessage({ type: "backend", backend: "cpu", note: gpuNote });
        result = { done: false };
      }
    } else {
      result = sim.cutTo(goal.index, goal.fraction, 40);
    }
  } finally {
    running = false;
  }
  if (mine !== generation) return;
  postEvents();
  if (!result.done) {
    postStats(true);
    postChunks(false, 40);
    scheduled = true;
    setTimeout(work, 0);
    return;
  }
  postStats(false);
  postChunks(true, 80);
  if (pending.size || sim.cursor < goal.index - (goal.fraction < 1 ? 1 : 0)) {
    // Finish the surface (or a goal that moved meanwhile) in further slices.
    scheduled = true;
    setTimeout(work, 0);
  }
}

function kick() {
  if (!scheduled && !running) work();
}

self.onmessage = (event) => {
  const message = event.data || {};
  try {
    if (message.type === "init") {
      options = {
        stock: message.stock,
        resolution: message.resolution,
        tools: message.tools || {},
        assemblies: message.assemblies || {},
        motions: message.motions || [],
        fixtures: message.fixtures || [],
        target: message.target || null,
        policy: message.policy || null,
        collisions: message.collisions !== false,
        extent: message.extent || null,
        limitBlocks: message.limitBlocks
      };
      filterTool = message.filterTool === undefined || message.filterTool === null ? "all" : message.filterTool;
      view = { deviation: Boolean(message.deviation) };
      backendChoice = message.backend === "cpu" ? "cpu" : "auto";
      build();
      goal = { index: -1, fraction: 1 };
      const mine = generation;
      prepareGpu().then((backend) => {
        if (mine !== generation) {
          backend?.destroy();
          return;
        }
        gpu = backend;
        self.postMessage({
          type: "ready",
          resolution: sim.grid.cell,
          blocks: sim.grid.state.length,
          backend: gpu ? `GPU (${gpu.info})` : gpuNote || "CPU",
          notes: sim.toolNotes()
        });
        postStats(false);
        postChunks(true, 200);
        kick();
      });
      return;
    }
    if (!sim) return;
    if (message.type === "cutTo") {
      goal = { index: Number(message.index), fraction: Math.max(0, Math.min(1, Number(message.fraction) || 0)) };
      kick();
      return;
    }
    if (message.type === "filter") {
      filterTool = message.tool === undefined || message.tool === null ? "all" : message.tool;
      build();
      const mine = generation;
      prepareGpu().then((backend) => {
        if (mine !== generation) { backend?.destroy(); return; }
        gpu = backend;
        kick();
      });
      return;
    }
    if (message.type === "view") {
      view = { deviation: Boolean(message.deviation) };
      pending = new Set(MESH.allChunks(sim.grid));
      kick();
      return;
    }
    if (message.type === "deviation") {
      self.postMessage({ type: "deviation", result: sim.deviation(message.limitMm) });
      return;
    }
    if (message.type === "journal") {
      self.postMessage({ type: "journal", rows: sim.journal(message.from, message.to) });
      return;
    }
    if (message.type === "export") {
      const stl = self.MeimadMaterial.writeStl(sim.exportTriangles(), message.header);
      self.postMessage({ type: "exported", stl }, [stl]);
    }
  } catch (error) {
    self.postMessage({ type: "error", message: (error && error.message) || String(error) });
  }
};
