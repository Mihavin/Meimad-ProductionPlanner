"use strict";

// Meimad Planner material removal worker. It owns the stock grid and cuts the program's motions
// in execution order up to the position the page asks for, posting progress and the current
// surface. Going backwards resets the stock and cuts again from the first motion.
//   page -> worker  { type: "init", kind, stock, resolution, tools, segments, stl? }
//                   { type: "cutTo", index, fraction }   cut motions 0..index (index partial by fraction)
//                   { type: "reset" }
//                   { type: "export" }
//   worker -> page  { type: "ready", cells, resolution }
//                   { type: "progress", done, total, busy }
//                   { type: "surface", positions (transferred), kind }
//                   { type: "exported", stl (transferred) }
//                   { type: "error", message }
importScripts("./meimad-material.js");

const Material = self.MeimadMaterial;
let state = null;
let cursor = 0;            // motions fully cut
let partialIndex = -1;     // motion cut partially
let partialFraction = 0;
let target = { index: -1, fraction: 1 };
let scheduled = false;
let lastSurfaceAt = 0;

function toolFor(segment) {
  const definition = state.tools[segment.tool] || {};
  if (state.kind === "lathe") {
    return {
      noseRadius: Number.isFinite(segment.toolRadius) ? segment.toolRadius : Number(definition.cornerRadius) || 0,
      tip: Number.isFinite(segment.toolTip) ? segment.toolTip : Number(definition.tip) || 0,
      type: segment.toolType || definition.type || "other",
      width: Number(definition.width) || 0,
      radius: Number.isFinite(definition.diameter) ? definition.diameter / 2 : 0
    };
  }
  const radius = Number.isFinite(segment.toolRadius) && segment.toolRadius > 0
    ? segment.toolRadius
    : Number.isFinite(definition.diameter) ? definition.diameter / 2 : 0;
  return {
    radius,
    type: segment.toolType || definition.type || "end-mill",
    cornerRadius: Number(segment.toolCornerRadius) || Number(definition.cornerRadius) || 0,
    pointAngle: definition.pointAngle,
    taperAngle: definition.taperAngle
  };
}

let filterTool = "all";

// Single-tool playback simulates only that tool's moves.
function cuts(segment) {
  return segment.cutting && Array.isArray(segment.points) && segment.points.length > 0
    && (filterTool === "all" || Number(segment.tool) === Number(filterTool));
}

function partialPoints(points, fraction) {
  if (fraction >= 1 || points.length < 2) return points;
  let total = 0;
  const lengths = [];
  for (let index = 1; index < points.length; index += 1) {
    const a = points[index - 1];
    const b = points[index];
    const length = Math.hypot((b.x ?? 0) - (a.x ?? 0), (b.y ?? 0) - (a.y ?? 0), (b.z ?? 0) - (a.z ?? 0));
    lengths.push(length);
    total += length;
  }
  const targetLength = total * Math.max(0, fraction);
  const result = [points[0]];
  let travelled = 0;
  for (let index = 1; index < points.length; index += 1) {
    const length = lengths[index - 1];
    if (travelled + length <= targetLength) {
      result.push(points[index]);
      travelled += length;
      continue;
    }
    const t = length > 0 ? (targetLength - travelled) / length : 0;
    const a = points[index - 1];
    const b = points[index];
    const point = { x: a.x + (b.x - a.x) * t, z: a.z + (b.z - a.z) * t };
    if (Number.isFinite(a.y) || Number.isFinite(b.y)) point.y = (a.y ?? 0) + ((b.y ?? 0) - (a.y ?? 0)) * t;
    if (Number.isFinite(a.r) || Number.isFinite(b.r)) point.r = (a.r ?? a.x / 2) + ((b.r ?? b.x / 2) - (a.r ?? a.x / 2)) * t;
    result.push(point);
    break;
  }
  return result;
}

function cutOne(segment, fraction) {
  if (!cuts(segment)) return;
  const tool = toolFor(segment);
  const points = partialPoints(segment.points, fraction);
  if (state.kind === "lathe") Material.cutLatheSegment(state.stock, points, tool);
  else Material.cutMillSegment(state.stock, points, tool, segment.axis);
}

function postSurface(force) {
  const now = Date.now();
  if (!force && now - lastSurfaceAt < 90) return;
  lastSurfaceAt = now;
  const positions = Material.triangles(state.stock, 64);
  self.postMessage({ type: "surface", positions, kind: state.kind, throughCut: state.stock.throughCut || 0 }, [positions.buffer]);
}

function postProgress(busy) {
  self.postMessage({ type: "progress", done: cursor, total: state.segments.length, busy });
}

function rebuild() {
  state.stock = state.kind === "lathe"
    ? Material.createLatheStock(state.definition, state.resolution)
    : state.stl
      ? Material.millStockFromStl(state.stl, state.resolution, state.definition.offset)
      : Material.createMillStock(state.definition, state.resolution);
  cursor = 0;
  partialIndex = -1;
  partialFraction = 0;
}

// Cuts in slices so progress and surfaces keep flowing.
function work() {
  scheduled = false;
  if (!state) return;
  const segments = state.segments;
  const wantIndex = Math.min(target.index, segments.length - 1);
  const wantFraction = target.fraction;
  if (wantIndex < cursor - 1 || (wantIndex === partialIndex && wantFraction < partialFraction - 1e-6) || (wantIndex < partialIndex)) {
    rebuild();
  }
  const started = Date.now();
  while (cursor <= wantIndex) {
    const fraction = cursor === wantIndex ? wantFraction : 1;
    if (fraction >= 1) {
      cutOne(segments[cursor], 1);
      cursor += 1;
      partialIndex = -1;
      partialFraction = 0;
    } else {
      // A partially executed motion: the grid keeps its earlier partial cut, only the new part is added.
      if (partialIndex !== cursor) partialFraction = 0;
      cutOne(segments[cursor], fraction);
      partialIndex = cursor;
      partialFraction = fraction;
      break;
    }
    if (Date.now() - started > 40) {
      postProgress(true);
      postSurface(false);
      scheduled = true;
      setTimeout(work, 0);
      return;
    }
  }
  postProgress(false);
  postSurface(true);
}

self.onmessage = (event) => {
  const message = event.data || {};
  try {
    if (message.type === "init") {
      state = {
        kind: message.kind === "lathe" ? "lathe" : "mill",
        definition: message.stock,
        resolution: message.resolution,
        tools: message.tools || {},
        segments: message.segments || [],
        stl: message.stl instanceof Float32Array ? message.stl : message.stl ? new Float32Array(message.stl) : undefined
      };
      filterTool = message.filterTool === undefined || message.filterTool === null ? "all" : message.filterTool;
      rebuild();
      target = { index: -1, fraction: 1 };
      self.postMessage({
        type: "ready",
        cells: state.kind === "lathe" ? state.stock.nz * state.stock.nr : state.stock.nx * state.stock.ny,
        resolution: state.stock.cell
      });
      postSurface(true);
      return;
    }
    if (!state) return;
    if (message.type === "reset") {
      rebuild();
      target = { index: -1, fraction: 1 };
      postProgress(false);
      postSurface(true);
      return;
    }
    if (message.type === "filter") {
      filterTool = message.tool === undefined || message.tool === null ? "all" : message.tool;
      rebuild();
      if (!scheduled) work();
      return;
    }
    if (message.type === "cutTo") {
      target = { index: Number(message.index), fraction: Math.max(0, Math.min(1, Number(message.fraction) || 0)) };
      if (!scheduled) work();
      return;
    }
    if (message.type === "export") {
      const stl = Material.writeStl(Material.triangles(state.stock, 96), message.header);
      self.postMessage({ type: "exported", stl }, [stl]);
    }
  } catch (error) {
    self.postMessage({ type: "error", message: (error && error.message) || String(error) });
  }
};
