// Meimad Planner NC viewer: which part of the toolpath is drawn (the Path selector next to Tool).
//   played     the moves already run while a run is in progress (the vendored viewer's own behaviour)
//   all        the whole toolpath at all times
//   remaining  the moves not yet run
//   dim        the moves already run, and the rest faint
//   split      the moves already run in teal grey and the rest in orange
//   hidden     no toolpath (the move being run and the selected row stay)
//   tool       only the moves of the tool that is running (all of them without a run)
//   rowTool    only the moves of the tool of the selected NC row
//   operation  only the moves of the CAM operation of the selected NC row (operations start at the
//              CAM's header rows such as "N21 (VOLUME MILLING ...)", else at every tool change)
// Without a run in progress (before Run, and after the program ends) every mode but hidden shows the
// whole toolpath. No DOM: meimad-viewer3d.js applies it to the vendored line shader and the client
// tests run it in V8 (PathVisibilityCoreTests).
//
// The vendored viewer (media/viewer3d.js rebuildGeometry) gives every drawn segment a pick id: 1..N
// over the visible segments in execution order, then N + 1.. over the visible compensated
// segments. The shader compares an instance's pick id with the first id that has not run yet; hidden
// break instances, the selection and the active move carry id 0 and are never affected.
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory();
  else root.MeimadPathVisibility = factory();
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  const MODES = Object.freeze({ all: 0, played: 1, remaining: 2, dim: 3, hidden: 4, split: 5, tool: 6, rowTool: 7, operation: 8 });
  const FILTERS = new Set(["tool", "rowTool", "operation"]);
  const DEFAULT_MODE = "played";
  const DIM_ALPHA = 0.16;
  // media/viewer3d.js RETURN_KINDS.
  const RETURN_KINDS = new Set(["g30", "home", "tool-change"]);

  function normalizeMode(mode) {
    return Object.prototype.hasOwnProperty.call(MODES, mode) ? mode : DEFAULT_MODE;
  }

  // media/viewer3d.js segmentVisible.
  function segmentVisible(segment, filters) {
    if (!filters.showNonCutting && segment.nonCutting) return false;
    if (filters.tool !== undefined && filters.tool !== "all" && segment.tool !== Number(filters.tool)) return false;
    if (RETURN_KINDS.has(segment.kind)) return Boolean(filters.showReturns);
    if (segment.kind === "rapid" && !filters.showRapids) return false;
    return true;
  }

  // Prefix counts per execution index: before[e] = visible segments that run before execution e; and
  // the execution index of every visible segment in pick-id order.
  function prefix(list, count, visible) {
    const before = new Int32Array(count + 1);
    const executions = [];
    let total = 0;
    for (let index = 0; index < list.length; index += 1) {
      const segment = list[index];
      if (!visible(segment)) continue;
      const execution = Number.isFinite(segment.executionIndex) ? segment.executionIndex : index;
      before[Math.max(0, Math.min(count, execution + 1))] += 1;
      executions.push(execution);
      total += 1;
    }
    for (let index = 1; index <= count; index += 1) before[index] += before[index - 1];
    return { before, total, executions };
  }

  // The pick id layout of one model under one set of filters.
  function index(model, filters) {
    const segments = (model && model.segments) || [];
    const value = filters || {};
    const count = segments.length;
    const base = prefix(segments, count, (segment) => segmentVisible(segment, value));
    const compensated = value.showCompensated
      ? prefix((model && model.compensatedSegments) || [], count, (segment) => segmentVisible({ ...segment, kind: "feed" }, value))
      : { before: new Int32Array(count + 1), total: 0, executions: [] };
    // Pick id -> execution index (0: no segment).
    const pickExecution = new Int32Array(base.total + compensated.total + 1).fill(-1);
    base.executions.forEach((execution, n) => { pickExecution[n + 1] = execution; });
    compensated.executions.forEach((execution, n) => { pickExecution[base.total + n + 1] = execution; });
    return { count, base, compensated, pickExecution };
  }

  // CAM operation number of every segment: a new operation starts at each header row of the program
  // (an N word followed by a comment) and, without header rows, at every tool change.
  function operations(segments, headerLines) {
    const result = new Int32Array(segments.length);
    const headers = (headerLines || []).slice().sort((a, b) => a - b);
    let operation = 0;
    let lastTool;
    let header = 0;
    let lastHeaderLine = -Infinity;
    segments.forEach((segment, index) => {
      const line = Number(segment.line) || 0;
      if (headers.length) {
        while (header < headers.length && headers[header] <= line) {
          if (headers[header] > lastHeaderLine) {
            operation += 1;
            lastHeaderLine = headers[header];
          }
          header += 1;
        }
      } else if (segment.tool !== lastTool) {
        operation += 1;
      }
      lastTool = segment.tool;
      result[index] = operation;
    });
    return result;
  }

  // Header rows of an NC program text (1-based line numbers): "N21 (VOLUME MILLING ...)".
  function headerLines(text) {
    const lines = [];
    String(text || "").split(/\r\n|\r|\n/).forEach((line, index) => {
      if (/^\s*N\d+\s*\(/i.test(line)) lines.push(index + 1);
    });
    return lines;
  }

  // Visible flags per pick id for the filter modes: keep(segment index) decides.
  function classes(layout, segments, keep) {
    const flags = new Uint8Array(Math.max(1, layout.pickExecution.length));
    for (let id = 1; id < layout.pickExecution.length; id += 1) {
      const execution = layout.pickExecution[id];
      flags[id] = execution >= 0 && keep(segments[execution], execution) ? 255 : 0;
    }
    return flags;
  }

  // Shader inputs for one mode at one playback state ({ completedIndex } while a run is in progress,
  // undefined otherwise).
  function uniforms(mode, layout, playback) {
    const name = normalizeMode(mode);
    const count = layout ? layout.count : 0;
    const baseTotal = layout ? layout.base.total : 0;
    const running = Boolean(playback) && !playback.complete && Number.isFinite(playback.completedIndex);
    // Execution indexes up to `completed` have run; without a run every move counts as run.
    const completed = running ? Math.max(-1, Math.min(count - 1, playback.completedIndex)) : count - 1;
    const runBase = layout ? layout.base.before[completed + 1] : 0;
    const runCompensated = layout ? layout.compensated.before[completed + 1] : 0;
    // Without a run in progress nothing is hidden or dimmed (remaining shows the whole path too); the
    // filter modes hide by the class texture whether or not a run is in progress.
    const effective = FILTERS.has(name) ? name : !running && name !== "hidden" ? "all" : name;
    return {
      mode: MODES[effective],
      baseCount: baseTotal,
      baseThreshold: 1 + runBase,
      compensatedThreshold: baseTotal + 1 + runCompensated,
      dim: DIM_ALPHA,
      filter: FILTERS.has(name) ? 1 : 0
    };
  }

  const ANCHOR_UNIFORM = "void main() {";
  const ANCHOR_COLOR = "vColor = instanceColor;";
  const ANCHOR_HIDE = "if (instanceStyle.z < 0.5) {";

  // The vendored line vertex shader with the path range test, or null when its text is not the one
  // this patch was written for (the toolpath then draws as the vendored viewer does).
  function patchLineShader(source) {
    const text = String(source || "");
    if (!text.includes("instancePick") || [ANCHOR_UNIFORM, ANCHOR_COLOR, ANCHOR_HIDE].some((anchor) => text.split(anchor).length !== 2)) {
      return null;
    }
    return text
      .replace(ANCHOR_UNIFORM, [
        "uniform float meimadPathMode;",
        "  uniform float meimadFilter;",
        "  uniform sampler2D meimadClass;",
        "  uniform float meimadBaseCount;",
        "  uniform float meimadBaseThreshold;",
        "  uniform float meimadCompensatedThreshold;",
        "  uniform float meimadDim;",
        "  " + ANCHOR_UNIFORM
      ].join("\n"))
      .replace(ANCHOR_COLOR, [
        ANCHOR_COLOR,
        "    float meimadId = floor(instancePick + 0.5);",
        "    bool meimadRemaining = meimadId <= meimadBaseCount ? meimadId >= meimadBaseThreshold : meimadId >= meimadCompensatedThreshold;",
        "    bool meimadFiltered = false;",
        "    if (meimadFilter > 0.5 && meimadId > 0.5) {",
        "      int meimadPick = int(meimadId);",
        "      meimadFiltered = texelFetch(meimadClass, ivec2(meimadPick % 4096, meimadPick / 4096), 0).r < 0.5;",
        "    }",
        "    if (meimadId > 0.5 && meimadPathMode > 4.5 && meimadPathMode < 5.5) vColor.rgb = meimadRemaining ? mix(vColor.rgb, vec3(1.0, 0.62, 0.12), 0.7) : mix(vColor.rgb, vec3(0.42, 0.62, 0.66), 0.6);",
        "    bool meimadHidden = meimadId > 0.5 && (meimadFiltered || (meimadPathMode > 3.5 && meimadPathMode < 4.5)",
        "      || (meimadPathMode > 0.5 && meimadPathMode < 1.5 && meimadRemaining)",
        "      || (meimadPathMode > 1.5 && meimadPathMode < 2.5 && !meimadRemaining));",
        "    if (meimadId > 0.5 && meimadPathMode > 2.5 && meimadPathMode < 3.5 && meimadRemaining) vColor.a *= meimadDim;"
      ].join("\n"))
      .replace(ANCHOR_HIDE, "if (instanceStyle.z < 0.5 || meimadHidden) {");
  }

  // Whether one pick id is drawn (and its alpha factor): the shader's rule, for tests.
  function drawn(id, values, flags) {
    if (id < 0.5) return { visible: true, alpha: 1 };
    const remaining = id <= values.baseCount ? id >= values.baseThreshold : id >= values.compensatedThreshold;
    const hidden = (values.filter && flags && !flags[id]) || values.mode === MODES.hidden
      || (values.mode === MODES.played && remaining)
      || (values.mode === MODES.remaining && !remaining);
    return { visible: !hidden, alpha: values.mode === MODES.dim && remaining ? values.dim : 1 };
  }

  return { MODES, DEFAULT_MODE, FILTERS, normalizeMode, segmentVisible, index, operations, headerLines, classes, uniforms, patchLineShader, drawn };
});
