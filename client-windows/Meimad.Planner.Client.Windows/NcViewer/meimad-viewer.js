"use strict";

// Meimad Planner additions to the viewer page: read-only release mode, "Apply Meimad Planner
// Format" and "Use for G-code release". Loaded after renderer.js, which owns the CodeMirror editor.
(() => {
  const desktop = window.fanucDesktop;
  const meimad = desktop && desktop.meimad;
  if (!meimad) return;
  const byId = (id) => document.getElementById(id);
  const elements = {
    badge: byId("meimadModeBadge"),
    format: byId("meimadFormatButton"),
    useForRelease: byId("meimadUseForReleaseButton"),
    newFile: byId("newFileButton"),
    openFile: byId("openFileButton"),
    saveFile: byId("saveFileButton"),
    saveAs: byId("saveAsButton"),
    modal: byId("meimadFormatModal"),
    form: byId("meimadFormatForm"),
    dialect: byId("meimadDialect"),
    dialectNote: byId("meimadDialectNote"),
    releaseNote: byId("meimadReleaseNote"),
    report: byId("meimadFormatReport"),
    validation: byId("meimadValidation"),
    changes: byId("meimadChanges"),
    warnings: byId("meimadWarnings"),
    warningsHeading: byId("meimadWarningsHeading"),
    feedback: byId("meimadFormatFeedback"),
    apply: byId("meimadFormatApplyButton"),
    cancel: byId("meimadFormatCancelButton"),
    close: byId("meimadFormatCloseButton"),
    editCopy: byId("meimadEditCopyButton"),
    release: byId("meimadReleaseButton"),
    releaseModal: byId("meimadReleaseModal"),
    releaseForm: byId("meimadReleaseForm"),
    releaseOperation: byId("meimadReleaseOperation"),
    releasePostprocessor: byId("meimadReleasePostprocessor"),
    releaseScope: byId("meimadReleaseScope"),
    releaseScopeNote: byId("meimadReleaseScopeNote"),
    releaseComment: byId("meimadReleaseComment"),
    releaseProcessDescription: byId("meimadReleaseProcessDescription"),
    releaseProcessDescriptionLabel: byId("meimadReleaseProcessDescriptionLabel"),
    releaseToolTable: byId("meimadReleaseToolTable"),
    releaseToolTableBrowse: byId("meimadReleaseToolTableBrowse"),
    releaseReuseTools: byId("meimadReleaseReuseTools"),
    releaseConfirmTools: byId("meimadReleaseConfirmTools"),
    releaseConfirmRevision: byId("meimadReleaseConfirmRevision"),
    releaseReport: byId("meimadReleaseReport"),
    releaseValidation: byId("meimadReleaseValidation"),
    releaseFeedback: byId("meimadReleaseFeedback"),
    releaseCheck: byId("meimadReleaseCheckButton"),
    releaseCancel: byId("meimadReleaseCancelButton"),
    releaseClose: byId("meimadReleaseCloseButton"),
    releaseSubmit: byId("meimadReleaseSubmitButton"),
    statusState: byId("statusState"),
    statusMessage: byId("statusMessage")
  };
  let state = { readOnly: false, canFormat: false, canUseForRelease: false, canRelease: false, canValidate: false, release: null, dialects: [], dialect: "HAAS_NGC" };
  let busy = false;

  const editor = () => document.querySelector(".CodeMirror")?.CodeMirror;
  const editorText = () => {
    const instance = editor();
    return instance ? instance.getValue() : byId("ncEditor").value;
  };
  const message = (error) => (error && error.message) || String(error || "Unknown error");

  function reportStatus(text, level) {
    const labels = { ready: "Ready", success: "Done", working: "Working", warning: "Review", error: "Error" };
    elements.statusState.className = `status-state ${level}`;
    elements.statusState.querySelector("span").textContent = labels[level] || "Ready";
    elements.statusMessage.textContent = text;
  }

  function applyMode(next) {
    state = { ...state, ...(next || {}) };
    document.body.classList.toggle("meimad-read-only", Boolean(state.readOnly));
    editor()?.setOption("readOnly", state.readOnly ? true : false);
    elements.badge.textContent = state.readOnly ? "\u{1F512} READ-ONLY RELEASE" : "✎ EDITABLE";
    elements.badge.className = `meimad-mode-badge ${state.readOnly ? "read-only" : "editable"}`;
    elements.badge.title = state.readOnly
      ? `${state.source || "Immutable Server release"}. The release cannot be changed; "Save copy as" keeps a local copy.`
      : state.savesToCaseFolder
        ? "Local program. Save puts it in the Case Working Folder, in the Gcode folder of the revision it becomes. Release it from here or through the Release G-code form."
        : "Local program. Save it, then release it through the Release G-code form.";
    elements.newFile.hidden = Boolean(state.readOnly);
    elements.openFile.hidden = Boolean(state.readOnly);
    elements.saveFile.hidden = Boolean(state.readOnly);
    elements.saveAs.textContent = state.readOnly ? "Save copy as" : "Save as";
    elements.useForRelease.hidden = !state.canUseForRelease;
    // A release is edited on a local copy first; the copy (or any editable program) can be
    // released as a new revision of the Operation that opened the viewer.
    elements.editCopy.hidden = !state.readOnly;
    elements.release.hidden = !state.canRelease || Boolean(state.readOnly);
    elements.release.disabled = busy;
    elements.format.disabled = !state.canFormat || busy;
    elements.format.title = state.canFormat
      ? "Insert the Meimad identity header, verification hook, event context, part-number output line and cycle markers. The Server checks the result."
      : "Connect to the Meimad Server to apply the Meimad Planner format.";
  }

  function fillDialects() {
    elements.dialect.replaceChildren();
    for (const option of state.dialects || []) {
      const element = document.createElement("option");
      element.value = option.id;
      element.textContent = option.name;
      elements.dialect.appendChild(element);
    }
    elements.dialect.value = state.dialect;
    elements.dialectNote.textContent = state.dialectKnown
      ? "Preselected from the Machine configuration. The dialect decides POPEN/PCLOS (FANUC) and DPRNT versus PUT / WRITE C (Okuma)."
      : "No single Machine dialect is known for this program; check the control before applying.";
  }

  function fillList(list, values, emptyText, marker) {
    list.replaceChildren();
    const entries = (values || []).filter(Boolean);
    for (const value of entries.length ? entries : [emptyText]) {
      const item = document.createElement("li");
      if (entries.length && marker) {
        const icon = document.createElement("span");
        icon.className = "meimad-marker";
        icon.setAttribute("aria-hidden", "true");
        icon.textContent = marker;
        item.appendChild(icon);
      }
      item.appendChild(document.createTextNode(value));
      list.appendChild(item);
    }
  }

  function showReport(result) {
    elements.report.hidden = false;
    const valid = Boolean(result.validation && result.validation.isValid);
    elements.validation.className = `meimad-validation ${valid ? "valid" : "invalid"}`;
    elements.validation.textContent = valid
      ? "✔ Valid Meimad canonical template (protocol v2). Release it through the Release G-code form."
      : `✖ Not yet valid: ${result.validation?.message || "unknown problem"} (${result.validation?.code || "validation"})`;
    fillList(elements.changes, result.changes, result.changed ? "" : "Nothing to add: the program already has every Meimad element.", "+");
    fillList(elements.warnings, result.warnings, "No placement warnings.", "!");
    elements.warningsHeading.hidden = false;
  }

  function openFormatDialog() {
    if (!state.canFormat) return;
    fillDialects();
    elements.releaseNote.hidden = !state.readOnly;
    elements.report.hidden = true;
    elements.feedback.textContent = "";
    elements.apply.disabled = false;
    if (typeof elements.modal.showModal === "function") elements.modal.showModal();
    else elements.modal.setAttribute("open", "");
    window.setTimeout(() => elements.dialect.focus(), 0);
  }

  function closeFormatDialog() {
    if (typeof elements.modal.close === "function") elements.modal.close();
    else elements.modal.removeAttribute("open");
  }

  async function applyFormat(event) {
    event?.preventDefault();
    if (busy) return;
    busy = true;
    elements.apply.disabled = true;
    elements.feedback.textContent = "Applying the Meimad Planner format...";
    try {
      const result = await meimad.applyFormat(editorText(), elements.dialect.value);
      showReport(result);
      elements.feedback.textContent = result.becameCopy
        ? "Applied to an unsaved local copy. The Server release is unchanged."
        : "Applied. Save the program before releasing it.";
    } catch (error) {
      elements.feedback.textContent = message(error);
    } finally {
      busy = false;
      elements.apply.disabled = false;
      applyMode();
    }
  }

  async function useForRelease() {
    if (busy) return;
    busy = true;
    reportStatus("Saving the program for the release form...", "working");
    try {
      const result = await meimad.useForRelease(editorText());
      if (result?.canceled) reportStatus("Operation canceled.", "ready");
    } catch (error) {
      reportStatus(message(error), "error");
    } finally {
      busy = false;
    }
  }

  // ----- Edit copy / Release to Server ---------------------------------------------------------

  async function editCopy() {
    if (busy) return;
    busy = true;
    try {
      await meimad.editCopy();
    } catch (error) {
      reportStatus(message(error), "error");
    } finally {
      busy = false;
      applyMode();
    }
  }

  function showReleaseValidation(validation) {
    const valid = Boolean(validation && validation.isValid);
    elements.releaseReport.hidden = false;
    elements.releaseValidation.className = `meimad-validation ${valid ? "valid" : "invalid"}`;
    elements.releaseValidation.textContent = valid
      ? "✔ Valid Meimad canonical template (protocol v2)."
      : `✖ Not a valid Meimad template: ${validation?.message || "unknown problem"} (${validation?.code || "validation"}). Apply the Meimad Planner format, then release again.`;
    return valid;
  }

  function isNewRevision() {
    return elements.releaseScope.value === "NEW_PROCESS_REVISION";
  }

  function refreshReleaseFields() {
    const context = state.release || {};
    const newRevision = isNewRevision();
    const hasActive = Boolean(context.hasActiveProcessRevision);
    elements.releaseProcessDescriptionLabel.hidden = !newRevision;
    elements.releaseReuseTools.disabled = !newRevision || !hasActive;
    if (!newRevision) elements.releaseReuseTools.checked = hasActive;
    if (!hasActive) elements.releaseReuseTools.checked = false;
    elements.releaseConfirmRevision.disabled = !newRevision;
    if (!newRevision) elements.releaseConfirmRevision.checked = false;
    const toolTableRequired = !hasActive || (newRevision && !elements.releaseReuseTools.checked);
    elements.releaseToolTable.required = toolTableRequired;
    elements.releaseScopeNote.textContent = newRevision
      ? "A new process revision: G-code releases of other postprocessors will not be current for it until they are regenerated. A tool table must be uploaded unless the active tool table is reused."
      : hasActive
        ? "A local post revision keeps the active manufacturing process and its tool table; other postprocessors are not affected."
        : "This Operation has no active process yet: the first release creates process revision 1 and needs its tool-table file.";
  }

  function openReleaseDialog() {
    if (!state.canRelease || state.readOnly) return;
    const context = state.release || {};
    elements.releaseOperation.textContent = `${context.operation ? `${context.operation}. ` : ""}Releases the saved program as a new G-code release of this Operation. The Server checks the Meimad canonical format first; nothing is uploaded when the check fails. Edit Mode in the Planner is required.${state.savesToCaseFolder ? " Before the upload, the program is saved in the Case Working Folder, in the Gcode folder of the new revision." : ""}`;
    elements.releasePostprocessor.replaceChildren();
    for (const target of context.postprocessors || []) {
      const option = document.createElement("option");
      option.value = target.id;
      option.textContent = target.status ? `${target.name} — ${target.status}` : target.name;
      elements.releasePostprocessor.appendChild(option);
    }
    if (context.defaultPostprocessorId) elements.releasePostprocessor.value = context.defaultPostprocessorId;
    const hasActive = Boolean(context.hasActiveProcessRevision);
    elements.releaseScope.value = hasActive ? "LOCAL_POST_REVISION" : "NEW_PROCESS_REVISION";
    elements.releaseScope.disabled = !hasActive;
    elements.releaseToolTable.value = context.toolTableFilePath || "";
    elements.releaseConfirmTools.checked = false;
    elements.releaseReport.hidden = true;
    elements.releaseFeedback.textContent = "";
    elements.releaseSubmit.disabled = false;
    elements.releaseSubmit.textContent = "Release";
    refreshReleaseFields();
    if (typeof elements.releaseModal.showModal === "function") elements.releaseModal.showModal();
    else elements.releaseModal.setAttribute("open", "");
    window.setTimeout(() => elements.releaseComment.focus(), 0);
  }

  function closeReleaseDialog() {
    if (typeof elements.releaseModal.close === "function") elements.releaseModal.close();
    else elements.releaseModal.removeAttribute("open");
  }

  async function checkFormat() {
    if (busy) return;
    busy = true;
    elements.releaseFeedback.textContent = "Checking the Meimad canonical format...";
    try {
      const result = await meimad.validate(editorText());
      showReleaseValidation(result.validation);
      elements.releaseFeedback.textContent = "";
    } catch (error) {
      elements.releaseFeedback.textContent = message(error);
    } finally {
      busy = false;
    }
  }

  function releaseOptions() {
    return {
      postprocessorId: elements.releasePostprocessor.value,
      changeScope: elements.releaseScope.value,
      releaseComment: elements.releaseComment.value.trim(),
      processChangeDescription: elements.releaseProcessDescription.value.trim(),
      confirmNewProcessRevision: elements.releaseConfirmRevision.checked,
      reuseActiveToolTable: elements.releaseReuseTools.checked,
      confirmToolTable: elements.releaseConfirmTools.checked,
      toolTableFilePath: elements.releaseToolTable.value.trim()
    };
  }

  async function releaseToServer(event) {
    event?.preventDefault();
    if (busy) return;
    const options = releaseOptions();
    if (!options.postprocessorId) {
      elements.releaseFeedback.textContent = "Choose the postprocessor.";
      return;
    }
    if (!options.releaseComment) {
      elements.releaseFeedback.textContent = "A release comment is required.";
      elements.releaseComment.focus();
      return;
    }
    if (!options.confirmToolTable) {
      elements.releaseFeedback.textContent = "Confirm the exact physical tool table used for this release.";
      return;
    }
    if (options.changeScope === "NEW_PROCESS_REVISION" && (!options.confirmNewProcessRevision || !options.processChangeDescription)) {
      elements.releaseFeedback.textContent = "A new process revision requires its confirmation and a process change description.";
      return;
    }
    busy = true;
    elements.releaseSubmit.disabled = true;
    elements.releaseFeedback.textContent = "Checking the format, saving and releasing...";
    try {
      const result = await meimad.release(editorText(), options);
      if (result.blocked) {
        showReleaseValidation(result.validation);
        elements.releaseFeedback.textContent = "Not released.";
        elements.releaseSubmit.disabled = false;
      } else if (result.canceled) {
        elements.releaseFeedback.textContent = "Release canceled (the program was not saved).";
        elements.releaseSubmit.disabled = false;
      } else if (result.released) {
        elements.releaseReport.hidden = false;
        elements.releaseValidation.className = "meimad-validation valid";
        elements.releaseValidation.textContent = `✔ ${result.message}`;
        elements.releaseFeedback.textContent = "";
        elements.releaseSubmit.textContent = "Released";
      } else {
        elements.releaseReport.hidden = false;
        elements.releaseValidation.className = "meimad-validation invalid";
        elements.releaseValidation.textContent = `✖ ${result.message}`;
        elements.releaseFeedback.textContent = "Not released.";
        elements.releaseSubmit.disabled = false;
      }
    } catch (error) {
      elements.releaseFeedback.textContent = message(error);
      elements.releaseSubmit.disabled = false;
    } finally {
      busy = false;
      applyMode();
    }
  }

  elements.editCopy.addEventListener("click", editCopy);
  elements.release.addEventListener("click", openReleaseDialog);
  elements.releaseForm.addEventListener("submit", releaseToServer);
  elements.releaseCheck.addEventListener("click", checkFormat);
  elements.releaseCancel.addEventListener("click", closeReleaseDialog);
  elements.releaseClose.addEventListener("click", closeReleaseDialog);
  elements.releaseScope.addEventListener("change", refreshReleaseFields);
  elements.releaseReuseTools.addEventListener("change", refreshReleaseFields);
  elements.releaseToolTableBrowse.addEventListener("click", async () => {
    try {
      const result = await meimad.chooseToolTable();
      if (result && !result.canceled && result.path) elements.releaseToolTable.value = result.path;
    } catch (error) {
      elements.releaseFeedback.textContent = message(error);
    }
  });
  elements.releaseModal.addEventListener("cancel", (event) => {
    event.preventDefault();
    closeReleaseDialog();
  });

  elements.format.addEventListener("click", openFormatDialog);
  elements.useForRelease.addEventListener("click", useForRelease);
  elements.form.addEventListener("submit", applyFormat);
  elements.cancel.addEventListener("click", closeFormatDialog);
  elements.close.addEventListener("click", closeFormatDialog);
  elements.modal.addEventListener("cancel", (event) => {
    event.preventDefault();
    closeFormatDialog();
  });
  // Codex is not part of Meimad Planner: keep its shortcut from opening an empty panel.
  document.addEventListener("keydown", (event) => {
    if ((event.ctrlKey || event.metaKey) && event.shiftKey && event.key.toLowerCase() === "i") {
      event.preventDefault();
      event.stopImmediatePropagation();
    }
  }, true);

  // The upstream settings dialog lists a program-memory folder per mill only (its lathe
  // interpreter has no subprogram support). Meimad inlines lathe M98/G65 calls from that
  // folder, so lathes get the same row: same markup, same data-machine-id input the dialog
  // saves, same Browse... handler.
  const programMemory = byId("settingsProgramMemory");
  async function addLatheProgramMemoryRows() {
    if (!programMemory || programMemory.querySelector("input[data-meimad-lathe]")) return;
    let settings;
    try {
      settings = await desktop.getSettings();
    } catch {
      return;
    }
    const folders = (settings && settings.programMemory) || {};
    const lathes = (Array.isArray(settings?.machines) ? settings.machines : []).filter((machine) => machine.type !== "mill");
    for (const machine of lathes) {
      if (programMemory.querySelector(`input[data-machine-id="${machine.id}"]`)) continue;
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
      input.placeholder = "Folder with the machine's programs (O9xxx subprograms, optional)";
      input.dataset.machineId = machine.id;
      input.dataset.meimadLathe = "1";
      input.value = folders[machine.id] || "";
      const browse = document.createElement("button");
      browse.type = "button";
      browse.className = "field-button";
      browse.textContent = "Browse...";
      browse.addEventListener("click", async () => {
        try {
          const result = await desktop.chooseProgramMemoryFolder(machine.id);
          if (result && !result.canceled && result.path) input.value = result.path;
        } catch (error) {
          reportStatus(message(error), "error");
        }
      });
      row.append(input, browse);
      label.appendChild(row);
      programMemory.appendChild(label);
    }
  }
  if (programMemory && typeof MutationObserver === "function") {
    new MutationObserver(() => { addLatheProgramMemoryRows(); }).observe(programMemory, { childList: true });
  }

  meimad.onMode(applyMode);
  meimad.state().then(applyMode).catch((error) => reportStatus(message(error), "error"));
})();
