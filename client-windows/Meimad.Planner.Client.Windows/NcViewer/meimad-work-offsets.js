/*
 * Meimad Planner: the "Measured work offset" card of the NC viewer (schema v116, exporter
 * definition §20.13). It shows two different things side by side and never mixes them:
 *
 *   Cimatron / NC Work CS (design)  - the Operation's programmed work coordinate system;
 *   Machine / G54 measured          - the physical offset the Machine reported through DPRNT.
 *
 * The measured value is the latest VALID record of the Machine + Case Operation + NC release + offset.
 * The card warns when there is none, when the newest measurement belongs to another NC release, and
 * when a measurement waits for review; a history dialog lists every measurement. "Apply to Home
 * offsets" copies the measured values into the viewer's Home offsets for the viewer machine (the same
 * table the simulation positions the machine with): the opened Operation's work offsets on the Server
 * (schema v120), or this PC's Home offsets for a program that is not an Operation's. It never changes
 * the Cimatron Work CS or the measurement.
 *
 * The first part is pure (no DOM) so the client tests run it in V8; the second part builds the card.
 */
(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.MeimadWorkOffsets = factory();
  }
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  const LINEAR = ["X", "Y", "Z"];
  const ROTARY = ["A", "B", "C"];
  const STATUS_TEXT = {
    VALID: "✔ Valid",
    REVIEW_REQUIRED: "⚠ Review required",
    REJECTED: "✖ Rejected",
    SUPERSEDED: "↺ Superseded",
    UNMATCHED: "? Unmatched"
  };
  const SOURCE_TEXT = {
    DPRNT_TCP: "DPRNT TCP",
    DPRNT_FILE: "DPRNT file",
    DPRNT_FTP: "DPRNT FTP",
    DPRNT_SERIAL: "DPRNT serial",
    DPRNT: "DPRNT",
    API: "Reader API"
  };

  /** Work offset labels as programs and the Server write them ("G54.1 P1", "G54.1P1", "G15 H1"). */
  function sameOffset(a, b) {
    return String(a || "").toUpperCase().replace(/\s+/g, "") === String(b || "").toUpperCase().replace(/\s+/g, "");
  }

  function formatNumber(value) {
    return Number.isFinite(value) ? (Math.abs(value) < 5e-5 ? "0.0000" : value.toFixed(4)) : "—";
  }

  function sourceLabel(source) {
    return SOURCE_TEXT[source] || String(source || "");
  }

  function statusLabel(status) {
    return STATUS_TEXT[status] || String(status || "");
  }

  function newestFirst(records) {
    return records.slice().sort((a, b) => String(b.measuredAt).localeCompare(String(a.measuredAt)));
  }

  /**
   * What the card shows for one work offset. `data` is the host's meimadWorkOffsets answer, `workCs`
   * the scene's Cimatron Work CS (or null), `offset` the chosen work offset ("G54" by default, else the
   * Work CS offset). Returns the design row, the measured row, the warnings and the history.
   */
  function summarize(data, workCs, offset) {
    const records = Array.isArray(data && data.records) ? data.records : [];
    const offsets = [];
    for (const record of records) {
      if (record.workOffset && !offsets.some((value) => sameOffset(value, record.workOffset))) offsets.push(record.workOffset);
    }
    const chosen = offset || (workCs && workCs.workOffset) || (offsets[0] || "G54");
    if (!offsets.some((value) => sameOffset(value, chosen))) offsets.unshift(chosen);
    const forOffset = newestFirst(records.filter((record) => sameOffset(record.workOffset, chosen)));
    const releaseId = data && data.releaseId;
    const valid = forOffset.filter((record) => record.status === "VALID");
    const current = valid.find((record) => !releaseId || record.ncReleaseId === releaseId) || null;
    const otherRelease = current ? null : (valid[0] || null);
    const waiting = forOffset.filter((record) => record.status === "REVIEW_REQUIRED"
      && (!releaseId || record.ncReleaseId === releaseId));
    const machines = [];
    for (const record of valid) {
      if (record.machineId && !machines.includes(record.machineId)) machines.push(record.machineId);
    }

    const warnings = [];
    if (data && data.error) warnings.push({ code: "error", text: data.error });
    if (!current && !otherRelease) {
      warnings.push({
        code: "missing",
        text: data && data.machineLabel
          ? `No measured ${chosen} for this Operation on ${data.machineLabel} yet. Measure on the machine and run the work offset report program.`
          : `No measured ${chosen} for this Operation yet. Measure on the machine and run the work offset report program.`
      });
    } else if (!current && otherRelease) {
      warnings.push({ code: "other_release", text: `The newest measured ${chosen} belongs to another NC release (${otherRelease.ncReleaseLabel || otherRelease.ncReleaseId || "unknown"}). Measure again for this release before trusting it.` });
    }
    if (current && current.ncReleaseIsCurrent === false) {
      warnings.push({ code: "stale_release", text: "This NC release is no longer the current G-code of the Operation." });
    }
    if (waiting.length > 0) {
      warnings.push({ code: "review", text: `${waiting.length} measurement(s) of ${chosen} wait for review (Reports → Work Offsets).` });
    }
    if (!data || !data.machineId) {
      if (machines.length > 1) warnings.push({ code: "machines", text: `Measured on ${machines.length} machines; the newest is shown. Open the program from the machine's assignment to see that machine only.` });
    }

    const shown = current || otherRelease;
    const values = shown ? valuesOf(shown) : null;
    return {
      offset: chosen,
      offsets,
      design: workCs ? {
        workOffset: workCs.workOffset || null,
        origin: workCs.originMm || { x: 0, y: 0, z: 0 },
        rotation: workCs.rotationDeg || { x: 0, y: 0, z: 0 },
        source: workCs.source || null
      } : null,
      measured: shown ? {
        record: shown,
        values,
        otherRelease: !current,
        source: sourceLabel(shown.source),
        machineLabel: shown.machineLabel || (data && data.machineLabel) || null,
        status: statusLabel(shown.status)
      } : null,
      warnings,
      history: forOffset
    };
  }

  function valuesOf(record) {
    const values = {};
    for (const axis of LINEAR.concat(ROTARY)) {
      const value = record[axis.toLowerCase()];
      if (Number.isFinite(value)) values[axis] = value;
    }
    return values;
  }

  /**
   * The measured values in the Home offsets table's units and axes: linear axes in inches when the
   * table is in inches, rotary axes unchanged, only axes the table has.
   */
  function homeValues(record, tableUnits, tableAxes) {
    const inches = /^in/i.test(String(tableUnits || ""));
    const result = {};
    const values = valuesOf(record);
    for (const [axis, value] of Object.entries(values)) {
      if (Array.isArray(tableAxes) && tableAxes.length && !tableAxes.includes(axis)) continue;
      result[axis] = LINEAR.includes(axis) && inches ? Math.round((value / 25.4) * 1e6) / 1e6 : value;
    }
    return result;
  }

  return { summarize, homeValues, sameOffset, formatNumber, sourceLabel, statusLabel, valuesOf };
});

(() => {
  if (typeof document === "undefined" || typeof window === "undefined") return;
  const core = window.MeimadWorkOffsets;
  const desktop = window.fanucDesktop;
  const card = document.getElementById("meimadWorkOffsetsCard");
  const webview = window.chrome && window.chrome.webview;
  if (!core || !desktop || !desktop.meimad || !card || !webview) return;

  // Own request ids far above the bridge's, so the bridge ignores these answers and this script ignores the bridge's.
  let nextId = 1e12;
  const pending = new Map();
  webview.addEventListener("message", (event) => {
    const message = event.data;
    if (!message || message.kind !== "result" || !pending.has(message.id)) return;
    const request = pending.get(message.id);
    pending.delete(message.id);
    if (message.ok) request.resolve(message.value);
    else request.reject(new Error(message.error || "The NC viewer host reported an error."));
  });
  function invoke(method, ...args) {
    return new Promise((resolve, reject) => {
      const id = nextId++;
      pending.set(id, { resolve, reject });
      try {
        webview.postMessage({ kind: "invoke", id, method, args });
      } catch (error) {
        pending.delete(id);
        reject(error);
      }
    });
  }

  const style = document.createElement("style");
  style.textContent = `
    .meimad-work-offsets .wo-row { margin: 6px 0; padding: 6px 8px; border: 1px solid rgba(127,127,127,.35); border-radius: 4px; }
    .meimad-work-offsets .wo-title { font-weight: 600; display: flex; gap: 6px; align-items: baseline; flex-wrap: wrap; }
    .meimad-work-offsets .wo-badge { font-size: 11px; padding: 1px 6px; border-radius: 3px; border: 1px solid currentColor; }
    .meimad-work-offsets .wo-values { font-family: Consolas, monospace; font-size: 12px; margin-top: 3px; }
    .meimad-work-offsets .wo-meta { font-size: 11px; opacity: .8; margin-top: 2px; }
    .meimad-work-offsets .wo-warning { margin: 4px 0; font-size: 12px; }
    .meimad-work-offsets .wo-actions { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 6px; }
    .meimad-work-offsets select { max-width: 100%; }
    dialog.meimad-wo-history { max-width: min(96vw, 1100px); }
    dialog.meimad-wo-history table { border-collapse: collapse; font-size: 12px; }
    dialog.meimad-wo-history th, dialog.meimad-wo-history td { border: 1px solid rgba(127,127,127,.4); padding: 3px 6px; text-align: left; vertical-align: top; }
    dialog.meimad-wo-history td.num { font-family: Consolas, monospace; text-align: right; }
  `;
  document.head.appendChild(style);

  const body = card.querySelector(".meimad-work-offsets-body") || card;
  let data = null;
  let workCs = null;
  let chosen = null;

  function element(tag, text, className) {
    const node = document.createElement(tag);
    if (text !== undefined && text !== null) node.textContent = text;
    if (className) node.className = className;
    return node;
  }

  function axesText(values, rotary) {
    const parts = [];
    for (const axis of rotary ? ["A", "B", "C"] : ["X", "Y", "Z"]) {
      if (Number.isFinite(values[axis])) parts.push(`${axis} ${core.formatNumber(values[axis])}`);
    }
    return parts.join("  ");
  }

  function localTime(value) {
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? String(value || "") : date.toLocaleString();
  }

  function homeTable() {
    // `model` is the vendored preview's current program model (media/preview.js).
    const current = typeof model !== "undefined" ? model : null; // eslint-disable-line no-undef
    return current && current.workOffsetTable ? { model: current, table: current.workOffsetTable } : null;
  }

  function applyToHome(record) {
    const home = homeTable();
    if (!home || home.model.kind !== "mill") {
      setStatus("Home offsets apply to a milling program with a work offset table.");
      return;
    }
    const table = home.table;
    const row = (table.rows || []).find((candidate) => core.sameOffset(candidate.label, record.workOffset));
    if (!row) {
      setStatus(`The program's Home offsets have no ${record.workOffset} row.`);
      return;
    }
    const axes = Object.keys(row.values || {});
    const saved = typeof savedWorkOffsets === "function" ? savedWorkOffsets() : {}; // eslint-disable-line no-undef
    saved[row.label] = core.homeValues(record, table.units, axes);
    const vscode = typeof window.acquireVsCodeApi === "function" ? window.acquireVsCodeApi() : null;
    if (!vscode || !home.model.machineDefinition) {
      setStatus("The viewer cannot save Home offsets right now.");
      return;
    }
    vscode.postMessage({ type: "workOffsetsChanged", machine: home.model.machineDefinition.id, offsets: saved });
    setStatus(`Measured ${record.workOffset} copied to the Home offsets of ${home.model.machineDefinition.name || home.model.machineDefinition.id}.`);
  }

  let statusNode = null;
  function setStatus(text) {
    if (statusNode) statusNode.textContent = text || "";
  }

  function showHistory(summary) {
    const dialog = element("dialog", null, "meimad-wo-history");
    dialog.appendChild(element("h3", `Measured ${summary.offset}: history`));
    const table = element("table");
    const head = element("tr");
    for (const title of ["Measured", "Status", "Machine", "NC release", "X", "Y", "Z", "A", "B", "C", "Source", "Reason"]) head.appendChild(element("th", title));
    table.appendChild(head);
    for (const record of summary.history) {
      const tr = element("tr");
      tr.appendChild(element("td", localTime(record.measuredAt)));
      tr.appendChild(element("td", core.statusLabel(record.status)));
      tr.appendChild(element("td", record.machineLabel || ""));
      tr.appendChild(element("td", record.ncReleaseLabel || record.ncReleaseId || ""));
      for (const axis of ["x", "y", "z", "a", "b", "c"]) tr.appendChild(element("td", Number.isFinite(record[axis]) ? core.formatNumber(record[axis]) : "", "num"));
      tr.appendChild(element("td", core.sourceLabel(record.source)));
      tr.appendChild(element("td", record.reasonText || ""));
      table.appendChild(tr);
    }
    if (!summary.history.length) {
      const tr = element("tr");
      const td = element("td", "No measurements yet.");
      td.colSpan = 12;
      tr.appendChild(td);
      table.appendChild(tr);
    }
    dialog.appendChild(table);
    const close = element("button", "Close");
    close.type = "button";
    close.addEventListener("click", () => dialog.close());
    dialog.appendChild(element("p")).appendChild(close);
    dialog.addEventListener("close", () => dialog.remove());
    document.body.appendChild(dialog);
    dialog.showModal();
  }

  function render() {
    body.replaceChildren();
    if (!data || !data.available) {
      body.appendChild(element("p", (data && data.reason) || "Measured work offsets are not available here.", "meimad-note"));
      return;
    }
    const summary = core.summarize(data, workCs, chosen);
    chosen = summary.offset;

    const pick = element("label", "Offset ");
    const select = element("select");
    select.title = "Work offset to compare";
    for (const offset of summary.offsets) {
      const option = element("option", offset);
      option.value = offset;
      select.appendChild(option);
    }
    select.value = summary.offset;
    select.addEventListener("change", () => {
      chosen = select.value;
      render();
    });
    pick.appendChild(select);
    body.appendChild(pick);

    const design = element("div", null, "wo-row wo-design");
    const designTitle = element("div", null, "wo-title");
    designTitle.appendChild(element("span", "Cimatron / NC Work CS (design)"));
    designTitle.appendChild(element("span", "design", "wo-badge"));
    design.appendChild(designTitle);
    if (summary.design) {
      const origin = summary.design.origin;
      const rotation = summary.design.rotation;
      design.appendChild(element("div", `${summary.design.workOffset || "Work CS"}  X ${core.formatNumber(origin.x)}  Y ${core.formatNumber(origin.y)}  Z ${core.formatNumber(origin.z)}`, "wo-values"));
      design.appendChild(element("div", `Rotation X ${core.formatNumber(rotation.x)}  Y ${core.formatNumber(rotation.y)}  Z ${core.formatNumber(rotation.z)} (part model coordinates)`, "wo-values"));
    } else {
      design.appendChild(element("div", "No Cimatron Work CS is saved for this Operation.", "wo-meta"));
    }
    body.appendChild(design);

    const measured = element("div", null, "wo-row wo-measured");
    const title = element("div", null, "wo-title");
    title.appendChild(element("span", `Machine / ${summary.offset} measured`));
    if (summary.measured) {
      title.appendChild(element("span", summary.measured.source, "wo-badge"));
      title.appendChild(element("span", summary.measured.status, "wo-badge"));
    }
    measured.appendChild(title);
    if (summary.measured) {
      const values = summary.measured.values;
      measured.appendChild(element("div", axesText(values, false), "wo-values"));
      const rotary = axesText(values, true);
      if (rotary) measured.appendChild(element("div", rotary, "wo-values"));
      const record = summary.measured.record;
      const release = record.ncReleaseLabel || record.ncReleaseId || "?";
      measured.appendChild(element("div", summary.measured.machineLabel
        ? `Measured ${localTime(record.measuredAt)} on ${summary.measured.machineLabel} · NC release ${release}`
        : `Measured ${localTime(record.measuredAt)} · NC release ${release}`, "wo-meta"));
    } else {
      measured.appendChild(element("div", "Not measured", "wo-meta"));
    }
    body.appendChild(measured);

    for (const warning of summary.warnings) {
      const line = element("p", null, `wo-warning wo-warning-${warning.code}`);
      line.setAttribute("role", "alert");
      const icon = element("span", "⚠ ");
      icon.setAttribute("aria-hidden", "true");
      line.appendChild(icon);
      line.appendChild(element("span", warning.text));
      body.appendChild(line);
    }

    const actions = element("div", null, "wo-actions");
    const history = element("button", "History…");
    history.type = "button";
    history.title = "Every measurement of this offset, newest first";
    history.addEventListener("click", () => showHistory(summary));
    actions.appendChild(history);
    const apply = element("button", "Apply to Home offsets");
    apply.type = "button";
    apply.title = "Copy the measured values into this PC's Home offsets of the viewer machine, which the simulation uses; the Cimatron Work CS is not changed";
    apply.disabled = !summary.measured;
    apply.addEventListener("click", () => {
      if (summary.measured) applyToHome(summary.measured.record);
    });
    actions.appendChild(apply);
    const refresh = element("button", "Refresh");
    refresh.type = "button";
    refresh.title = "Read the measured work offsets from the Server again";
    refresh.addEventListener("click", () => load());
    actions.appendChild(refresh);
    body.appendChild(actions);
    statusNode = element("p", "", "meimad-note");
    statusNode.setAttribute("role", "status");
    body.appendChild(statusNode);
  }

  async function load() {
    try {
      const [answer, scene] = await Promise.all([
        invoke("meimadWorkOffsets"),
        desktop.meimad.scene ? desktop.meimad.scene.load().catch(() => null) : Promise.resolve(null)
      ]);
      data = answer;
      workCs = scene && scene.workCs ? scene.workCs : null;
    } catch (error) {
      data = { available: true, error: `Measured work offsets could not be read: ${error.message}`, records: [] };
    }
    card.hidden = false;
    render();
  }

  load();
})();
