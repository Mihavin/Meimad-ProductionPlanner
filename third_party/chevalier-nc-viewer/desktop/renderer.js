"use strict";

(() => {
  // Renderer/preload contract: preload owns all filesystem and parser work. It
  // exposes the methods used below as window.fanucDesktop and forwards the
  // existing preview's acquireVsCodeApi().postMessage calls to the main process.
  // Render/selection messages are converted back to DOM MessageEvents because
  // media/preview.js intentionally remains shared with the VS Code extension.
  const desktop = window.fanucDesktop || {};
  const byId = (id) => document.getElementById(id);

  const elements = {
    workspace: byId("workspace"),
    editorPane: byId("editorPane"),
    editorHost: byId("editorHost"),
    editorTextArea: byId("ncEditor"),
    splitter: byId("workspaceSplitter"),
    documentIdentity: document.querySelector(".document-identity"),
    documentName: byId("editorDocumentName"),
    dirtyIndicator: byId("dirtyIndicator"),
    lineCount: byId("editorLineCount"),
    editorEncoding: byId("editorEncoding"),
    newFile: byId("newFileButton"),
    openFile: byId("openFileButton"),
    saveFile: byId("saveFileButton"),
    saveAs: byId("saveAsButton"),
    showToolTable: byId("showToolTableButton"),
    codexToggle: byId("codexToggleButton"),
    showSettings: byId("showSettingsButton"),
    statusState: byId("statusState"),
    statusMessage: byId("statusMessage"),
    statusXml: byId("statusXml"),
    statusPath: byId("statusPath"),
    statusPosition: byId("statusPosition"),
    settingsModal: byId("settingsModal"),
    settingsForm: byId("settingsForm"),
    settingsClose: byId("settingsCloseButton"),
    settingsCancel: byId("settingsCancelButton"),
    settingsSave: byId("settingsSaveButton"),
    settingsFeedback: byId("settingsFeedback"),
    settingsG30X: byId("settingsG30X"),
    settingsG30Z: byId("settingsG30Z"),
    settingsInitialVariables: byId("settingsInitialVariables"),
    settingsMachineFile: byId("settingsMachineFile"),
    settingsToolFile: byId("settingsToolFile"),
    settingsAiEnabled: byId("settingsAiEnabled"),
    settingsAiModel: byId("settingsAiModel"),
    settingsAiDebounce: byId("settingsAiDebounce"),
    settingsApiKey: byId("settingsApiKey"),
    settingsApiKeyStatus: byId("settingsApiKeyStatus"),
    toggleApiKey: byId("toggleApiKeyButton"),
    clearApiKey: byId("clearApiKeyButton"),
    toolTableModal: byId("toolTableModal"),
    toolTableForm: byId("toolTableForm"),
    toolTableClose: byId("toolTableCloseButton"),
    toolTableCancel: byId("toolTableCancelButton"),
    toolTableSave: byId("toolTableSaveButton"),
    toolTableReveal: byId("toolTableRevealButton"),
    toolTableFeedback: byId("toolTableFeedback"),
    toolTableEditorSource: byId("toolTableEditorSource"),
    toolTableName: byId("toolTableName"),
    toolTableUnits: byId("toolTableUnits"),
    toolEditorRows: byId("toolEditorRows"),
    toolEditorHead: byId("toolEditorHead"),
    toolEditorEmpty: byId("toolEditorEmpty"),
    settingsMachine: byId("settingsMachine"),
    settingsProgramMemory: byId("settingsProgramMemory"),
    codexPanel: byId("codexPanel"),
    codexClose: byId("codexCloseButton"),
    codexScroll: byId("codexScroll"),
    codexTranscript: byId("codexTranscript"),
    codexEmptyState: byId("codexEmptyState"),
    codexSelectedReferences: byId("codexSelectedReferences"),
    codexUsedReferences: byId("codexUsedReferences"),
    codexSelectReferenceWorkspace: byId("codexSelectReferenceWorkspaceButton"),
    codexClearReferenceWorkspace: byId("codexClearReferenceWorkspaceButton"),
    codexReferenceWorkspaceLabel: byId("codexReferenceWorkspaceLabel"),
    codexReferenceWorkspaceStatus: byId("codexReferenceWorkspaceStatus"),
    codexAttachFile: byId("codexAttachFileButton"),
    codexClearAttachments: byId("codexClearAttachmentsButton"),
    codexAttachmentList: byId("codexAttachmentList"),
    codexAttachmentStatus: byId("codexAttachmentStatus"),
    codexProposalCard: byId("codexProposalCard"),
    codexProposalSummary: byId("codexProposalSummary"),
    codexProposalProgram: byId("codexProposalProgram"),
    codexProposalAssumptions: byId("codexProposalAssumptions"),
    codexProposalWarnings: byId("codexProposalWarnings"),
    codexProposalStatus: byId("codexProposalStatus"),
    codexApplyProposal: byId("codexApplyProposalButton"),
    codexDiscardProposal: byId("codexDiscardProposalButton"),
    codexForm: byId("codexForm"),
    codexPrompt: byId("codexPrompt"),
    codexNewChat: byId("codexNewChatButton"),
    codexStop: byId("codexStopButton"),
    codexSend: byId("codexSendButton")
  };

  let editor;
  let applyingDocumentState = false;
  let suppressSelectionUpdate = false;
  let lastSelectionLine = 1;
  let selectedLineHandle;
  let documentRevision = 0;
  let pendingDocumentUpdate = false;
  let documentUpdateTimer;
  let documentState = {
    name: "Untitled.NC",
    filePath: "",
    text: "",
    dirty: false,
    selectedLine: 1,
    toolTablePath: "",
    machineParameterPath: ""
  };
  let settingsState = { keyConfigured: false };
  let decorationState = { errorLines: [], playbackLine: undefined };
  let markedErrorLines = new Set();
  let markedPlaybackLine;
  let codexConversation = [];
  let codexRequestSequence = 0;
  let codexRequestBusy = false;
  let codexStopRequested = false;
  let codexContextBusy = false;
  let codexReferenceWorkspace;
  let codexAttachments = [];
  let currentCodexProposal;
  const disposers = [];

  const MAX_CODEX_HISTORY_ITEMS = 12;
  const MAX_CODEX_HISTORY_CHARACTERS = 24000;
  const MAX_CODEX_HISTORY_ITEM_CHARACTERS = 6000;
  const MAX_CODEX_PROGRAM_PREVIEW_CHARACTERS = 18000;
  const TOOL_TYPES = [
    "external-cutter",
    "internal-cutter",
    "drill",
    "groove",
    "cutoff",
    "threading",
    "tap",
    "reamer",
    "bar-puller",
    "non-cutting",
    "other"
  ];
  const TOOL_HANDS = ["right", "left", "neutral", "unknown"];

  function isFunction(value) {
    return typeof value === "function";
  }

  function errorMessage(error) {
    if (typeof error === "string") {
      return error;
    }
    return error?.message || "The desktop operation could not be completed.";
  }

  function callDesktop(method, ...args) {
    if (!isFunction(desktop[method])) {
      return Promise.reject(new Error(`Desktop bridge method ${method}() is unavailable.`));
    }
    // Every request works on the current text: send pending edits first
    // (IPC keeps the order).
    if (method !== "updateSelection") {
      flushDocumentUpdate();
    }
    try {
      return Promise.resolve(desktop[method](...args));
    } catch (error) {
      return Promise.reject(error);
    }
  }

  function subscribe(method, callback) {
    if (!isFunction(desktop[method])) {
      return false;
    }
    try {
      const dispose = desktop[method](callback);
      if (isFunction(dispose)) {
        disposers.push(dispose);
      }
      return true;
    } catch (error) {
      reportStatus(errorMessage(error), "error");
      return false;
    }
  }

  function reportStatus(message, level = "ready", label) {
    const normalizedLevel = ["ready", "success", "working", "warning", "error"]
      .includes(level)
      ? level
      : "ready";
    const stateLabel = label || {
      ready: "Ready",
      success: "Saved",
      working: "Working",
      warning: "Review",
      error: "Error"
    }[normalizedLevel];
    elements.statusState.className = `status-state ${normalizedLevel}`;
    elements.statusState.querySelector("span").textContent = stateLabel;
    elements.statusMessage.textContent = message || "Ready";
  }

  function applyStatus(status) {
    if (typeof status === "string") {
      reportStatus(status, "ready");
      return;
    }
    if (!status || typeof status !== "object") {
      return;
    }
    const rawLevel = status.level || status.state || (status.busy ? "working" : "ready");
    const level = rawLevel === "busy"
      ? "working"
      : rawLevel === "info"
        ? "ready"
        : rawLevel;
    reportStatus(status.message || status.text || "Ready", level, status.label);
  }

  function editorValue() {
    return editor ? editor.getValue() : elements.editorTextArea.value;
  }

  function editorLineCount() {
    if (editor) {
      return Math.max(1, editor.lineCount());
    }
    return Math.max(1, elements.editorTextArea.value.split(/\r?\n/).length);
  }

  function editorCursor() {
    if (editor) {
      const cursor = editor.getCursor();
      return { line: cursor.line + 1, column: cursor.ch + 1 };
    }
    const text = elements.editorTextArea.value;
    const before = text.slice(0, elements.editorTextArea.selectionStart || 0);
    const lines = before.split(/\r?\n/);
    return { line: lines.length, column: lines[lines.length - 1].length + 1 };
  }

  function updateEditorMetrics() {
    const count = editorLineCount();
    const cursor = editorCursor();
    elements.lineCount.textContent = `${count.toLocaleString()} ${count === 1 ? "line" : "lines"}`;
    elements.statusPosition.textContent = `Ln ${cursor.line}, Col ${cursor.column}`;
    lastSelectionLine = cursor.line;
    markSelectedSourceLine(cursor.line);
  }

  function setEditorText(text) {
    const value = String(text ?? "");
    if (editor) {
      if (editor.getValue() !== value) {
        editor.setValue(value);
        editor.clearHistory();
      }
    } else if (elements.editorTextArea.value !== value) {
      elements.editorTextArea.value = value;
    }
    updateEditorMetrics();
  }

  function markSelectedSourceLine(lineNumber) {
    if (!editor) {
      return;
    }
    const lineIndex = Math.max(0, Math.min(editor.lineCount() - 1, Math.trunc(lineNumber) - 1));
    editor.operation(() => {
      if (selectedLineHandle) {
        editor.removeLineClass(selectedLineHandle, "background", "cm-source-selected-line");
      }
      selectedLineHandle = editor.addLineClass(
        lineIndex,
        "background",
        "cm-source-selected-line"
      );
    });
  }

  function setEditorSelection(lineNumber, reveal = true) {
    const numericLine = Number(lineNumber);
    if (!Number.isFinite(numericLine)) {
      return;
    }
    const lineIndex = Math.max(0, Math.min(editorLineCount() - 1, Math.trunc(numericLine) - 1));
    const current = editorCursor();
    suppressSelectionUpdate = true;
    if (editor) {
      if (current.line !== lineIndex + 1) {
        editor.setCursor({ line: lineIndex, ch: 0 });
      }
      markSelectedSourceLine(lineIndex + 1);
      if (reveal) {
        editor.scrollIntoView({ line: lineIndex, ch: 0 }, 90);
      }
    } else {
      const lines = elements.editorTextArea.value.split(/\r?\n/);
      const start = lines.slice(0, lineIndex).reduce((total, line) => total + line.length + 1, 0);
      elements.editorTextArea.setSelectionRange(start, start);
    }
    lastSelectionLine = lineIndex + 1;
    documentState.selectedLine = lineIndex + 1;
    updateEditorMetrics();
    window.setTimeout(() => {
      suppressSelectionUpdate = false;
    }, 0);
  }

  function dispatchPreviewMessage(message) {
    if (!message) {
      return;
    }
    window.dispatchEvent(new MessageEvent("message", { data: message }));
  }

  function updateDocumentChrome() {
    const name = documentState.name || basename(documentState.filePath) || "Untitled.NC";
    elements.documentName.textContent = name;
    elements.documentIdentity.classList.toggle("dirty", Boolean(documentState.dirty));
    elements.dirtyIndicator.setAttribute(
      "aria-label",
      documentState.dirty ? "Document has unsaved changes" : "Document is saved"
    );
    elements.statusPath.textContent = documentState.filePath || "Not saved";
    elements.statusPath.title = documentState.filePath || "Current document has not been saved";
    elements.editorEncoding.textContent = [
      documentState.encoding || "UTF-8",
      documentState.eol || "CRLF"
    ].join(" · ");
    elements.saveFile.disabled = false;
    document.title = `${documentState.dirty ? "*" : ""}${name} - CNC Toolpath Preview`;

    // Mills show the machine and its program memory; the FANUC parameter
    // export only applies to the lathe.
    const mill = documentState.machine?.type === "mill";
    const dataSources = [
      mill
        ? `Machine: ${documentState.machine.name}`
        : documentState.machineParameterPath && `Machine: ${basename(documentState.machineParameterPath)}`,
      mill && documentState.machine.programMemory && `Memory: ${basename(documentState.machine.programMemory)}`,
      documentState.toolTablePath && `Tools: ${basename(documentState.toolTablePath)}`
    ].filter(Boolean);
    elements.statusXml.textContent = dataSources.length ? dataSources.join(" | ") : "Data: bundled defaults";
    elements.statusXml.title = [
      mill
        ? documentState.machine.programMemory && `Program memory: ${documentState.machine.programMemory}`
        : documentState.machineParameterPath,
      documentState.toolTablePath
    ].filter(Boolean).join("\n") || "Bundled data defaults";
  }

  function basename(filePath) {
    return String(filePath || "").split(/[\\/]/).filter(Boolean).pop() || "";
  }

  function applyDocumentState(nextState) {
    if (!nextState || typeof nextState !== "object") {
      return;
    }
    const state = nextState.documentState || nextState.document || nextState;
    if (!state || typeof state !== "object") {
      return;
    }
    documentState = { ...documentState, ...state };
    applyingDocumentState = true;
    if (typeof state.text === "string") {
      setEditorText(state.text);
    }
    applyingDocumentState = false;
    updateDocumentChrome();
    if (Number.isFinite(state.selectedLine)) {
      setEditorSelection(state.selectedLine, false);
    }
    updateEditorMetrics();
    refreshCodexProposalEligibility();
  }

  function lineMarker(kind, title) {
    const marker = document.createElement("span");
    marker.className = `editor-gutter-marker ${kind}`;
    marker.title = title;
    marker.setAttribute("aria-label", title);
    return marker;
  }

  function normalizedErrors(errors) {
    const byLine = new Map();
    (Array.isArray(errors) ? errors : []).forEach((entry) => {
      const line = Number(typeof entry === "number" ? entry : entry?.line);
      if (!Number.isFinite(line)) {
        return;
      }
      const lineNumber = Math.trunc(line);
      const message = typeof entry === "object" && entry?.message
        ? String(entry.message)
        : `Preview issue on row ${lineNumber}`;
      const messages = byLine.get(lineNumber) || [];
      messages.push(message);
      byLine.set(lineNumber, messages);
    });
    return [...byLine.entries()].map(([line, messages]) => ({
      line,
      message: [...new Set(messages)].join("\n")
    }));
  }

  function applyDecorations(nextDecorations) {
    if (!nextDecorations || typeof nextDecorations !== "object") {
      return;
    }
    decorationState = {
      errorLines: nextDecorations.errorLines === undefined
        ? decorationState.errorLines
        : normalizedErrors(nextDecorations.errorLines),
      playbackLine: nextDecorations.playbackLine === undefined
        ? decorationState.playbackLine
        : Number.isFinite(Number(nextDecorations.playbackLine))
          ? Number(nextDecorations.playbackLine)
          : undefined
    };
    if (!editor) {
      return;
    }

    editor.operation(() => {
      markedErrorLines.forEach((lineHandle) => {
        editor.removeLineClass(lineHandle, "background", "cm-error-line");
      });
      if (markedPlaybackLine) {
        editor.removeLineClass(markedPlaybackLine, "background", "cm-playback-line");
      }
      editor.clearGutter("fanuc-diagnostics-gutter");
      editor.clearGutter("fanuc-playback-gutter");
      markedErrorLines = new Set();
      markedPlaybackLine = undefined;

      decorationState.errorLines.forEach((issue) => {
        const lineIndex = Math.trunc(issue.line) - 1;
        if (lineIndex < 0 || lineIndex >= editor.lineCount()) {
          return;
        }
        const lineHandle = editor.addLineClass(lineIndex, "background", "cm-error-line");
        editor.setGutterMarker(
          lineIndex,
          "fanuc-diagnostics-gutter",
          lineMarker("error", issue.message)
        );
        markedErrorLines.add(lineHandle);
      });

      if (Number.isFinite(decorationState.playbackLine)) {
        const lineIndex = Math.trunc(decorationState.playbackLine) - 1;
        if (lineIndex >= 0 && lineIndex < editor.lineCount()) {
          markedPlaybackLine = editor.addLineClass(
            lineIndex,
            "background",
            "cm-playback-line"
          );
          editor.setGutterMarker(
            lineIndex,
            "fanuc-playback-gutter",
            lineMarker("playback", `Toolpath playback row ${lineIndex + 1}`)
          );
          editor.scrollIntoView({ line: lineIndex, ch: 0 }, 60);
        }
      }
    });
  }

  function deriveRenderDecorations(message) {
    const issues = message?.payload?.compensationIssues;
    if (!Array.isArray(issues)) {
      return;
    }
    applyDecorations({
      errorLines: issues
        .filter((issue) => Number.isFinite(issue?.line))
        .map((issue) => ({ line: issue.line, message: issue.message || "Compensation interference" })),
      playbackLine: decorationState.playbackLine
    });
  }

  function handleRender(message) {
    const renderMessage = message?.type === "render"
      ? message
      : message?.payload
        ? { type: "render", ...message }
        : undefined;
    if (!renderMessage) {
      return;
    }
    if (renderMessage.name) {
      documentState.name = renderMessage.name;
      updateDocumentChrome();
    }
    if (renderMessage.settings) {
      settingsState = { ...settingsState, ...renderMessage.settings };
    }
    deriveRenderDecorations(renderMessage);
    dispatchPreviewMessage(renderMessage);

    const errorCount = Array.isArray(renderMessage.payload?.errors)
      ? renderMessage.payload.errors.length
      : 0;
    const warningCount = Array.isArray(renderMessage.payload?.warnings)
      ? renderMessage.payload.warnings.length
      : 0;
    if (errorCount) {
      reportStatus(
        `${errorCount} preview ${errorCount === 1 ? "error" : "errors"}. Select a marked row for details.`,
        "error"
      );
    } else if (warningCount) {
      reportStatus(
        `Preview updated with ${warningCount} ${warningCount === 1 ? "note" : "notes"}.`,
        "warning"
      );
    } else {
      reportStatus("Preview is up to date.", "ready");
    }
  }

  function handleSelection(selection) {
    const line = Number(typeof selection === "number" ? selection : selection?.line);
    if (!Number.isFinite(line)) {
      return;
    }
    setEditorSelection(line, true);
    dispatchPreviewMessage({ type: "selection", line: Math.trunc(line) });
  }

  function handleEditorChange() {
    if (applyingDocumentState) {
      return;
    }
    const cursor = editorCursor();
    const firstEdit = !documentState.dirty;
    documentState = {
      ...documentState,
      dirty: true,
      selectedLine: cursor.line
    };
    updateDocumentChrome();
    updateEditorMetrics();
    refreshCodexProposalEligibility();
    // The main process learns about unsaved changes at once (close prompt);
    // the text itself follows after a pause in typing.
    if (firstEdit && isFunction(desktop.markDirty)) {
      desktop.markDirty();
    }
    queueDocumentUpdate();
  }

  // Sending the whole program and re-parsing it on every keystroke froze
  // large files, so the text goes to the main process after a pause that
  // grows with the program length.
  function documentUpdateDelay() {
    const lines = editor ? editor.lineCount() : 0;
    return Math.min(1500, 300 + Math.round(lines / 100));
  }

  function queueDocumentUpdate() {
    pendingDocumentUpdate = true;
    window.clearTimeout(documentUpdateTimer);
    documentUpdateTimer = window.setTimeout(flushDocumentUpdate, documentUpdateDelay());
  }

  function flushDocumentUpdate() {
    window.clearTimeout(documentUpdateTimer);
    if (!pendingDocumentUpdate) {
      return;
    }
    pendingDocumentUpdate = false;
    const text = editorValue();
    documentState = { ...documentState, text };
    reportStatus("Updating toolpath preview...", "working");
    try {
      desktop.updateDocument(text, editorCursor().line);
    } catch (error) {
      reportStatus(errorMessage(error), "error");
    }
  }

  function handleCursorActivity() {
    updateEditorMetrics();
    if (applyingDocumentState || suppressSelectionUpdate) {
      return;
    }
    const cursor = editorCursor();
    if (cursor.line === documentState.selectedLine && cursor.line === lastSelectionLine) {
      return;
    }
    documentState.selectedLine = cursor.line;
    lastSelectionLine = cursor.line;
    callDesktop("updateSelection", cursor.line)
      .catch((error) => reportStatus(errorMessage(error), "error"));
  }

  function initializeEditor() {
    if (window.CodeMirror && isFunction(window.CodeMirror.fromTextArea)) {
      editor = window.CodeMirror.fromTextArea(elements.editorTextArea, {
        mode: "text/x-fanuc-nc",
        theme: "fanuc-dark",
        lineNumbers: true,
        lineWrapping: false,
        smartIndent: false,
        electricChars: false,
        indentUnit: 2,
        tabSize: 2,
        cursorBlinkRate: 530,
        showCursorWhenSelecting: true,
        gutters: [
          "CodeMirror-linenumbers",
          "fanuc-diagnostics-gutter",
          "fanuc-playback-gutter"
        ],
        extraKeys: {
          "Ctrl-S": () => saveDocument(false),
          "Cmd-S": () => saveDocument(false),
          "Ctrl-Shift-S": () => saveDocument(true),
          "Cmd-Shift-S": () => saveDocument(true),
          Tab(instance) {
            instance.replaceSelection("  ", "end", "+input");
          }
        }
      });
      editor.on("change", handleEditorChange);
      editor.on("cursorActivity", handleCursorActivity);
      editor.on("gutterClick", (_instance, lineIndex, gutter) => {
        if (gutter !== "fanuc-diagnostics-gutter" && gutter !== "fanuc-playback-gutter") {
          return;
        }
        setEditorSelection(lineIndex + 1, true);
        const issue = decorationState.errorLines.find((entry) => entry.line === lineIndex + 1);
        if (issue) {
          reportStatus(issue.message, "error", `Row ${lineIndex + 1}`);
        }
      });
      editor.setSize("100%", "100%");
      window.setTimeout(() => editor.refresh(), 0);
    } else {
      elements.editorTextArea.addEventListener("input", handleEditorChange);
      ["click", "keyup", "select"].forEach((eventName) => {
        elements.editorTextArea.addEventListener(eventName, handleCursorActivity);
      });
      reportStatus("Code editor resources were not found; using the basic editor.", "warning");
    }
    updateEditorMetrics();
  }

  async function performDocumentAction(method, statusText, ...args) {
    reportStatus(statusText, "working");
    try {
      const result = await callDesktop(method, ...args);
      if (result?.error) {
        reportStatus(result.error, "error");
      } else if (result?.canceled) {
        reportStatus("Operation canceled.", "ready");
      } else if (result && (
        typeof result.text === "string" ||
        typeof result.name === "string" ||
        Object.prototype.hasOwnProperty.call(result, "dirty")
      )) {
        applyDocumentState(result);
      } else {
        reportStatus("Ready.", "ready");
      }
      return result;
    } catch (error) {
      reportStatus(errorMessage(error), "error");
      return undefined;
    }
  }

  function newDocument() {
    return performDocumentAction("newFile", "Creating a new NC program...");
  }

  function openDocument() {
    return performDocumentAction("openFile", "Opening NC program...");
  }

  async function saveDocument(saveAs) {
    const method = saveAs ? "saveFileAs" : "saveFile";
    const result = await performDocumentAction(
      method,
      saveAs ? "Choosing a destination..." : "Saving NC program...",
      editorValue()
    );
    if (result && !result.canceled && !result.error) {
      reportStatus("NC program saved.", "success");
    }
    return result;
  }

  function toolEditorSelect(field, values, selected) {
    const select = document.createElement("select");
    select.dataset.field = field;
    values.forEach((value) => {
      const option = document.createElement("option");
      option.value = value;
      option.textContent = value.replace(/-/g, " ");
      option.selected = value === selected;
      select.appendChild(option);
    });
    return select;
  }

  function toolEditorNumberInput(field, value, options = {}) {
    const input = document.createElement("input");
    input.type = "number";
    input.dataset.field = field;
    input.min = String(options.min ?? 0);
    input.max = String(options.max ?? 100000);
    input.step = options.step || "any";
    input.required = Boolean(options.required);
    input.value = value === null || value === undefined ? "" : String(value);
    return input;
  }

  function appendToolEditorCell(row, control, className) {
    const cell = document.createElement("td");
    if (className) {
      cell.className = className;
    }
    cell.appendChild(control);
    row.appendChild(cell);
  }

  function renderToolEditorHead(columns) {
    if (!elements.toolEditorHead) {
      return;
    }
    const row = document.createElement("tr");
    for (const label of columns) {
      const cell = document.createElement("th");
      cell.textContent = label;
      row.appendChild(cell);
    }
    elements.toolEditorHead.replaceChildren(row);
  }

  function renderToolEditor(table) {
    elements.toolTableName.value = table?.name || `${documentState.name || "Program"} tools`;
    elements.toolTableUnits.value = table?.units === "inch" ? "inch" : "mm";
    const source = table?.sourcePath || "In-memory tool table";
    elements.toolTableEditorSource.textContent = table?.saved
      ? `Companion XML: ${basename(source)}`
      : "In-memory table; save the NC program to create its companion XML file.";
    elements.toolTableEditorSource.title = source;
    elements.toolTableReveal.disabled = !table?.saved;
    elements.toolEditorRows.replaceChildren();
    elements.toolTableForm.dataset.machineType = table?.machineType === "mill" ? "mill" : "lathe";

    const tools = Array.isArray(table?.tools) ? table.tools : [];
    if (table?.machineType === "mill") {
      renderToolEditorHead(["Tool", "Type", "Diameter", "Corner R", "Length", "Description"]);
      const types = Array.isArray(table.toolTypes) && table.toolTypes.length ? table.toolTypes : TOOL_TYPES;
      tools.forEach((tool) => {
        const row = document.createElement("tr");
        row.dataset.toolNumber = String(tool.number);
        const toolLabel = document.createElement("strong");
        toolLabel.textContent = `T${String(tool.number).padStart(2, "0")}`;
        appendToolEditorCell(row, toolLabel, "tool-editor-number");
        appendToolEditorCell(row, toolEditorSelect("type", types, types.includes(tool.type) ? tool.type : "other"));
        appendToolEditorCell(row, toolEditorNumberInput("diameter", tool.diameter));
        appendToolEditorCell(row, toolEditorNumberInput("cornerRadius", tool.cornerRadius, { max: 10000, required: true }));
        appendToolEditorCell(row, toolEditorNumberInput("length", tool.length));
        const description = document.createElement("input");
        description.type = "text";
        description.dataset.field = "description";
        description.maxLength = 500;
        description.value = tool.description || "";
        appendToolEditorCell(row, description, "tool-editor-description");
        elements.toolEditorRows.appendChild(row);
      });
      elements.toolEditorEmpty.hidden = tools.length !== 0;
      elements.toolTableSave.disabled = tools.length === 0;
      return;
    }
    renderToolEditorHead(["Tool", "Type", "Nose R", "TIP", "Hand", "Diameter", "Width", "Description"]);
    tools.forEach((tool) => {
      const row = document.createElement("tr");
      row.dataset.toolNumber = String(tool.number);

      const toolLabel = document.createElement("strong");
      toolLabel.textContent = `T${String(tool.number).padStart(2, "0")}`;
      appendToolEditorCell(row, toolLabel, "tool-editor-number");
      appendToolEditorCell(
        row,
        toolEditorSelect("type", TOOL_TYPES, tool.type || "other")
      );
      appendToolEditorCell(
        row,
        toolEditorNumberInput("cornerRadius", tool.cornerRadius, {
          max: 10000,
          required: true
        })
      );
      appendToolEditorCell(
        row,
        toolEditorNumberInput("tip", tool.tip, {
          max: 9,
          step: "1",
          required: true
        })
      );
      appendToolEditorCell(
        row,
        toolEditorSelect("hand", TOOL_HANDS, tool.hand || "unknown")
      );
      appendToolEditorCell(
        row,
        toolEditorNumberInput("diameter", tool.diameter)
      );
      appendToolEditorCell(
        row,
        toolEditorNumberInput("width", tool.width)
      );

      const description = document.createElement("input");
      description.type = "text";
      description.dataset.field = "description";
      description.maxLength = 500;
      description.value = tool.description || "";
      appendToolEditorCell(row, description, "tool-editor-description");
      elements.toolEditorRows.appendChild(row);
    });
    elements.toolEditorEmpty.hidden = tools.length !== 0;
    elements.toolTableSave.disabled = tools.length === 0;
  }

  function setToolTableFeedback(message, level = "") {
    elements.toolTableFeedback.textContent = message || "";
    elements.toolTableFeedback.className = `settings-feedback ${level}`.trim();
  }

  function setToolTableBusy(busy) {
    elements.toolTableSave.disabled = busy || elements.toolEditorRows.children.length === 0;
    elements.toolTableCancel.disabled = busy;
    elements.toolTableClose.disabled = busy;
    elements.toolTableReveal.disabled = busy || elements.toolTableReveal.dataset.available !== "true";
    elements.toolTableForm.querySelectorAll("input, select").forEach((control) => {
      control.disabled = busy;
    });
  }

  function showToolTableDialog() {
    if (isFunction(elements.toolTableModal.showModal)) {
      if (!elements.toolTableModal.open) {
        elements.toolTableModal.showModal();
      }
    } else {
      elements.toolTableModal.setAttribute("open", "");
    }
  }

  function closeToolTableDialog() {
    if (isFunction(elements.toolTableModal.close)) {
      elements.toolTableModal.close();
    } else {
      elements.toolTableModal.removeAttribute("open");
    }
    setToolTableFeedback("");
  }

  async function openToolTableEditor() {
    showToolTableDialog();
    elements.toolTableReveal.dataset.available = "false";
    setToolTableFeedback("Loading tool definitions...");
    setToolTableBusy(true);
    try {
      const table = await callDesktop("getToolTable");
      renderToolEditor(table);
      elements.toolTableReveal.dataset.available = String(Boolean(table?.saved));
      setToolTableFeedback("");
      window.setTimeout(() => elements.toolTableName.focus(), 0);
    } catch (error) {
      setToolTableFeedback(errorMessage(error), "error");
    } finally {
      setToolTableBusy(false);
    }
  }

  function toolEditorPayload() {
    const mill = elements.toolTableForm.dataset.machineType === "mill";
    const tools = [...elements.toolEditorRows.querySelectorAll("tr")].map((row) => {
      const value = (field) => row.querySelector(`[data-field="${field}"]`)?.value ?? "";
      const optionalNumber = (field) => {
        const raw = value(field).trim();
        return raw === "" ? null : Number(raw);
      };
      if (mill) {
        return {
          number: Number(row.dataset.toolNumber),
          type: value("type"),
          cornerRadius: Number(value("cornerRadius")),
          diameter: optionalNumber("diameter"),
          length: optionalNumber("length"),
          description: value("description")
        };
      }
      return {
        number: Number(row.dataset.toolNumber),
        type: value("type"),
        cornerRadius: Number(value("cornerRadius")),
        tip: Number(value("tip")),
        hand: value("hand"),
        diameter: optionalNumber("diameter"),
        width: optionalNumber("width"),
        description: value("description")
      };
    });
    return {
      name: elements.toolTableName.value,
      units: elements.toolTableUnits.value,
      tools
    };
  }

  async function saveToolTableEditor(event) {
    event.preventDefault();
    if (!elements.toolTableForm.reportValidity()) {
      return;
    }
    setToolTableFeedback("Saving companion tool table...");
    setToolTableBusy(true);
    try {
      const result = await callDesktop("saveToolTable", toolEditorPayload());
      if (result?.documentState) {
        applyDocumentState(result.documentState);
      }
      closeToolTableDialog();
      reportStatus(
        result?.table?.saved
          ? "Tool table saved and preview updated."
          : "Tool table updated in memory; save the NC program to create XML.",
        "success"
      );
    } catch (error) {
      setToolTableFeedback(errorMessage(error), "error");
    } finally {
      setToolTableBusy(false);
    }
  }

  async function revealToolTable() {
    reportStatus("Opening tool table location...", "working");
    try {
      const result = await callDesktop("showToolTable");
      if (result?.shown === false) {
        reportStatus("The active tool table is in memory; there is no file to show.", "warning");
      } else {
        reportStatus("Tool table shown in Explorer.", "ready");
      }
    } catch (error) {
      reportStatus(errorMessage(error), "error");
    }
  }

  function replaceResultList(list, entries, emptyMessage) {
    list.replaceChildren();
    const values = (Array.isArray(entries) ? entries : [])
      .map((entry) => String(entry || "").trim())
      .filter(Boolean);
    (values.length ? values : [emptyMessage]).forEach((value) => {
      const item = document.createElement("li");
      item.textContent = value;
      list.appendChild(item);
    });
  }

  function setSettingsFeedback(message, level = "") {
    elements.settingsFeedback.textContent = message || "";
    elements.settingsFeedback.className = `settings-feedback ${level}`.trim();
  }

  function setSettingsBusy(busy) {
    elements.settingsSave.disabled = busy;
    elements.settingsCancel.disabled = busy;
    elements.settingsClose.disabled = busy;
    elements.clearApiKey.disabled = busy || !settingsState.keyConfigured;
  }

  function updateApiKeyStatus() {
    const configured = Boolean(settingsState.keyConfigured);
    elements.settingsApiKeyStatus.textContent = configured ? "Key securely saved" : "No key saved";
    elements.settingsApiKeyStatus.classList.toggle("configured", configured);
    elements.clearApiKey.disabled = !configured;
  }

  function fillMachineOptions() {
    const select = elements.settingsMachine;
    if (!select) {
      return;
    }
    select.replaceChildren();
    const auto = document.createElement("option");
    auto.value = "auto";
    auto.textContent = "Auto detect from the program";
    select.appendChild(auto);
    for (const machine of Array.isArray(settingsState.machines) ? settingsState.machines : []) {
      const option = document.createElement("option");
      option.value = machine.id;
      option.textContent = `${machine.name} (${machine.type}, ${machine.control})`;
      select.appendChild(option);
    }
    select.value = [...select.options].some((option) => option.value === settingsState.machine)
      ? settingsState.machine
      : "auto";
  }

  // One folder row per mill: its program memory (O09xxx macros and other
  // programs the NC file calls).
  function fillProgramMemoryRows() {
    const container = elements.settingsProgramMemory;
    if (!container) {
      return;
    }
    container.replaceChildren();
    const folders = settingsState.programMemory || {};
    const machines = (Array.isArray(settingsState.machines) ? settingsState.machines : [])
      .filter((machine) => machine.type === "mill");
    for (const machine of machines) {
      const label = document.createElement("label");
      const inputId = `settingsProgramMemory-${machine.id}`;
      label.htmlFor = inputId;
      label.textContent = `${machine.name} program memory`;
      const row = document.createElement("div");
      row.className = "secret-input-row";
      const input = document.createElement("input");
      input.id = inputId;
      input.type = "text";
      input.spellcheck = false;
      input.placeholder = "Folder with the machine's programs (optional)";
      input.dataset.machineId = machine.id;
      input.value = folders[machine.id] || "";
      const browse = document.createElement("button");
      browse.type = "button";
      browse.className = "field-button";
      browse.textContent = "Browse...";
      browse.addEventListener("click", async () => {
        try {
          const result = await callDesktop("chooseProgramMemoryFolder", machine.id);
          if (result && !result.canceled && result.path) {
            input.value = result.path;
          }
        } catch (error) {
          setSettingsFeedback(errorMessage(error), "error");
        }
      });
      row.append(input, browse);
      label.appendChild(row);
      container.appendChild(label);
    }
  }

  function fillSettingsForm(settings) {
    settingsState = { ...settingsState, ...(settings || {}) };
    fillMachineOptions();
    fillProgramMemoryRows();
    elements.settingsG30X.value = settingsState.g30X !== null &&
      settingsState.g30X !== undefined && Number.isFinite(Number(settingsState.g30X))
      ? String(settingsState.g30X)
      : "250";
    elements.settingsG30Z.value = settingsState.g30Z !== null &&
      settingsState.g30Z !== undefined && Number.isFinite(Number(settingsState.g30Z))
      ? String(settingsState.g30Z)
      : "100";
    elements.settingsInitialVariables.value = settingsState.initialVariables || "";
    elements.settingsMachineFile.value = settingsState.machineParametersFile || "";
    elements.settingsToolFile.value = settingsState.toolTableFile || "";
    elements.settingsAiEnabled.checked = settingsState.aiToolRecognitionEnabled !== false;
    elements.settingsAiModel.value = settingsState.aiModel || "gpt-5.6-sol";
    elements.settingsAiDebounce.value = Number.isFinite(Number(settingsState.aiDebounceMs))
      ? String(settingsState.aiDebounceMs)
      : "1800";
    elements.settingsApiKey.value = "";
    elements.settingsApiKey.type = "password";
    elements.toggleApiKey.textContent = "Show";
    elements.toggleApiKey.setAttribute("aria-pressed", "false");
    updateApiKeyStatus();
  }

  function showSettingsDialog() {
    if (isFunction(elements.settingsModal.showModal)) {
      if (!elements.settingsModal.open) {
        elements.settingsModal.showModal();
      }
    } else {
      elements.settingsModal.setAttribute("open", "");
    }
  }

  function closeSettingsDialog() {
    if (isFunction(elements.settingsModal.close)) {
      elements.settingsModal.close();
    } else {
      elements.settingsModal.removeAttribute("open");
    }
    setSettingsFeedback("");
  }

  async function openSettingsDialog() {
    showSettingsDialog();
    setSettingsFeedback("Loading settings...");
    setSettingsBusy(true);
    try {
      const settings = await callDesktop("getSettings");
      fillSettingsForm(settings || settingsState);
      setSettingsFeedback("");
      window.setTimeout(() => elements.settingsG30X.focus(), 0);
    } catch (error) {
      fillSettingsForm(settingsState);
      setSettingsFeedback(errorMessage(error), "error");
    } finally {
      setSettingsBusy(false);
    }
  }

  function settingsFromForm() {
    const g30X = Number(elements.settingsG30X.value);
    const g30Z = Number(elements.settingsG30Z.value);
    const aiDebounceMs = Number(elements.settingsAiDebounce.value);
    if (!Number.isFinite(g30X) || !Number.isFinite(g30Z)) {
      throw new Error("G30 X and Z must be valid numbers.");
    }
    if (!Number.isFinite(aiDebounceMs) || aiDebounceMs < 500 || aiDebounceMs > 30000) {
      throw new Error("Recognition delay must be between 500 and 30000 ms.");
    }
    return {
      machine: elements.settingsMachine?.value || "auto",
      g30X,
      g30Z,
      initialVariables: elements.settingsInitialVariables.value.trim(),
      machineParametersFile: elements.settingsMachineFile.value.trim(),
      toolTableFile: elements.settingsToolFile.value.trim(),
      programMemory: Object.fromEntries(
        [...(elements.settingsProgramMemory?.querySelectorAll("input[data-machine-id]") || [])]
          .map((input) => [input.dataset.machineId, input.value.trim()])
          .filter(([, folder]) => folder)
      ),
      aiToolRecognitionEnabled: elements.settingsAiEnabled.checked,
      aiModel: elements.settingsAiModel.value.trim() || "gpt-5.6-sol",
      aiDebounceMs
    };
  }

  async function saveSettings(event) {
    event?.preventDefault();
    let nextSettings;
    try {
      nextSettings = settingsFromForm();
    } catch (error) {
      setSettingsFeedback(errorMessage(error), "error");
      return;
    }
    setSettingsBusy(true);
    setSettingsFeedback("Saving settings...");
    try {
      const saved = await callDesktop("saveSettings", nextSettings);
      settingsState = { ...settingsState, ...nextSettings, ...(saved || {}) };
      const apiKey = elements.settingsApiKey.value.trim();
      if (apiKey) {
        await callDesktop("setApiKey", apiKey);
        settingsState.keyConfigured = true;
      }
      fillSettingsForm(settingsState);
      setSettingsFeedback("Settings saved.", "success");
      reportStatus("Preview settings saved.", "success");
      window.setTimeout(closeSettingsDialog, 260);
    } catch (error) {
      setSettingsFeedback(errorMessage(error), "error");
      reportStatus(errorMessage(error), "error");
    } finally {
      setSettingsBusy(false);
    }
  }

  async function clearApiKey() {
    setSettingsBusy(true);
    setSettingsFeedback("Removing saved API key...");
    try {
      await callDesktop("clearApiKey");
      settingsState.keyConfigured = false;
      elements.settingsApiKey.value = "";
      updateApiKeyStatus();
      setSettingsFeedback("Saved API key removed.", "success");
      reportStatus("OpenAI API key removed.", "success");
    } catch (error) {
      setSettingsFeedback(errorMessage(error), "error");
      reportStatus(errorMessage(error), "error");
    } finally {
      setSettingsBusy(false);
    }
  }

  function boundedCodexConversation(items) {
    const recent = [];
    let remainingCharacters = MAX_CODEX_HISTORY_CHARACTERS;
    const source = Array.isArray(items) ? items : [];
    for (let index = source.length - 1; index >= 0; index -= 1) {
      if (recent.length >= MAX_CODEX_HISTORY_ITEMS || remainingCharacters <= 0) {
        break;
      }
      const role = source[index]?.role === "assistant"
        ? "assistant"
        : source[index]?.role === "user"
          ? "user"
          : undefined;
      if (!role) {
        continue;
      }
      const rawContent = String(source[index]?.content || "").trim();
      if (!rawContent) {
        continue;
      }
      const content = rawContent.slice(
        0,
        Math.min(MAX_CODEX_HISTORY_ITEM_CHARACTERS, remainingCharacters)
      );
      remainingCharacters -= content.length;
      recent.push({ role, content });
    }
    return recent.reverse();
  }

  function scrollCodexToEnd() {
    window.requestAnimationFrame(() => {
      elements.codexScroll.scrollTop = elements.codexScroll.scrollHeight;
    });
  }

  function appendCodexMessage(role, content) {
    const text = String(content || "").trim();
    if (!text) {
      return;
    }
    const normalizedRole = ["user", "assistant", "error", "notice"].includes(role)
      ? role
      : "assistant";
    const message = document.createElement("article");
    message.className = `codex-message ${normalizedRole}`;
    const label = document.createElement("strong");
    label.className = "codex-message-label";
    label.textContent = {
      user: "You",
      assistant: "Codex",
      error: "Codex error",
      notice: "Status"
    }[normalizedRole];
    const body = document.createElement("p");
    body.className = "codex-message-content";
    body.textContent = text;
    message.append(label, body);
    elements.codexEmptyState.hidden = true;
    elements.codexTranscript.appendChild(message);
    scrollCodexToEnd();
  }

  function codexReferenceLabel(reference) {
    if (typeof reference === "string") {
      return reference.trim();
    }
    if (!reference || typeof reference !== "object") {
      return "";
    }
    const rawName = reference.displayName || reference.name || reference.fileName ||
      reference.label || reference.path;
    const name = basename(String(rawName || ""));
    const detail = String(reference.kind || reference.type || "").trim();
    return name && detail ? `${name} (${detail})` : name;
  }

  function replaceCodexReferenceList(list, references, emptyMessage) {
    list.replaceChildren();
    const labels = (Array.isArray(references) ? references : [])
      .map(codexReferenceLabel)
      .filter(Boolean);
    (labels.length ? [...new Set(labels)] : [emptyMessage]).forEach((label) => {
      const item = document.createElement("li");
      item.textContent = label;
      list.appendChild(item);
    });
  }

  function showCodexReferences(usedReferences, selectedReferences = usedReferences) {
    const references = Array.isArray(usedReferences) ? usedReferences : [];
    const selectedSource = Array.isArray(selectedReferences) ? selectedReferences : [];
    const selected = selectedSource.filter((reference) =>
      typeof reference !== "object" || reference === null || reference.selected !== false
    );
    const used = references.filter((reference) =>
      typeof reference !== "object" || reference === null || reference.used !== false
    );
    replaceCodexReferenceList(
      elements.codexSelectedReferences,
      selected,
      "None selected."
    );
    replaceCodexReferenceList(
      elements.codexUsedReferences,
      used,
      "No reference contents used."
    );
  }

  function codexWorkspaceFromResult(result) {
    if (!result || result.canceled) {
      return undefined;
    }
    return result.workspace && typeof result.workspace === "object"
      ? result.workspace
      : result;
  }

  function codexWorkspaceLabel(workspace) {
    if (!workspace || typeof workspace !== "object") {
      return "No local workspace selected";
    }
    const publicLabel = workspace.displayName || workspace.localLabel || workspace.displayLabel ||
      workspace.label || workspace.name || workspace.folderName;
    if (publicLabel) {
      return String(publicLabel);
    }
    const publicPath = workspace.displayPath || workspace.folderPath || workspace.path;
    return publicPath ? basename(String(publicPath)) : "Local reference workspace";
  }

  function renderCodexReferenceWorkspace(workspace, statusMessage, level = "") {
    codexReferenceWorkspace = workspace && typeof workspace === "object"
      ? workspace
      : undefined;
    elements.codexReferenceWorkspaceLabel.textContent = codexWorkspaceLabel(
      codexReferenceWorkspace
    );
    const count = Number(
      codexReferenceWorkspace?.fileCount ??
      codexReferenceWorkspace?.manifestCount ??
      codexReferenceWorkspace?.referenceCount
    );
    const localPath = String(codexReferenceWorkspace?.path || "").trim();
    const largeCount = Number(codexReferenceWorkspace?.largeFileCount);
    const largeNote = Number.isFinite(largeCount) && largeCount > 0
      ? ` ${largeCount.toLocaleString()} ${largeCount === 1 ? "file is" : "files are"} marked large.`
      : "";
    const defaultStatus = codexReferenceWorkspace
      ? `${localPath ? `Local: ${localPath}. ` : ""}${Number.isFinite(count)
        ? `${count.toLocaleString()} supported reference ${count === 1 ? "file" : "files"} available by manifest.`
        : "Reference manifest available."}${largeNote} Contents still require separate approval.`
      : "Select a folder to make its supported reference manifest available to Codex.";
    const providedStatus = String(
      statusMessage || codexReferenceWorkspace?.status ||
      codexReferenceWorkspace?.summary || ""
    ).trim();
    elements.codexReferenceWorkspaceStatus.textContent = codexReferenceWorkspace
      ? [defaultStatus, providedStatus]
        .filter((value, index, values) => value && values.indexOf(value) === index)
        .join(" ")
      : providedStatus || defaultStatus;
    elements.codexReferenceWorkspaceStatus.className =
      `codex-workspace-status ${level}`.trim();
    updateCodexContextControls();
  }

  function codexAttachmentId(attachment) {
    const value = attachment?.id ?? attachment?.attachmentId;
    return value === undefined || value === null ? "" : String(value);
  }

  function codexAttachmentName(attachment) {
    const raw = attachment?.displayName || attachment?.name || attachment?.fileName ||
      attachment?.label || attachment?.path;
    return basename(String(raw || "")) || "Attached file";
  }

  function formatCodexAttachmentSize(bytes) {
    const value = Number(bytes);
    if (!Number.isFinite(value) || value < 0) {
      return "";
    }
    if (value < 1024) {
      return `${value} B`;
    }
    if (value < 1024 * 1024) {
      return `${(value / 1024).toFixed(1)} KiB`;
    }
    return `${(value / (1024 * 1024)).toFixed(1)} MiB`;
  }

  function attachmentArrayFromResult(result) {
    const values = Array.isArray(result)
      ? result
      : Array.isArray(result?.attachments)
        ? result.attachments
        : [];
    return values.filter((attachment) =>
      attachment && typeof attachment === "object" && codexAttachmentId(attachment)
    );
  }

  function renderCodexAttachments(attachments, statusMessage, level = "") {
    codexAttachments = attachmentArrayFromResult(attachments);
    elements.codexAttachmentList.replaceChildren();
    if (!codexAttachments.length) {
      const empty = document.createElement("span");
      empty.className = "codex-attachment-empty";
      empty.textContent = "No files attached";
      elements.codexAttachmentList.appendChild(empty);
    } else {
      codexAttachments.forEach((attachment) => {
        const chip = document.createElement("span");
        chip.className = "codex-attachment-chip";
        chip.setAttribute("role", "listitem");
        const name = document.createElement("span");
        name.className = "codex-attachment-name";
        name.textContent = codexAttachmentName(attachment);
        const details = document.createElement("span");
        details.className = "codex-attachment-details";
        details.textContent = [
          String(attachment.kind || attachment.type || "").trim(),
          formatCodexAttachmentSize(
            attachment.bytes ?? attachment.byteLength ?? attachment.size
          ),
          attachment.large === true ? "large" : ""
        ].filter(Boolean).join(" · ");
        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = "codex-attachment-remove";
        remove.textContent = "×";
        remove.title = `Remove ${name.textContent}`;
        remove.setAttribute("aria-label", `Remove attached file ${name.textContent}`);
        remove.disabled = codexRequestBusy || codexContextBusy;
        remove.addEventListener("click", () => {
          void removeCodexAttachment(codexAttachmentId(attachment));
        });
        chip.append(name);
        if (details.textContent) {
          chip.append(details);
        }
        chip.append(remove);
        elements.codexAttachmentList.appendChild(chip);
      });
    }
    const attachmentSummary = codexAttachments.length
      ? `${codexAttachments.length.toLocaleString()} ${codexAttachments.length === 1 ? "file" : "files"} selected. `
      : "";
    const defaultStatus = `${attachmentSummary}Only attachment metadata is included initially. File contents stay local until Codex requests exact files and you approve access in the native dialog.`;
    elements.codexAttachmentStatus.textContent = statusMessage || defaultStatus;
    elements.codexAttachmentStatus.className =
      `codex-attachment-status ${level}`.trim();
    updateCodexContextControls();
  }

  function invalidateCodexProposalContext() {
    if (!currentCodexProposal) {
      return;
    }
    currentCodexProposal.serverStale = true;
    refreshCodexProposalEligibility();
  }

  function updateCodexContextControls() {
    const busy = codexRequestBusy || codexContextBusy;
    elements.codexSelectReferenceWorkspace.disabled = busy;
    elements.codexClearReferenceWorkspace.disabled = busy || !codexReferenceWorkspace;
    elements.codexAttachFile.disabled = busy;
    elements.codexClearAttachments.disabled = busy || !codexAttachments.length;
    elements.codexNewChat.disabled = busy;
    elements.codexAttachmentList
      .querySelectorAll(".codex-attachment-remove")
      .forEach((button) => {
        button.disabled = busy;
      });
  }

  function setCodexContextBusy(busy) {
    codexContextBusy = Boolean(busy);
    updateCodexContextControls();
    updateCodexSendState();
  }

  async function chooseReferenceWorkspace() {
    if (codexRequestBusy || codexContextBusy) {
      return;
    }
    setCodexPanelOpen(true, { focusPrompt: false });
    setCodexContextBusy(true);
    elements.codexReferenceWorkspaceStatus.textContent =
      "Choose a local reference workspace...";
    try {
      const result = await callDesktop("chooseReferenceWorkspace");
      if (result?.error) {
        throw new Error(String(result.error));
      }
      if (!result || result.canceled) {
        renderCodexReferenceWorkspace(codexReferenceWorkspace);
        return;
      }
      renderCodexReferenceWorkspace(
        codexWorkspaceFromResult(result),
        result.status || result.summary,
        "success"
      );
      invalidateCodexProposalContext();
      reportStatus("Codex reference workspace selected.", "success", "Codex");
    } catch (error) {
      renderCodexReferenceWorkspace(
        codexReferenceWorkspace,
        errorMessage(error),
        "error"
      );
      reportStatus(errorMessage(error), "error", "Codex");
    } finally {
      setCodexContextBusy(false);
    }
  }

  async function clearReferenceWorkspace() {
    if (!codexReferenceWorkspace || codexRequestBusy || codexContextBusy) {
      return;
    }
    setCodexContextBusy(true);
    try {
      const result = await callDesktop("clearReferenceWorkspace");
      if (result?.error) {
        throw new Error(String(result.error));
      }
      renderCodexReferenceWorkspace(undefined, "Reference workspace cleared.");
      showCodexReferences([]);
      invalidateCodexProposalContext();
      reportStatus("Codex reference workspace cleared.", "ready", "Codex");
    } catch (error) {
      renderCodexReferenceWorkspace(codexReferenceWorkspace, errorMessage(error), "error");
      reportStatus(errorMessage(error), "error", "Codex");
    } finally {
      setCodexContextBusy(false);
    }
  }

  async function chooseCodexAttachments() {
    if (codexRequestBusy || codexContextBusy) {
      return;
    }
    setCodexPanelOpen(true, { focusPrompt: false });
    setCodexContextBusy(true);
    elements.codexAttachmentStatus.textContent = "Choose files to attach...";
    const previousIds = codexAttachments.map(codexAttachmentId).join("\n");
    try {
      const result = await callDesktop("chooseCodexAttachments");
      if (result?.error) {
        throw new Error(String(result.error));
      }
      if (result?.canceled) {
        renderCodexAttachments(codexAttachments);
        return;
      }
      const nextAttachments = attachmentArrayFromResult(result);
      const changed = nextAttachments.map(codexAttachmentId).join("\n") !== previousIds;
      renderCodexAttachments(
        nextAttachments,
        changed ? undefined : "Attachment selection unchanged.",
        changed ? "success" : ""
      );
      if (changed) {
        invalidateCodexProposalContext();
        reportStatus("Codex attachments updated.", "success", "Codex");
      }
    } catch (error) {
      renderCodexAttachments(codexAttachments, errorMessage(error), "error");
      reportStatus(errorMessage(error), "error", "Codex");
    } finally {
      setCodexContextBusy(false);
    }
  }

  async function removeCodexAttachment(id) {
    if (!id || codexRequestBusy || codexContextBusy) {
      return;
    }
    setCodexContextBusy(true);
    try {
      const result = await callDesktop("removeCodexAttachment", id);
      if (result?.error) {
        throw new Error(String(result.error));
      }
      const next = Array.isArray(result) || Array.isArray(result?.attachments)
        ? attachmentArrayFromResult(result)
        : codexAttachments.filter((attachment) => codexAttachmentId(attachment) !== id);
      renderCodexAttachments(next, "Attachment removed.");
      invalidateCodexProposalContext();
    } catch (error) {
      renderCodexAttachments(codexAttachments, errorMessage(error), "error");
      reportStatus(errorMessage(error), "error", "Codex");
    } finally {
      setCodexContextBusy(false);
    }
  }

  async function clearCodexAttachments({ quiet = false } = {}) {
    if (codexRequestBusy || codexContextBusy) {
      return false;
    }
    setCodexContextBusy(true);
    try {
      const result = await callDesktop("clearCodexAttachments");
      if (result?.error) {
        throw new Error(String(result.error));
      }
      renderCodexAttachments([], quiet ? undefined : "All attachments cleared.");
      invalidateCodexProposalContext();
      return true;
    } catch (error) {
      renderCodexAttachments(codexAttachments, errorMessage(error), "error");
      reportStatus(errorMessage(error), "error", "Codex");
      return false;
    } finally {
      setCodexContextBusy(false);
    }
  }

  function discardCodexProposal({ announce = false } = {}) {
    const hadProposal = Boolean(currentCodexProposal);
    currentCodexProposal = undefined;
    elements.codexProposalCard.hidden = true;
    elements.codexProposalCard.classList.remove("stale");
    elements.codexProposalSummary.textContent = "";
    elements.codexProposalProgram.textContent = "";
    elements.codexProposalAssumptions.replaceChildren();
    elements.codexProposalWarnings.replaceChildren();
    elements.codexProposalStatus.textContent = "";
    elements.codexApplyProposal.disabled = false;
    if (announce && hadProposal) {
      appendCodexMessage("notice", "Proposed G-code discarded. The editor was not changed.");
    }
  }

  function refreshCodexProposalEligibility() {
    if (!currentCodexProposal || elements.codexProposalCard.hidden) {
      return;
    }
    const hasContextToken = /^[a-f0-9]{64}$/i.test(
      String(currentCodexProposal.baseContextFingerprint || "")
    ) && /^[a-f0-9]{64}$/i.test(
      String(currentCodexProposal.baseFingerprint || "")
    );
    const matchesBase = editorValue() === currentCodexProposal.baseProgram &&
      !currentCodexProposal.serverStale && hasContextToken;
    elements.codexProposalCard.classList.toggle("stale", !matchesBase);
    elements.codexApplyProposal.disabled = !matchesBase;
    elements.codexProposalStatus.textContent = matchesBase
      ? "Applying replaces the editor text only. The document remains unsaved and requires full review."
      : "This proposal is stale because the editor or machine/reference context changed after the request started. Discard it or ask Codex again.";
  }

  function renderCodexProposal(result, baseProgram) {
    const proposedProgram = typeof result?.proposedProgram === "string"
      ? result.proposedProgram
      : "";
    if (!proposedProgram) {
      discardCodexProposal();
      return;
    }
    currentCodexProposal = {
      program: proposedProgram,
      baseProgram,
      baseFingerprint: result.baseFingerprint,
      baseContextFingerprint: result.baseContextFingerprint,
      serverStale: result.stale === true
    };
    elements.codexProposalSummary.textContent = String(
      result.summary || "Codex proposed an NC program change for review."
    );
    const previewText = proposedProgram.length > MAX_CODEX_PROGRAM_PREVIEW_CHARACTERS
      ? `${proposedProgram.slice(0, MAX_CODEX_PROGRAM_PREVIEW_CHARACTERS)}\n\n(Preview truncated; Apply to editor uses the complete proposal.)`
      : proposedProgram;
    elements.codexProposalProgram.textContent = previewText;
    replaceResultList(
      elements.codexProposalAssumptions,
      result.assumptions,
      "No assumptions reported. Verify the proposal independently."
    );
    replaceResultList(
      elements.codexProposalWarnings,
      result.warnings,
      "No warnings reported. This does not make the proposal machine-safe."
    );
    elements.codexProposalCard.hidden = false;
    refreshCodexProposalEligibility();
    scrollCodexToEnd();
  }

  async function applyCodexProposal() {
    const proposal = currentCodexProposal;
    if (!proposal) {
      return;
    }
    if (proposal.serverStale || codexContextBusy || codexRequestBusy) {
      refreshCodexProposalEligibility();
      reportStatus(
        "Codex proposal refused because its review context is changing or already stale.",
        "error",
        "Stale draft"
      );
      return;
    }
    if (editorValue() !== proposal.baseProgram) {
      refreshCodexProposalEligibility();
      reportStatus(
        "Codex proposal refused because the editor changed after the request started.",
        "error",
        "Stale draft"
      );
      return;
    }

    elements.codexApplyProposal.disabled = true;
    elements.codexProposalStatus.textContent =
      "Rechecking the NC source, machine profile, tool table, settings, and reference set...";
    let validation;
    try {
      validation = await callDesktop("codexValidateApply", {
        baseFingerprint: proposal.baseFingerprint,
        baseContextFingerprint: proposal.baseContextFingerprint,
        proposedProgram: proposal.program
      });
    } catch (error) {
      validation = { allowed: false, reason: errorMessage(error) };
    }
    if (currentCodexProposal !== proposal) {
      return;
    }
    if (
      !validation?.allowed ||
      proposal.serverStale ||
      codexContextBusy ||
      codexRequestBusy ||
      editorValue() !== proposal.baseProgram
    ) {
      const contextChanged = proposal.serverStale || codexContextBusy || codexRequestBusy;
      proposal.serverStale = true;
      refreshCodexProposalEligibility();
      const reason = validation?.reason || (
        contextChanged
          ? "The Codex reference context changed while the proposal was being checked."
          : "The editor changed while the proposal context was being checked."
      );
      appendCodexMessage("notice", `Proposal not applied: ${reason}`);
      reportStatus(reason, "error", "Stale draft");
      return;
    }
    if (proposal.program === proposal.baseProgram) {
      discardCodexProposal();
      appendCodexMessage("notice", "The proposed program is identical to the current editor text.");
      reportStatus("Codex proposal contained no editor changes.", "ready", "Codex");
      return;
    }

    if (editor) {
      editor.operation(() => editor.setValue(proposal.program));
      editor.focus();
    } else {
      elements.editorTextArea.value = proposal.program;
      handleEditorChange();
      elements.editorTextArea.focus();
    }
    discardCodexProposal();
    appendCodexMessage(
      "notice",
      "Proposed G-code applied to the editor as an unsaved change. Review the source and preview before saving or using it."
    );
    reportStatus(
      "Codex proposal applied to the editor only. REVIEW ONLY; verify every block.",
      "warning",
      "Review"
    );
  }

  function updateCodexSendState() {
    elements.codexSend.disabled = codexRequestBusy || codexContextBusy ||
      !elements.codexPrompt.value.trim();
  }

  function setCodexBusy(busy) {
    codexRequestBusy = Boolean(busy);
    elements.codexPanel.setAttribute("aria-busy", String(codexRequestBusy));
    elements.codexPrompt.readOnly = codexRequestBusy;
    elements.codexStop.disabled = !codexRequestBusy || codexStopRequested;
    elements.codexToggle.classList.toggle("busy", codexRequestBusy);
    updateCodexContextControls();
    updateCodexSendState();
  }

  function setCodexPanelOpen(open, { focusPrompt = true } = {}) {
    const shouldOpen = Boolean(open);
    document.body.classList.toggle("codex-panel-open", shouldOpen);
    elements.codexPanel.hidden = !shouldOpen;
    elements.codexToggle.setAttribute("aria-expanded", String(shouldOpen));
    scheduleLayoutRefresh();
    if (shouldOpen && focusPrompt) {
      window.setTimeout(() => elements.codexPrompt.focus(), 0);
    } else if (!shouldOpen && elements.codexPanel.contains(document.activeElement)) {
      elements.codexToggle.focus();
    }
  }

  function toggleCodexPanel() {
    setCodexPanelOpen(elements.codexPanel.hidden);
  }

  async function newCodexChat() {
    if (codexRequestBusy || codexContextBusy) {
      return;
    }
    if (!await clearCodexAttachments({ quiet: true })) {
      return;
    }
    codexRequestSequence += 1;
    codexConversation = [];
    elements.codexTranscript.querySelectorAll(".codex-message").forEach((message) => {
      message.remove();
    });
    elements.codexEmptyState.hidden = false;
    elements.codexPrompt.value = "";
    showCodexReferences([]);
    discardCodexProposal();
    updateCodexSendState();
    elements.codexPrompt.focus();
    reportStatus("Started a new Codex chat.", "ready", "Codex");
  }

  async function stopCodexRequest() {
    if (!codexRequestBusy || codexStopRequested) {
      return;
    }
    codexStopRequested = true;
    elements.codexStop.disabled = true;
    appendCodexMessage("notice", "Stop requested. No proposed code will be applied automatically.");
    reportStatus("Stopping Codex request...", "working", "Codex");
    try {
      const result = await callDesktop("codexCancel");
      if (!result?.canceled) {
        reportStatus(
          "The request had already finished at the service boundary; its response will still be ignored.",
          "warning",
          "Codex"
        );
      }
    } catch (error) {
      reportStatus(
        `${errorMessage(error)} The response will still be ignored locally.`,
        "error",
        "Codex"
      );
    }
  }

  async function sendCodexRequest(event) {
    event?.preventDefault();
    if (codexRequestBusy || codexContextBusy) {
      return;
    }
    const prompt = elements.codexPrompt.value.trim();
    if (!prompt) {
      elements.codexPrompt.focus();
      return;
    }
    const baseProgram = editorValue();
    const attachmentIds = codexAttachments
      .map(codexAttachmentId)
      .filter(Boolean);
    const priorConversation = boundedCodexConversation(codexConversation);
    const requestId = ++codexRequestSequence;
    codexStopRequested = false;
    discardCodexProposal();
    appendCodexMessage("user", prompt);
    elements.codexPrompt.value = "";
    setCodexBusy(true);
    reportStatus("Codex is reviewing the current editor text...", "working", "Codex");

    try {
      const result = await callDesktop("codexAsk", {
        prompt,
        currentProgram: baseProgram,
        conversation: priorConversation,
        attachmentIds
      });
      if (requestId !== codexRequestSequence || codexStopRequested) {
        return;
      }
      if (result?.error) {
        throw new Error(String(result.error));
      }
      if (!result || typeof result !== "object") {
        throw new Error("Codex returned an invalid response.");
      }
      if (result.canceled) {
        appendCodexMessage("notice", "Codex request canceled. No proposed code was applied.");
        reportStatus("Codex request canceled.", "ready", "Codex");
        return;
      }
      const reply = String(result.reply || result.summary || "Codex completed the review.").trim();
      appendCodexMessage("assistant", reply);
      codexConversation = boundedCodexConversation([
        ...priorConversation,
        { role: "user", content: prompt },
        { role: "assistant", content: reply }
      ]);
      showCodexReferences(
        result.usedReferences,
        Array.isArray(result.uploadedReferences)
          ? result.uploadedReferences
          : result.usedReferences
      );
      renderCodexProposal(result, baseProgram);
      reportStatus(
        result.proposedProgram
          ? "Codex returned review-only proposed G-code. Inspect it before applying."
          : "Codex response received.",
        result.proposedProgram ? "warning" : "ready",
        "Codex"
      );
    } catch (error) {
      if (requestId !== codexRequestSequence) {
        return;
      }
      appendCodexMessage("error", errorMessage(error));
      reportStatus(errorMessage(error), "error", "Codex");
    } finally {
      if (requestId === codexRequestSequence) {
        codexStopRequested = false;
        setCodexBusy(false);
        elements.codexPrompt.focus();
      }
    }
  }

  function handleCommand(commandEvent) {
    const rawCommand = typeof commandEvent === "string"
      ? commandEvent
      : commandEvent?.command || commandEvent?.type;
    const command = String(rawCommand || "").toLowerCase().replace(/[\s:_-]/g, "");
    if (command === "new" || command === "newfile") {
      newDocument();
    } else if (command === "open" || command === "openfile") {
      openDocument();
    } else if (command === "save" || command === "savefile") {
      saveDocument(false);
    } else if (command === "saveas" || command === "savefileas") {
      saveDocument(true);
    } else if (command === "codex" || command === "chat" || command === "togglecodex") {
      toggleCodexPanel();
    } else if (command === "selectreferenceworkspace") {
      void chooseReferenceWorkspace();
    } else if (command === "attachcodexfile") {
      void chooseCodexAttachments();
    } else if (command === "settings" || command === "opensettings") {
      openSettingsDialog();
    } else if (command === "stepviewer" || command === "openstepviewer") {
      window.fanucStepViewer?.open();
    } else if (
      command === "tooltable" ||
      command === "showtooltable" ||
      command === "edittooltable"
    ) {
      openToolTableEditor();
    } else if (command === "revealtooltable") {
      revealToolTable();
    } else if (command === "reload" || command === "reloadxml") {
      performDocumentAction("reloadXml", "Reloading machine and tool data...");
    } else if (command === "fit") {
      byId("fitButton")?.click();
    } else if (command === "run") {
      byId("runButton")?.click();
    } else if (command === "pause") {
      byId("pauseButton")?.click();
    } else if (command === "restart") {
      byId("restartButton")?.click();
    } else if (command === "find") {
      if (editor && isFunction(editor.execCommand)) {
        editor.focus();
        editor.execCommand("find");
      } else {
        elements.editorTextArea.focus();
      }
    } else if (command === "apikey" || command === "setapikey") {
      openSettingsDialog().then(() => elements.settingsApiKey.focus());
    } else if (command === "clearapikey") {
      clearApiKey();
    } else if (command === "focuseditor") {
      editor ? editor.focus() : elements.editorTextArea.focus();
    }
  }

  function bindBridgeEvents() {
    subscribe("onRender", handleRender);
    subscribe("onSelection", handleSelection);
    // Newer preload builds separate editor-originated preview selection from
    // path-originated editor selection to avoid an IPC echo loop.
    subscribe("onPreviewSelection", (selection) => {
      const line = Number(typeof selection === "number" ? selection : selection?.line);
      if (Number.isFinite(line)) {
        dispatchPreviewMessage({ type: "selection", line: Math.trunc(line) });
      }
    });
    subscribe("onDocumentState", applyDocumentState);
    // Before a main-process save (close prompt), send the text still waiting
    // for the typing pause.
    subscribe("onFlushRequest", (request) => {
      flushDocumentUpdate();
      if (isFunction(desktop.confirmFlush)) {
        desktop.confirmFlush(request?.id);
      }
    });
    subscribe("onDecorations", applyDecorations);
    subscribe("onStatus", applyStatus);
    subscribe("onCommand", handleCommand);
  }

  function bindControls() {
    elements.newFile.addEventListener("click", newDocument);
    elements.openFile.addEventListener("click", openDocument);
    elements.saveFile.addEventListener("click", () => saveDocument(false));
    elements.saveAs.addEventListener("click", () => saveDocument(true));
    elements.showToolTable.addEventListener("click", openToolTableEditor);
    elements.codexToggle.addEventListener("click", toggleCodexPanel);
    elements.codexClose.addEventListener("click", () => setCodexPanelOpen(false));
    elements.codexForm.addEventListener("submit", sendCodexRequest);
    elements.codexPrompt.addEventListener("input", updateCodexSendState);
    elements.codexPrompt.addEventListener("keydown", (event) => {
      if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
        event.preventDefault();
        sendCodexRequest(event);
      }
    });
    elements.codexNewChat.addEventListener("click", newCodexChat);
    elements.codexStop.addEventListener("click", stopCodexRequest);
    elements.codexSelectReferenceWorkspace.addEventListener(
      "click",
      chooseReferenceWorkspace
    );
    elements.codexClearReferenceWorkspace.addEventListener(
      "click",
      clearReferenceWorkspace
    );
    elements.codexAttachFile.addEventListener("click", chooseCodexAttachments);
    elements.codexClearAttachments.addEventListener("click", () => {
      void clearCodexAttachments();
    });
    elements.codexApplyProposal.addEventListener("click", applyCodexProposal);
    elements.codexDiscardProposal.addEventListener("click", () => {
      discardCodexProposal({ announce: true });
    });
    elements.codexPanel.addEventListener("keydown", (event) => {
      if (event.key === "Escape" && !event.defaultPrevented) {
        event.preventDefault();
        setCodexPanelOpen(false);
      }
    });
    elements.showSettings.addEventListener("click", openSettingsDialog);
    elements.settingsClose.addEventListener("click", closeSettingsDialog);
    elements.settingsCancel.addEventListener("click", closeSettingsDialog);
    elements.settingsForm.addEventListener("submit", saveSettings);
    elements.clearApiKey.addEventListener("click", clearApiKey);
    elements.toggleApiKey.addEventListener("click", () => {
      const show = elements.settingsApiKey.type === "password";
      elements.settingsApiKey.type = show ? "text" : "password";
      elements.toggleApiKey.textContent = show ? "Hide" : "Show";
      elements.toggleApiKey.setAttribute("aria-pressed", String(show));
      elements.settingsApiKey.focus();
    });
    elements.settingsModal.addEventListener("cancel", (event) => {
      event.preventDefault();
      closeSettingsDialog();
    });
    elements.settingsModal.addEventListener("click", (event) => {
      if (event.target === elements.settingsModal) {
        closeSettingsDialog();
      }
    });
    elements.toolTableClose.addEventListener("click", closeToolTableDialog);
    elements.toolTableCancel.addEventListener("click", closeToolTableDialog);
    elements.toolTableReveal.addEventListener("click", revealToolTable);
    elements.toolTableForm.addEventListener("submit", saveToolTableEditor);
    elements.toolTableModal.addEventListener("cancel", (event) => {
      event.preventDefault();
      closeToolTableDialog();
    });
    elements.toolTableModal.addEventListener("click", (event) => {
      if (event.target === elements.toolTableModal) {
        closeToolTableDialog();
      }
    });
    document.addEventListener("keydown", (event) => {
      if (!(event.ctrlKey || event.metaKey) || event.altKey) {
        return;
      }
      const key = event.key.toLowerCase();
      if (key === "n") {
        event.preventDefault();
        newDocument();
      } else if (key === "o") {
        event.preventDefault();
        openDocument();
      } else if (key === "s") {
        event.preventDefault();
        saveDocument(event.shiftKey);
      } else if (key === "i" && event.shiftKey) {
        event.preventDefault();
        toggleCodexPanel();
      } else if (key === ",") {
        event.preventDefault();
        openSettingsDialog();
      }
    });
  }

  function scheduleLayoutRefresh() {
    window.requestAnimationFrame(() => {
      if (editor) {
        editor.refresh();
      }
      window.dispatchEvent(new Event("resize"));
    });
  }

  function setEditorWidth(percent, persist = true) {
    const width = Math.max(25, Math.min(70, Number(percent) || 43));
    document.documentElement.style.setProperty("--editor-width", `${width}%`);
    elements.splitter.setAttribute("aria-valuenow", String(Math.round(width)));
    if (persist) {
      try {
        window.localStorage.setItem("fanucToolpath.editorWidth", String(width));
      } catch (_error) {
        // Local storage is optional; resizing still works when it is disabled.
      }
    }
    scheduleLayoutRefresh();
  }

  function bindSplitter() {
    try {
      const saved = Number(window.localStorage.getItem("fanucToolpath.editorWidth"));
      if (Number.isFinite(saved)) {
        setEditorWidth(saved, false);
      }
    } catch (_error) {
      // Ignore unavailable local storage in hardened Electron configurations.
    }

    const updateFromPointer = (event) => {
      const bounds = elements.workspace.getBoundingClientRect();
      const percent = ((event.clientX - bounds.left) / Math.max(1, bounds.width)) * 100;
      setEditorWidth(percent, false);
    };
    const stopDragging = (event) => {
      if (!elements.splitter.classList.contains("dragging")) {
        return;
      }
      updateFromPointer(event);
      elements.splitter.classList.remove("dragging");
      try {
        elements.splitter.releasePointerCapture(event.pointerId);
      } catch (_error) {
        // Capture can already be released when the pointer leaves the window.
      }
      const value = elements.splitter.getAttribute("aria-valuenow");
      setEditorWidth(Number(value), true);
    };
    elements.splitter.addEventListener("pointerdown", (event) => {
      elements.splitter.classList.add("dragging");
      elements.splitter.setPointerCapture(event.pointerId);
      updateFromPointer(event);
      event.preventDefault();
    });
    elements.splitter.addEventListener("pointermove", (event) => {
      if (elements.splitter.classList.contains("dragging")) {
        updateFromPointer(event);
      }
    });
    elements.splitter.addEventListener("pointerup", stopDragging);
    elements.splitter.addEventListener("pointercancel", stopDragging);
    elements.splitter.addEventListener("keydown", (event) => {
      const current = Number(elements.splitter.getAttribute("aria-valuenow")) || 43;
      if (event.key === "ArrowLeft") {
        event.preventDefault();
        setEditorWidth(current - (event.shiftKey ? 5 : 2));
      } else if (event.key === "ArrowRight") {
        event.preventDefault();
        setEditorWidth(current + (event.shiftKey ? 5 : 2));
      } else if (event.key === "Home") {
        event.preventDefault();
        setEditorWidth(30);
      } else if (event.key === "End") {
        event.preventDefault();
        setEditorWidth(65);
      }
    });
  }

  async function loadInitialState() {
    if (!isFunction(desktop.getInitialState)) {
      reportStatus("Desktop bridge is unavailable. Restart the application.", "error");
      return;
    }
    try {
      const initial = await callDesktop("getInitialState");
      if (!initial) {
        return;
      }
      applyDocumentState(initial.documentState || initial.document || initial);
      if (initial.settings) {
        settingsState = { ...settingsState, ...initial.settings };
      }
      const codexState = initial.codex && typeof initial.codex === "object"
        ? initial.codex
        : {};
      const savedWorkspacePath = String(
        initial.settings?.referenceWorkspace || ""
      ).trim();
      renderCodexReferenceWorkspace(
        initial.referenceWorkspace ??
        initial.codexReferenceWorkspace ??
        codexState.referenceWorkspace ??
        (savedWorkspacePath
          ? { path: savedWorkspacePath, displayName: basename(savedWorkspacePath) }
          : undefined)
      );
      renderCodexAttachments(
        initial.codexAttachments ?? initial.attachments ?? codexState.attachments ?? []
      );
      if (initial.decorations) {
        applyDecorations(initial.decorations);
      }
      if (initial.status) {
        applyStatus(initial.status);
      }
      if (initial.render) {
        window.setTimeout(() => handleRender(initial.render), 0);
      }
    } catch (error) {
      reportStatus(errorMessage(error), "error");
    }
  }

  function initialize() {
    initializeEditor();
    bindBridgeEvents();
    bindControls();
    bindSplitter();
    updateDocumentChrome();
    applyDecorations(decorationState);
    setCodexBusy(false);
    showCodexReferences([]);
    renderCodexReferenceWorkspace();
    renderCodexAttachments([]);
    loadInitialState();

    window.addEventListener("beforeunload", () => {
      disposers.splice(0).forEach((dispose) => {
        try {
          dispose();
        } catch (_error) {
          // The IPC channel may already be closed during application shutdown.
        }
      });
    });
  }

  initialize();
})();
