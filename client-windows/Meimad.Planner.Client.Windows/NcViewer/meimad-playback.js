"use strict";

// Meimad Planner NC viewer: called programs, breakpoints, CNC-like playback controls, the tool at the
// selected row and the macro variables window.
//
//   Called programs  When the program calls macros or subprograms, the source column splits
//                    horizontally: the NC program stays on the calling row above and the called
//                    program below follows playback. The list above it shows every program the
//                    program calls at any depth (model meimadCallTree: programs of the same file,
//                    the program's folder with the release's subprograms, and the machine's
//                    program memory folder with Renishaw and other macros).
//   Breakpoints      A click in the breakpoint gutter of either editor toggles a breakpoint on the
//                    row; playback stops before every execution of the row.
//   Single step      Runs the next block and stops; a stop or breakpoint is a step of its own.
//   Stop             Ends playback and returns to the program start (reset).
//   Optional stop    M01 stops playback only while it is on; M00, #3006 and #3000 always stop.
//   Selected row     With "Tool follows row" on, selecting a row puts the tool where that row runs
//                    (the end of its move, or where the tool is when a macro statement runs; the
//                    same row again goes to its next execution) and shows only that tool's path.
//   Macro variables  Opens a window (hosted by the application) with the value of every variable
//                    the program uses at the playback position or the selected row.
//
// The rules live in meimad-playback-core.js; playback itself stays the vendored preview.js, driven
// through window.cncPreviewSeek and the 3D wrapper's model, playback and selection events.
(() => {
  const core = window.MeimadPlaybackCore;
  if (!core) return;
  const byId = (id) => document.getElementById(id);
  const elements = {
    editorPane: byId("editorPane"),
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
    toolFilter: byId("toolFilter")
  };
  if (!elements.pane || !elements.textarea || !elements.step) return;
  const host = window.fanucDesktop?.meimad;

  const STORAGE_SPLIT = "meimad.ncViewer.callSplit";
  const STORAGE_OPTIONAL = "meimad.ncViewer.optionalStop";
  const STORAGE_FOLLOW = "meimad.ncViewer.followRow";
  const GUTTER = "meimad-breakpoint-gutter";
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
  const breakpoints = new Set();
  let halts = [];
  let hasCalls = false;
  let userHidden = false;
  let previousActive = -1;
  let lastPlayback;
  let acknowledgedStop;
  let optionalStop = readStorage(STORAGE_OPTIONAL) === "on";
  let callEditor;
  let mainAttached;
  let shown = { unit: undefined, line: undefined, text: undefined };
  let highlighted;
  let pinnedByUser = false;
  let rowTarget;
  let lastEdit = 0;
  let variablesOpen = false;
  let variablesTimer = 0;
  let lastPick = 0;

  const mainEditor = () => document.querySelector("#editorHost .CodeMirror")?.CodeMirror;
  const playbackState = () => (typeof window.cncPreviewState === "function" ? window.cncPreviewState().playback : "idle");
  const segments = () => model.segments || [];

  // ----- breakpoints ---------------------------------------------------------------------------

  const mainKey = (line) => core.keyOf("main", line + 1, false);
  const callKey = (line) => (shown.unit ? core.keyOf(shown.unit, line + 1, Boolean(units[shown.unit]?.inFile)) : undefined);

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

  function addGutter(editor) {
    const gutters = editor.getOption("gutters") || [];
    if (!gutters.includes(GUTTER)) editor.setOption("gutters", [GUTTER, ...gutters]);
  }

  // The renderer creates the NC program editor; it is wired once it exists.
  function attachMainEditor() {
    const editor = mainEditor();
    if (!editor || mainAttached === editor) return;
    mainAttached = editor;
    addGutter(editor);
    editor.on("gutterClick", (cm, line, gutter) => {
      if (gutter === GUTTER) toggleBreakpoint(mainKey(line));
    });
    editor.on("changes", () => {
      lastEdit = Date.now();
    });
    editor.on("cursorActivity", (cm) => scheduleRow(() => mainKey(cm.getCursor().line), false));
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
    callEditor.on("gutterClick", (cm, line, gutter) => {
      if (gutter === GUTTER) toggleBreakpoint(callKey(line));
    });
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
    const unit = units[name];
    if (!unit) return undefined;
    if (unit.inFile) return mainEditor()?.getValue() ?? "";
    return typeof unit.text === "string" ? unit.text : undefined;
  }

  function clearMarker() {
    if (highlighted !== undefined) callEditor?.removeLineClass(highlighted, "background", "cm-playback-line");
    highlighted = undefined;
    shown.line = undefined;
  }

  // Loads a program into the lower editor (without a row marker).
  function loadUnit(name) {
    const editor = ensureEditor();
    const text = unitText(name);
    if (!editor || text === undefined) return false;
    if (shown.unit !== name || shown.text !== text) {
      editor.setValue(text);
      shown = { unit: name, line: undefined, text };
      highlighted = undefined;
      renderMarkers(editor, callKey);
    }
    const entry = units[name];
    elements.heading.textContent = name;
    elements.heading.title = entry?.path || entry?.location || "";
    if (elements.list && [...elements.list.options].some((option) => option.value === name)) elements.list.value = name;
    return true;
  }

  // Shows `call` (from core.callOf) or, with no call, keeps the last program without a marker.
  function showCall(call, running) {
    if (!ensureEditor()) return;
    if (!call) {
      clearMarker();
      if (shown.unit) elements.info.textContent = running ? "Back in the main program" : "";
      return;
    }
    if (!loadUnit(call.unit)) return;
    elements.info.textContent = `Called from row ${call.callerLine} · level ${call.depth}`;
    if (shown.line === call.line) return;
    clearMarker();
    const index = Math.max(0, Math.min(callEditor.lineCount() - 1, call.line - 1));
    highlighted = callEditor.addLineClass(index, "background", "cm-playback-line");
    callEditor.scrollIntoView({ line: index, ch: 0 }, 60);
    shown.line = call.line;
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

  function currentSeconds() {
    const state = playbackState();
    if (!lastPlayback || state === "idle") return 0;
    if (state === "complete") return timeline.total;
    const active = activeIndex();
    const full = polylineLength(segments()[active]?.points);
    const fraction = lastPlayback.active?.points && full > 0 ? polylineLength(lastPlayback.active.points) / full : 0;
    return core.secondsAt(timeline, active, fraction);
  }

  function seek(seconds) {
    window.cncPreviewSeek?.(seconds);
  }

  function haltAt(stop) {
    const entry = timeline.entries[stop.executionIndex];
    if (!entry) return;
    previousActive = stop.executionIndex;
    acknowledgedStop = stop.executionIndex;
    seek(entry.start);
    showNotice(stop);
  }

  function onPlayback(playback) {
    lastPlayback = playback;
    const list = segments();
    if (!playback) {
      previousActive = -1;
      if (!pinnedByUser) showCall(undefined, false);
      return;
    }
    const active = Math.min(Math.max(-1, playback.completedIndex) + 1, list.length - 1);
    const running = playbackState() === "running";
    if (running) {
      pinnedByUser = false;
      const stop = core.stopCrossed(halts, previousActive, active, optionalStop, list.length);
      if (stop) {
        haltAt(stop);
        return;
      }
      if (active !== previousActive) showNotice(undefined);
    }
    previousActive = active;
    if (!pinnedByUser) showCall(core.callOf(list[active]), running || playbackState() === "paused");
    if (variablesOpen) scheduleVariables(running ? 300 : 0);
  }

  function step() {
    if (!timeline.entries.length) return;
    const target = core.nextStep(timeline, halts, currentSeconds(), optionalStop, acknowledgedStop);
    if (!target) {
      showNotice(undefined);
      return;
    }
    pinnedByUser = false;
    rowTarget = undefined;
    if (target.stop) {
      haltAt(target.stop);
      return;
    }
    acknowledgedStop = undefined;
    showNotice(undefined);
    previousActive = target.block.last + 1;
    seek(target.seconds);
  }

  function stopPlayback() {
    acknowledgedStop = undefined;
    previousActive = -1;
    pinnedByUser = false;
    rowTarget = undefined;
    showNotice(undefined);
    if (timeline.entries.length) seek(0);
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

  function selectRow(key, fromCallPane) {
    if (!key || !elements.follow?.checked) return;
    // A toolpath pick moves the editor cursor to its row; that pick, not the row's first
    // execution, is what the user chose.
    if (playbackState() === "running" || Date.now() - lastEdit < 400 || Date.now() - lastPick < 700) return;
    const target = core.rowTarget(timeline, segments(), trace, units, key, rowTarget);
    if (!target) return;
    rowTarget = target;
    pinnedByUser = fromCallPane;
    acknowledgedStop = undefined;
    showNotice(undefined);
    previousActive = Math.min(target.position, segments().length - 1);
    seek(target.seconds);
    const tool = Number.isFinite(target.tool) ? String(target.tool) : undefined;
    if (tool && elements.toolFilter && elements.toolFilter.value !== tool &&
        [...elements.toolFilter.options].some((option) => option.value === tool)) {
      elements.toolFilter.value = tool;
      elements.toolFilter.dispatchEvent(new Event("change"));
    }
    if (variablesOpen) scheduleVariables(0);
  }

  // ----- macro variables window -------------------------------------------------------------------

  function positionText() {
    if (rowTarget && playbackState() !== "running") {
      const times = rowTarget.count > 1 ? ` · execution ${rowTarget.choice + 1} of ${rowTarget.count}` : "";
      return `Selected ${core.describeKey(rowTarget.key)}${times}`;
    }
    const segment = segments()[Math.max(0, Math.min(activeIndex(), segments().length - 1))];
    if (!segment) return "Program start";
    return segment.sourceUnit
      ? `Playback at ${segment.sourceUnit} row ${segment.sourceLine} (called from row ${segment.line})`
      : `Playback at row ${segment.line}`;
  }

  function variableContext() {
    if (rowTarget && playbackState() !== "running") {
      return { position: rowTarget.position, level: rowTarget.level, step: rowTarget.step };
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
    hasCalls = tree.length > 0 || (model.segments || []).some((segment) => segment.sourceUnit);
    previousActive = -1;
    acknowledgedStop = undefined;
    lastPlayback = undefined;
    rowTarget = undefined;
    pinnedByUser = false;
    showNotice(undefined);
    attachMainEditor();
    fillList();
    setPaneVisible(true);
    if (hasCalls) {
      // Until playback enters a call, the pane shows the first called program without a marker.
      shown = { unit: undefined, line: undefined, text: undefined };
      const first = core.callOf((model.segments || []).find((segment) => segment.sourceUnit));
      const name = first?.unit || tree.find((node) => !node.missing)?.name;
      if (name && loadUnit(name)) elements.info.textContent = first ? `Called from row ${first.callerLine}` : "";
    }
    refreshBreakpoints();
    elements.step.disabled = !timeline.entries.length;
    elements.stop.disabled = !timeline.entries.length;
    if (variablesOpen) scheduleVariables(0);
  }

  function onSelection(line, executionIndex) {
    if (Number.isInteger(executionIndex)) lastPick = Date.now();
    const segment = Number.isInteger(executionIndex) ? segments()[executionIndex] : undefined;
    const call = core.callOf(segment);
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
    clearMarker();
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
  // Run continues after a stop; the notice goes as soon as playback moves on.
  elements.runButton?.addEventListener("click", () => {
    showNotice(undefined);
    pinnedByUser = false;
    rowTarget = undefined;
  });
  window.addEventListener("meimad:viewer3d", (event) => attach(event.detail));
  attach(window.meimadViewer3d);
  attachMainEditor();
})();
