"use strict";

// Meimad Planner NC viewer: called-program pane and CNC-like playback controls.
//
//   Called program  When the program calls a macro or subprogram (G65/G66/M98/M97, custom macro
//                   calls), the source column splits horizontally: the NC program stays on the
//                   calling row above and the called program below follows playback and the
//                   selected toolpath row. Called programs come from the model (meimadUnits);
//                   a program of the same file shows this file's text.
//   Single step     Runs the next block and stops (the control's single block); an M00/M01/#3006
//                   block is a step of its own.
//   Stop            Ends playback and returns to the program start (reset).
//   Optional stop   M01 stops playback only while it is on; M00 and #3006 always stop.
//
// Stops come from the engine (meimadStops, the segment index they halt before). The rules live
// in meimad-playback-core.js; playback itself stays the vendored preview.js, driven through
// window.cncPreviewSeek and the 3D wrapper's playback/selection events.
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
    hide: byId("meimadCallHide"),
    show: byId("meimadCallShow"),
    textarea: byId("meimadCallEditor"),
    step: byId("meimadStepButton"),
    stop: byId("meimadStopButton"),
    optional: byId("meimadOptionalStopButton"),
    notice: byId("meimadStopNotice"),
    runButton: byId("runButton")
  };
  if (!elements.pane || !elements.textarea || !elements.step) return;

  const STORAGE_SPLIT = "meimad.ncViewer.callSplit";
  const STORAGE_OPTIONAL = "meimad.ncViewer.optionalStop";
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
  let hasCalls = false;
  let userHidden = false;
  let previousActive = -1;
  let lastPlayback;
  let acknowledgedStop;
  let optionalStop = readStorage(STORAGE_OPTIONAL) === "on";
  let callEditor;
  let shown = { unit: undefined, line: undefined, text: undefined };
  let highlighted;

  const mainEditor = () => document.querySelector("#editorHost .CodeMirror")?.CodeMirror;
  const playbackState = () => (typeof window.cncPreviewState === "function" ? window.cncPreviewState().playback : "idle");

  // ----- called-program pane -------------------------------------------------------------------

  function ensureEditor() {
    if (callEditor || !window.CodeMirror?.fromTextArea) return callEditor;
    callEditor = window.CodeMirror.fromTextArea(elements.textarea, {
      mode: "text/x-fanuc-nc",
      theme: "fanuc-dark",
      lineNumbers: true,
      readOnly: true,
      lineWrapping: false,
      gutters: ["CodeMirror-linenumbers"]
    });
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

  // Shows `call` (from core.callOf) or, with no call, keeps the last program without a marker.
  function showCall(call, running) {
    const editor = ensureEditor();
    if (!editor) return;
    if (!call) {
      clearMarker();
      if (shown.unit) elements.info.textContent = running ? "Back in the main program" : "";
      return;
    }
    const text = unitText(call.unit);
    if (text === undefined) return;
    if (shown.unit !== call.unit || shown.text !== text) {
      editor.setValue(text);
      shown = { unit: call.unit, line: undefined, text };
      highlighted = undefined;
    }
    const location = units[call.unit]?.inFile ? "this program" : units[call.unit]?.location || units[call.unit]?.path || "";
    elements.heading.textContent = call.unit;
    elements.heading.title = units[call.unit]?.path || location;
    elements.info.textContent = `Called from row ${call.callerLine} · level ${call.depth}`;
    if (shown.line === call.line) return;
    clearMarker();
    const index = Math.max(0, Math.min(editor.lineCount() - 1, call.line - 1));
    highlighted = editor.addLineClass(index, "background", "cm-playback-line");
    editor.scrollIntoView({ line: index, ch: 0 }, 60);
    shown.line = call.line;
  }

  function firstCall() {
    const segment = (model.segments || []).find((candidate) => candidate.sourceUnit);
    return core.callOf(segment);
  }

  // ----- stops ---------------------------------------------------------------------------------

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

  function currentSeconds() {
    const state = playbackState();
    if (!lastPlayback || state === "idle") return 0;
    if (state === "complete") return timeline.total;
    const active = Math.max(-1, lastPlayback.completedIndex) + 1;
    const full = polylineLength(model.segments?.[active]?.points);
    const fraction = lastPlayback.active?.points && full > 0 ? polylineLength(lastPlayback.active.points) / full : 0;
    return core.secondsAt(timeline, active, fraction);
  }

  function haltAt(stop) {
    const entry = timeline.entries[stop.executionIndex];
    if (!entry) return;
    previousActive = stop.executionIndex;
    acknowledgedStop = stop.executionIndex;
    window.cncPreviewSeek?.(entry.start);
    showNotice(stop);
  }

  function onPlayback(playback) {
    lastPlayback = playback;
    const segments = model.segments || [];
    if (!playback) {
      previousActive = -1;
      showCall(undefined, false);
      return;
    }
    const active = Math.min(Math.max(-1, playback.completedIndex) + 1, segments.length - 1);
    const running = playbackState() === "running";
    if (running) {
      const stop = core.stopCrossed(stops, previousActive, active, optionalStop, segments.length);
      if (stop) {
        haltAt(stop);
        return;
      }
      if (active !== previousActive) showNotice(undefined);
    }
    previousActive = active;
    showCall(core.callOf(segments[active]), running || playbackState() === "paused");
  }

  function step() {
    if (!timeline.entries.length) return;
    const target = core.nextStep(timeline, stops, currentSeconds(), optionalStop, acknowledgedStop);
    if (!target) {
      showNotice(undefined);
      return;
    }
    if (target.stop) {
      haltAt(target.stop);
      return;
    }
    acknowledgedStop = undefined;
    showNotice(undefined);
    previousActive = target.block.last + 1;
    window.cncPreviewSeek?.(target.seconds);
  }

  function stopPlayback() {
    acknowledgedStop = undefined;
    previousActive = -1;
    showNotice(undefined);
    if (timeline.entries.length) window.cncPreviewSeek?.(0);
  }

  function setOptionalStop(on) {
    optionalStop = Boolean(on);
    writeStorage(STORAGE_OPTIONAL, optionalStop ? "on" : "off");
    elements.optional.setAttribute("aria-pressed", optionalStop ? "true" : "false");
    elements.optional.textContent = optionalStop ? "Optional stop: On" : "Optional stop: Off";
    elements.optional.classList.toggle("meimad-toggle-on", optionalStop);
  }

  // ----- model ---------------------------------------------------------------------------------

  function onModel(next) {
    model = next || { segments: [] };
    timeline = core.buildTimeline(model.segments);
    stops = Array.isArray(model.meimadStops) ? model.meimadStops : [];
    units = model.meimadUnits && typeof model.meimadUnits === "object" ? model.meimadUnits : {};
    hasCalls = (model.segments || []).some((segment) => segment.sourceUnit);
    previousActive = -1;
    acknowledgedStop = undefined;
    lastPlayback = undefined;
    showNotice(undefined);
    setPaneVisible(true);
    if (hasCalls) {
      // Until playback enters a call, the pane shows the first called program without a marker.
      shown = { unit: undefined, line: undefined, text: undefined };
      const first = firstCall();
      showCall(first, false);
      clearMarker();
      if (first) elements.info.textContent = `Called from row ${first.callerLine}`;
    }
    elements.step.disabled = !timeline.entries.length;
    elements.stop.disabled = !timeline.entries.length;
  }

  function onSelection(line, executionIndex) {
    const segment = Number.isInteger(executionIndex) ? model.segments?.[executionIndex] : undefined;
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
  elements.splitter.addEventListener("pointerdown", startDrag);
  elements.splitter.addEventListener("keydown", keyResize);
  elements.step.addEventListener("click", step);
  elements.stop.addEventListener("click", stopPlayback);
  elements.optional.addEventListener("click", () => setOptionalStop(!optionalStop));
  elements.hide?.addEventListener("click", () => {
    userHidden = true;
    setPaneVisible(false);
  });
  elements.show?.addEventListener("click", () => {
    userHidden = false;
    setPaneVisible(true);
  });
  // Run continues after a stop; the notice goes as soon as playback moves on.
  elements.runButton?.addEventListener("click", () => showNotice(undefined));
  window.addEventListener("meimad:viewer3d", (event) => attach(event.detail));
  attach(window.meimadViewer3d);
})();
