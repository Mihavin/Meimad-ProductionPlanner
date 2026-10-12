"use strict";

// Meimad Planner: the NC viewer's operator dashboard (nc-viewer visualization spec, "Dashboard view").
//   - The operation card: the Case picture (a placeholder without one), Part Number, Case name,
//     Operation, Machine, the active tool and one main result with an icon and a word (Ready,
//     Running, Paused, Collision, Gouge, Completed, Error) - color repeats it, never replaces it.
//   - Icon buttons with tooltips and shortcuts: Play/Pause (Space), Reset (R), Single step (S),
//     Follow tool (T), Toolpath visibility (P cycles), Machine view (M), Collisions (C), Details (D:
//     shows or hides the technical panels).
//   - The information strip on the 3D view: tool, row, progress, collisions, gouges, removed volume.
//   - Operator mode hides the editor and the technical panels. It starts on for a release opened
//     read-only and off for a program being edited; the choice is remembered on this PC.
// The picture is read once per opened Operation; nothing here recalculates the material simulation.
(() => {
  const desktop = window.fanucDesktop;
  const byId = (id) => document.getElementById(id);
  const elements = {
    card: byId("meimadOperatorCard"),
    picture: byId("meimadOperatorPicture"),
    placeholder: byId("meimadOperatorPlaceholder"),
    part: byId("meimadOperatorPart"),
    caseName: byId("meimadOperatorCase"),
    operation: byId("meimadOperatorOperation"),
    machine: byId("meimadOperatorMachine"),
    status: byId("meimadOperatorStatus"),
    tool: byId("meimadOperatorTool"),
    strip: byId("meimadInfoStrip"),
    play: byId("meimadOpPlay"),
    reset: byId("meimadOpReset"),
    step: byId("meimadOpStep"),
    follow: byId("meimadOpFollow"),
    path: byId("meimadOpPath"),
    machineView: byId("meimadOpMachine"),
    collisions: byId("meimadOpCollisions"),
    details: byId("meimadOpDetails"),
    runButton: byId("runButton"),
    pauseButton: byId("pauseButton"),
    restartButton: byId("restartButton"),
    stepButton: byId("meimadStepButton"),
    pathMode: byId("meimadPathMode"),
    frameSelect: byId("frameSelect"),
    verifyCard: byId("meimadVerifyCard")
  };
  if (!elements.card) return;

  const MODE_KEY = "meimad.ncViewer.operatorMode";
  const read = (key) => { try { return window.localStorage?.getItem(key) ?? null; } catch { return null; } };
  const write = (key, value) => { try { window.localStorage?.setItem(key, value); } catch { /* not remembered */ } };

  let hooks = null;
  let model = null;
  let playback = null;
  let verification = { events: [], stats: null };
  let readOnly = null;
  let operatorMode = false;

  // ----- card ---------------------------------------------------------------------------------

  function showCard(card, operationTitle) {
    const value = card || {};
    elements.card.hidden = false;
    if (value.picture) {
      elements.picture.src = value.picture;
      elements.picture.alt = value.partNumber ? `Part ${value.partNumber}` : "Part";
      elements.picture.hidden = false;
      elements.placeholder.hidden = true;
    } else {
      elements.picture.hidden = true;
      elements.placeholder.hidden = false;
      elements.placeholder.textContent = value.partNumber || "—";
    }
    elements.part.textContent = value.partNumber || operationTitle || "Program";
    elements.caseName.textContent = value.caseName || "";
    elements.caseName.hidden = !value.caseName;
    const number = Number.isFinite(value.operationNumber) ? `OP${String(value.operationNumber).padStart(2, "0")}` : "";
    elements.operation.textContent = [number, value.operationName].filter(Boolean).join(" ") || "";
    elements.operation.hidden = !elements.operation.textContent;
    elements.machine.textContent = value.machineName ? `Machine ${value.machineName}` : "";
    elements.machine.hidden = !value.machineName;
    refresh();
  }

  // ----- status and the information strip -------------------------------------------------------

  const STATUS = {
    ready: ["◯", "Ready"],
    running: ["▶", "Running"],
    paused: ["⏸", "Paused"],
    collision: ["⛔", "Collision"],
    gouge: ["⛔", "Gouge"],
    completed: ["✔", "Completed"],
    error: ["⚠", "Error"]
  };

  function playbackState() {
    return typeof window.cncPreviewState === "function" ? window.cncPreviewState().playback : "idle";
  }

  function mainResult() {
    const errors = byId("errorCard");
    if (errors && !errors.hidden && byId("errors")?.children.length) return "error";
    const events = verification.events || [];
    if (events.some((event) => event.check === "gouge" && event.severity !== "warning")) return "gouge";
    if (events.some((event) => event.check !== "gouge" && (event.severity === "error" || event.severity === "stop"))) return "collision";
    const state = playbackState();
    if (state === "running") return "running";
    if (state === "paused" || state === "stopped") return "paused";
    if (state === "complete") return "completed";
    return "ready";
  }

  function activeSegment() {
    const segments = model?.segments || [];
    if (!segments.length) return null;
    if (playback && Number.isFinite(playback.completedIndex)) return segments[Math.min(segments.length - 1, Math.max(0, playback.completedIndex + 1))];
    return null;
  }

  function refresh() {
    const result = mainResult();
    const [icon, word] = STATUS[result];
    elements.status.textContent = `${icon} ${word}`;
    elements.status.className = `meimad-operator-status status-${result}`;
    const segment = activeSegment();
    const total = (model?.segments || []).length;
    const done = playback && Number.isFinite(playback.completedIndex) ? playback.completedIndex + 1 : playbackState() === "complete" ? total : 0;
    elements.tool.textContent = segment ? `Tool T${segment.tool}` : "";
    elements.tool.hidden = !segment;
    if (elements.strip) {
      const stats = verification.stats;
      const parts = [];
      if (segment) {
        parts.push(`Tool T${segment.tool}`);
        parts.push(segment.sourceUnit ? `${segment.sourceUnit} row ${segment.sourceLine ?? "?"}` : `Row ${segment.line ?? "?"}`);
      }
      parts.push(`Progress ${total ? Math.round((done / total) * 100) : 0}%`);
      if (stats) {
        parts.push(`Collisions ${stats.collisions}`);
        parts.push(`Gouges ${stats.gouges}`);
        parts.push(`Removed ${Math.round(stats.removedVolume).toLocaleString()} mm³`);
      }
      elements.strip.textContent = parts.join("   ·   ");
    }
    if (elements.play) {
      const running = playbackState() === "running";
      elements.play.textContent = running ? "⏸" : "▶";
      elements.play.title = running ? "Pause (Space)" : "Play (Space)";
      elements.play.setAttribute("aria-label", running ? "Pause" : "Play");
    }
    if (elements.collisions) {
      const count = (verification.events || []).length;
      elements.collisions.textContent = count ? `⚠ ${count}` : "⚠";
      elements.collisions.title = count ? `Collisions and gouges: ${count} (C)` : "Collisions and gouges: none (C)";
    }
  }

  // ----- operator mode --------------------------------------------------------------------------

  function setOperatorMode(value, remember) {
    operatorMode = Boolean(value);
    document.body.classList.toggle("meimad-operator-mode", operatorMode);
    if (elements.details) {
      elements.details.setAttribute("aria-pressed", operatorMode ? "false" : "true");
      elements.details.title = operatorMode ? "Details: show the program and the technical panels (D)" : "Details: hide the program and the technical panels (D)";
    }
    if (remember) write(MODE_KEY, operatorMode ? "on" : "off");
    // The 3D view changes size with the layout.
    window.dispatchEvent(new Event("resize"));
  }

  // ----- buttons ------------------------------------------------------------------------------------

  const click = (button) => { if (button && !button.disabled) button.click(); };

  function cyclePath() {
    const select = elements.pathMode;
    if (!select || select.disabled) return;
    const order = ["played", "split", "remaining", "tool", "all", "hidden"];
    const next = order[(order.indexOf(select.value) + 1) % order.length];
    select.value = next;
    select.dispatchEvent(new Event("change"));
    window.meimadStatus?.(`Toolpath: ${select.options[select.selectedIndex]?.textContent || next}`);
  }

  function toggleMachineView() {
    const select = elements.frameSelect;
    if (!select || select.closest("[hidden]")) return;
    select.value = select.value === "machine" ? "part" : "machine";
    select.dispatchEvent(new Event("change"));
  }

  function showCollisions() {
    if (operatorMode) setOperatorMode(false, false);
    elements.verifyCard?.scrollIntoView({ block: "nearest" });
    byId("meimadEvents")?.querySelector("li")?.focus();
  }

  const actions = {
    play: () => click(playbackState() === "running" ? elements.pauseButton : elements.runButton),
    reset: () => click(elements.restartButton),
    step: () => click(elements.stepButton),
    follow: () => window.meimadSceneFlags?.setFollowing(!window.meimadSceneFlags.following()),
    path: cyclePath,
    machine: toggleMachineView,
    collisions: showCollisions,
    details: () => setOperatorMode(!operatorMode, true)
  };
  const bind = (element, action) => element?.addEventListener("click", () => {
    action();
    setTimeout(refresh, 50);
  });
  bind(elements.play, actions.play);
  bind(elements.reset, actions.reset);
  bind(elements.step, actions.step);
  bind(elements.follow, actions.follow);
  bind(elements.path, actions.path);
  bind(elements.machineView, actions.machine);
  bind(elements.collisions, actions.collisions);
  bind(elements.details, actions.details);

  window.addEventListener("keydown", (event) => {
    if (event.ctrlKey || event.altKey || event.metaKey) return;
    if (event.target && /^(INPUT|SELECT|TEXTAREA|BUTTON)$/.test(event.target.tagName)) return;
    if (event.target && event.target.closest && event.target.closest(".CodeMirror")) return;
    const key = event.key;
    const action = key === " " ? actions.play : key === "r" || key === "R" ? actions.reset : key === "s" || key === "S" ? actions.step
      : key === "p" || key === "P" ? actions.path : key === "m" || key === "M" ? actions.machine : key === "c" || key === "C" ? actions.collisions
        : key === "d" || key === "D" ? actions.details : null;
    if (!action) return;
    event.preventDefault();
    action();
    setTimeout(refresh, 50);
  });

  window.addEventListener("meimad:follow", (event) => {
    const on = Boolean(event.detail && event.detail.following);
    elements.follow?.setAttribute("aria-pressed", on ? "true" : "false");
    elements.follow?.classList.toggle("active", on);
  });

  // ----- wiring ---------------------------------------------------------------------------------------

  function attach(value) {
    if (!value || hooks === value || !value.captured) return;
    hooks = value;
    hooks.on("modelApplied", (next) => {
      model = next || null;
      refresh();
    });
    hooks.on("playback", (next) => {
      playback = next || null;
      refresh();
    });
  }
  if (window.meimadViewer3d) attach(window.meimadViewer3d);
  window.addEventListener("meimad:viewer3d", (event) => attach(event.detail));

  window.addEventListener("meimad:scene-loaded", (event) => showCard(event.detail && event.detail.card, event.detail && event.detail.operationTitle));
  window.addEventListener("meimad:verification", (event) => {
    verification = event.detail || verification;
    refresh();
  });
  // The playback state changes without an event of its own (run, pause, end): refresh while visible.
  setInterval(() => { if (!document.hidden) refresh(); }, 500);

  // Operator mode: remembered on this PC, else on for a read-only release.
  const remembered = read(MODE_KEY);
  if (remembered === "on" || remembered === "off") setOperatorMode(remembered === "on", false);
  const onDocument = (state) => {
    if (!state || typeof state.readOnly !== "boolean" || readOnly === state.readOnly) return;
    readOnly = state.readOnly;
    if (read(MODE_KEY) === null) setOperatorMode(readOnly, false);
  };
  desktop?.onDocumentState?.(onDocument);
  showCard(window.meimadSceneGeometry?.card?.() || null, window.meimadSceneGeometry?.operationTitle?.());
})();
