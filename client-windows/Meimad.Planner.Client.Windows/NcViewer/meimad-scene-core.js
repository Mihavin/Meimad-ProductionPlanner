/*
 * Meimad Planner: geometry placement for the NC viewer's setup scene (pure functions, no DOM or
 * three.js, so the client tests run them in V8).
 *
 * Work CS (the Operation's Cimatron setup, Server schema v113): origin (mm) and rotations about X,
 * then Y, then Z (degrees, fixed axes), all in the part-model coordinates that Cimatron exported
 * the fixture and rest-material files in. R = Rz·Ry·Rx; its columns are the Work CS axes in model
 * coordinates. A model point p is p' = Rᵀ (p − origin) in Work CS (program) coordinates.
 *
 * Machine model (Server Machine ncViewerModels): an STL in the frame of one kinematic node, placed
 * by the same rotation convention followed by offsetMm: p' = R p + offset.
 *
 * Positions are flat arrays of x, y, z triples (9 numbers per triangle), as meimad-material.js
 * readStl returns them. Every function returns a new Float32Array.
 */
(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.MeimadScene = factory();
  }
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  const DEGREE = Math.PI / 180;

  function number(value) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : 0;
  }

  function vector(value) {
    const source = value || {};
    return { x: number(source.x), y: number(source.y), z: number(source.z) };
  }

  /** R = Rz(z)·Ry(y)·Rx(x) as a row-major 3x3 array. */
  function rotationMatrix(rotationDeg) {
    const angles = vector(rotationDeg);
    const [cx, sx] = [Math.cos(angles.x * DEGREE), Math.sin(angles.x * DEGREE)];
    const [cy, sy] = [Math.cos(angles.y * DEGREE), Math.sin(angles.y * DEGREE)];
    const [cz, sz] = [Math.cos(angles.z * DEGREE), Math.sin(angles.z * DEGREE)];
    return [
      cz * cy, cz * sy * sx - sz * cx, cz * sy * cx + sz * sx,
      sz * cy, sz * sy * sx + cz * cx, sz * sy * cx - cz * sx,
      -sy, cy * sx, cy * cx
    ];
  }

  function isIdentityPlacement(rotation, translation, scale) {
    const identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];
    return rotation.every((value, index) => Math.abs(value - identity[index]) < 1e-12)
      && Math.abs(translation.x) < 1e-12 && Math.abs(translation.y) < 1e-12 && Math.abs(translation.z) < 1e-12
      && Math.abs(scale - 1) < 1e-12;
  }

  /** out = (M · (p + before)) · scale + after, for every point. */
  function transformPositions(positions, matrix, before, scale, after) {
    const out = new Float32Array(positions.length);
    for (let index = 0; index + 2 < positions.length; index += 3) {
      const x = positions[index] + before.x;
      const y = positions[index + 1] + before.y;
      const z = positions[index + 2] + before.z;
      out[index] = (matrix[0] * x + matrix[1] * y + matrix[2] * z) * scale + after.x;
      out[index + 1] = (matrix[3] * x + matrix[4] * y + matrix[5] * z) * scale + after.y;
      out[index + 2] = (matrix[6] * x + matrix[7] * y + matrix[8] * z) * scale + after.z;
    }
    return out;
  }

  function transpose(matrix) {
    return [matrix[0], matrix[3], matrix[6], matrix[1], matrix[4], matrix[7], matrix[2], matrix[5], matrix[8]];
  }

  /**
   * Model (Cimatron export) coordinates to drawing coordinates: into the Work CS, scaled to the
   * program's units, then shifted from the Work CS's work offset to the drawing frame.
   */
  function modelToDrawing(positions, workCs, options) {
    const settings = options || {};
    const scale = Number.isFinite(settings.scale) && settings.scale > 0 ? settings.scale : 1;
    const shift = vector(settings.shift);
    const origin = workCs ? vector(workCs.originMm) : { x: 0, y: 0, z: 0 };
    const inverse = transpose(rotationMatrix(workCs ? workCs.rotationDeg : null));
    const before = { x: -origin.x, y: -origin.y, z: -origin.z };
    if (isIdentityPlacement(inverse, before, scale) && Math.abs(shift.x) + Math.abs(shift.y) + Math.abs(shift.z) < 1e-12) {
      return Float32Array.from(positions);
    }
    return transformPositions(positions, inverse, before, scale, shift);
  }

  /**
   * A machine model's STL into its kinematic node's frame (program units): scaled by the mesh's own
   * scale (Machine Designer alignment, e.g. 25.4 for a model drawn in inches), rotated, then moved.
   */
  function placeOnNode(positions, model, scale) {
    const factor = Number.isFinite(scale) && scale > 0 ? scale : 1;
    const meshScale = model && Number.isFinite(model.scale) && model.scale > 0 ? model.scale : 1;
    const offset = vector(model && model.offsetMm);
    return transformPositions(
      positions,
      rotationMatrix(model && model.rotationDeg),
      { x: 0, y: 0, z: 0 },
      factor * meshScale,
      { x: offset.x * factor, y: offset.y * factor, z: offset.z * factor });
  }

  /** The Work CS point that a model point maps to (for tests and the readout). */
  function modelPointToWork(point, workCs) {
    const result = modelToDrawing([point.x, point.y, point.z], workCs);
    return { x: result[0], y: result[1], z: result[2] };
  }

  /**
   * The shift from a work offset to the drawing frame (the program's first work offset), from the
   * model's work offset table, as the stock does it. Lathe models and unknown offsets give zero.
   */
  function offsetShift(model, label) {
    if (!model || model.kind === "lathe" || !label) return { x: 0, y: 0, z: 0 };
    const table = model.workOffsetTable;
    const frameMatch = String((model.frame && model.frame.label) || "").match(/G5\d(?:\.\d+ ?P\d+)?/);
    const frameLabel = frameMatch ? frameMatch[0] : "";
    const target = String(label).trim().toUpperCase();
    if (!table || !frameLabel || target === frameLabel) return { x: 0, y: 0, z: 0 };
    const value = (name, axis) => {
      const row = (table.rows || []).find((candidate) => candidate.label === name);
      const entry = row && row.values ? row.values[axis] : undefined;
      const result = entry && Number.isFinite(entry.saved) ? entry.saved
        : entry && Number.isFinite(entry.initial) ? entry.initial
          : table.defaults ? table.defaults[axis] : undefined;
      return Number.isFinite(result) ? result : 0;
    };
    if (!(table.rows || []).some((row) => row.label === target)) return { x: 0, y: 0, z: 0 };
    return {
      x: value(target, "X") - value(frameLabel, "X"),
      y: value(target, "Y") - value(frameLabel, "Y"),
      z: value(target, "Z") - value(frameLabel, "Z")
    };
  }

  /**
   * Work coordinates (relative to the program's first work offset, the model frame) to the drawing frame the engine
   * draws the toolpath in. The engine stores each toolpath point as the inverse of (workpiece mount at zero rotary
   * axes · frame offset) applied to the machine position, so on a Machine Designer definition whose table mount is not
   * at the machine origin the toolpath is not drawn in plain work coordinates; geometry measured from the work offset
   * (Cimatron stock, fixtures, part, the simulation stock) must go through the same conversion to sit on it.
   * `partMatrix` is that 4x4 row-major matrix at zero joints (meimad-axes-core.js partMatrix), `frame` the model frame.
   * Returns { matrix: 3x3 row-major, offset } with q = matrix · d + offset, or null when it is the identity or unknown.
   */
  function workToDrawing(partMatrix, frame) {
    if (!Array.isArray(partMatrix) || partMatrix.length < 12 || !frame) return null;
    const m = partMatrix.map(number);
    const f = vector(frame);
    const matrix = [m[0], m[4], m[8], m[1], m[5], m[9], m[2], m[6], m[10]];
    const v = { x: f.x - m[3], y: f.y - m[7], z: f.z - m[11] };
    const offset = {
      x: matrix[0] * v.x + matrix[1] * v.y + matrix[2] * v.z,
      y: matrix[3] * v.x + matrix[4] * v.y + matrix[5] * v.z,
      z: matrix[6] * v.x + matrix[7] * v.y + matrix[8] * v.z
    };
    return isIdentityPlacement(matrix, offset, 1) ? null : { matrix, offset };
  }

  /** Positions in work coordinates placed into the drawing frame (workToDrawing); a copy when there is no placement. */
  function placeInDrawing(positions, placement) {
    if (!placement) return Float32Array.from(positions);
    return transformPositions(positions, placement.matrix, { x: 0, y: 0, z: 0 }, 1, placement.offset);
  }

  /**
   * Whether an imported Cimatron file is rest material ("stock") or a fixture: Cimatron writes
   * stock exports as STOCK_OP1.stl or into an NC\STOCK folder; everything else is a fixture.
   */
  function guessKind(path) {
    const parts = String(path || "").split(/[\\/]+/).filter(Boolean);
    const name = (parts[parts.length - 1] || "").toLowerCase();
    const folder = (parts[parts.length - 2] || "").toLowerCase();
    if (folder === "stock" || /stock|rest|remain/.test(name)) return "stock";
    return "fixture";
  }

  /** Axis-aligned bounds of positions: { min: {x,y,z}, max: {x,y,z} } or undefined when empty. */
  function bounds(positions) {
    if (!positions || positions.length < 3) return undefined;
    const min = { x: Infinity, y: Infinity, z: Infinity };
    const max = { x: -Infinity, y: -Infinity, z: -Infinity };
    for (let index = 0; index + 2 < positions.length; index += 3) {
      const x = positions[index];
      const y = positions[index + 1];
      const z = positions[index + 2];
      if (x < min.x) min.x = x;
      if (y < min.y) min.y = y;
      if (z < min.z) min.z = z;
      if (x > max.x) max.x = x;
      if (y > max.y) max.y = y;
      if (z > max.z) max.z = z;
    }
    return { min, max };
  }

  function describeWorkCs(workCs) {
    if (!workCs) return "";
    const origin = vector(workCs.originMm);
    const rotation = vector(workCs.rotationDeg);
    const fixed = (value) => {
      const text = value.toFixed(3).replace(/\.?0+$/, "");
      return text === "-0" ? "0" : text;
    };
    const offset = workCs.workOffset ? `${workCs.workOffset} · ` : "";
    return `${offset}origin X${fixed(origin.x)} Y${fixed(origin.y)} Z${fixed(origin.z)} · rotation X${fixed(rotation.x)}° Y${fixed(rotation.y)}° Z${fixed(rotation.z)}°`;
  }

  return {
    rotationMatrix,
    modelToDrawing,
    placeOnNode,
    modelPointToWork,
    workToDrawing,
    placeInDrawing,
    offsetShift,
    guessKind,
    bounds,
    describeWorkCs
  };
});
