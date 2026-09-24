"use strict";

// Control-dialect translations for the vendored interpreters. The FANUC lathe interpreter
// (src/parser.js) reads FANUC 0i-T syntax and the mill interpreter (src/haas-mill.js) reads Haas
// NGC or FANUC 30i syntax; the Meimad machines add controls whose programs differ:
//
//   okuma-osp-lathe     Okuma OSP-P200L: LAP cycles (G85/G86/G87 with G81/G82...G80 contours),
//                       compound thread cycles G71/G72, grooving G73/G74, G94/G95 feed modes,
//                       G04 F dwell, CALL/RTS subprograms, VC variables, IF/GOTO with named labels,
//                       T aabbcc tool words, G20/G21 home moves, G75/G76 chamfer/round modifiers.
//   haas-lathe          Haas lathe (Classic/NGC): one-block G71/G72/G73/G74/G75/G76 cycles and the
//                       one-block G90/G92/G94 turning, threading and facing cycles.
//   mazak-tilt-a-as-b   Mazak Variaxis: the A tilt axis is presented to the interpreter as B, the
//                       axis its tilted-plane / tool-centre-point solver moves.
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

// ----- Okuma OSP lathe --------------------------------------------------------------------------

const OSP_LABEL = /^\s*N([A-Z][A-Z0-9]*|[0-9]+[A-Z][A-Z0-9]*)\b/i;
// OSP dwell time is F seconds; the FANUC lathe interpreter reads X/U seconds.
const OSP_DWELL = new RegExp(`(?<![A-Z#])G0*4(?![\\d.])\\s*F\\s*(${NUMBER})`, "g");
const OSP_DEFINITIONS = /^\s*(DEF\b|DRAW\b|PS\b|PT\b|TRANS\b|LAP\b|NAT\b|CLEAR\b|ROTATE\b|SCALE\b|VZO)/i;

function okumaOsp(text) {
  const raw = String(text).split("\n");
  const out = new Output();
  // Pass 1: named sequence labels get numeric sequence numbers; LAP contour blocks get numbers too.
  const labels = new Map();
  let nextLabel = 90000;
  raw.forEach((line) => {
    const match = stripComments(line).match(OSP_LABEL);
    if (match && !labels.has(match[1].toUpperCase())) labels.set(match[1].toUpperCase(), nextLabel++);
  });
  const contours = new Map(); // label -> { first, last, kind }
  const sequenceOf = new Array(raw.length).fill(undefined);
  raw.forEach((line, index) => {
    const code = stripComments(line).toUpperCase();
    const label = code.match(OSP_LABEL)?.[1];
    if (!label || !/(?<![A-Z#])G0*8[123](?![\d.])/.test(code)) return;
    const kind = /(?<![A-Z#])G0*82(?![\d.])/.test(code) ? 82 : /(?<![A-Z#])G0*83(?![\d.])/.test(code) ? 83 : 81;
    let end = index + 1;
    while (end < raw.length && !/(?<![A-Z#])G0*80(?![\d.])/.test(stripComments(raw[end]).toUpperCase())) end += 1;
    const blocks = [];
    for (let i = index + 1; i < end; i += 1) {
      if (!stripComments(raw[i]).trim()) continue;
      const own = stripComments(raw[i]).toUpperCase().match(/^\s*N(\d+)\b/);
      sequenceOf[i] = own ? Number(own[1]) : nextLabel++;
      blocks.push(i);
    }
    if (blocks.length) contours.set(label, { first: sequenceOf[blocks[0]], last: sequenceOf[blocks[blocks.length - 1]], kind, endLine: end });
  });
  const contourEnds = new Set([...contours.values()].map((contour) => contour.endLine));
  const state = { feedPerMinute: false, inch: false };
  const retract = () => (state.inch ? "0.02" : "0.5");
  const seqFor = (label) => {
    const upper = String(label || "").toUpperCase();
    return /^\d+$/.test(upper) ? Number(upper) : labels.get(upper);
  };

  raw.forEach((line, index) => {
    const origin = index + 1;
    let code = stripComments(line).toUpperCase().trim();
    const tail = comments(line);
    if (!code) {
      out.push(line, origin);
      return;
    }
    if (/^\s*\$\S*%?\s*$/.test(code)) {
      // "$NAME.MIN%" transfer header of an OSP program file.
      out.push(`(${code.replace(/[()]/g, "")})`, origin);
      return;
    }
    if (OSP_DEFINITIONS.test(code)) {
      out.push(`(${code.replace(/[()]/g, "")})`, origin);
      out.note("OSP definition statements (DEF, DRAW, PS, PT, ...) are not part of the toolpath.");
      return;
    }
    // Named labels -> numeric sequence numbers; assigned numbers for LAP contour blocks.
    const labelMatch = code.match(OSP_LABEL);
    if (labelMatch) code = code.replace(OSP_LABEL, `N${labels.get(labelMatch[1].toUpperCase())}`);
    else if (sequenceOf[index] !== undefined && !/^\s*N\d+/.test(code)) code = `N${sequenceOf[index]} ${code}`;
    // Branching and subprograms.
    code = code.replace(/\bGOTO\s+N?([A-Z0-9]+)/g, (match, label) => `GOTO ${seqFor(label) ?? label}`);
    code = code.replace(/^(\s*(?:N\d+\s+)?IF\s*\[.*\])\s*N?([A-Z][A-Z0-9]*|\d+)\s*$/, (match, head, label) => `${head} GOTO ${seqFor(label) ?? label}`);
    if (/^\s*(?:N\d+\s+)?RTS\b/.test(code)) code = code.replace(/\bRTS\b/, "M99");
    const call = code.match(/\bCALL\s+O(\w+)(?:\s+Q(\d+))?/);
    if (call) {
      if (/^\d+$/.test(call[1])) {
        code = code.replace(call[0], `M98 P${Number(call[1])}${call[2] ? ` L${call[2]}` : ""}`);
        if (/\bP[A-Z]\s*=/.test(code)) {
          code = code.replace(/\bP[A-Z]\s*=\s*[-+.\d]+/g, " ");
          out.note("CALL arguments (PA=, PB=, ...) are not passed to the subprogram in the preview.");
        }
      } else {
        out.push(`(${code.replace(/[()]/g, "")})`, origin);
        out.note(`Subprogram call ${call[0].trim()} uses a named program, which the preview cannot look up.`);
        return;
      }
    }
    // OSP M98/M99 are tailstock thrust codes, not subprograms.
    if (/(?<![A-Z#])M0*9[89](?![\d.])/.test(code) && !call && !/\bM99\b.*\bRTS\b/.test(line)) {
      if (!/^\s*(?:N\d+\s+)?M99\s*$/.test(code) || !/\bRTS\b/i.test(line)) {
        code = code.replace(/(?<![A-Z#])M0*9[89](?![\d.])/g, " ");
        out.note("OSP M98/M99 (tailstock thrust) are ignored.");
      }
    }
    // Variables.
    code = code.replace(/\bVC(\d{1,3})\b/g, (match, number) => `#${500 + Number(number)}`);
    code = code.replace(/\bVS(\d{1,2})\b/g, (match, number) => `#${Number(number)}`);
    // Tool word T aabbcc -> T aabb.
    code = code.replace(/(?<![A-Z#])T(\d{2})(\d{2})(\d{2})(?!\d)/g, "T$1$2");
    // Feed modes, dwell, home moves, turret selection, mirror.
    if (/(?<![A-Z#])G0*94(?![\d.])/.test(code)) state.feedPerMinute = true;
    if (/(?<![A-Z#])G0*95(?![\d.])/.test(code)) state.feedPerMinute = false;
    code = code.replace(/(?<![A-Z#])G0*94(?![\d.])/g, "G98").replace(/(?<![A-Z#])G0*95(?![\d.])/g, "G99");
    code = code.replace(OSP_DWELL, "G04 X$1");
    if (/(?<![A-Z#])G0*2[01](?![\d.])/.test(code)) {
      code = withoutCodes(code, [20, 21]);
      out.note("OSP G20/G21 (home position moves) are not drawn; inch/metric follows the machine parameter.");
    }
    if (/(?<![A-Z#])G0*(?:13|14|62|64|65)(?![\d.])/.test(code)) code = withoutCodes(code, [13, 14, 62, 64, 65]);
    const words = readWords(code);
    // Chamfer / rounding modifiers on G01 blocks.
    if (/(?<![A-Z#])G0*7[56](?![\d.])/.test(code) && (has(words, "X") || has(words, "Z") || has(words, "U") || has(words, "W")) && !has(words, "D")) {
      code = withoutWords(withoutCodes(code, [75, 76]), ["L"]);
      out.note("OSP G75/G76 chamfers and roundings are drawn as sharp corners.");
    }
    const rewords = readWords(code);
    // LAP cycles.
    const lapLabel = code.match(/(?<![A-Z#])G0*8[567](?![\d.]).*?\bN([A-Z0-9]+)/)?.[1];
    if (lapLabel && (hasG(rewords, 85) || hasG(rewords, 86) || hasG(rewords, 87))) {
      const contour = contours.get(lapLabel.toUpperCase());
      if (!contour) {
        out.push(`(${code.replace(/[()]/g, "")})`, origin);
        out.note(`LAP cycle refers to contour N${lapLabel}, which was not found.`);
        return;
      }
      const depth = last(rewords, "D");
      const finishU = has(rewords, "U") ? fmt(last(rewords, "U")) : "0.";
      const finishW = has(rewords, "W") ? fmt(last(rewords, "W")) : "0.";
      const feed = has(rewords, "F") ? ` F${fmt(last(rewords, "F"))}` : "";
      if (hasG(rewords, 87)) {
        out.push(`G70 P${contour.first} Q${contour.last} ${tail}`.trim(), origin);
      } else if (hasG(rewords, 86)) {
        out.push(`G73 U${fmt(depth ?? 1)} W0. R1 ${tail}`.trim(), origin);
        out.push(`G73 P${contour.first} Q${contour.last} U${finishU} W${finishW}${feed}`, origin);
        out.note("OSP G86 copy turning is approximated by one FANUC G73 contour pass.");
      } else {
        const cycle = contour.kind === 82 ? 72 : 71;
        out.push(`G${cycle} ${cycle === 71 ? "U" : "W"}${fmt(depth ?? 1)} R${retract()} ${tail}`.trim(), origin);
        out.push(`G${cycle} P${contour.first} Q${contour.last} U${finishU} W${finishW}${feed}`, origin);
      }
      out.note("OSP LAP cycles are converted to FANUC G71/G72/G70 cycles for the preview.");
      return;
    }
    if (labelMatch && /(?<![A-Z#])G0*8[123](?![\d.])/.test(code)) {
      out.push(`(LAP CONTOUR N${labels.get(labelMatch[1].toUpperCase())} START)`, origin);
      return;
    }
    if (contourEnds.has(index) || /^\s*(?:N\d+\s+)?G0*80\s*$/.test(code)) {
      out.push("(LAP CONTOUR END)", origin);
      return;
    }
    if (/(?<![A-Z#])G0*8[48](?![\d.])/.test(code)) {
      out.push(`(${code.replace(/[()]/g, "")})`, origin);
      out.note("OSP G84 cutting-condition changes and G88 LAP thread cycles are not simulated.");
      return;
    }
    // Compound thread cycles G71 (longitudinal) / G72 (transverse) -> FANUC G76.
    if ((hasG(rewords, 71) || hasG(rewords, 72)) && (has(rewords, "H") || has(rewords, "D")) && !has(rewords, "P")) {
      const height = last(rewords, "H");
      const first = last(rewords, "D");
      const lead = has(rewords, "F") ? last(rewords, "F") / Math.max(1, last(rewords, "J") ?? 1) : undefined;
      const angle = Math.max(0, Math.min(99, Math.round(last(rewords, "B") ?? 60)));
      out.push(`G76 P0100${String(angle).padStart(2, "0")} Q${fmt(Math.max(0.01, (first ?? 0.2) / 10))} R${fmt(last(rewords, "U") ?? 0)} ${tail}`.trim(), origin);
      out.push(`G76 X${fmt(last(rewords, "X") ?? 0)} Z${fmt(last(rewords, "Z") ?? 0)}${Number.isFinite(height) ? ` P${fmt(height)}` : ""}${Number.isFinite(first) ? ` Q${fmt(first)}` : ""}${has(rewords, "I") ? ` R${fmt(last(rewords, "I"))}` : ""}${Number.isFinite(lead) ? ` F${fmt(lead)}` : ""}`, origin);
      out.note("OSP compound thread cycles G71/G72 are converted to the FANUC G76 cycle for the preview.");
      return;
    }
    // Grooving cycles G73 (longitudinal) / G74 (transverse) -> FANUC G75 / G74.
    if ((hasG(rewords, 73) || hasG(rewords, 74)) && has(rewords, "D") && !has(rewords, "P")) {
      const fanuc = hasG(rewords, 73) ? 75 : 74;
      const feed = has(rewords, "F") ? ` F${fmt(last(rewords, "F"))}` : "";
      const target = `${has(rewords, "X") ? ` X${fmt(last(rewords, "X"))}` : ""}${has(rewords, "Z") ? ` Z${fmt(last(rewords, "Z"))}` : ""}`;
      const depth = last(rewords, "D");
      const shift = fanuc === 75 ? last(rewords, "K") : last(rewords, "I");
      out.push(`G${fanuc} R${retract()} ${tail}`.trim(), origin);
      out.push(`G${fanuc}${target}${fanuc === 75 ? ` P${fmt(depth)}` : ""}${Number.isFinite(shift) ? (fanuc === 75 ? ` Q${fmt(shift)}` : ` P${fmt(shift)}`) : ""}${fanuc === 74 ? ` Q${fmt(depth)}` : ""}${feed}`, origin);
      out.note(`OSP G${hasG(rewords, 73) ? 73 : 74} grooving is converted to the FANUC G${fanuc} peck cycle for the preview.`);
      return;
    }
    // Fixed thread cycles G31/G32/G33: one thread pass at the pitch feed.
    if ((hasG(rewords, 31) || hasG(rewords, 32) || hasG(rewords, 33)) && (has(rewords, "X") || has(rewords, "Z"))) {
      const feed = has(rewords, "F") ? ` F${fmt(last(rewords, "F"))}` : "";
      out.push(`G99 G01${has(rewords, "X") ? ` X${fmt(last(rewords, "X"))}` : ""}${has(rewords, "Z") ? ` Z${fmt(last(rewords, "Z"))}` : ""}${feed} ${tail}`.trim(), origin);
      if (state.feedPerMinute) out.push("G98", origin);
      out.note("OSP fixed thread cycles G31/G32/G33 are shown as one thread pass at the pitch feed.");
      return;
    }
    if (/(?<![A-Z#])G0*3[45](?![\d.])/.test(code)) out.note("OSP variable-lead threads G34/G35 are not simulated.");
    if (/(?<![A-Z#])G0*1[89]\d(?![\d.])/.test(code)) out.note("OSP machine compound fixed cycles G180-G191 are not drawn.");
    out.push(`${code} ${tail}`.trim(), origin);
  });
  return { lines: out.lines, map: out.map, notes: out.notes };
}

function translate(text, machine) {
  switch (machine?.controlDefinition?.translation) {
    case "okuma-osp-lathe":
      return okumaOsp(text);
    case "haas-lathe":
      return haasLathe(text);
    case "mazak-tilt-a-as-b":
      return mazakTiltAsB(text);
    default:
      return identity(text);
  }
}

module.exports = { translate, identity, stripComments, readWords };
