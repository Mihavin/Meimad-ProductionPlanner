"use strict";

const PREVIEW_SETTING_DEFAULTS = Object.freeze({
  "axis.xProgramming": "diameter",
  "motion.rapidRate": 15000,
  "turret.indexSeconds": 1.2,
  "reference.g30X": 250,
  "reference.g30Z": 100,
  "machine.tailstockPresent": true,
  "safety.retractOrder": "X_THEN_Z",
  "macro.maximumExecutionSteps": 20000,
  "macro.maximumLineVisits": 100
});

const SETTING_DEFINITIONS = Object.freeze({
  "axis.xProgramming": {
    type: "string",
    unit: "",
    description: "X coordinates are programmed as diameters or radii."
  },
  "motion.rapidRate": {
    type: "number",
    unit: "mm/min",
    description: "Rapid rate used by the preview time estimate."
  },
  "turret.indexSeconds": {
    type: "number",
    unit: "s",
    description: "Preview-only elapsed time for a turret index."
  },
  "reference.g30X": {
    type: "number",
    unit: "diameter-mm",
    description: "Preview work-coordinate destination for a G30 X return."
  },
  "reference.g30Z": {
    type: "number",
    unit: "mm",
    description: "Preview work-coordinate destination for a G30 Z return."
  },
  "machine.tailstockPresent": {
    type: "boolean",
    unit: "",
    description: "Preview safety assumption that the lathe has a tailstock."
  },
  "safety.retractOrder": {
    type: "string",
    unit: "",
    description: "Preview safety policy for turret returns."
  },
  "macro.maximumExecutionSteps": {
    type: "integer",
    unit: "",
    description: "Maximum interpreted macro blocks before termination."
  },
  "macro.maximumLineVisits": {
    type: "integer",
    unit: "",
    description: "Maximum visits to one row before a loop warning."
  }
});

const PARAMETER_METADATA = Object.freeze({
  5101: {
    name: "Canned cycle",
    bits: {
      0: { symbol: "FXY" },
      1: { symbol: "EXC" },
      2: { symbol: "RTR" },
      7: { symbol: "M5B" }
    }
  },
  5102: {
    name: "FS10/11 format and canned cycle",
    bits: {
      2: { symbol: "QSR" },
      3: { symbol: "F16" },
      6: { symbol: "RAB" },
      7: { symbol: "RDI" }
    }
  },
  5103: {
    name: "FS10/11 canned-cycle options",
    bits: {
      0: { symbol: "SIJ" },
      2: { symbol: "DCY" },
      3: { symbol: "PNA" },
      6: { symbol: "TCZ" }
    }
  },
  5104: {
    name: "Canned-cycle checking",
    bits: {
      2: { symbol: "FCK", implemented: true },
      6: { symbol: "PCT" }
    }
  },
  5105: {
    name: "Canned-cycle execution",
    bits: {
      0: { symbol: "SBC" },
      1: { symbol: "RF1", implemented: true },
      2: { symbol: "RF2", implemented: true },
      3: { symbol: "M5T" },
      4: { symbol: "K0D" },
      5: { symbol: "TFA" },
      6: { symbol: "GIJ" }
    }
  },
  5106: {
    name: "Canned-cycle G-code handling",
    bits: {
      2: { symbol: "NT1", implemented: true },
      3: { symbol: "NT2" }
    }
  },
  5107: {
    name: "Multiple repetitive cycle behavior",
    bits: {
      0: { symbol: "ASU", implemented: true },
      1: { symbol: "ASC" },
      2: { symbol: "OCM" },
      5: { symbol: "GMC" }
    }
  },
  5108: {
    name: "Canned cycle",
    bits: {
      0: { symbol: "R16", implemented: true },
      1: { symbol: "DTP", implemented: true },
      3: { symbol: "NSP", implemented: true },
      5: { symbol: "NIC" },
      6: { symbol: "SPH" }
    }
  },
  5109: {
    name: "Canned-cycle validation and format",
    bits: {
      0: { symbol: "DSA" },
      1: { symbol: "CCI" },
      2: { symbol: "TAE" }
    }
  },
  5110: { name: "C-axis clamp M code", unit: "M-code" },
  5111: { name: "C-axis unclamp dwell", unit: "ms" },
  5112: { name: "Drilling-cycle spindle forward M code", unit: "M-code" },
  5113: { name: "Drilling-cycle spindle reverse M code", unit: "M-code" },
  5114: { name: "High-speed peck return value" },
  5115: { name: "Peck-drilling clearance" },
  5130: { name: "Thread-cycle chamfer amount" },
  5131: { name: "Thread-cycle cutting angle", unit: "deg" },
  5132: { name: "G71/G72 depth of cut", unit: "radius" },
  5133: { name: "G71/G72 escape", unit: "radius" },
  5134: { name: "G71/G72 clearance", unit: "radius" },
  5135: { name: "G73 second in-plane axis retraction", unit: "radius" },
  5136: { name: "G73 first in-plane axis retraction", unit: "radius" },
  5137: { name: "G73 division count", unit: "cycle" },
  5139: { name: "G74/G75 return amount", unit: "radius" },
  5140: { name: "G76 minimum depth of cut", unit: "radius" },
  5141: { name: "G76 finish allowance", unit: "radius" },
  5142: { name: "G76 final-finishing repeat count", unit: "cycle" },
  5143: { name: "G76 tool nose angle", unit: "deg" },
  5145: { name: "G71/G72 profile-check allowance 1" },
  5146: { name: "G71/G72 profile-check allowance 2" }
});

const VALUE_EXPRESSION = /(?:([ALST])(\d+))?([PMI])([-+]?(?:\d+(?:\.\d*)?|\.\d+))/g;

function parseValueEntries(payload, lineNumber) {
  const entries = [];
  let cursor = 0;
  let match;
  VALUE_EXPRESSION.lastIndex = 0;
  while ((match = VALUE_EXPRESSION.exec(payload)) !== null) {
    if (payload.slice(cursor, match.index).trim()) {
      throw new Error(`Invalid CNC parameter data on line ${lineNumber}`);
    }
    const sourceValue = Number(match[4]);
    if (!Number.isFinite(sourceValue)) {
      throw new Error(`Invalid CNC parameter value on line ${lineNumber}`);
    }
    const selector = match[1] || "";
    const index = selector ? Number(match[2]) : null;
    entries.push({
      key: selector ? `${selector}${index}` : "global",
      selector: selector || "global",
      index,
      format: match[3],
      raw: match[4],
      sourceValue,
      value: match[3] === "I" ? sourceValue * 25.4 : sourceValue
    });
    cursor = VALUE_EXPRESSION.lastIndex;
  }
  if (!entries.length || payload.slice(cursor).trim()) {
    throw new Error(`Invalid CNC parameter data on line ${lineNumber}`);
  }
  return entries;
}

function primaryEntry(entries) {
  const preferredKeys = ["global", "L1", "A1", "S1", "T1"];
  for (const key of preferredKeys) {
    const selected = entries.find((entry) => entry.key === key);
    if (selected) {
      return selected;
    }
  }
  return entries[0];
}

function bitsFromRaw(raw, metadata = {}) {
  if (!/^[01]{8}$/.test(raw)) {
    return undefined;
  }
  const bits = {};
  for (let index = 0; index < 8; index += 1) {
    const definition = metadata[index] || {};
    const value = Number(raw[7 - index]);
    bits[index] = {
      index,
      symbol: definition.symbol || "",
      default: value,
      value,
      zero: definition.zero || "",
      one: definition.one || "",
      implemented: Boolean(definition.implemented)
    };
  }
  return bits;
}

function scopedEntry(profile, parameter, key) {
  return profile.fanuc?.[String(parameter)]?.values?.[key];
}

function bitFromEntry(entry, bit) {
  if (!entry || !/^[01]{8}$/.test(entry.raw)) {
    return undefined;
  }
  return Number(entry.raw[7 - Number(bit)]);
}

function axisMap(profile) {
  const result = {};
  const axes = profile.fanuc?.["1020"]?.values || {};
  for (const entry of Object.values(axes)) {
    if (entry.selector !== "A" || !Number.isInteger(entry.index)) {
      continue;
    }
    const code = Math.trunc(entry.value);
    if (code <= 0 || code > 0x10ffff) {
      continue;
    }
    const name = String.fromCodePoint(code).trim().toUpperCase();
    if (name) {
      result[name] = entry.index;
    }
  }
  return result;
}

function applyControllerSettings(profile) {
  const axes = axisMap(profile);
  profile.machineData.axes = axes;

  const xAxis = axes.X || 1;
  const zAxis = axes.Z || 2;
  const xDiameterBit = bitFromEntry(
    scopedEntry(profile, 1006, `A${xAxis}`),
    3
  );
  if (xDiameterBit === 0) {
    throw new Error(
      "The CNC export uses radius-programmed X; this preview requires diameter-programmed X"
    );
  }
  if (xDiameterBit === 1) {
    profile.settings["axis.xProgramming"] = "diameter";
  }

  const rapidRates = [xAxis, zAxis]
    .map((axis) => scopedEntry(profile, 1420, `A${axis}`)?.value)
    .filter((value) => Number.isFinite(value) && value > 0);
  if (rapidRates.length) {
    profile.settings["motion.rapidRate"] = Math.min(...rapidRates);
  }

  profile.machineData.rapidRates = Object.fromEntries(
    Object.entries(axes)
      .map(([name, axis]) => [
        name,
        scopedEntry(profile, 1420, `A${axis}`)?.value
      ])
      .filter(([, value]) => Number.isFinite(value))
  );
  profile.machineData.secondReference = Object.fromEntries(
    Object.entries(axes)
      .map(([name, axis]) => [
        name,
        scopedEntry(profile, 1241, `A${axis}`)?.value
      ])
      .filter(([, value]) => Number.isFinite(value))
  );
}

function parseCncParameters(source) {
  const text = String(source ?? "").replace(/^\uFEFF/, "");
  const profile = {
    schemaVersion: "CNC-PARA-1.0",
    name: "Chevalier CNC-PARA machine data",
    control: "FANUC Series 0i-MODEL F",
    reference: "CNC-PARA.TXT controller export",
    referenceSection: "Active controller parameter memory",
    settings: { ...PREVIEW_SETTING_DEFAULTS },
    defaults: { ...PREVIEW_SETTING_DEFAULTS },
    settingDefinitions: { ...SETTING_DEFINITIONS },
    fanuc: {},
    machineData: {
      recordCount: 0,
      primaryPath: 1,
      axes: {},
      rapidRates: {},
      secondReference: {}
    }
  };

  const lines = text.split(/\r?\n/);
  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index].trim();
    if (!line || line === "%") {
      continue;
    }
    const record = line.match(/^N(\d{5})Q(\d+)(.+)$/);
    if (!record) {
      throw new Error(`Invalid CNC parameter record on line ${index + 1}`);
    }
    const number = String(Number(record[1]));
    if (Object.hasOwn(profile.fanuc, number)) {
      throw new Error(`Duplicate CNC parameter ${number} on line ${index + 1}`);
    }
    const entries = parseValueEntries(record[3], index + 1);
    const selected = primaryEntry(entries);
    const metadata = PARAMETER_METADATA[number] || {};
    const bits = selected.format === "P" && metadata.bits
      ? bitsFromRaw(selected.raw, metadata.bits)
      : undefined;
    const parameter = {
      number,
      path: Number(record[2]),
      name: metadata.name || "",
      type: bits
        ? "bits"
        : selected.format === "P" && Number.isInteger(selected.value)
          ? "integer"
          : "number",
      unit: metadata.unit || "",
      raw: selected.raw,
      value: selected.value,
      values: Object.fromEntries(entries.map((entry) => [entry.key, entry]))
    };
    if (bits) {
      parameter.bits = bits;
    }
    profile.fanuc[number] = parameter;
    profile.machineData.recordCount += 1;
  }

  if (!profile.machineData.recordCount) {
    throw new Error("No CNC parameter records were found");
  }
  applyControllerSettings(profile);
  return profile;
}

function fanucBit(profile, parameter, bit, fallback = 0) {
  const value = profile?.fanuc?.[String(parameter)]?.bits?.[Number(bit)]?.value;
  return value === 0 || value === 1 ? value : fallback;
}

function fanucValue(profile, parameter, fallback = 0) {
  const value = profile?.fanuc?.[String(parameter)]?.value;
  return Number.isFinite(value) ? value : fallback;
}

module.exports = {
  fanucBit,
  fanucValue,
  parseCncParameters
};
