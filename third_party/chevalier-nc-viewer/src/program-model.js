"use strict";

// Chooses the interpreter for the selected machine and gives both machine
// types the same preview contract: model.kind, model.machineDefinition,
// model.machineRows (sidebar), model.statsRows (sidebar) and model.view.

const { parseProgram, toolDefinitionsForSource } = require("./parser");
const { millToolDefinitionsForSource, parseHaasMillProgram } = require("./haas-mill");
const { publicMachine, resolveMachineSelection, defaultRegistry, machineSummaries } = require("./machines");

const MM_PER_INCH = 25.4;

function formatMm(value) {
  return Number.isFinite(value) ? `${Number(value.toFixed(3))}` : "—";
}

function axisRange(axis, unit) {
  if (!axis) return "—";
  if (!Number.isFinite(axis.min) && !Number.isFinite(axis.max)) return "continuous";
  return `${formatMm(axis.min)} to ${formatMm(axis.max)} ${unit}`;
}

function latheStatsRows(model) {
  const meta = model.meta || {};
  const allowance = (value) => value
    ? `U${Number(value.xDiameter).toFixed(3)} / W${Number(value.z).toFixed(3)}`
    : "—";
  return [
    ["NC lines", meta.lineCount || 0],
    ["Executed blocks", meta.executedBlockCount || 0],
    ["Macro jumps", meta.jumpCount || 0],
    ["Path segments", meta.segmentCount || 0],
    ["G71 rough passes", meta.g71PassCount || 0],
    ["G71 rough-finish", meta.g71RoughFinishPassCount || 0],
    ["G71 finish U/W", allowance(meta.g71FinishAllowance)],
    ["G72 facing passes", meta.g72PassCount || 0],
    ["G72 rough-finish", meta.g72RoughFinishPassCount || 0],
    ["G72 finish U/W", allowance(meta.g72FinishAllowance)],
    ["G73 copy passes", meta.g73PassCount || 0],
    ["G73 finish U/W", allowance(meta.g73FinishAllowance)],
    ["G70 finish cycles", meta.g70FinishCount || 0],
    ["G74 drill pecks", meta.g74PeckCount || 0],
    ["G75 grooves/pecks", `${meta.g75GrooveCount || 0} / ${meta.g75PeckCount || 0}`],
    ["G76 rough/finish", `${meta.g76RoughPassCount || 0} / ${meta.g76FinishPassCount || 0}`],
    ["G52 changes", meta.g52ChangeCount || 0],
    ["Estimated cycle", meta.estimatedCycleSeconds, "duration"],
    ["Turret indexes", meta.turretIndexCount || 0],
    ["Unestimated moves", meta.unestimatedSegmentCount || 0],
    ["Compensated paths", meta.compensatedSegmentCount || 0],
    ["Preview errors", (model.errors || []).length],
    ["Tools found", (model.tools || []).length],
    ["Units", meta.units || "mm"]
  ];
}

function latheMachineRows(model, machine) {
  const parsed = model.machine || {};
  const fanuc = parsed.fanucParameters || {};
  const behavior = model.meta?.g71Behavior || {};
  return [
    ["Machine", machine?.name || "—"],
    ["Control", parsed.control || machine?.controlDefinition?.name || "—"],
    ["Kinematics", machine?.kinematics?.description ? "Lathe: Z carriage, X cross-slide, turret; C spindle" : "—"],
    ["X travel (dia)", axisRange(machine?.axes?.X, "mm")],
    ["Z travel", axisRange(machine?.axes?.Z, "mm")],
    ["Rapid", `${parsed.rapidRate ?? "—"} mm/min`],
    ["Turret index", `${parsed.turretIndexSeconds ?? "—"} s`],
    ["G30", `X${model.settings?.g30X ?? "—"} / Z${model.settings?.g30Z ?? "—"}`],
    ["Tailstock", parsed.tailstockPresent ? "Yes" : "No"],
    ["Retract order", parsed.retractOrder || "—"],
    ["5104#2 FCK", String(fanuc.fck ?? "—")],
    ["5105 RF1/RF2", `${fanuc.rf1 ?? "—"} / ${fanuc.rf2 ?? "—"}`],
    ["5106#2 NT1", String(fanuc.nt1 ?? "—")],
    ["5107#0 ASU", String(fanuc.asu ?? "—")],
    ["5108 R16/DTP/NSP", `${fanuc.r16 ?? "—"} / ${fanuc.dtp ?? "—"} / ${fanuc.nsp ?? "—"}`],
    ["Effective R16", String(behavior.effectiveR16 ?? "—")],
    ["G71 direction", behavior.radialDirection || "—"],
    ["G72 direction", model.meta?.g72Behavior?.radialDirection || "—"],
    ["Machine data", parsed.sourcePath || "Bundled CNC-PARA.TXT", "path"],
    ["Machine definition", machine?.sourcePath || "—", "path"]
  ];
}

function fanucMillRows(model, machine, resolveSubprogram) {
  const axes = machine.axes || {};
  const center = machine.mrzp || {};
  const parameters = machine.parameters || {};
  const flag = (key, on, off) => (Number(parameters[key]) === 1 ? on : off);
  const reference = machine.referencePositions?.["1"] || {};
  const travel = ["X", "Y", "Z"]
    .map((axis) => axes[axis] ? `${axis} ${formatMm(axes[axis].min)}..${formatMm(axes[axis].max)}` : undefined)
    .filter(Boolean)
    .join(", ");
  const rotaryNames = Object.entries(axes).filter(([, axis]) => axis.type === "rotary").map(([name]) => name);
  return [
    ["Machine", machine.name],
    ["Control", machine.controlDefinition?.name || machine.control],
    ["Kinematics", machine.kinematics?.type === "table-table" ? `Table-table: ${rotaryNames.join(" carrying ")}` : machine.kinematics?.type || "—"],
    ["Machine travels", `${travel} mm`],
    ...rotaryNames.map((name) => [`${name} range`, axisRange(axes[name], "°")]),
    ["Rapid X/Y/Z", `${formatMm(axes.X?.rapidRate)} mm/min`],
    [center.label || "Rotary centre", `X${formatMm(center.x)} Y${formatMm(center.y)} Z${formatMm(center.z)}${center.verified === false ? " (placeholder)" : ""}`],
    ["Reference (1240)", `X${formatMm(reference.X)} Y${formatMm(reference.Y)} Z${formatMm(reference.Z)}`],
    ["Rotary direction", machine.rotaryConvention?.verified === false ? "ISO table convention (unverified)" : "Verified"],
    ["Spindle", `${machine.spindle?.maxRpm ?? "—"} rpm ${machine.spindle?.taper || ""}`.trim()],
    ["Tool change", `${machine.toolChanger?.changeSeconds ?? "—"} s (${machine.toolChanger?.capacity ?? "?"} pockets)`],
    ["Default tool length", `${formatMm(machine.defaultToolLength)} mm`],
    ["1401#1 rapids", flag("1401#1", "Linear (LRP 1)", "Each axis at its rate (LRP 0)")],
    ["3401#0 decimals", flag("3401#0", "Calculator (DPI 1)", "Least increment (DPI 0)")],
    ["5004#2 D offsets", flag("5004#2", "Diameter (ODI 1)", "Radius (ODI 0)")],
    ["Offset memory", `C (6000#3 V15 ${Number(parameters["6000#3"]) === 1 ? 1 : 0})`],
    ["Program memory", resolveSubprogram?.memoryFolders?.length ? resolveSubprogram.memoryFolders.join("; ") : "Not set (fanucToolpath.programMemory)", resolveSubprogram?.memoryFolders?.length ? "path" : undefined],
    ["Drawing frame", model.frame ? `${model.frame.label} at X${formatMm(model.frame.x)} Y${formatMm(model.frame.y)} Z${formatMm(model.frame.z)} (${model.meta?.units || "mm"})` : "—"],
    ["Machine definition", machine.sourcePath || "—", "path"]
  ];
}

function millMachineRows(model, machine, resolveSubprogram) {
  if (machine.controlDefinition?.interpreter === "fanuc-mill") return fanucMillRows(model, machine, resolveSubprogram);
  const axes = machine.axes || {};
  const mrzp = machine.mrzp || {};
  const settings = { ...(machine.controlSettings || {}) };
  const travel = ["X", "Y", "Z"]
    .map((axis) => Number.isFinite(axes[axis]?.min) ? `${axis}${formatMm(Math.abs(axes[axis].min - (axes[axis].max || 0)))}` : undefined)
    .filter(Boolean)
    .join(" ");
  return [
    ["Machine", machine.name],
    ["Control", machine.controlDefinition?.name || machine.control],
    ["Kinematics", machine.kinematics?.type === "table-table" ? "Table-table: B tilt (about Y) carrying C rotary" : machine.kinematics?.type || "—"],
    ["Travels", `${travel} mm`],
    ["B range", axisRange(axes.B, "°")],
    ["C range", axisRange(axes.C, "°")],
    ["Rapid X/Y/Z", `${formatMm(axes.X?.rapidRate)} mm/min`],
    ["Rapid B/C", `${formatMm(axes.B?.rapidRate)} / ${formatMm(axes.C?.rapidRate)} °/min`],
    ["MRZP (S255-257)", `X${formatMm(mrzp.x)} Y${formatMm(mrzp.y)} Z${formatMm(mrzp.z)}${mrzp.verified === false ? " (placeholder)" : ""}`],
    ["S254 rotary centre", formatMm(mrzp.rotaryCenterDistance)],
    ["Rotary direction", machine.rotaryConvention?.verified === false ? "ISO table convention (unverified)" : "Verified"],
    ["Spindle", `${machine.spindle?.maxRpm ?? "—"} rpm ${machine.spindle?.taper || ""}`.trim()],
    ["Tool change", `${machine.toolChanger?.changeSeconds ?? "—"} s (${machine.toolChanger?.capacity ?? "?"} pockets)`],
    ["Default tool length", `${formatMm(machine.defaultToolLength)} mm`],
    ["S9 units", String(settings["9"] ?? "MM")],
    ["S335 linear rapid", String(settings["335"] ?? "OFF")],
    ["S40 D offsets", String(settings["40"] ?? "DIAMETER")],
    ["Program memory", resolveSubprogram?.memoryFolders?.length ? resolveSubprogram.memoryFolders.join("; ") : "Not set (fanucToolpath.programMemory)", resolveSubprogram?.memoryFolders?.length ? "path" : undefined],
    ["Drawing frame", model.frame ? `${model.frame.label} at X${formatMm(model.frame.x)} Y${formatMm(model.frame.y)} Z${formatMm(model.frame.z)} (${model.meta?.units || "mm"})` : "—"],
    ["Machine definition", machine.sourcePath || "—", "path"]
  ];
}

function viewDescriptor(machine, model) {
  const control = machine.controlDefinition?.shortName || (machine.controlDefinition?.name || "").toUpperCase();
  const unitScale = model.meta?.units === "inch" ? 1 / MM_PER_INCH : 1;
  if (machine.type === "lathe") {
    return {
      type: "lathe",
      eyebrow: `${control} | ${machine.name.toUpperCase()} | G18 X-Z | DIAMETER X`,
      defaultView: "lathe",
      unitScale
    };
  }
  const linear = Object.entries(machine.axes || {}).filter(([, axis]) => axis.type === "linear").map(([name]) => name).join("");
  const rotary = Object.entries(machine.axes || {}).filter(([, axis]) => axis.type === "rotary").map(([name]) => name).join("");
  return {
    type: "mill",
    eyebrow: `${control} | ${machine.name.toUpperCase()} | ${linear}${rotary ? ` + ${rotary}` : ""}`,
    defaultView: "iso",
    unitScale
  };
}

// options: machineSelection, settings (g30X/g30Z/initialVariables),
// machineParameters (FANUC CNC-PARA profile), toolTable, resolveSubprogram
// (or subprogramResolverFor(machine), for search paths that depend on the
// resolved machine, such as its program memory folder), controlSettings,
// registry.
function parseProgramForMachine(source, options = {}) {
  const registry = options.registry || defaultRegistry();
  const resolution = resolveMachineSelection(options.machineSelection, source, registry);
  const machine = resolution.machine;
  if (!machine) {
    throw new Error("No machine definitions are available.");
  }
  const resolveSubprogram = options.resolveSubprogram ||
    (typeof options.subprogramResolverFor === "function" ? options.subprogramResolverFor(machine) : undefined);
  let model;
  if (machine.type === "mill") {
    model = parseHaasMillProgram(source, {
      machine,
      initialVariables: options.settings?.initialVariables ?? options.initialVariables ?? "",
      toolTable: options.toolTable,
      resolveSubprogram,
      // Saved home (work) offsets, by machine id: { "haas-umc-500": { G54: {...} } }.
      workOffsets: options.workOffsets?.[machine.id],
      controlSettings: options.controlSettings,
      blockDelete: options.blockDelete
    });
    model.statsRows = model.statsRows || [];
    model.machine = {
      name: machine.name,
      control: machine.controlDefinition?.name || machine.control
    };
  } else {
    model = parseProgram(source, {
      ...(options.settings || {}),
      machineParameters: options.machineParameters,
      toolTable: options.toolTable
    });
    model.kind = "lathe";
    model.coordinateSystem = "xz-diameter";
    model.machine.sourcePath = options.machineParameterPath;
    model.statsRows = latheStatsRows(model);
  }
  model.machineDefinition = publicMachine(machine);
  model.machineSelection = {
    selection: resolution.selection,
    id: machine.id,
    detected: resolution.detected,
    reason: resolution.reason,
    confidence: resolution.confidence,
    available: machineSummaries(registry)
  };
  model.machineRows = machine.type === "mill"
    ? millMachineRows(model, machine, resolveSubprogram)
    : latheMachineRows(model, machine);
  model.view = viewDescriptor(machine, model);
  return model;
}

function machineForSelection(selection, source, registry = defaultRegistry()) {
  return resolveMachineSelection(selection, source, registry).machine;
}

function toolDefinitionsForMachine(source, toolTable, machine) {
  return machine?.type === "mill"
    ? millToolDefinitionsForSource(source, toolTable)
    : toolDefinitionsForSource(source, toolTable);
}

module.exports = {
  machineForSelection,
  parseProgramForMachine,
  toolDefinitionsForMachine
};
