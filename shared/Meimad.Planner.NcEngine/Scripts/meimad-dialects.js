"use strict";

// Control-dialect translations for the vendored interpreters. The FANUC lathe interpreter
// (src/parser.js) reads FANUC 0i-T syntax and the mill interpreter (src/haas-mill.js) reads Haas
// NGC or FANUC 30i syntax; the Meimad machines add controls of the same G-code family whose
// programs differ in form:
//
//   haas-lathe          Haas lathe (Classic/NGC): one-block G71/G72/G73/G74/G75/G76 cycles and the
//                       one-block G90/G92/G94 turning, threading and facing cycles.
//   mazak-tilt-a-as-b   Mazak Variaxis: the A tilt axis is presented to the interpreter as B, the
//                       axis its tilted-plane / tool-centre-point solver moves.
//
// Okuma OSP is a different language, not a FANUC dialect (G71 is a thread cycle there, G85 a LAP
// roughing cycle): its programs are never translated here. meimad-okuma.js executes them.
//
// translate() returns { lines, map, notes }: map[i] = { line } is the 1-based line of the original
// program that produced output line i, so the viewer can highlight the right row.

const NUMBER = "[-+]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)";

function stripComments(line) {
  return String(line).replace(/\([^)]*\)?/g, " ").replace(/;.*$/, "");
}

function comments(line) {
  return (String(line).match(/\([^)]*\)/g) || []).join(" ");
}

function readWords(code) {
  const words = new Map();
  const expression = new RegExp(`(?<![A-Z#])([A-Z])\\s*(${NUMBER})`, "gi");
  let match;
  while ((match = expression.exec(code)) !== null) {
    const letter = match[1].toUpperCase();
    const list = words.get(letter) || [];
    list.push({ raw: match[2], value: Number(match[2]) });
    words.set(letter, list);
  }
  return words;
}

const last = (words, letter) => words.get(letter)?.at(-1)?.value;
const has = (words, letter) => words.has(letter);
const gList = (words) => (words.get("G") || []).map((entry) => entry.value);
const hasG = (words, code) => gList(words).some((value) => Math.abs(value - code) < 1e-9);

function fmt(value, digits = 4) {
  if (!Number.isFinite(value)) return "0.";
  const text = Number(value.toFixed(digits)).toString();
  return text.includes(".") || text.includes("e") ? text : `${text}.`;
}

function withoutWords(code, letters) {
  let result = code;
  for (const letter of letters) {
    result = result.replace(new RegExp(`(?<![A-Z#])${letter}\\s*${NUMBER}`, "gi"), " ");
  }
  return result.replace(/\s+/g, " ").trim();
}

function withoutCodes(code, codes) {
  let result = code;
  for (const value of codes) {
    const [whole, fraction] = String(value).split(".");
    result = result.replace(new RegExp(`(?<![A-Z#])G0*${whole}${fraction ? `\\.${fraction}` : ""}(?![\\d.])`, "gi"), " ");
  }
  return result.replace(/\s+/g, " ").trim();
}

class Output {
  constructor() {
    this.lines = [];
    this.map = [];
    this.notes = [];
    this.noted = new Set();
  }
  push(line, origin) {
    this.lines.push(line);
    this.map.push({ line: origin });
  }
  note(text) {
    if (this.noted.has(text)) return;
    this.noted.add(text);
    this.notes.push(text);
  }
}

function identity(text) {
  const lines = String(text).split("\n");
  return { lines, map: lines.map((_, index) => ({ line: index + 1 })), notes: [] };
}

// ----- Mazak Variaxis: A tilt programmed as A, interpreted as B --------------------------------

function mazakTiltAsB(text) {
  const out = new Output();
  String(text).split("\n").forEach((line, index) => {
    const code = stripComments(line);
    let translated = line;
    if (/(?<![A-Z#])A\s*[-+.\d[#]/i.test(code) && !/(?<![A-Z#])G0*6[567](?![\d.])/i.test(code)) {
      // Rename address A outside comments and macro calls (whose A is the #1 argument).
      translated = line.replace(/\([^)]*\)|(?<![A-Z#])A(?=\s*[-+.\d[#])/gi, (match) =>
        match.startsWith("(") ? match : match.replace(/A/i, "B"));
      out.note("The A tilt axis is interpreted as B (the interpreter's tilt axis); rotary values are shown as B.");
    }
    out.push(translated, index + 1);
  });
  return { lines: out.lines, map: out.map, notes: out.notes };
}

// ----- Haas lathe one-block cycles -------------------------------------------------------------

function haasLathe(text) {
  const out = new Output();
  const state = { x: undefined, z: undefined, inch: false, feedPerMinute: false, activeCycle: undefined, pendingTwoBlock: undefined };
  const retract = () => (state.inch ? "0.02" : "0.5");
  const trackPosition = (words) => {
    if (has(words, "X")) state.x = last(words, "X");
    else if (has(words, "U") && Number.isFinite(state.x)) state.x += last(words, "U");
    if (has(words, "Z")) state.z = last(words, "Z");
    else if (has(words, "W") && Number.isFinite(state.z)) state.z += last(words, "W");
  };
  const lines = String(text).split("\n");
  lines.forEach((line, index) => {
    const origin = index + 1;
    const code = stripComments(line).toUpperCase().trim();
    const tail = comments(line);
    if (!code) {
      out.push(line, origin);
      return;
    }
    const words = readWords(code);
    const g = gList(words);
    if (hasG(words, 20)) state.inch = true;
    if (hasG(words, 21)) state.inch = false;
    if (hasG(words, 98)) state.feedPerMinute = true;
    if (hasG(words, 99)) state.feedPerMinute = false;
    const pending = state.pendingTwoBlock;
    state.pendingTwoBlock = undefined;

    // One-block turning/facing/threading cycles (Haas and FANUC system A): modal repeats carry X only.
    const cycleCode = [90, 92, 94].find((value) => hasG(words, value));
    const onlyX = !words.has("G") && !words.has("M") && !words.has("T") && (has(words, "X") || has(words, "U")) && !has(words, "Z") && !has(words, "W");
    if (cycleCode !== undefined || (state.activeCycle && onlyX)) {
      const cycle = cycleCode !== undefined
        ? { type: cycleCode, f: last(words, "F"), z: has(words, "Z") ? last(words, "Z") : (has(words, "W") && Number.isFinite(state.z) ? state.z + last(words, "W") : undefined), taper: last(words, "I") ?? last(words, "K") }
        : { ...state.activeCycle, f: last(words, "F") ?? state.activeCycle.f };
      const targetX = has(words, "X") ? last(words, "X") : (has(words, "U") && Number.isFinite(state.x) ? state.x + last(words, "U") : undefined);
      if (cycleCode === 90 && !Number.isFinite(targetX) && !Number.isFinite(cycle.z)) {
        out.push(`G90 ${tail}`.trim(), origin);
        return;
      }
      const startX = state.x;
      const startZ = state.z;
      const feed = Number.isFinite(cycle.f) ? `F${fmt(cycle.f)}` : "";
      const label = cycle.type === 92 ? "G92 threading" : cycle.type === 94 ? "G94 facing" : "G90 turning";
      if (Number.isFinite(cycle.taper)) out.note(`${label} cycle taper (I/K) is drawn as a straight cut.`);
      if (!Number.isFinite(startX) || !Number.isFinite(startZ)) {
        out.push(`G01 ${Number.isFinite(targetX) ? `X${fmt(targetX)}` : ""} ${Number.isFinite(cycle.z) ? `Z${fmt(cycle.z)}` : ""} ${feed} ${tail}`.replace(/\s+/g, " ").trim(), origin);
        out.note(`${label} cycle expanded without its return moves (start position unknown).`);
      } else if (cycle.type === 94) {
        out.push(`G00 Z${fmt(cycle.z)} ${tail}`.trim(), origin);
        out.push(`G01 X${fmt(targetX)} ${feed}`.trim(), origin);
        out.push(`G00 Z${fmt(startZ)}`, origin);
        out.push(`G00 X${fmt(startX)}`, origin);
      } else {
        out.push(`G00 X${fmt(targetX)} ${tail}`.trim(), origin);
        if (cycle.type === 92) {
          out.push(`G99 G01 Z${fmt(cycle.z)} ${feed}`.trim(), origin);
          if (state.feedPerMinute) out.push("G98", origin);
        } else {
          out.push(`G01 Z${fmt(cycle.z)} ${feed}`.trim(), origin);
        }
        out.push(`G00 X${fmt(startX)}`, origin);
        out.push(`G00 Z${fmt(startZ)}`, origin);
      }
      state.activeCycle = cycle;
      return;
    }
    state.activeCycle = undefined;

    const p = last(words, "P");
    const q = last(words, "Q");
    const extra = withoutWords(withoutCodes(code, [70, 71, 72, 73, 74, 75, 76]), ["P", "Q", "U", "W", "D", "I", "K", "R", "F", "X", "Z", "A"]);
    const carry = extra ? ` ${extra}` : "";
    if ([71, 72, 73].some((value) => hasG(words, value)) && Number.isFinite(p) && Number.isFinite(q) && (has(words, "D") || (!pending && !has(words, "R")))) {
      const cycle = hasG(words, 71) ? 71 : hasG(words, 72) ? 72 : 73;
      const depth = last(words, "D");
      const finishU = has(words, "U") ? fmt(last(words, "U")) : "0.";
      const finishW = has(words, "W") ? fmt(last(words, "W")) : "0.";
      const feed = has(words, "F") ? ` F${fmt(last(words, "F"))}` : "";
      if (cycle === 73) {
        out.push(`G73 U${fmt(last(words, "I") ?? 0)} W${fmt(last(words, "K") ?? 0)} R${fmt(depth ?? 1, 0)}${carry} ${tail}`.trim(), origin);
      } else {
        out.push(`G${cycle} ${cycle === 71 ? "U" : "W"}${fmt(depth ?? 1)} R${retract()}${carry} ${tail}`.trim(), origin);
      }
      out.push(`G${cycle} P${p} Q${q} U${finishU} W${finishW}${feed}`, origin);
      out.note(`Haas one-block G${cycle} converted to the FANUC two-block cycle for the preview.`);
      return;
    }
    if ((hasG(words, 74) || hasG(words, 75)) && (has(words, "X") || has(words, "U") || has(words, "Z") || has(words, "W")) && (has(words, "I") || has(words, "K") || has(words, "D")) && !pending) {
      const cycle = hasG(words, 74) ? 74 : 75;
      const target = `${has(words, "X") ? ` X${fmt(last(words, "X"))}` : has(words, "U") ? ` U${fmt(last(words, "U"))}` : ""}${has(words, "Z") ? ` Z${fmt(last(words, "Z"))}` : has(words, "W") ? ` W${fmt(last(words, "W"))}` : ""}`;
      const peckX = last(words, "I");
      const peckZ = last(words, "K");
      const relief = last(words, "D");
      const feed = has(words, "F") ? ` F${fmt(last(words, "F"))}` : "";
      out.push(`G${cycle} R${retract()}${carry} ${tail}`.trim(), origin);
      out.push(`G${cycle}${target}${Number.isFinite(peckX) ? ` P${fmt(peckX)}` : ""}${Number.isFinite(peckZ) ? ` Q${fmt(peckZ)}` : ""}${Number.isFinite(relief) ? ` R${fmt(relief)}` : ""}${feed}`, origin);
      out.note(`Haas one-block G${cycle} converted to the FANUC two-block cycle for the preview.`);
      return;
    }
    if (hasG(words, 76) && (has(words, "X") || has(words, "U")) && (has(words, "Z") || has(words, "W")) && (has(words, "K") || has(words, "D")) && !pending) {
      const height = last(words, "K");
      const first = last(words, "D");
      const angle = Math.max(0, Math.min(99, Math.round(last(words, "A") ?? 60)));
      const feed = has(words, "F") ? ` F${fmt(last(words, "F"))}` : has(words, "E") ? ` F${fmt(last(words, "E"))}` : "";
      const target = `${has(words, "X") ? ` X${fmt(last(words, "X"))}` : ` U${fmt(last(words, "U"))}`}${has(words, "Z") ? ` Z${fmt(last(words, "Z"))}` : ` W${fmt(last(words, "W"))}`}`;
      out.push(`G76 P0100${String(angle).padStart(2, "0")} Q${fmt(Math.max(state.inch ? 0.001 : 0.01, (first ?? 0.2) / 10))} R0.${carry} ${tail}`.trim(), origin);
      out.push(`G76${target}${Number.isFinite(height) ? ` P${fmt(height)}` : ""}${Number.isFinite(first) ? ` Q${fmt(first)}` : ""}${has(words, "I") ? ` R${fmt(last(words, "I"))}` : ""}${feed}`, origin);
      out.note("Haas one-block G76 converted to the FANUC two-block threading cycle for the preview.");
      return;
    }
    if ([71, 72, 73, 74, 75, 76].some((value) => hasG(words, value)) && !(Number.isFinite(p) && Number.isFinite(q)) && !has(words, "X") && !has(words, "Z") && !has(words, "U") && !has(words, "W")) {
      state.pendingTwoBlock = g;
    }
    if (g.some((value) => value >= 0 && value <= 3) || (!words.has("G") && (has(words, "X") || has(words, "Z") || has(words, "U") || has(words, "W")))) {
      trackPosition(words);
    }
    out.push(line, origin);
  });
  return { lines: out.lines, map: out.map, notes: out.notes };
}

function translate(text, machine) {
  switch (machine?.controlDefinition?.translation) {
    case "haas-lathe":
      return haasLathe(text);
    case "mazak-tilt-a-as-b":
      return mazakTiltAsB(text);
    default:
      return identity(text);
  }
}

module.exports = { translate, identity, stripComments, readWords };
