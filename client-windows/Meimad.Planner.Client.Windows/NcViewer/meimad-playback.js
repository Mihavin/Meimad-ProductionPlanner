"use strict";

// Meimad Planner NC viewer: called programs, breakpoints, CNC-like playback controls, the tool at the
// selected row and the macro variables window.
//
//   Called programs  When the program calls macros or subprograms, the source column splits
//                    horizontally: the NC program stays above and the called program below. The
//                    list in the pane header shows every program the program calls at any depth
//                    (model meimadCallTree: programs of the same file, the program's folder with
//                    the release's subprograms, and the machine's program memory folder with
//                    Renishaw and other macros). While the tool is in a called program the pane
//                    shows its active row; in the main program it shows the next program to be
//                    called.
//   Single step      Runs the next executed row and stops: every block, including macro
//                    statements and the rows of called programs, in the order the control runs
//                    them (model meimadTrace). A breakpoint row is landed on before it runs; a
//                    stop row (M00/M01/#3006) after.
//   Breakpoints      A click on a row's number or on the dot column left of it, or F9 on the
//                    cursor row, in either editor; playback stops before every execution.
//   Stop             Ends playback and returns to the program start (reset).
//   Optional stop    M01 stops playback only while it is on; M00, #3006 and #3000 always stop.
//   Selected row     With "Tool follows row" on, selecting a row puts the tool where that row has
//                    run (the end of its move, or where the tool is when a macro statement runs;
//                    the same row again goes to its next execution) and shows that tool's path.
//   Macro variables  Opens a window (hosted by the application) with the value of every variable
//                    the program and its called programs use, at the playback position or the
//                    stepped/selected row.
//
// The rules live in meimad-playback-core.js; playback itself stays the vendored preview.js, driven
// through window.cncPreviewSeek and the 3D wrapper's model, playback and selection events.
(() => {
  const core = window.MeimadPlaybackCore;
  if (!core) return;
  const byId = (id) => document.getElementById(id);
  const elements = {
    editorPane: byId("editorPane"),
    editorHost: byId("editorHost"),
    splitter: byId("meimadCallSplitter"),
    pane: byId("meimadCallPane"),
    heading: byId("meimadCallHeading"),
    info: byId("meimadCallInfo"),
    list: byId("meimadCallList"),
    hide: byId("meimadCallHide"),
    show: byId("meimadCallShow"),
    textarea: byId("meimadCallEditor"),
    step: byId("meimadStepButton"),
    stop: byId("meimadStopButton"),
    optional: byId("meimadOptionalStopButton"),
    variables: byId("meimadVariablesButton"),
    follow: byId("meimadFollowRow"),
    notice: byId("meimadStopNotice"),
    runButton: byId("runButton"),
    restartButton: byId("restartButton"),
    toolFilter: byId("toolFilter")
  };
  if (!elements.pane || !elements.textarea || !elements.step) return;
  const host = window.fanucDesktop?.meimad;

  const STORAGE_SPLIT = "meimad.ncViewer.callSplit";
  const STORAGE_OPTIONAL = "meimad.ncViewer.optionalStop";
  const STORAGE_FOLLOW = "meimad.ncViewer.followRow";
  const GUTTER = "meimad-breakpoint-gutter";
  const STEP_CLASS = "meimad-step-line";
  const CALL_CLASS = "cm-playback-line";
  const readStorage = (key) => {
    try {
      return window.localStorage.getItem(key);
    } catch {
      return null;
    }
  };
  const writeStorage = (key, value) => {
    try {
      window.localStorage.setItem(key, value);
    } catch {
      // Storage is a convenience; the controls work without it.
    }
  };

  let hooks;
  let model = { segments: [] };
  let timeline = core.buildTimeline([]);
  let stops = [];
  let units = {};
  let tree = [];
  let trace = { rows: {}, writes: [], systemReads: [], used: [] };
  let executions = [];
  let mainRanges = [];
  const breakpoints = new Set();
  let halts = [];
  let passedHalts = new Set();
  let hasCalls = false;
  let userHidden = false;
  let previousActive = -1;
  let lastPlayback;
  let optionalStop = readStorage(STORAGE_OPTIONAL) === "on";
  let callEditor;
  let mainAttached;
  let shown = { unit: undefined, line: undefined, text: undefined };
  let callMarked;
  let stepMarked;
  let pinnedByUser = false;
  let stepping = false;
  let stepIndex = -1;
  let stepBefore = false;
  let rowPick;
  let lastEdit = 0;
  let lastPick = 0;
  let variablesOpen = false;
  let variablesTimer = 0;

  const mainEditor = () => document.querySelector("#editorHost .CodeMirror")?.CodeMirror;
  const playbackState = () => (typeof window.cncPreviewState === "function" ? window.cncPreviewState().playback : "idle");
  const segments = () => model.segments || [];

  // ----- the main file's programs ----------------------------------------------------------------

  // Programs later in the NC program's own file (O2000 after M30): their rows are rows of this
  // file, shown in the pane with the file's text.
  function buildMainRanges() {
    const headers = [];
    (mainEditor()?.getValue() ?? "").split("\n").forEach((raw, index) => {
      const code = raw.replace(/\([^)]*\)?/g, " ").replace(/;.*$/, "").trim().toUpperCase().replace(/^\/\d?\s*/, "");
      const header = code.match(/^O\s*(\d+)/) || code.match(/^:\s*(\d+)/);
      if (header) headers.push({ row: index + 1, label: `O${header[1].padStart(4, "0")}` });
    });
    mainRanges = headers
      .filter((header, position) => !(position === 0 && header.row <= 4))
      .map((header, position, list) => ({ label: header.label, start: header.row, end: (list[position + 1]?.row ?? Infinity) - 1 }));
  }

  const inFileLabelAt = (row) => mainRanges.find((range) => row >= range.start && row <= range.end)?.label;
  const isMainOwn = (entry) => entry.unit === "main" && !inFileLabelAt(entry.row);
  const unitLabelOf = (entry) => (entry.unit === "main" ? inFileLabelAt(entry.row) : entry.unit);
  const isInFileName = (name) => Boolean(units[name]?.inFile) || mainRanges.some((range) => range.label === name);

  // ----- breakpoints ---------------------------------------------------------------------------

  const mainKey = (line) => core.keyOf("main", line + 1, false);
  const callKey = (line) => (shown.unit ? core.keyOf(shown.unit, line + 1, isInFileName(shown.unit)) : undefined);

  function marker() {
    const element = document.createElement("span");
    element.className = "meimad-breakpoint";
    element.title = "Breakpoint: playback stops before this row";
    element.textContent = "●";
    return element;
  }

  function renderMarkers(editor, keyForLine) {
    if (!editor) return;
    editor.clearGutter(GUTTER);
    if (!breakpoints.size) return;
    for (let line = 0; line < editor.lineCount(); line += 1) {
      const key = keyForLine(line);
      if (key && breakpoints.has(key)) editor.setGutterMarker(line, GUTTER, marker());
    }
  }

  function refreshBreakpoints() {
    halts = core.halts(stops, breakpoints, trace);
    renderMarkers(mainEditor(), mainKey);
    renderMarkers(callEditor, callKey);
  }

  function toggleBreakpoint(key) {
    if (!key) return;
    if (breakpoints.has(key)) breakpoints.delete(key);
    else breakpoints.add(key);
    refreshBreakpoints();
  }

  function wireBreakpoints(editor, keyForLine) {
    editor.on("gutterClick", (cm, line, gutter) => {
      if (gutter === GUTTER || gutter === "CodeMirror-linenumbers") toggleBreakpoint(keyForLine(line));
    });
    editor.addKeyMap({ F9: (cm) => toggleBreakpoint(keyForLine(cm.getCursor().line)) });
  }

  // The renderer creates the NC program editor; it is wired once it exists, and its markers come
  // back after the renderer replaces the text.
  let markerTimer = 0;
  function attachMainEditor() {
    const editor = mainEditor();
    if (!editor || mainAttached === editor) return;
    mainAttached = editor;
    const gutters = editor.getOption("gutters") || [];
    if (!gutters.includes(GUTTER)) editor.setOption("gutters", [GUTTER, ...gutters]);
    wireBreakpoints(editor, mainKey);
    editor.on("changes", () => {
      lastEdit = Date.now();
      window.clearTimeout(markerTimer);
      markerTimer = window.setTimeout(() => {
        buildMainRanges();
        renderMarkers(mainEditor(), mainKey);
      }, 200);
    });
    editor.on("cursorActivity", (cm) => scheduleRow(() => mainKey(cm.getCursor().line), false));
    buildMainRanges();
    renderMarkers(editor, mainKey);
  }

  // ----- called-program pane -------------------------------------------------------------------

  function ensureEditor() {
    if (callEditor || !window.CodeMirror?.fromTextArea) return callEditor;
    callEditor = window.CodeMirror.fromTextArea(elements.textarea, {
      mode: "text/x-fanuc-nc",
      theme: "fanuc-dark",
      lineNumbers: true,
      readOnly: true,
      lineWrapping: false,
      gutters: [GUTTER, "CodeMirror-linenumbers"]
    });
    wireBreakpoints(callEditor, callKey);
    callEditor.on("cursorActivity", (cm) => scheduleRow(() => callKey(cm.getCursor().line), true));
    return callEditor;
  }

  function applySplit(percent) {
    const main = Math.min(85, Math.max(15, Number(percent) || 55));
    elements.editorPane.style.setProperty("--meimad-main-rows", `${main}fr`);
    elements.editorPane.style.setProperty("--meimad-call-rows", `${100 - main}fr`);
    elements.splitter.setAttribute("aria-valuenow", String(Math.round(main)));
    return main;
  }

  function setPaneVisible(visible) {
    const open = visible && hasCalls && !userHidden;
    elements.pane.hidden = !open;
    elements.splitter.hidden = !open;
    elements.editorPane.classList.toggle("meimad-call-open", open);
    if (elements.show) elements.show.hidden = !(hasCalls && userHidden);
    if (open) {
      ensureEditor();
      window.requestAnimationFrame(() => {
        callEditor?.refresh();
        mainEditor()?.refresh();
      });
    } else {
      window.requestAnimationFrame(() => mainEditor()?.refresh());
    }
  }

  function unitText(name) {
    if (isInFileName(name)) return mainEditor()?.getValue() ?? "";
    const unit = units[name];
    return typeof unit?.text === "string" ? unit.text : undefined;
  }

  function clearCallMarker() {
    if (callMarked !== undefined) callEditor?.removeLineClass(callMarked, "background", CALL_CLASS);
    callMarked = undefined;
    shown.line = undefined;
  }

  function markCallRow(row) {
    if (!callEditor || shown.line === row) return;
    clearCallMarker();
    const index = Math.max(0, Math.min(callEditor.lineCount() - 1, row - 1));
    callMarked = callEditor.addLineClass(index, "background", CALL_CLASS);
    callEditor.scrollIntoView({ line: index, ch: 0 }, 60);
    shown.line = row;
  }

  // The stepped row of the NC program's own file, or the row that called the stepped program.
  function markMainRow(row) {
    const editor = mainEditor();
    if (!editor) return;
    if (stepMarked !== undefined) editor.removeLineClass(stepMarked, "background", STEP_CLASS);
    stepMarked = undefined;
    if (row === undefined) return;
    const index = Math.max(0, Math.min(editor.lineCount() - 1, row - 1));
    stepMarked = editor.addLineClass(index, "background", STEP_CLASS);
    editor.scrollIntoView({ line: index, ch: 0 }, 60);
  }

  // Loads a program into the lower editor (without a row marker).
  function loadUnit(name) {
    const editor = ensureEditor();
    const text = unitText(name);
    if (!editor || text === undefined) return false;
    if (shown.unit !== name || shown.text !== text) {
      editor.setValue(text);
      shown = { unit: name, line: undefined, text };
      callMarked = undefined;
      renderMarkers(editor, callKey);
    }
    const entry = units[name];
    elements.heading.textContent = name;
    elements.heading.title = entry?.path || entry?.location || "";
    if (elements.list && [...elements.list.options].some((option) => option.value === name)) elements.list.value = name;
    return true;
  }

  // Shows the called row of a segment while playback runs, or, with no call, keeps the last
  // program without a marker.
  function showCall(call, running) {
    if (!ensureEditor()) return;
    if (!call) {
      clearCallMarker();
      if (shown.unit) elements.info.textContent = running ? "Back in the main program" : "";
      return;
    }
    if (!loadUnit(call.unit)) return;
    elements.info.textContent = `Called from row ${call.callerLine} · level ${call.depth}`;
    markCallRow(call.line);
  }

  // The pane while the tool is in the main program: the next program to be called after row
  // execution `index` (-1: from the program start).
  function previewNextCall(index) {
    if (pinnedByUser || !ensureEditor()) return;
    const next = core.nextCall(executions, index, isMainOwn);
    const label = next ? unitLabelOf(next.entry) : tree.find((node) => !node.missing)?.name;
    if (!label || !loadUnit(label)) return;
    clearCallMarker();
    elements.info.textContent = next ? `Next: called from row ${next.callerRow}` : "No further calls";
  }

  // Shows row execution `index` of the executed program: a row of the main program in the NC
  // editor with the next call previewed below, or a called program's row in the pane with the
  // calling row marked above.
  function showExecution(index) {
    const entry = executions[index];
    if (!entry) return;
    if (isMainOwn(entry)) {
      markMainRow(entry.row);
      previewNextCall(index);
      return;
    }
    const caller = core.callerRow(executions, index, isMainOwn);
    markMainRow(caller);
    const label = unitLabelOf(entry);
    if (!label || !loadUnit(label)) return;
    pinnedByUser = false;
    markCallRow(entry.row);
    elements.info.textContent = caller !== undefined ? `Called from row ${caller}` : "";
  }

  function fillList() {
    if (!elements.list) return;
    elements.list.replaceChildren();
    for (const node of tree) {
      const option = document.createElement("option");
      const indent = "  ".repeat(Math.max(0, node.depth - 1)) + (node.depth > 1 ? "└ " : "");
      const place = node.missing ? (node.computed ? "" : " — not found") : node.location ? ` — ${node.location}` : "";
      const file = node.name && node.name !== node.label && !node.computed ? ` · ${node.name}` : "";
      option.value = node.missing ? "" : node.name;
      option.textContent = `${indent}${node.label}${file}${place}${node.repeated ? " (listed above)" : ""}`;
      option.title = node.path || "";
      option.disabled = Boolean(node.missing);
      elements.list.appendChild(option);
    }
    elements.list.hidden = tree.length === 0;
    elements.list.title = `${tree.filter((node) => !node.missing).length} called programs`;
  }

  // ----- stops, breakpoints and single step -----------------------------------------------------

  function showNotice(stop) {
    if (!elements.notice) return;
    elements.notice.textContent = stop ? core.stopText(stop) : "";
    elements.notice.hidden = !stop;
    elements.notice.dataset.kind = stop?.kind || "";
  }

  function polylineLength(points) {
    let total = 0;
    for (let index = 1; index < (points?.length || 0); index += 1) {
      const a = points[index - 1];
      const b = points[index];
      total += model.kind === "lathe"
        ? Math.hypot((b.z || 0) - (a.z || 0), ((b.x || 0) - (a.x || 0)) / 2)
        : Math.hypot((b.x || 0) - (a.x || 0), (b.y || 0) - (a.y || 0), (b.z || 0) - (a.z || 0));
    }
    return total;
  }

  function activeIndex() {
    if (!lastPlayback) return playbackState() === "complete" ? segments().length : 0;
    return Math.max(-1, lastPlayback.completedIndex) + 1;
  }

  // The executed row playback stands on outside single step.
  function currentExecution() {
    const state = playbackState();
    if (state === "idle" || !executions.length) return -1;
    if (state === "complete") return executions.length - 1;
    const atStart = !lastPlayback?.active?.points || polylineLength(lastPlayback.active.points) <= 1e-9;
    return core.executionAt(executions, activeIndex(), atStart);
  }

  function seek(seconds) {
    window.cncPreviewSeek?.(seconds);
  }

  function setToolFilter(tool) {
    const value = Number.isFinite(tool) ? String(tool) : undefined;
    if (!value || !elements.toolFilter || elements.toolFilter.value === value) return;
    if (![...elements.toolFilter.options].some((option) => option.value === value)) return;
    elements.toolFilter.value = value;
    elements.toolFilter.dispatchEvent(new Event("change"));
  }

  // Lands single step on row execution `index`: before the row (a breakpoint, about to run) or
  // after it (the row has run, its move is drawn).
  function landOn(index, options = {}) {
    const entry = executions[index];
    if (!entry) return;
    stepping = true;
    stepIndex = index;
    stepBefore = Boolean(options.before);
    const target = core.stepTarget(timeline, segments(), executions, index);
    const seconds = stepBefore
      ? (entry.position < timeline.entries.length ? timeline.entries[entry.position].start : timeline.total)
      : target?.seconds ?? 0;
    passedHalts = core.haltsUpTo(executions, index, halts, units, optionalStop).passed;
    previousActive = entry.position - 1;
    showExecution(index);
    showNotice(options.halt);
    seek(seconds);
    if (options.tool) setToolFilter(target?.tool);
    if (variablesOpen) scheduleVariables(0);
  }

  function step() {
    if (!executions.length) return;
    rowPick = undefined;
    if (stepping && stepBefore) {
      // The row we stopped before runs now.
      landOn(stepIndex, { before: false });
      return;
    }
    const next = (stepping ? stepIndex : currentExecution()) + 1;
    if (next >= executions.length) {
      showNotice(undefined);
      return;
    }
    const { here } = core.haltsUpTo(executions, next, halts, units, optionalStop);
    const breakpoint = here.find((halt) => halt.kind === "BREAK");
    landOn(next, breakpoint ? { before: true, halt: breakpoint } : { before: false, halt: here[0] });
  }

  // A halt while playback runs: the stopped row is shown and single step continues from it.
  function haltAt(stop) {
    const index = core.haltExecution(executions, stop, units);
    if (index >= 0) {
      landOn(index, { before: stop.kind === "BREAK", halt: stop });
      return;
    }
    const entry = timeline.entries[stop.executionIndex];
    if (!entry) return;
    previousActive = stop.executionIndex;
    passedHalts = new Set([stop]);
    seek(entry.start);
    showNotice(stop);
  }

  function leaveStepMode() {
    stepping = false;
    stepIndex = -1;
    stepBefore = false;
    rowPick = undefined;
    markMainRow(undefined);
  }

  function stopPlayback() {
    leaveStepMode();
    passedHalts = new Set();
    previousActive = -1;
    pinnedByUser = false;
    showNotice(undefined);
    if (timeline.entries.length) seek(0);
    previewNextCall(-1);
  }

  function onPlayback(playback) {
    lastPlayback = playback;
    const list = segments();
    if (!playback) {
      previousActive = -1;
      if (!stepping && !pinnedByUser) showCall(undefined, false);
      return;
    }
    const active = Math.min(Math.max(-1, playback.completedIndex) + 1, list.length - 1);
    const running = playbackState() === "running";
    if (running) {
      if (stepping) leaveStepMode();
      pinnedByUser = false;
      // A restart went back to the program start.
      if (active < previousActive) {
        previousActive = -1;
        passedHalts = new Set();
      }
      const stop = core.stopCrossed(halts, previousActive, active, optionalStop, list.length, passedHalts);
      if (stop) {
        haltAt(stop);
        return;
      }
      if (passedHalts.size && [...passedHalts][0].executionIndex < active) passedHalts = new Set();
      if (active !== previousActive) showNotice(undefined);
      previousActive = active;
      showCall(core.callOf(list[active]), true);
    } else if (!stepping) {
      previousActive = active;
      if (!pinnedByUser) showCall(core.callOf(list[active]), playbackState() === "paused");
    }
    if (variablesOpen) scheduleVariables(running ? 300 : 0);
  }

  function setOptionalStop(on) {
    optionalStop = Boolean(on);
    writeStorage(STORAGE_OPTIONAL, optionalStop ? "on" : "off");
    elements.optional.setAttribute("aria-pressed", optionalStop ? "true" : "false");
    elements.optional.textContent = optionalStop ? "Optional stop: On" : "Optional stop: Off";
    elements.optional.classList.toggle("meimad-toggle-on", optionalStop);
  }

  // ----- the tool at the selected row -----------------------------------------------------------

  let rowTimer = 0;
  function scheduleRow(keyOf, fromCallPane) {
    window.clearTimeout(rowTimer);
    rowTimer = window.setTimeout(() => selectRow(keyOf(), fromCallPane), 150);
  }

  // Selecting a row lands single step after that row (the same row again: its next execution).
  function selectRow(key, fromCallPane) {
    if (!key || !elements.follow?.checked || !executions.length) return;
    // A toolpath pick moves the editor cursor to its row; that pick, not the row's first
    // execution, is what the user chose.
    if (playbackState() === "running" || Date.now() - lastEdit < 400 || Date.now() - lastPick < 700) return;
    const indexes = core.rowExecutions(executions, key);
    if (!indexes.length) return;
    const choice = rowPick && rowPick.key === key ? (rowPick.choice + 1) % indexes.length : 0;
    const wasPinned = pinnedByUser;
    landOn(indexes[choice], { before: false, tool: true });
    rowPick = { key, choice };
    if (fromCallPane) pinnedByUser = wasPinned;
  }

  // ----- macro variables window -------------------------------------------------------------------

  function positionText() {
    if (stepping && executions[stepIndex]) {
      const entry = executions[stepIndex];
      const runs = core.rowExecutions(executions, entry.key);
      const times = runs.length > 1 ? ` · execution ${runs.indexOf(stepIndex) + 1} of ${runs.length}` : "";
      return `${stepBefore ? "Before" : "After"} ${core.describeKey(entry.key)}${times}`;
    }
    const segment = segments()[Math.max(0, Math.min(activeIndex(), segments().length - 1))];
    if (!segment) return "Program start";
    return segment.sourceUnit
      ? `Playback at ${segment.sourceUnit} row ${segment.sourceLine} (called from row ${segment.line})`
      : `Playback at row ${segment.line}`;
  }

  function variableContext() {
    if (stepping && executions[stepIndex]) {
      const entry = executions[stepIndex];
      return { position: entry.position, level: entry.level, step: stepBefore ? entry.step - 1 : entry.step };
    }
    const index = Math.max(0, Math.min(activeIndex(), segments().length - 1));
    return { position: activeIndex(), level: segments()[index]?.meimadLocals ?? 1 };
  }

  function sendVariables(open) {
    if (!host?.variables) return;
    host.variables({
      open: Boolean(open),
      position: positionText(),
      rows: core.variablesAt(trace, variableContext())
    });
  }

  function scheduleVariables(delay) {
    if (variablesTimer) return;
    variablesTimer = window.setTimeout(() => {
      variablesTimer = 0;
      if (variablesOpen) sendVariables(false);
    }, delay);
  }

  // ----- model ---------------------------------------------------------------------------------

  function onModel(next) {
    model = next || { segments: [] };
    timeline = core.buildTimeline(model.segments);
    stops = Array.isArray(model.meimadStops) ? model.meimadStops : [];
    units = model.meimadUnits && typeof model.meimadUnits === "object" ? model.meimadUnits : {};
    tree = Array.isArray(model.meimadCallTree) ? model.meimadCallTree : [];
    trace = model.meimadTrace && typeof model.meimadTrace === "object" ? model.meimadTrace : { rows: {}, writes: [], systemReads: [], used: [] };
    executions = core.executionList(trace);
    hasCalls = tree.length > 0 || (model.segments || []).some((segment) => segment.sourceUnit);
    previousActive = -1;
    passedHalts = new Set();
    lastPlayback = undefined;
    pinnedByUser = false;
    leaveStepMode();
    showNotice(undefined);
    attachMainEditor();
    buildMainRanges();
    fillList();
    setPaneVisible(true);
    if (hasCalls) {
      shown = { unit: undefined, line: undefined, text: undefined };
      previewNextCall(-1);
    }
    refreshBreakpoints();
    elements.step.disabled = !executions.length && !timeline.entries.length;
    elements.stop.disabled = !timeline.entries.length;
    if (variablesOpen) scheduleVariables(0);
  }

  function onSelection(line, executionIndex) {
    if (!Number.isInteger(executionIndex)) return;
    lastPick = Date.now();
    const call = core.callOf(segments()[executionIndex]);
    if (call) showCall(call, false);
  }

  function attach(detail) {
    if (!detail || hooks === detail) return;
    hooks = detail;
    hooks.on("model", onModel);
    hooks.on("playback", onPlayback);
    hooks.on("selection", onSelection);
  }

  // ----- splitter ------------------------------------------------------------------------------

  function startDrag(event) {
    event.preventDefault();
    const rect = elements.editorPane.getBoundingClientRect();
    const header = elements.editorPane.querySelector(".pane-header")?.getBoundingClientRect().height || 46;
    const move = (moveEvent) => {
      const available = rect.height - header;
      const percent = ((moveEvent.clientY - rect.top - header) / Math.max(1, available)) * 100;
      writeStorage(STORAGE_SPLIT, String(applySplit(percent)));
      mainEditor()?.refresh();
      callEditor?.refresh();
    };
    const up = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  }

  function keyResize(event) {
    const current = Number(elements.splitter.getAttribute("aria-valuenow")) || 55;
    const delta = event.key === "ArrowUp" ? -5 : event.key === "ArrowDown" ? 5 : 0;
    if (!delta) return;
    event.preventDefault();
    writeStorage(STORAGE_SPLIT, String(applySplit(current + delta)));
    mainEditor()?.refresh();
    callEditor?.refresh();
  }

  applySplit(readStorage(STORAGE_SPLIT) ?? 55);
  setOptionalStop(optionalStop);
  if (elements.follow) {
    elements.follow.checked = readStorage(STORAGE_FOLLOW) !== "off";
    elements.follow.addEventListener("change", () => writeStorage(STORAGE_FOLLOW, elements.follow.checked ? "on" : "off"));
  }
  elements.splitter.addEventListener("pointerdown", startDrag);
  elements.splitter.addEventListener("keydown", keyResize);
  elements.step.addEventListener("click", step);
  elements.stop.addEventListener("click", stopPlayback);
  elements.optional.addEventListener("click", () => setOptionalStop(!optionalStop));
  elements.list?.addEventListener("change", () => {
    if (!elements.list.value) return;
    pinnedByUser = true;
    clearCallMarker();
    if (loadUnit(elements.list.value)) {
      const node = tree.find((candidate) => candidate.name === elements.list.value);
      elements.info.textContent = node ? `Called from ${node.parent === "main" ? "the NC program" : node.parent} row ${node.rows.join(", ")}` : "";
    }
  });
  elements.variables?.addEventListener("click", () => {
    variablesOpen = true;
    sendVariables(true);
  });
  host?.onVariablesClosed?.(() => {
    variablesOpen = false;
  });
  elements.hide?.addEventListener("click", () => {
    userHidden = true;
    setPaneVisible(false);
  });
  elements.show?.addEventListener("click", () => {
    userHidden = false;
    setPaneVisible(true);
  });
  // Run continues after a stop or a step; the notice goes as soon as playback moves on.
  elements.runButton?.addEventListener("click", () => {
    showNotice(undefined);
    pinnedByUser = false;
    leaveStepMode();
  });
  elements.restartButton?.addEventListener("click", () => {
    showNotice(undefined);
    leaveStepMode();
    passedHalts = new Set();
    previousActive = -1;
  });
  window.addEventListener("meimad:viewer3d", (event) => attach(event.detail));
  attach(window.meimadViewer3d);
  attachMainEditor();
  if (elements.editorHost && !mainAttached) {
    new MutationObserver(() => attachMainEditor()).observe(elements.editorHost, { childList: true });
  }
})();
