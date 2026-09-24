"use strict";

// Subprogram expansion for the lathe interpreter, which (unlike the mill interpreter) does not
// resolve M98 / G65 / M97 calls itself. Called programs are inlined before parsing: from the
// program's folder or the machine's program memory folder (src/subprograms.js resolver), or, for
// Haas M97, from a local N block of the same program. Every inlined line maps to the calling line
// of the main program with the called program's name and row, so the viewer highlights the call.

const MAX_DEPTH = 6;
const MAX_LINES = 200000;
const ARGUMENT_VARIABLES = {
  A: 1, B: 2, C: 3, I: 4, J: 5, K: 6, D: 7, E: 8, F: 9, H: 11, M: 13,
  Q: 17, R: 18, S: 19, T: 20, U: 21, V: 22, W: 23, X: 24, Y: 25, Z: 26
};

function stripComments(line) {
  return String(line).replace(/\([^)]*\)?/g, " ").replace(/;.*$/, "").toUpperCase();
}

function parseCall(code) {
  const m98 = code.match(/(?<![A-Z#])M0*98(?![\d.])/);
  const m97 = code.match(/(?<![A-Z#])M0*97(?![\d.])/);
  const g65 = code.match(/(?<![A-Z#])G0*65(?![\d.])/);
  if (!m98 && !m97 && !g65) return undefined;
  const p = code.match(/(?<![A-Z#])P\s*(\d+)/);
  if (!p) return undefined;
  const l = code.match(/(?<![A-Z#])L\s*(\d+)/);
  let program = Number(p[1]);
  let repeats = l ? Number(l[1]) : 1;
  if (m98 && p[1].length === 8) {
    // FANUC M98 Pkkkknnnn: repeat count then program number.
    repeats = Number(p[1].slice(0, 4)) || 1;
    program = Number(p[1].slice(4));
  }
  return { kind: m97 ? "local" : m98 ? "m98" : "g65", program, repeats: Math.max(1, Math.min(repeats, 999)), code };
}

function argumentAssignments(code) {
  const assignments = [];
  const expression = /(?<![A-Z#])([A-Z])\s*([-+]?(?:\d+(?:\.\d*)?|\.\d+))/g;
  let match;
  while ((match = expression.exec(code)) !== null) {
    const variable = ARGUMENT_VARIABLES[match[1]];
    if (variable !== undefined && !["G", "P", "L"].includes(match[1])) assignments.push(`#${variable}=${match[2]}`);
  }
  return assignments;
}

function bodyOf(lines) {
  // The subprogram runs until its first M99; an M02/M30 also ends it.
  const body = [];
  for (const line of lines) {
    const code = stripComments(line);
    if (/^\s*%\s*$/.test(code)) continue;
    if (/^\s*O\d+/.test(code) && body.length === 0) continue;
    if (/(?<![A-Z#])M0*(?:99|30|02)(?![\d.])/.test(code)) {
      const rest = code.replace(/(?<![A-Z#])M0*(?:99|30|02)(?![\d.])/g, " ").trim();
      if (rest) body.push(rest);
      break;
    }
    body.push(line);
  }
  return body;
}

function localBody(lines, sequence) {
  const start = lines.findIndex((line) => new RegExp(`^\\s*N0*${sequence}(?![\\d])`).test(stripComments(line)));
  if (start < 0) return undefined;
  const body = [];
  for (let index = start; index < lines.length; index += 1) {
    const code = stripComments(lines[index]);
    if (index > start && /(?<![A-Z#])M0*99(?![\d.])/.test(code)) break;
    body.push(index === start ? lines[index].replace(/^\s*N\d+/i, "") : lines[index]);
  }
  return body;
}

// lines/map: the (translated) main program; resolve(target): src/subprograms.js resolver or
// undefined; translateSub(text): the dialect translation for a called program's text.
function expand({ lines, map }, resolve, translateSub) {
  const out = { lines: [], map: [], notes: [], external: [] };
  const noted = new Set();
  const note = (text) => {
    if (noted.has(text)) return;
    noted.add(text);
    out.notes.push(text);
  };
  const seenExternal = new Set();
  let total = 0;

  const emit = (line, origin) => {
    if (total >= MAX_LINES) return false;
    out.lines.push(line);
    out.map.push(origin);
    total += 1;
    return true;
  };

  const inline = (bodyLines, origin, unitName, depth, stack) => {
    for (let k = 0; k < bodyLines.length; k += 1) {
      const line = bodyLines[k];
      const call = parseCall(stripComments(line));
      const unitOrigin = { line: origin.line, unit: unitName, unitLine: k + 1 };
      if (call && depth < MAX_DEPTH && !stack.includes(call.program)) {
        expandCall(call, line, unitOrigin, depth + 1, [...stack, call.program], bodyLines);
      } else {
        if (call && depth >= MAX_DEPTH) note("Subprogram nesting deeper than 6 levels is not expanded.");
        if (call && stack.includes(call.program)) note(`Recursive subprogram call O${call.program} is not expanded.`);
        if (!emit(line, unitOrigin)) return;
      }
    }
  };

  const expandCall = (call, line, origin, depth, stack, scopeLines) => {
    let body;
    let unitName;
    if (call.kind === "local") {
      body = localBody(scopeLines, call.program);
      unitName = `N${call.program}`;
      if (!body) {
        emit(`(M97 P${call.program}: local subprogram N${call.program} not found)`, origin);
        note(`Local subprogram N${call.program} (M97) was not found in the program.`);
        return;
      }
    } else {
      const found = typeof resolve === "function" ? resolve(call.program) : undefined;
      if (!found) {
        emit(`(${call.kind === "g65" ? "G65" : "M98"} P${call.program}: subprogram not found)`, origin);
        note(`Subprogram O${call.program} was not found in the program folder or the machine's program memory folder.`);
        return;
      }
      unitName = found.name;
      if (!seenExternal.has(found.path)) {
        seenExternal.add(found.path);
        out.external.push({ name: found.name, location: found.location });
      }
      const translated = typeof translateSub === "function" ? translateSub(found.source) : String(found.source).split("\n");
      body = bodyOf(translated);
    }
    const assignments = call.kind === "g65" ? argumentAssignments(call.code) : [];
    emit(`(${call.kind === "local" ? "M97" : call.kind === "g65" ? "G65" : "M98"} P${call.program} -> ${unitName}${call.repeats > 1 ? ` x${call.repeats}` : ""})`, origin);
    for (let repeat = 0; repeat < call.repeats; repeat += 1) {
      for (const assignment of assignments) emit(assignment, origin);
      inline(body, origin, unitName, depth, stack);
      if (total >= MAX_LINES) {
        note("Subprogram expansion stopped at 200000 lines.");
        return;
      }
    }
  };

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    const origin = map[index] || { line: index + 1 };
    const call = parseCall(stripComments(line));
    if (call) expandCall(call, line, origin, 1, [call.program], lines);
    else if (!emit(line, origin)) break;
  }
  return out;
}

module.exports = { expand, parseCall };
