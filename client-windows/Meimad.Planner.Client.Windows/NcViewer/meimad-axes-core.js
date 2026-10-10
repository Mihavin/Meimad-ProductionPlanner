/*
 * Meimad Planner: machine axis positions and travel ranges of a parsed NC program (pure functions,
 * no DOM, so the client tests run them in V8).
 *
 * The toolpath points of a milling model are tool tips in the drawing frame (the program's first
 * work offset). The machine joints follow the vendored viewer's own mapping (viewer3d.js
 * updateFrames): the part frame is the workpiece mount of the kinematic chain at the rotary
 * position, shifted by the drawing frame; the tip in machine coordinates gives X/Y, and Z adds the
 * physical tool length. A lathe model has no kinematic chain, so its positions are the program's
 * X (diameter) and Z.
 *
 * Every point drawn is visited, so an arc that bulges past its end points still counts. Rotary
 * axes are interpolated along a segment the way the preview's tool pose is.
 */
(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.MeimadAxes = factory();
  }
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  const LINEAR = ["X", "Y", "Z"];
  const ROTARY = ["A", "B", "C"];
  const TOLERANCE = 1e-6;

  function finite(value) {
    return typeof value === "number" && Number.isFinite(value);
  }

  /** The machine definition's units scaled to the program's (the viewer's view.unitScale). */
  function unitScaleOf(model) {
    const scale = model && model.view && model.view.unitScale;
    return finite(scale) && scale > 0 ? scale : 1;
  }

  /** The kinematic chain in program units, exactly as viewer3d.js buildMachine builds it. */
  function chainFor(model, kinematics) {
    const definition = model && model.machineDefinition;
    if (!kinematics || !definition || !definition.kinematics || !(definition.kinematics.nodes || []).length) return undefined;
    const scale = unitScaleOf(model);
    const nodes = definition.kinematics.nodes.map((node) => Object.assign({}, node, {
      origin: Array.isArray(node.origin) ? node.origin.map((value) => value * scale) : undefined,
      pivot: Array.isArray(node.pivot) ? node.pivot.map((value) => value * scale) : undefined
    }));
    try {
      return kinematics.buildChain({ nodes });
    } catch (error) {
      return undefined;
    }
  }

  /**
   * A position calculator for one model: joints(point, rotary, toolLength) returns the machine
   * axis values for a tool tip in the drawing frame.
   */
  function createCalculator(model, kinematics) {
    const lathe = !model || model.kind === "lathe";
    if (lathe) {
      return {
        kind: "lathe",
        joints(point) {
          if (!point) return undefined;
          const diameter = finite(point.r) ? point.r * 2 : point.x;
          return { X: diameter, Z: point.z };
        }
      };
    }
    const chain = chainFor(model, kinematics);
    const frame = (model && model.frame) || { x: 0, y: 0, z: 0 };
    const shift = { x: Number(frame.x) || 0, y: Number(frame.y) || 0, z: Number(frame.z) || 0 };
    const cache = new Map();
    const partMatrix = (rotary) => {
      if (!chain || !kinematics) return undefined;
      const key = `${rotary.A}|${rotary.B}|${rotary.C}`;
      let matrix = cache.get(key);
      if (!matrix) {
        const mount = kinematics.workpieceTransform(chain, rotary);
        matrix = kinematics.multiply(mount, kinematics.translation(shift.x, shift.y, shift.z));
        if (cache.size > 4096) cache.clear();
        cache.set(key, matrix);
      }
      return matrix;
    };
    return {
      kind: "mill",
      hasChain: Boolean(chain),
      joints(point, rotary, toolLength) {
        if (!point || !finite(point.x) || !finite(point.y) || !finite(point.z)) return undefined;
        const angles = { A: Number(rotary && rotary.A) || 0, B: Number(rotary && rotary.B) || 0, C: Number(rotary && rotary.C) || 0 };
        const matrix = partMatrix(angles);
        const tip = matrix
          ? kinematics.transformPoint(matrix, point)
          : { x: point.x + shift.x, y: point.y + shift.y, z: point.z + shift.z };
        return {
          X: tip.x,
          Y: tip.y,
          Z: tip.z + (Number(toolLength) || 0),
          A: angles.A,
          B: angles.B,
          C: angles.C
        };
      }
    };
  }

  function rotaryAt(segment, fraction) {
    const rotary = (segment && segment.rotary) || {};
    const at = (start, end) => (Number(start) || 0) + ((Number(end) || 0) - (Number(start) || 0)) * fraction;
    return { A: at(rotary.a0, rotary.a1), B: at(rotary.b0, rotary.b1), C: at(rotary.c0, rotary.c1) };
  }

  /** The axes the machine definition declares, in a stable order, with limits in program units. */
  function machineAxes(model) {
    const declared = (model && model.machineDefinition && model.machineDefinition.axes) || {};
    const scale = unitScaleOf(model);
    const names = Object.keys(declared).sort((left, right) => {
      const order = (name) => {
        const index = LINEAR.concat(ROTARY).indexOf(name);
        return index < 0 ? 99 : index;
      };
      return order(left) - order(right) || left.localeCompare(right);
    });
    return names.map((name) => {
      const definition = declared[name] || {};
      const rotary = definition.type === "rotary" || ROTARY.includes(name);
      const factor = rotary ? 1 : scale;
      return {
        axis: name,
        label: definition.label || definition.programmedAs || name,
        type: rotary ? "rotary" : "linear",
        limitMin: finite(definition.min) ? definition.min * factor : null,
        limitMax: finite(definition.max) ? definition.max * factor : null,
        wraps: Boolean(definition.wrap)
      };
    });
  }

  /**
   * The reached range of every machine axis over the whole program and its check against the
   * machine's travel. Returns { kind, rows, outside, positionsUnknown, defaultedWorkOffsets }.
   * A row: { axis, label, type, min, max, minLine, maxLine, limitMin, limitMax, status, overBy }
   * where status is "inside", "outside", "no-limit" or "not-used".
   */
  function travelRanges(model, kinematics, options) {
    const settings = options || {};
    const calculator = createCalculator(model, kinematics);
    const ranges = new Map();
    const note = (axis, value, line) => {
      if (!finite(value)) return;
      let range = ranges.get(axis);
      if (!range) {
        range = { min: value, max: value, minLine: line, maxLine: line };
        ranges.set(axis, range);
        return;
      }
      if (value < range.min) { range.min = value; range.minLine = line; }
      if (value > range.max) { range.max = value; range.maxLine = line; }
    };
    const includeRapids = settings.includeRapids !== false;
    for (const segment of (model && model.segments) || []) {
      if (!segment || (!includeRapids && segment.kind === "rapid")) continue;
      const points = segment.points || [];
      const count = points.length;
      for (let index = 0; index < count; index += 1) {
        const fraction = count > 1 ? index / (count - 1) : 1;
        const joints = calculator.joints(points[index], rotaryAt(segment, fraction), segment.toolLength);
        if (!joints) continue;
        for (const axis of Object.keys(joints)) note(axis, joints[axis], segment.line);
      }
    }
    const declared = calculator.kind === "lathe" ? [] : machineAxes(model);
    const rows = [];
    const seen = new Set();
    for (const axis of declared) {
      seen.add(axis.axis);
      rows.push(rangeRow(axis, ranges.get(axis.axis)));
    }
    // Axes the program moves that the machine definition does not declare (and every lathe axis).
    const fallback = calculator.kind === "lathe" ? ["X", "Z"] : LINEAR.concat(ROTARY);
    for (const name of fallback) {
      if (seen.has(name) || !ranges.has(name)) continue;
      const range = ranges.get(name);
      // A rotary axis the machine does not have and the program never turns is noise.
      if (ROTARY.includes(name) && Math.abs(range.min) < TOLERANCE && Math.abs(range.max) < TOLERANCE) continue;
      rows.push(rangeRow({ axis: name, label: name, type: ROTARY.includes(name) ? "rotary" : "linear", limitMin: null, limitMax: null, wraps: false }, range));
    }
    const defaulted = (model && model.meta && model.meta.defaultedWorkOffsets) || [];
    return {
      kind: calculator.kind,
      rows,
      outside: rows.filter((row) => row.status === "outside").length,
      // Machine X/Y/Z are only meaningful when the work offsets have real values.
      positionsUnknown: calculator.kind === "mill" && defaulted.length > 0,
      defaultedWorkOffsets: defaulted.slice()
    };
  }

  function rangeRow(axis, range) {
    const row = {
      axis: axis.axis,
      label: axis.label,
      type: axis.type,
      min: range ? range.min : null,
      max: range ? range.max : null,
      minLine: range ? range.minLine : null,
      maxLine: range ? range.maxLine : null,
      limitMin: axis.limitMin,
      limitMax: axis.limitMax,
      status: "inside",
      overBy: 0,
      overLine: null
    };
    if (!range) {
      row.status = "not-used";
      return row;
    }
    if (axis.wraps || (axis.limitMin === null && axis.limitMax === null)) {
      row.status = "no-limit";
      return row;
    }
    const below = axis.limitMin !== null ? axis.limitMin - range.min : 0;
    const above = axis.limitMax !== null ? range.max - axis.limitMax : 0;
    if (below > TOLERANCE || above > TOLERANCE) {
      row.status = "outside";
      row.overBy = Math.max(below, above);
      row.overLine = below >= above ? range.minLine : range.maxLine;
    }
    return row;
  }

  /** The machine axis values of one tool pose (the preview's {tip, rotary, length}). */
  function poseJoints(model, kinematics, pose, calculator) {
    if (!pose || !pose.tip) return undefined;
    const calc = calculator || createCalculator(model, kinematics);
    return calc.joints(pose.tip, pose.rotary, pose.length);
  }

  function format(value, decimals) {
    if (!finite(value)) return "—";
    const places = finite(decimals) ? decimals : 3;
    const fixed = value.toFixed(places);
    return fixed === `-${(0).toFixed(places)}` ? (0).toFixed(places) : fixed;
  }

  return { createCalculator, travelRanges, poseJoints, machineAxes, format };
});
