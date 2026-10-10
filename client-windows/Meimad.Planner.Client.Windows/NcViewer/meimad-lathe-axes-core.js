/*
 * Meimad Planner: the milling axes of a lathe program (pure functions, no DOM, so the client tests
 * run them in V8).
 *
 * The vendored lathe interpreter draws the X-Z turning profile and ignores the C (spindle position)
 * and Y (turret off-centre) words of live-tool milling. This pass reads them back from the program
 * rows of each drawn segment, so the Machine view can turn the spindle and move the turret in Y and
 * the Axes card can show C and Y:
 *   C<deg> absolute spindle position, H<deg> incremental C (FANUC/Haas lathe convention),
 *   Y<mm> absolute Y, V<mm> incremental Y. A C written after a comma (",C1.") is a chamfer.
 * Rows without a drawn move (C-only indexing such as "G0 C90.") are read from the program text
 * between two drawn rows of the main program, so indexing before a move is not lost. Calls, loops
 * and macros are followed only as far as the drawn segments show them.
 * G112/G12.1 (polar interpolation) and G107/G7.1 (cylindrical interpolation) are reported, not
 * interpreted: their X/C or Z/C words are not machine positions.
 */
(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.MeimadLatheAxes = factory();
  }
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  const WORD = /(,?)([A-Z])\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+))/gi;
  const POLAR_ON = /\bG0*112(?![.\d])|\bG12\.1\b/i;
  const POLAR_OFF = /\bG0*113(?![.\d])|\bG13\.1\b/i;
  const CYLINDRICAL = /\bG0*107(?![.\d])|\bG7\.1\b/i;

  function stripComments(line) {
    return String(line || "").replace(/\([^)]*\)/g, " ").replace(/;.*$/, " ");
  }

  /** The C/H/Y/V words of one row: { c?, h?, y?, v?, polarOn, polarOff, cylindrical }. */
  function readRow(line) {
    const text = stripComments(line);
    const result = { polarOn: POLAR_ON.test(text), polarOff: POLAR_OFF.test(text), cylindrical: CYLINDRICAL.test(text) };
    // Macro and variable rows (#1=..., V1=V1+1, IF/WHILE/GOTO) and calls (G65 P.. H.., M98) carry
    // letters that are arguments or variables, not axis positions.
    if (/[=\[]|\b(?:IF|WHILE|GOTO|CALL)\b|\bG6[56](?!\d)|\bM9[789](?!\d)/i.test(text)) return result;
    let match;
    WORD.lastIndex = 0;
    while ((match = WORD.exec(text)) !== null) {
      if (match[1] === ",") continue; // ,C chamfer and ,R corner
      const letter = match[2].toUpperCase();
      const value = Number(match[3]);
      if (!Number.isFinite(value)) continue;
      // A letter glued to a preceding letter is part of a word like "IF" or "GOTO"; skip those.
      const before = text[match.index - 1];
      if (before && /[A-Z#]/i.test(before)) continue;
      if (letter === "C") result.c = value;
      else if (letter === "H") result.h = value;
      else if (letter === "Y") result.y = value;
      else if (letter === "V") result.v = value;
    }
    return result;
  }

  /**
   * Per drawn segment: { y0, y1, c0, c1 } and a summary { usesC, usesY, polar, cylindrical }.
   * <sourceLines> (optional) is the main program's text by row, for rows that draw nothing.
   */
  function annotate(segments, sourceLines) {
    const list = segments || [];
    const lines = Array.isArray(sourceLines) ? sourceLines : [];
    const result = { segments: new Array(list.length), usesC: false, usesY: false, polar: false, cylindrical: false };
    let c = 0;
    let y = 0;
    let polar = false;
    let lastLine = 0;
    const apply = (row) => {
      if (row.polarOn) { polar = true; result.polar = true; }
      if (row.cylindrical) result.cylindrical = true;
      if (polar) {
        if (row.polarOff) polar = false;
        return; // X/C under polar interpolation are not spindle positions
      }
      if (row.c !== undefined) { c = row.c; result.usesC = true; }
      if (row.h !== undefined) { c += row.h; result.usesC = true; }
      if (row.y !== undefined) { y = row.y; result.usesY = true; }
      if (row.v !== undefined) { y += row.v; result.usesY = true; }
      if (row.polarOff) polar = false;
    };
    for (let index = 0; index < list.length; index += 1) {
      const segment = list[index] || {};
      const line = Number(segment.line);
      const mainProgram = !segment.sourceUnit;
      // Rows between the previous drawn row and this one (main program only, forward only).
      if (mainProgram && Number.isFinite(line) && line > lastLine + 1 && lines.length) {
        for (let row = lastLine + 1; row < line && row <= lines.length; row += 1) apply(readRow(lines[row - 1]));
      }
      const c0 = c;
      const y0 = y;
      apply(readRow(segment.raw));
      result.segments[index] = { c0, c1: c, y0, y1: y };
      if (mainProgram && Number.isFinite(line)) lastLine = Math.max(lastLine, line);
    }
    return result;
  }

  /** C and Y at a fraction of one segment. */
  function at(annotation, index, fraction) {
    const entry = annotation && annotation.segments ? annotation.segments[index] : undefined;
    if (!entry) return { C: 0, Y: 0 };
    const t = Number.isFinite(fraction) ? Math.min(1, Math.max(0, fraction)) : 1;
    return { C: entry.c0 + (entry.c1 - entry.c0) * t, Y: entry.y0 + (entry.y1 - entry.y0) * t };
  }

  /** Lowest and highest C and Y over the program, with the rows. */
  function ranges(annotation, segments) {
    const out = {};
    const note = (axis, value, line) => {
      const range = out[axis] || (out[axis] = { min: value, max: value, minLine: line, maxLine: line });
      if (value < range.min) { range.min = value; range.minLine = line; }
      if (value > range.max) { range.max = value; range.maxLine = line; }
    };
    (annotation && annotation.segments || []).forEach((entry, index) => {
      if (!entry) return;
      const line = segments && segments[index] ? segments[index].line : null;
      if (annotation.usesC) { note("C", entry.c0, line); note("C", entry.c1, line); }
      if (annotation.usesY) { note("Y", entry.y0, line); note("Y", entry.y1, line); }
    });
    return out;
  }

  function finite(value) {
    return typeof value === "number" && Number.isFinite(value);
  }

  /**
   * The lathe definition's kinematic nodes for the Machine view, in program units:
   * - a virtual "meimad-y-slide" carries the turret when the machine has a Y axis but no Y node;
   * - nodes without geometry get placeholder boxes sized from the travel, so the machine is visible
   *   before STL models are added (a Machine 3D model on the node hides its placeholder).
   * The chain origin is the spindle (chuck) face on the spindle axis.
   */
  function prepareNodes(definition, unitScale) {
    const scale = finite(unitScale) && unitScale > 0 ? unitScale : 1;
    const axes = (definition && definition.axes) || {};
    const source = ((definition && definition.kinematics && definition.kinematics.nodes) || []).map((node) => Object.assign({}, node));
    if (axes.Y && !source.some((node) => node.axis === "Y")) {
      const mount = source.find((node) => node.toolMount);
      if (mount) {
        source.push({ id: "meimad-y-slide", parent: mount.parent, label: "Y slide", type: "linear", axis: "Y", direction: [0, 1, 0], geometry: null, model: null });
        mount.parent = "meimad-y-slide";
      }
    }
    const xTravel = Math.abs((axes.X && finite(axes.X.min) ? axes.X.min : -400)) / ((axes.X && axes.X.diameter) ? 2 : 1);
    const zTravel = Math.abs(axes.Z && finite(axes.Z.min) ? axes.Z.min : -500);
    const placeholder = (node) => {
      if (node.geometry && node.geometry.shape) return node.geometry;
      if (node.toolMount) return { shape: "cylinder", radius: 90, height: 60, center: [90, 0, 0], axis: "z", placeholder: true };
      if (node.axis === "X") return { shape: "box", size: [160, 180, 140], center: [200, 0, 0], placeholder: true };
      if (node.axis === "Y") return { shape: "box", size: [60, 220, 120], center: [120, 0, 0], placeholder: true };
      if (node.axis === "Z") return { shape: "box", size: [80, 320, 260], center: [xTravel + 260, 0, 0], placeholder: true };
      if (!node.parent) return { shape: "box", size: [60, 420, zTravel + 600], center: [-260, 0, -(zTravel / 2) + 100], placeholder: true };
      return null;
    };
    return source.map((node) => {
      const geometry = placeholder(node);
      return Object.assign({}, node, {
        origin: Array.isArray(node.origin) ? node.origin.map((value) => value * scale) : undefined,
        pivot: Array.isArray(node.pivot) ? node.pivot.map((value) => value * scale) : undefined,
        geometry: geometry ? Object.assign({}, geometry, {
          size: geometry.size ? geometry.size.map((value) => value * scale) : undefined,
          center: Array.isArray(geometry.center) ? geometry.center.map((value) => value * scale) : [0, 0, 0],
          radius: finite(geometry.radius) ? geometry.radius * scale : undefined,
          height: finite(geometry.height) ? geometry.height * scale : undefined
        }) : null
      });
    });
  }

  /**
   * Where the spindle face sits on the program's Z axis: the back of the stock (front Z minus
   * length) when a stock is defined, else just behind the deepest point of the toolpath.
   */
  function spindleFaceZ(model, stock) {
    const source = stock && finite(stock.frontZ) && finite(stock.length) ? stock
      : model && model.stock && finite(model.stock.frontZ) && finite(model.stock.length) ? model.stock : null;
    if (source) return source.frontZ - source.length;
    let min = Infinity;
    for (const segment of (model && model.segments) || []) {
      for (const point of segment.points || []) if (finite(point.z) && point.z < min) min = point.z;
    }
    return Number.isFinite(min) ? min - 5 : 0;
  }

  return { readRow, annotate, at, ranges, prepareNodes, spindleFaceZ };
});
