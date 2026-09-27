"use strict";

const {
  evaluateExpression,
  expandAddressExpressions,
  initialiseVariables
} = require("./macro");
const { fanucBit, fanucValue } = require("./machine-parameters");

const CYCLE_CODES = new Set([70, 72, 73, 74, 75, 76, 77, 78, 79, 81, 82, 83, 84, 85, 86, 87, 88, 89]);
const MAX_SYNTHETIC_CYCLE_SEGMENTS = 100000;
const CYCLE_RESOURCE_LIMIT_PREFIX = "Preview resource limit";

function cycleResourceLimit(cycle, estimatedSegments, availableSegments) {
  if (
    Number.isSafeInteger(estimatedSegments) &&
    estimatedSegments >= 0 &&
    estimatedSegments <= availableSegments
  ) {
    return undefined;
  }
  const estimate = Number.isFinite(estimatedSegments)
    ? Math.max(0, Math.ceil(estimatedSegments)).toLocaleString("en-US")
    : "an unbounded number of";
  return `${CYCLE_RESOURCE_LIMIT_PREFIX}: G${cycle} would generate ${estimate} synthetic segments; only ${Math.max(0, availableSegments).toLocaleString("en-US")} remain in the ${MAX_SYNTHETIC_CYCLE_SEGMENTS.toLocaleString("en-US")}-segment preview budget.`;
}

function stripComments(line) {
  return line.replace(/\([^)]*\)/g, " ").replace(/;.*$/, " ");
}

function last(values) {
  return values && values.length ? values[values.length - 1] : undefined;
}

function readWords(line) {
  const words = new Map();
  const expression = /(?<![A-Z])([A-Z])\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+))/gi;
  let match;
  while ((match = expression.exec(line)) !== null) {
    const letter = match[1].toUpperCase();
    const value = Number(match[2]);
    if (!Number.isFinite(value)) {
      continue;
    }
    const values = words.get(letter) || [];
    values.push(value);
    words.set(letter, values);
  }
  return words;
}

function addressText(line, letter) {
  const expression = new RegExp(
    `(?:^|\\s)${letter}\\s*([+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+))`,
    "i"
  );
  const match = stripComments(line).match(expression);
  return match ? match[1] : undefined;
}

function fixedCycleValue(raw, units) {
  if (raw === undefined) {
    return undefined;
  }
  const number = Number(raw);
  if (!Number.isFinite(number)) {
    return undefined;
  }
  if (String(raw).includes(".")) {
    return number;
  }
  return number * (units === "inch" ? 0.0001 : 0.001);
}

function resolveCode(raw, variables, onUndefined) {
  return expandAddressExpressions(
    stripComments(raw).toUpperCase(),
    variables,
    onUndefined
  );
}

function normalizePositive(angle) {
  const twoPi = Math.PI * 2;
  let result = angle % twoPi;
  if (result < 0) {
    result += twoPi;
  }
  return result;
}

function directedSweep(startAngle, endAngle, clockwise) {
  if (clockwise) {
    return -normalizePositive(startAngle - endAngle);
  }
  return normalizePositive(endAngle - startAngle);
}

function centerFromRadius(start, end, radius, clockwise) {
  const sx = start.z;
  const sy = start.x / 2;
  const ex = end.z;
  const ey = end.x / 2;
  const dx = ex - sx;
  const dy = ey - sy;
  const chord = Math.hypot(dx, dy);
  const absoluteRadius = Math.abs(radius);

  if (chord === 0 || chord > absoluteRadius * 2 + 1e-7) {
    return undefined;
  }

  const mx = (sx + ex) / 2;
  const my = (sy + ey) / 2;
  const height = Math.sqrt(Math.max(0, absoluteRadius ** 2 - (chord / 2) ** 2));
  const nx = -dy / chord;
  const ny = dx / chord;
  const candidates = [
    { z: mx + nx * height, radius: my + ny * height },
    { z: mx - nx * height, radius: my - ny * height }
  ];

  const scored = candidates.map((center) => {
    const a0 = Math.atan2(sy - center.radius, sx - center.z);
    const a1 = Math.atan2(ey - center.radius, ex - center.z);
    const sweep = directedSweep(a0, a1, clockwise);
    return { center, a0, sweep, magnitude: Math.abs(sweep) };
  });

  scored.sort((a, b) => {
    if (radius < 0) {
      return b.magnitude - a.magnitude;
    }
    return a.magnitude - b.magnitude;
  });
  return scored[0];
}

function interpolateArc(start, end, words, clockwise) {
  const rWord = last(words.get("R"));
  const iWord = last(words.get("I"));
  const kWord = last(words.get("K"));
  let arc;

  if (Number.isFinite(rWord)) {
    arc = centerFromRadius(start, end, rWord, clockwise);
  } else if (Number.isFinite(iWord) || Number.isFinite(kWord)) {
    const center = {
      z: start.z + (Number.isFinite(kWord) ? kWord : 0),
      radius: start.x / 2 + (Number.isFinite(iWord) ? iWord : 0)
    };
    const a0 = Math.atan2(start.x / 2 - center.radius, start.z - center.z);
    const a1 = Math.atan2(end.x / 2 - center.radius, end.z - center.z);
    arc = {
      center,
      a0,
      sweep: directedSweep(a0, a1, clockwise)
    };
  }

  if (!arc) {
    return [start, end];
  }

  const physicalRadius = Math.hypot(
    start.z - arc.center.z,
    start.x / 2 - arc.center.radius
  );
  const steps = Math.max(12, Math.ceil(Math.abs(arc.sweep) / (Math.PI / 36)));
  const points = [];
  for (let index = 0; index <= steps; index += 1) {
    const angle = arc.a0 + arc.sweep * (index / steps);
    points.push({
      z: arc.center.z + Math.cos(angle) * physicalRadius,
      x: (arc.center.radius + Math.sin(angle) * physicalRadius) * 2
    });
  }
  points[0] = start;
  points[points.length - 1] = end;
  return points;
}

function parseStock(source) {
  const stockMatch = source.match(
    /\(\s*STOCK\s+TURN\s+OD\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+))\s+ID\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+))\s+L\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+))\s+PZ\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+))/i
  );
  if (!stockMatch) {
    return undefined;
  }
  return {
    od: Number(stockMatch[1]),
    id: Number(stockMatch[2]),
    length: Number(stockMatch[3]),
    frontZ: Number(stockMatch[4])
  };
}

function inferToolType(comment) {
  const text = String(comment || "").toUpperCase();
  if (/(?:^|[_\s-])(?:BAR[_\s-]*PULLER|PART[_\s-]*EXTRUDER)(?:$|[_\s-])/.test(text)) {
    return "bar-puller";
  }
  if (/(?:^|[_\s-])(?:INTERNAL|BORE|BORING)(?:$|[_\s-])/.test(text)) {
    return "internal-cutter";
  }
  if (/(?:^|[_\s-])DRILL(?:$|[_\s-])/.test(text)) {
    return "drill";
  }
  if (/(?:^|[_\s-])THREAD/.test(text)) {
    return "threading";
  }
  if (/(?:^|[_\s-])GROOV/.test(text)) {
    return "groove";
  }
  if (/(?:^|[_\s-])(?:CUT[_\s-]*OFF|PARTING)(?:$|[_\s-])/.test(text)) {
    return "cutoff";
  }
  if (/(?:^|[_\s-])(?:EXTERNAL|EXT)(?:$|[_\s-])/.test(text)) {
    return "external-cutter";
  }
  return "other";
}

function parseToolComments(source) {
  const definitions = new Map();
  const lines = source.split(/\r?\n/);
  for (const raw of lines) {
    const toolMatch = raw.match(/\bT(\d{4,})\b/i);
    if (toolMatch) {
      const digits = toolMatch[1].padStart(4, "0");
      const tool = Number(digits.slice(0, -2));
      const comment = (raw.match(/\([^)]*\)/g) || [])
        .join(" ")
        .replace(/[()]/g, "")
        .trim();
      const radiusMatch = comment.match(/(?:^|[_\s-])R(?:ADIUS)?[_=\s-]*(\d+(?:\.\d+)?)/i);
      const tipMatch = comment.match(/(?:^|[_\s-])TIP[_=\s-]*([0-9])(?:$|[_\s-])/i);
        definitions.set(tool, {
          number: tool,
          cornerRadius: radiusMatch ? Number(radiusMatch[1]) : 0,
          tip: tipMatch ? Number(tipMatch[1]) : 0,
          type: inferToolType(comment),
          description: comment,
          hand: "unknown",
          recognition: "heuristic",
          confidence: comment ? 0.45 : 0.15,
          evidence: comment ? `NC tool-change comment: ${comment}` : "T code only",
          locked: false,
          tipKnown: Boolean(tipMatch)
        });
    }

    const ltoolMatch = raw.match(
      /\bLTOOL\s+(\d+)[^)]*?\bCR\s*=?\s*(\d+(?:\.\d+)?)/i
    );
    if (ltoolMatch) {
      const tool = Number(ltoolMatch[1]);
      const existing = definitions.get(tool) || {
        number: tool,
        tip: 0,
        type: "other",
        description: ""
      };
      definitions.set(tool, {
        ...existing,
        cornerRadius: Number(ltoolMatch[2])
      });
    }
  }
  return definitions;
}

function resolveToolDefinitions(source, toolTable) {
  const inferred = parseToolComments(source);
  const resolved = new Map(inferred);
  for (const configured of Object.values(toolTable?.tools || {})) {
    const number = Number(configured.number);
    if (!Number.isInteger(number) || number < 1) {
      continue;
    }
      const fallback = inferred.get(number);
      const configuredTipIsKnown = configured.tipKnown !== false;
      const configuredTipIsValid =
        Number.isInteger(configured.tip) && configured.tip >= 0 && configured.tip <= 9;
      const resolvedTip = configuredTipIsKnown && configuredTipIsValid
        ? configured.tip
        : fallback?.tipKnown
          ? fallback.tip
          : configuredTipIsValid
            ? configured.tip
            : fallback?.tip || 0;
      resolved.set(number, {
        ...fallback,
        ...configured,
        number,
        cornerRadius: Number.isFinite(configured.cornerRadius)
          ? configured.cornerRadius
        : fallback?.cornerRadius || 0,
      tip: resolvedTip,
      tipKnown: configuredTipIsKnown || Boolean(fallback?.tipKnown),
      type: configured.type || fallback?.type || "other",
      description: configured.description || fallback?.description || ""
    });
  }
  return resolved;
}

function toolDefinitionsForSource(source, toolTable) {
  const inferred = parseToolComments(source);
  const resolved = resolveToolDefinitions(source, toolTable);
  return [...inferred.keys()]
    .sort((a, b) => a - b)
    .map((number) => resolved.get(number));
}

function defaultToolDefinition(number) {
    return {
      number,
      cornerRadius: 0,
      tip: 0,
      type: "other",
      description: "",
      hand: "unknown",
      recognition: "heuristic",
      confidence: 0.1,
      evidence: "T code only",
      locked: false,
      tipKnown: false
    };
  }

function isNonCuttingTool(definition) {
  return (
    definition?.type === "bar-puller" ||
    definition?.type === "non-cutting"
  );
}

function findSequenceIndex(lines, sequence) {
  for (let index = 0; index < lines.length; index += 1) {
    const words = readWords(stripComments(lines[index]).toUpperCase());
    if ((words.get("N") || []).some((value) => value === sequence)) {
      return index;
    }
  }
  return -1;
}

function sequenceIndexMap(lines) {
  const sequences = new Map();
  lines.forEach((raw, index) => {
    const match = stripComments(raw).toUpperCase().match(/(?:^|\s)N\s*(\d+)/);
    if (match && !sequences.has(Number(match[1]))) {
      sequences.set(Number(match[1]), index);
    }
  });
  return sequences;
}

function extractProfile(
  lines,
  firstIndex,
  lastIndex,
  variables,
  initialMotion,
  onUndefined,
  initialPosition,
  localCoordinateOffset = { x: 0, z: 0 }
) {
  const profile = [];
  let x = initialPosition?.x;
  let z = initialPosition?.z;
  let motion = initialMotion;

  for (let index = firstIndex; index <= lastIndex; index += 1) {
    const words = readWords(resolveCode(lines[index], variables, onUndefined));
    const gCodes = words.get("G") || [];
    const explicitMotion = gCodes.find((value) => value >= 0 && value <= 3);
    if (Number.isFinite(explicitMotion)) {
      motion = Math.trunc(explicitMotion);
    }

    const xWord = last(words.get("X"));
    const uWord = last(words.get("U"));
    const zWord = last(words.get("Z"));
    const wWord = last(words.get("W"));
    let nextX = x;
    let nextZ = z;

    if (Number.isFinite(xWord)) {
      nextX = xWord + (localCoordinateOffset.x || 0);
    } else if (Number.isFinite(uWord) && Number.isFinite(x)) {
      nextX = x + uWord;
    }
    if (Number.isFinite(zWord)) {
      nextZ = zWord + (localCoordinateOffset.z || 0);
    } else if (Number.isFinite(wWord) && Number.isFinite(z)) {
      nextZ = z + wWord;
    }

    if (!Number.isFinite(nextX) || !Number.isFinite(nextZ)) {
      x = nextX;
      z = nextZ;
      continue;
    }

    const end = { x: nextX, z: nextZ };
    if (!Number.isFinite(x) || !Number.isFinite(z)) {
      profile.push(end);
    } else {
      const start = { x, z };
      const points =
        (motion === 2 || motion === 3)
          ? interpolateArc(start, end, words, motion === 2)
          : [start, end];
      profile.push(
        ...points
          .slice(profile.length ? 1 : 0)
          .map((point) => ({ ...point, sourceLine: index + 1 }))
      );
    }
    x = nextX;
    z = nextZ;
  }
  return profile;
}

function trimTurningContour(profile, startDiameter, radialDirection = "external") {
  if (!profile.length) {
    return [];
  }
  const contour = [...profile];

  const startIndex = contour.findIndex((point, index) => {
    const next = contour[index + 1];
    // A Type-I P block may intentionally cross the spindle center (for
    // example X-0.2 before a Z face cut). Keep that endpoint in the roughing
    // envelope; requiring positive X leaves the center/front stock uncut.
    const isInsideStart =
      radialDirection === "internal"
        ? point.x > startDiameter
        : point.x < startDiameter;
    return (
      isInsideStart &&
      next &&
      next.z < point.z - 1e-7
    );
  });
  return startIndex >= 0 ? contour.slice(startIndex) : contour.filter((point) => point.x > 0);
}

function contourCrossing(contour, thresholdX, radialDirection = "external") {
  if (!contour.length) return undefined;
  for (let index = 1; index < contour.length; index += 1) {
    const previous = contour[index - 1];
    const point = contour[index];
    const crossed =
      radialDirection === "internal"
        ? previous.x >= thresholdX && point.x < thresholdX
        : previous.x <= thresholdX && point.x > thresholdX;
    if (crossed) {
      const fraction =
        Math.abs(point.x - previous.x) < 1e-9
          ? 0
          : (thresholdX - previous.x) / (point.x - previous.x);
      return {
        point: {
          x: thresholdX,
          z: previous.z + (point.z - previous.z) * fraction
        },
        position: index - 1 + fraction
      };
    }
  }
  return {
    point: {
      x: thresholdX,
      z: Math.min(...contour.map((point) => point.z))
    },
    position: undefined
  };
}

function pointAtContourPosition(contour, position) {
  if (position <= 0) return { ...contour[0] };
  if (position >= contour.length - 1) {
    return { ...contour[contour.length - 1] };
  }
  const index = Math.floor(position);
  const fraction = position - index;
  const start = contour[index];
  const end = contour[index + 1];
  return {
    x: start.x + (end.x - start.x) * fraction,
    z: start.z + (end.z - start.z) * fraction,
    sourceLine: end.sourceLine
  };
}

function contourPath(contour, startPosition, endPosition) {
  if (
    !Number.isFinite(startPosition) ||
    !Number.isFinite(endPosition) ||
    endPosition <= startPosition + 1e-9
  ) {
    return [];
  }
  const points = [pointAtContourPosition(contour, startPosition)];
  for (
    let index = Math.floor(startPosition) + 1;
    index < endPosition - 1e-9;
    index += 1
  ) {
    points.push({ ...contour[index] });
  }
  points.push(pointAtContourPosition(contour, endPosition));
  return points;
}

function contourThresholdRuns(contour, thresholdX, radialDirection = "external") {
  const tolerance = 1e-7;
  const isExposed = (x) =>
    radialDirection === "internal"
      ? x >= thresholdX - tolerance
      : x <= thresholdX + tolerance;
  const runs = [];
  let active;

  for (let index = 0; index < contour.length - 1; index += 1) {
    const start = contour[index];
    const end = contour[index + 1];
    const fractions = [0, 1];
    const deltaX = end.x - start.x;
    if (Math.abs(deltaX) > tolerance) {
      const crossing = (thresholdX - start.x) / deltaX;
      if (crossing > tolerance && crossing < 1 - tolerance) {
        fractions.push(crossing);
      }
    }
    fractions.sort((a, b) => a - b);

    for (let part = 0; part < fractions.length - 1; part += 1) {
      const from = fractions[part];
      const to = fractions[part + 1];
      const middle = (from + to) / 2;
      const middleX = start.x + deltaX * middle;
      if (!isExposed(middleX)) {
        active = undefined;
        continue;
      }

      const startPosition = index + from;
      const endPosition = index + to;
      if (
        active &&
        Math.abs(active.endPosition - startPosition) <= tolerance
      ) {
        active.endPosition = endPosition;
      } else {
        active = { startPosition, endPosition };
        runs.push(active);
      }
    }
  }

  return runs
    .map((run) => ({
      ...run,
      start: pointAtContourPosition(contour, run.startPosition),
      end: pointAtContourPosition(contour, run.endPosition)
    }))
    .filter(
      (run) =>
        Math.abs(run.endPosition - run.startPosition) > tolerance &&
        (Math.abs(run.end.z - run.start.z) > tolerance ||
          Math.abs(run.end.x - run.start.x) > tolerance)
    );
}

function firstContourThresholdAfter(
  contour,
  startPosition,
  thresholdX,
  radialDirection = "external"
) {
  const tolerance = 1e-7;
  const reached = (x) =>
    radialDirection === "internal"
      ? x <= thresholdX + tolerance
      : x >= thresholdX - tolerance;
  const firstSegment = Math.max(0, Math.floor(startPosition));

  for (let index = firstSegment; index < contour.length - 1; index += 1) {
    const segmentStart = Math.max(startPosition, index);
    const start = pointAtContourPosition(contour, segmentStart);
    const end = contour[index + 1];
    if (reached(start.x)) return segmentStart;
    if (!reached(end.x)) continue;

    const deltaX = end.x - start.x;
    if (Math.abs(deltaX) <= tolerance) return index + 1;
    const fraction = (thresholdX - start.x) / deltaX;
    return segmentStart + (index + 1 - segmentStart) * fraction;
  }
  return contour.length - 1;
}

function firstAxisOnlyStop(contour, startPosition) {
  const startSegment = Math.max(0, Math.floor(startPosition));
  for (let index = startSegment; index < contour.length - 1; index += 1) {
    const segmentStart = Math.max(startPosition, index);
    const start = pointAtContourPosition(contour, segmentStart);
    const end = contour[index + 1];
    if (
      Math.abs(end.x - start.x) < 1e-7 &&
      Math.abs(end.z - start.z) > 1e-7
    ) {
      return segmentStart;
    }
  }
  return contour.length - 1;
}

function profileCompensationCodes(
  lines,
  firstIndex,
  lastIndex,
  variables,
  onUndefined
) {
  const codes = new Set();
  for (let index = firstIndex; index <= lastIndex; index += 1) {
    const words = readWords(resolveCode(lines[index], variables, onUndefined));
    for (const code of words.get("G") || []) {
      if (code === 40 || code === 41 || code === 42) {
        codes.add(code);
      }
    }
  }
  return [...codes].sort((a, b) => a - b);
}

function monotoneViolations(contour, cycleType, toleranceZ, toleranceX) {
  const violations = [];
  const checkAxis = (axis, tolerance) => {
    let direction = 0;
    for (let index = 1; index < contour.length; index += 1) {
      const delta = contour[index][axis] - contour[index - 1][axis];
      if (Math.abs(delta) <= tolerance) continue;
      const sign = Math.sign(delta);
      if (!direction) {
        direction = sign;
      } else if (sign !== direction) {
        violations.push(axis.toUpperCase());
        return;
      }
    }
  };
  checkAxis("z", Math.max(0, Math.abs(toleranceZ)));
  if (cycleType === "I") {
    checkAxis("x", Math.max(0, Math.abs(toleranceX)) * 2);
  }
  return violations;
}

function expandG71(
  lines,
  index,
  words,
  pending,
  variables,
  state,
  onUndefined,
  machineParameters,
  segmentBudget = MAX_SYNTHETIC_CYCLE_SEGMENTS
) {
  const p = last(words.get("P"));
  const q = last(words.get("Q"));
  if (!pending || !Number.isFinite(p) || !Number.isFinite(q)) {
    return { segments: [], passes: 0 };
  }
  const firstIndex = findSequenceIndex(lines, p);
  const lastIndex = findSequenceIndex(lines, q);
  if (firstIndex < 0 || lastIndex < firstIndex) {
    return { segments: [], passes: 0 };
  }

  const firstProfileWords = readWords(
    resolveCode(lines[firstIndex], variables, onUndefined)
  );
  const hasFirstX =
    firstProfileWords.has("X") || firstProfileWords.has("U");
  const hasFirstZ =
    firstProfileWords.has("Z") || firstProfileWords.has("W");
  const cycleType = hasFirstX && hasFirstZ ? "II" : "I";
  const roughFinishSuppressed =
    cycleType === "II"
      ? fanucBit(machineParameters, 5105, 2)
      : fanucBit(machineParameters, 5105, 1);
  const nsp =
    cycleType === "II" ? fanucBit(machineParameters, 5108, 3) : 0;
  const asu = fanucBit(machineParameters, 5107, 0);
  const requestedR16 =
    cycleType === "II" ? fanucBit(machineParameters, 5108, 0) : 0;
  // FANUC B-64607EN/01 defines 5108#0 R16 and 5108#3 NSP as
  // independent Type-II controls. Do not infer one bit from the other.
  const effectiveR16 = requestedR16;
  const dtp = cycleType === "I" ? fanucBit(machineParameters, 5108, 1) : 0;
  const nt1 = fanucBit(machineParameters, 5106, 2);
  const fck = fanucBit(machineParameters, 5104, 2);
  const compensationCodes = profileCompensationCodes(
    lines,
    firstIndex,
    lastIndex,
    variables,
    onUndefined
  );
  if (compensationCodes.length && !nt1) {
    return {
      segments: [],
      passes: 0,
      cycleType,
      nsp,
      requestedR16,
      effectiveR16,
      dtp,
      asu,
      fck,
      nt1,
      alarm: `PS0325: G${compensationCodes.join("/G")} is not allowed in the G71 target profile when 5106#2 NT1=0.`
    };
  }

  const profile = extractProfile(
    lines,
    firstIndex,
    lastIndex,
    variables,
    state.motion,
    onUndefined,
    pending.start,
    state.localCoordinateOffset
  );
  const allowanceX = Number.isFinite(last(words.get("U"))) ? last(words.get("U")) : 0;
  const allowanceZ = Number.isFinite(last(words.get("W"))) ? last(words.get("W")) : 0;
  const profileMinimumX = Math.min(...profile.map((point) => point.x));
  const profileMaximumX = Math.max(...profile.map((point) => point.x));
  const radialDirection =
    allowanceX < -1e-7 ||
    (Math.abs(allowanceX) <= 1e-7 &&
      profileMaximumX - pending.start.x > pending.start.x - profileMinimumX)
      ? "internal"
      : "external";
  // Type II must retain the P-block lead-in and front-face portion. Lower-X
  // roughing passes intersect that face before entering the finished solid;
  // trimming to the first OD corner incorrectly removes those passes and also
  // leaves a short, non-DOC step at the OD transition.
  const contour =
    cycleType === "II"
      ? profile
      : trimTurningContour(profile, pending.start.x, radialDirection);
  if (contour.length < 2 || !Number.isFinite(pending.depth) || pending.depth <= 0) {
    return { segments: [], passes: 0 };
  }

  const violations = monotoneViolations(
    contour,
    cycleType,
    fanucValue(machineParameters, 5145, 0),
    fanucValue(machineParameters, 5146, 0)
  );
  if (fck && violations.length) {
    return {
      segments: [],
      passes: 0,
      cycleType,
      nsp,
      requestedR16,
      effectiveR16,
      dtp,
      asu,
      fck,
      nt1,
      alarm: `G71 type ${cycleType} profile check failed: non-monotone ${violations.join("/")} axis.`
    };
  }

  const feed = Number.isFinite(last(words.get("F"))) ? last(words.get("F")) : state.feed;
  // FANUC G71 second-block U is a programmed diameter allowance. W is axial.
  const allowanceContour = contour.map((point) => ({
    x: point.x + allowanceX,
    z: point.z + allowanceZ,
    sourceLine: point.sourceLine
  }));
  // The rough-finishing pass must retain the complete P-Q path, including
  // the P-block lead-in and face cut. The initial cycle position is already
  // the control's starting point; U/W apply to the commanded profile points.
  const roughFinishPath = profile.map((point, pointIndex) => ({
    x: point.x + (pointIndex ? allowanceX : 0),
    z: point.z + (pointIndex ? allowanceZ : 0),
    sourceLine: point.sourceLine
  }));
  const targetDiameter =
    radialDirection === "internal"
      ? Math.max(...allowanceContour.map((point) => point.x))
      : Math.min(...allowanceContour.map((point) => point.x));
  const diameterStep = pending.depth * 2;
  const entryZ = Math.max(...profile.map((point) => point.z));
  const estimatedPasses = Math.ceil(
    Math.abs(pending.start.x - targetDiameter) / diameterStep
  );
  const perPassEstimate =
    cycleType === "II"
      ? contour.length * 10 + 4
      : contour.length + 4;
  const estimatedSegments =
    estimatedPasses * perPassEstimate + profile.length + 7;
  const resourceAlarm = cycleResourceLimit(
    71,
    estimatedSegments,
    segmentBudget
  );
  if (resourceAlarm) {
    return {
      segments: [],
      passes: 0,
      cycleType,
      radialDirection,
      resourceLimit: true,
      alarm: resourceAlarm
    };
  }
  const passDiameters = [];
  let previousDiameter = pending.start.x;
  while (
    radialDirection === "internal"
      ? previousDiameter < targetDiameter - 1e-7
      : previousDiameter > targetDiameter + 1e-7
  ) {
    const diameter =
      radialDirection === "internal"
        ? Math.min(targetDiameter, previousDiameter + diameterStep)
        : Math.max(targetDiameter, previousDiameter - diameterStep);
    passDiameters.push(diameter);
    previousDiameter = diameter;
  }

  const segments = [];
  let resourceExceeded = false;
  let current = { ...pending.start };
  const pushPath = (
    kind,
    points,
    pass,
    label,
    phase,
    sourceLine = index + 1,
    rawOverride
  ) => {
    const valid = points.filter(
      (point) => Number.isFinite(point.x) && Number.isFinite(point.z)
    );
    if (valid.length < 2) {
      return;
    }
    const start = valid[0];
    const end = valid[valid.length - 1];
    if (start.x === end.x && start.z === end.z && valid.length === 2) {
      return;
    }
    if (segments.length >= segmentBudget) {
      resourceExceeded = true;
      return;
    }
    segments.push({
      line: sourceLine,
      raw:
        rawOverride ||
        `G71 ${label} ${pass}/${passDiameters.length}`,
      tool: state.tool,
      kind,
      motion: kind === "rapid" ? 0 : 1,
      feed,
      feedMode: state.feedMode,
      compensation: 40,
      spindleMode: state.spindleMode,
      spindleSpeed: state.spindleSpeed,
      spindleLimit: state.spindleLimit,
      units: state.units,
      points: valid,
      start,
      end,
      toolRadius: state.toolDefinition?.cornerRadius || 0,
      toolTip: state.toolDefinition?.tip || 0,
      toolType: state.toolDefinition?.type || "other",
      nonCutting: isNonCuttingTool(state.toolDefinition),
      cycle: "G71",
      cyclePass: pass,
      cyclePassCount: passDiameters.length,
      cyclePhase: phase,
      synthetic: true
    });
  };
  const push = (kind, start, end, pass, label, phase) =>
    pushPath(kind, [start, end], pass, label, phase);
  const pushProfilePath = (kind, points, pass, label, phase) => {
    if (points.length < 2) return;
    let group = [points[0]];
    let groupLine = points[1].sourceLine || index + 1;
    for (let pointIndex = 1; pointIndex < points.length; pointIndex += 1) {
      const point = points[pointIndex];
      const line = point.sourceLine || groupLine;
      if (line !== groupLine && group.length > 1) {
        pushPath(
          kind,
          group,
          pass,
          label,
          phase,
          groupLine,
          `${lines[groupLine - 1].trim()} (G71 ${label})`
        );
        group = [group[group.length - 1]];
        groupLine = line;
      }
      group.push(point);
    }
    if (group.length > 1) {
      pushPath(
        kind,
        group,
        pass,
        label,
        phase,
        groupLine,
        `${lines[groupLine - 1].trim()} (G71 ${label})`
      );
    }
  };

  passDiameters.forEach((diameter, passIndex) => {
    const pass = passIndex + 1;

    // With Type-II, a constant-diameter roughing level can contain
    // several exposed Z intervals separated by finished material. Treating the
    // entire level as one axial stroke makes a recessed/undercut profile cut
    // through the larger front land. Split the level at every contour crossing,
    // enter a recessed interval from the safe cycle-start diameter, and remove
    // only one radial DOC before traversing that interval in Z.
    if (cycleType === "II") {
      const runs = contourThresholdRuns(
        allowanceContour,
        diameter,
        radialDirection
      );
      const tangentIndex = allowanceContour.findIndex(
        (point) => Math.abs(point.x - diameter) <= 1e-7
      );
      if (
        tangentIndex >= 0 &&
        (!runs.length || runs[0].startPosition > tangentIndex + 1e-7)
      ) {
        // A final DOC level can touch the profile only at the P-block point.
        // The contour interval is then zero-length, but the axial stroke from
        // the cycle entry plane to that point is still real motion.
        const tangent = allowanceContour[tangentIndex];
        runs.unshift({
          startPosition: tangentIndex,
          endPosition: tangentIndex,
          start: { ...tangent },
          end: { ...tangent },
          frontBoundary: true
        });
      }
      const priorDiameter =
        passIndex > 0 ? passDiameters[passIndex - 1] : pending.start.x;

      runs.forEach((run, runIndex) => {
        const frontConnected =
          run.frontBoundary ||
          (runIndex === 0 && run.startPosition <= 1 + 1e-7);
        const cutStart = {
          x: diameter,
          z: frontConnected ? entryZ : run.start.z
        };

        if (frontConnected) {
          push(
            "rapid",
            current,
            cutStart,
            pass,
            `APPROACH INTERVAL ${runIndex + 1}/${runs.length}`,
            "approach"
          );
        } else {
          const safeAtEntry = { x: pending.start.x, z: entryZ };
          push(
            "rapid",
            current,
            safeAtEntry,
            pass,
            `POCKET SAFE X ${runIndex + 1}/${runs.length}`,
            "pocket-position"
          );
          const safeAtPocket = {
            x: pending.start.x,
            z: run.start.z
          };
          push(
            "rapid",
            safeAtEntry,
            safeAtPocket,
            pass,
            `POCKET POSITION ${runIndex + 1}/${runs.length}`,
            "pocket-position"
          );
          const previousLevel = {
            x: priorDiameter,
            z: run.start.z
          };
          push(
            "rapid",
            safeAtPocket,
            previousLevel,
            pass,
            `POCKET CLEARANCE ${runIndex + 1}/${runs.length}`,
            "pocket-approach"
          );
          push(
            "g71",
            previousLevel,
            cutStart,
            pass,
            `POCKET STEP X${diameter.toFixed(3)}`,
            "radial-rough"
          );
        }

        let cutEnd = { x: diameter, z: run.end.z };
        push(
          "g71",
          cutStart,
          cutEnd,
          pass,
          `ROUGH X${diameter.toFixed(3)} INTERVAL ${runIndex + 1}/${runs.length}`,
          "axial-rough"
        );

        const retract = Number.isFinite(pending.retract)
          ? pending.retract
          : 0;
        let escapesFromBottom = false;
        if (Number.isFinite(run.endPosition)) {
          // FANUC B-64694EN-1/01, pp. 41-47:
          // Fig. 4.2.1(i) separates two movements: cut along the workpiece
          // figure by one roughing depth (to the preceding pass level), then
          // escape by e along the second axis. Fig. 4.2.1(j) replaces those
          // movements with a 45-degree e escape when moving from a bottom.
          // R16=1 continues through a first-axis-only block instead.
          const bottomStop = firstAxisOnlyStop(
            allowanceContour,
            run.endPosition
          );
          escapesFromBottom =
            !effectiveR16 && bottomStop <= run.endPosition + 1e-7;
          let contourEnd = firstContourThresholdAfter(
            allowanceContour,
            run.endPosition,
            priorDiameter,
            radialDirection
          );
          const nextRun = runs[runIndex + 1];
          if (nextRun) {
            // Do not follow the figure through the next roughing interval.
            // That interval is cut by its own constant-diameter stroke.
            contourEnd = Math.min(contourEnd, nextRun.startPosition);
          }
          if (!effectiveR16 && bottomStop < contourEnd - 1e-7) {
            contourEnd = bottomStop;
            escapesFromBottom = true;
          }
          if (!escapesFromBottom) {
            const follow = contourPath(
              allowanceContour,
              run.endPosition,
              contourEnd
            );
            if (follow.length > 1) {
              pushProfilePath(
                "g71-contour",
                follow,
                pass,
                `CUT ALONG FIGURE TO PREVIOUS DEPTH`,
                "cut-along-figure"
              );
              cutEnd = follow[follow.length - 1];
            }
          }
        }

        const reverseFirstAxis = Math.sign(entryZ - cutEnd.z) || 1;
        const retractEnd = {
          x:
            cutEnd.x +
            (radialDirection === "internal" ? -1 : 1) * retract * 2,
          z: cutEnd.z + (escapesFromBottom ? reverseFirstAxis * retract : 0)
        };
        push(
          "feed",
          cutEnd,
          retractEnd,
          pass,
          escapesFromBottom
            ? "ESCAPE 45 DEG FROM BOTTOM"
            : "ESCAPE e ALONG SECOND AXIS",
          "cut-up"
        );

        let returnPoint;
        if (frontConnected && runs.length === 1) {
          // Fig. 4.2.1(i): after the local second-axis escape, return toward
          // the turning start along the first axis. The next pass changes X
          // at the entry plane; do not retract to cycle-start X here.
          returnPoint = { x: retractEnd.x, z: entryZ };
          push(
            asu ? "rapid" : "feed",
            retractEnd,
            returnPoint,
            pass,
            `RETURN ALONG FIRST AXIS ASU=${asu}`,
            "return-to-turning-start"
          );
        } else {
          // Multiple-pocket positioning follows the separate safe-height
          // sequence described with Fig. 4.2.1(o).
          const safeX = { x: pending.start.x, z: retractEnd.z };
          push(
            asu ? "rapid" : "feed",
            retractEnd,
            safeX,
            pass,
            `RETURN SAFE X ASU=${asu}`,
            "return-to-turning-start"
          );
          returnPoint = { x: pending.start.x, z: entryZ };
          push(
            asu ? "rapid" : "feed",
            safeX,
            returnPoint,
            pass,
            `RETURN ASU=${asu}`,
            "return-to-turning-start"
          );
        }
        current = returnPoint;
      });
      return;
    }

    const approach = { x: diameter, z: entryZ };
    push("rapid", current, approach, pass, "APPROACH", "approach");

    const crossing = contourCrossing(
      allowanceContour,
      diameter,
      radialDirection
    );
    let cutEnd = crossing.point;
    push(
      "g71",
      approach,
      cutEnd,
      pass,
      `ROUGH X${diameter.toFixed(3)}`,
      "axial-rough"
    );

    const retract = Number.isFinite(pending.retract) ? pending.retract : 0;
    const retractEnd = {
      // G71 R is a radial escape from the actual contour exit point. X is
      // programmed as diameter, so the outward X displacement is twice R.
      x:
        cutEnd.x +
        (radialDirection === "internal" ? -1 : 1) * retract * 2,
      z: cutEnd.z + (Math.sign(entryZ - cutEnd.z) || 1) * retract
    };
    push(
      "feed",
      cutEnd,
      retractEnd,
      pass,
      "ESCAPE 45 DEG CUTTING FEED",
      "cut-up"
    );
    const returnPoint = { x: retractEnd.x, z: entryZ };
    push(
      asu ? "rapid" : "feed",
      retractEnd,
      returnPoint,
      pass,
      `RETURN ASU=${asu}`,
      "return-to-turning-start"
    );
    current = returnPoint;
  });

  push(
    "rapid",
    current,
    pending.start,
    passDiameters.length,
    "RETURN TO CYCLE START",
    "return-to-cycle-start"
  );
  current = { ...pending.start };

  let roughFinishPassCount = 0;
  if (!roughFinishSuppressed && roughFinishPath.length > 1) {
    const finishStart = roughFinishPath[0];
    push(
      "rapid",
      current,
      finishStart,
      passDiameters.length,
      "ROUGH-FINISH APPROACH",
      "rough-finish-approach"
    );
    pushProfilePath(
      "g71-finish",
      roughFinishPath,
      passDiameters.length,
      `ROUGH FINISH RF${cycleType === "II" ? 2 : 1}=0`,
      "rough-finish"
    );
    roughFinishPassCount = 1;
    current = { ...roughFinishPath[roughFinishPath.length - 1] };

    if (cycleType === "II") {
      // Type-II rough finishing ends at Q and returns directly. The R escape
      // belongs to each roughing cut and must not be repeated after P-Q.
      push(
        "rapid",
        current,
        pending.start,
        passDiameters.length,
        "CYCLE END",
        "cycle-return"
      );
      current = { ...pending.start };
    } else if (dtp) {
      push(
        "rapid",
        current,
        pending.start,
        passDiameters.length,
        "DIRECT RETURN DTP=1",
        "cycle-return"
      );
      current = { ...pending.start };
    } else {
      const retract = Number.isFinite(pending.retract) ? pending.retract : 0;
      const escape = {
        x:
          current.x +
          (radialDirection === "internal" ? -1 : 1) * retract * 2,
        z: current.z + retract
      };
      push(
        "feed",
        current,
        escape,
        passDiameters.length,
        "ROUGH-FINISH ESCAPE",
        "rough-finish-escape"
      );
      current = escape;

      if (cycleType === "I" && !dtp) {
        const xAllowanceReturn = {
          x: pending.start.x + allowanceX,
          z: current.z
        };
        const zAllowanceReturn = {
          x: xAllowanceReturn.x,
          z: pending.start.z + allowanceZ
        };
        push(
          "rapid",
          current,
          xAllowanceReturn,
          passDiameters.length,
          "DTP=0 RETURN X",
          "cycle-return"
        );
        push(
          "rapid",
          xAllowanceReturn,
          zAllowanceReturn,
          passDiameters.length,
          "DTP=0 RETURN Z",
          "cycle-return"
        );
        current = zAllowanceReturn;
      }
      push(
        "rapid",
        current,
        pending.start,
        passDiameters.length,
        "CYCLE END",
        "cycle-return"
      );
      current = { ...pending.start };
    }
  }

  if (resourceExceeded) {
    return {
      segments: [],
      passes: 0,
      cycleType,
      radialDirection,
      resourceLimit: true,
      alarm: cycleResourceLimit(71, segmentBudget + 1, segmentBudget)
    };
  }

  return {
    segments,
    passes: passDiameters.length,
    roughFinishPassCount,
    allowance: {
      xDiameter: allowanceX,
      z: allowanceZ,
      finalDiameter: targetDiameter
    },
    cycleType,
    radialDirection,
    nsp,
    asu,
    requestedR16,
    effectiveR16,
    dtp,
    fck,
    nt1,
    compensationCodesIgnored: compensationCodes.length ? compensationCodes : [],
    profileViolationsIgnored: !fck ? violations : [],
    roughFinishSuppressed,
    alarm: undefined
  };
}

function facingContourCrossing(contour, thresholdZ) {
  if (!contour.length) return undefined;
  for (let index = 1; index < contour.length; index += 1) {
    const previous = contour[index - 1];
    const point = contour[index];
    const deltaZ = point.z - previous.z;
    if (
      Math.abs(deltaZ) > 1e-9 &&
      thresholdZ >= Math.min(previous.z, point.z) - 1e-7 &&
      thresholdZ <= Math.max(previous.z, point.z) + 1e-7
    ) {
      const fraction = (thresholdZ - previous.z) / deltaZ;
      return {
        x: previous.x + (point.x - previous.x) * fraction,
        z: thresholdZ
      };
    }
  }
  const nearest = [...contour].sort(
    (a, b) => Math.abs(a.z - thresholdZ) - Math.abs(b.z - thresholdZ)
  )[0];
  return nearest ? { x: nearest.x, z: thresholdZ } : undefined;
}

function expandG72(
  lines,
  index,
  words,
  pending,
  variables,
  state,
  onUndefined,
  machineParameters,
  segmentBudget = MAX_SYNTHETIC_CYCLE_SEGMENTS
) {
  const p = last(words.get("P"));
  const q = last(words.get("Q"));
  if (!pending || !Number.isFinite(p) || !Number.isFinite(q)) {
    return { segments: [], passes: 0 };
  }
  const firstIndex = findSequenceIndex(lines, p);
  const lastIndex = findSequenceIndex(lines, q);
  if (firstIndex < 0 || lastIndex < firstIndex) {
    return { segments: [], passes: 0 };
  }
  const firstProfileWords = readWords(
    resolveCode(lines[firstIndex], variables, onUndefined)
  );
  const hasFirstX = firstProfileWords.has("X") || firstProfileWords.has("U");
  const hasFirstZ = firstProfileWords.has("Z") || firstProfileWords.has("W");
  const cycleType = hasFirstX && hasFirstZ ? "II" : "I";
  const roughFinishSuppressed = fanucBit(
    machineParameters,
    5105,
    cycleType === "II" ? 2 : 1
  );
  const asu = fanucBit(machineParameters, 5107, 0);
  const profile = extractProfile(
    lines,
    firstIndex,
    lastIndex,
    variables,
    state.motion,
    onUndefined,
    pending.start,
    state.localCoordinateOffset
  );
  if (profile.length < 3 || !Number.isFinite(pending.depth) || pending.depth <= 0) {
    return { segments: [], passes: 0, cycleType, roughFinishSuppressed, asu };
  }

  const allowanceX = Number.isFinite(last(words.get("U"))) ? last(words.get("U")) : 0;
  const allowanceZ = Number.isFinite(last(words.get("W"))) ? last(words.get("W")) : 0;
  const feed = Number.isFinite(last(words.get("F"))) ? last(words.get("F")) : state.feed;
  const profileMinimumX = Math.min(...profile.map((point) => point.x));
  const profileMaximumX = Math.max(...profile.map((point) => point.x));
  const radialDirection =
    allowanceX < -1e-7 ||
    (Math.abs(allowanceX) <= 1e-7 &&
      profileMaximumX - pending.start.x > pending.start.x - profileMinimumX)
      ? "internal"
      : "external";
  // Omit the cycle-start point but retain the P-block Z positioning point.
  const contour = profile.slice(1).map((point) => ({
    x: point.x + allowanceX,
    z: point.z + allowanceZ,
    sourceLine: point.sourceLine
  }));
  const targetZ = Math.min(...contour.map((point) => point.z));
  const entryX = pending.start.x;
  const estimatedPasses = Math.ceil(
    Math.abs(pending.start.z - targetZ) / pending.depth
  );
  const resourceAlarm = cycleResourceLimit(
    72,
    estimatedPasses * 4 + contour.length + 4,
    segmentBudget
  );
  if (resourceAlarm) {
    return {
      segments: [],
      passes: 0,
      cycleType,
      radialDirection,
      resourceLimit: true,
      alarm: resourceAlarm
    };
  }
  const passPositions = [];
  let previousZ = pending.start.z;
  while (previousZ > targetZ + 1e-7) {
    const passZ = Math.max(targetZ, previousZ - pending.depth);
    passPositions.push(passZ);
    previousZ = passZ;
  }

  const segments = [];
  let current = { ...pending.start };
  const pushPath = (
    kind,
    points,
    pass,
    label,
    phase,
    sourceLine = index + 1,
    rawOverride
  ) => {
    const valid = points.filter(
      (point) => Number.isFinite(point.x) && Number.isFinite(point.z)
    );
    if (valid.length < 2) return;
    const start = valid[0];
    const end = valid[valid.length - 1];
    if (valid.length === 2 && start.x === end.x && start.z === end.z) return;
    segments.push({
      line: sourceLine,
      raw: rawOverride || `G72 ${label} ${pass}/${passPositions.length}`,
      tool: state.tool,
      kind,
      motion: kind === "rapid" ? 0 : 1,
      feed,
      feedMode: state.feedMode,
      compensation: 40,
      spindleMode: state.spindleMode,
      spindleSpeed: state.spindleSpeed,
      spindleLimit: state.spindleLimit,
      units: state.units,
      points: valid,
      start,
      end,
      toolRadius: state.toolDefinition?.cornerRadius || 0,
      toolTip: state.toolDefinition?.tip || 0,
      toolType: state.toolDefinition?.type || "other",
      nonCutting: isNonCuttingTool(state.toolDefinition),
      cycle: "G72",
      cyclePass: pass,
      cyclePassCount: passPositions.length,
      cyclePhase: phase,
      synthetic: true
    });
  };
  const push = (kind, start, end, pass, label, phase) =>
    pushPath(kind, [start, end], pass, label, phase);
  const pushProfilePath = (points, label, phase) => {
    if (points.length < 2) return;
    let group = [points[0]];
    let groupLine = points[1].sourceLine || index + 1;
    for (let pointIndex = 1; pointIndex < points.length; pointIndex += 1) {
      const point = points[pointIndex];
      const line = point.sourceLine || groupLine;
      if (line !== groupLine && group.length > 1) {
        pushPath(
          "g72-finish",
          group,
          passPositions.length,
          label,
          phase,
          groupLine,
          `${lines[groupLine - 1].trim()} (G72 ${label})`
        );
        group = [group[group.length - 1]];
        groupLine = line;
      }
      group.push(point);
    }
    if (group.length > 1) {
      pushPath(
        "g72-finish",
        group,
        passPositions.length,
        label,
        phase,
        groupLine,
        `${lines[groupLine - 1].trim()} (G72 ${label})`
      );
    }
  };

  passPositions.forEach((passZ, passIndex) => {
    const pass = passIndex + 1;
    const approach = { x: entryX, z: passZ };
    push("rapid", current, approach, pass, "APPROACH", "approach");
    const crossing = facingContourCrossing(contour, passZ);
    if (!crossing) return;
    push(
      "g72",
      approach,
      crossing,
      pass,
      `ROUGH Z${passZ.toFixed(3)}`,
      "radial-rough"
    );
    const retract = Number.isFinite(pending.retract) ? pending.retract : 0;
    const escape = {
      // G72 R is radial. External facing clears toward larger diameter;
      // internal facing must clear back toward the bore center (smaller X).
      x:
        crossing.x +
        (radialDirection === "internal" ? -1 : 1) * retract * 2,
      z: crossing.z + retract
    };
    push("rapid", crossing, escape, pass, "RETRACT", "cut-up");
    const returnPoint = { x: entryX, z: escape.z };
    push(
      asu ? "rapid" : "feed",
      escape,
      returnPoint,
      pass,
      `RETURN ASU=${asu}`,
      "return-to-facing-start"
    );
    current = returnPoint;
  });

  push(
    "rapid",
    current,
    pending.start,
    passPositions.length,
    "RETURN TO CYCLE START",
    "return-to-cycle-start"
  );
  current = { ...pending.start };

  let roughFinishPassCount = 0;
  if (!roughFinishSuppressed && contour.length > 1) {
    const finishEntry = {
      x: pending.start.x,
      z: profile[1].z + allowanceZ,
      sourceLine: profile[1].sourceLine
    };
    push(
      "rapid",
      current,
      finishEntry,
      passPositions.length,
      "ROUGH-FINISH APPROACH",
      "rough-finish-approach"
    );
    const finishContour = [finishEntry, ...contour.slice(1)];
    pushProfilePath(
      finishContour,
      `ROUGH FINISH RF${cycleType === "II" ? 2 : 1}=0`,
      "rough-finish"
    );
    roughFinishPassCount = 1;
    current = { ...finishContour[finishContour.length - 1] };
    push(
      "rapid",
      current,
      pending.start,
      passPositions.length,
      "CYCLE END",
      "cycle-return"
    );
  }

  return {
    segments,
    passes: passPositions.length,
    roughFinishPassCount,
    allowance: {
      xDiameter: allowanceX,
      z: allowanceZ,
      finalZ: targetZ
    },
    cycleType,
    radialDirection,
    roughFinishSuppressed,
    asu,
    alarm: undefined
  };
}

function expandG73(
  lines,
  index,
  words,
  pending,
  variables,
  state,
  onUndefined,
  segmentBudget = MAX_SYNTHETIC_CYCLE_SEGMENTS
) {
  const p = last(words.get("P"));
  const q = last(words.get("Q"));
  if (!pending || !Number.isFinite(p) || !Number.isFinite(q)) {
    return { segments: [], passes: 0 };
  }
  const firstIndex = findSequenceIndex(lines, p);
  const lastIndex = findSequenceIndex(lines, q);
  if (firstIndex < 0 || lastIndex < firstIndex) {
    return {
      segments: [],
      passes: 0,
      alarm: `G73 profile N${p}-N${q} was not found.`
    };
  }
  const divisions = Math.max(1, Math.trunc(pending.divisions || 0));
  const profile = extractProfile(
    lines,
    firstIndex,
    lastIndex,
    variables,
    state.motion,
    onUndefined,
    pending.start,
    state.localCoordinateOffset
  );
  if (profile.length < 2) {
    return {
      segments: [],
      passes: 0,
      alarm: "G73 P-Q profile does not contain enough motion."
    };
  }
  const resourceAlarm = cycleResourceLimit(
    73,
    divisions * (profile.length + 1),
    segmentBudget
  );
  if (resourceAlarm) {
    return {
      segments: [],
      passes: 0,
      resourceLimit: true,
      alarm: resourceAlarm
    };
  }
  const allowanceX = Number.isFinite(last(words.get("U"))) ? last(words.get("U")) : 0;
  const allowanceZ = Number.isFinite(last(words.get("W"))) ? last(words.get("W")) : 0;
  const feed = Number.isFinite(last(words.get("F"))) ? last(words.get("F")) : state.feed;
  // FANUC defines first-block G73 U as a radius relief even when X is
  // diameter-programmed. Convert it to a diameter displacement for display.
  const totalDiameterRelief = pending.reliefX * 2;
  const totalAxialRelief = pending.reliefZ;
  const segments = [];

  const pushPath = (
    kind,
    points,
    pass,
    phase,
    sourceLine = index + 1,
    rawOverride
  ) => {
    const valid = points.filter(
      (point) => Number.isFinite(point.x) && Number.isFinite(point.z)
    );
    if (valid.length < 2) return;
    const start = valid[0];
    const end = valid[valid.length - 1];
    if (valid.length === 2 && start.x === end.x && start.z === end.z) return;
    segments.push({
      line: sourceLine,
      raw: rawOverride || `G73 COPY ${pass}/${divisions}`,
      tool: state.tool,
      kind,
      motion: kind === "rapid" ? 0 : 1,
      feed,
      feedMode: state.feedMode,
      compensation: 40,
      spindleMode: state.spindleMode,
      spindleSpeed: state.spindleSpeed,
      spindleLimit: state.spindleLimit,
      units: state.units,
      points: valid,
      start,
      end,
      toolRadius: state.toolDefinition?.cornerRadius || 0,
      toolTip: state.toolDefinition?.tip || 0,
      toolType: state.toolDefinition?.type || "other",
      nonCutting: isNonCuttingTool(state.toolDefinition),
      cycle: "G73",
      cyclePass: pass,
      cyclePassCount: divisions,
      cyclePhase: phase,
      synthetic: true
    });
  };
  const pushProfilePath = (points, pass) => {
    let group = [points[0]];
    let groupLine = points[1]?.sourceLine || index + 1;
    for (let pointIndex = 1; pointIndex < points.length; pointIndex += 1) {
      const point = points[pointIndex];
      const line = point.sourceLine || groupLine;
      if (line !== groupLine && group.length > 1) {
        pushPath(
          "g73",
          group,
          pass,
          "copy-profile",
          groupLine,
          `${lines[groupLine - 1].trim()} (G73 COPY ${pass}/${divisions})`
        );
        group = [group[group.length - 1]];
        groupLine = line;
      }
      group.push(point);
    }
    if (group.length > 1) {
      pushPath(
        "g73",
        group,
        pass,
        "copy-profile",
        groupLine,
        `${lines[groupLine - 1].trim()} (G73 COPY ${pass}/${divisions})`
      );
    }
  };

  for (let pass = 1; pass <= divisions; pass += 1) {
    // The final repetition reaches the second-block U/W allowance contour.
    const factor = (divisions - pass) / divisions;
    const translated = profile.slice(1).map((point) => ({
      x: point.x + allowanceX + totalDiameterRelief * factor,
      z: point.z + allowanceZ + totalAxialRelief * factor,
      sourceLine: point.sourceLine
    }));
    const passPath = [{ ...pending.start }, ...translated];
    pushProfilePath(passPath, pass);
    const end = passPath[passPath.length - 1];
    pushPath(
      "rapid",
      [end, pending.start],
      pass,
      "copy-return"
    );
  }

  return {
    segments,
    passes: divisions,
    allowance: {
      xDiameter: allowanceX,
      z: allowanceZ
    },
    totalDiameterRelief,
    totalAxialRelief,
    alarm: undefined
  };
}

function expandG70(
  lines,
  index,
  words,
  variables,
  state,
  onUndefined,
  segmentBudget = MAX_SYNTHETIC_CYCLE_SEGMENTS
) {
  const p = last(words.get("P"));
  const q = last(words.get("Q"));
  if (!Number.isFinite(p) || !Number.isFinite(q)) {
    return {
      segments: [],
      alarm: "G70 requires valid P and Q sequence numbers."
    };
  }
  const firstIndex = findSequenceIndex(lines, p);
  const lastIndex = findSequenceIndex(lines, q);
  if (firstIndex < 0 || lastIndex < firstIndex) {
    return {
      segments: [],
      alarm: `G70 profile N${p}-N${q} was not found.`
    };
  }
  const resourceAlarm = cycleResourceLimit(
    70,
    lastIndex - firstIndex + 1,
    segmentBudget
  );
  if (resourceAlarm) {
    return { segments: [], resourceLimit: true, alarm: resourceAlarm };
  }

  const segments = [];
  let x = state.x;
  let z = state.z;
  let motion = state.motion;
  let feed = state.feed;
  let distanceMode = state.distanceMode;

  for (let profileIndex = firstIndex; profileIndex <= lastIndex; profileIndex += 1) {
    const resolved = resolveCode(lines[profileIndex], variables, onUndefined);
    const profileWords = readWords(resolved);
    const gCodes = profileWords.get("G") || [];
    const explicitMotion = gCodes.find((value) => value >= 0 && value <= 3);
    if (Number.isFinite(explicitMotion)) motion = Math.trunc(explicitMotion);
    if (gCodes.includes(90)) distanceMode = "absolute";
    if (gCodes.includes(91)) distanceMode = "incremental";
    const feedWord = last(profileWords.get("F"));
    if (Number.isFinite(feedWord)) feed = feedWord;

    const xWord = last(profileWords.get("X"));
    const uWord = last(profileWords.get("U"));
    const zWord = last(profileWords.get("Z"));
    const wWord = last(profileWords.get("W"));
    let nextX = x;
    let nextZ = z;
    if (Number.isFinite(xWord)) {
      nextX =
        distanceMode === "incremental" && Number.isFinite(x)
          ? x + xWord
          : xWord + (state.localCoordinateOffset?.x || 0);
    } else if (Number.isFinite(uWord) && Number.isFinite(x)) {
      nextX = x + uWord;
    }
    if (Number.isFinite(zWord)) {
      nextZ =
        distanceMode === "incremental" && Number.isFinite(z)
          ? z + zWord
          : zWord + (state.localCoordinateOffset?.z || 0);
    } else if (Number.isFinite(wWord) && Number.isFinite(z)) {
      nextZ = z + wWord;
    }

    if (
      !Number.isFinite(x) ||
      !Number.isFinite(z) ||
      !Number.isFinite(nextX) ||
      !Number.isFinite(nextZ) ||
      (x === nextX && z === nextZ)
    ) {
      x = nextX;
      z = nextZ;
      continue;
    }

    const start = { x, z };
    const end = { x: nextX, z: nextZ };
    let points = [start, end];
    let kind = motion === 0 ? "rapid" : "g71-finish";
    if ((motion === 2 || motion === 3) && state.plane === 18) {
      points = interpolateArc(start, end, profileWords, motion === 2);
      kind = motion === 2 ? "arc-cw" : "arc-ccw";
    }
    const segmentState = { ...state, motion, feed, distanceMode };
    segments.push({
      ...createMotionSegment(
        profileIndex,
        lines[profileIndex],
        segmentState,
        kind,
        points,
        start,
        end
      ),
      cycle: "G70",
      cyclePhase: "finish-profile",
      synthetic: true
    });
    x = nextX;
    z = nextZ;
  }

  return {
    segments,
    end: { x, z },
    motion,
    feed,
    distanceMode,
    alarm: undefined
  };
}

function expandG74(
  index,
  raw,
  words,
  pending,
  state,
  segmentBudget = MAX_SYNTHETIC_CYCLE_SEGMENTS
) {
  if (
    !pending ||
    !Number.isFinite(pending.start.x) ||
    !Number.isFinite(pending.start.z)
  ) {
    return {
      segments: [],
      passes: 0,
      alarm: "G74 second block has no valid setup block or cycle start position."
    };
  }
  const zWord = last(words.get("Z"));
  const wWord = last(words.get("W"));
  const targetZ = Number.isFinite(zWord)
    ? state.distanceMode === "incremental"
      ? pending.start.z + zWord
      : zWord + (state.localCoordinateOffset?.z || 0)
    : Number.isFinite(wWord)
      ? pending.start.z + wWord
      : undefined;
  const peck = Math.abs(
    fixedCycleValue(addressText(raw, "Q"), state.units) || 0
  );
  const feed = Number.isFinite(last(words.get("F")))
    ? last(words.get("F"))
    : state.feed;
  if (!Number.isFinite(targetZ) || peck <= 0) {
    return {
      segments: [],
      passes: 0,
      alarm: "G74 drilling preview requires a Z/W target and a positive Q peck depth."
    };
  }

  const direction = Math.sign(targetZ - pending.start.z);
  if (!direction) {
    return { segments: [], passes: 0, end: { ...pending.start } };
  }
  const distance = Math.abs(targetZ - pending.start.z);
  const passCount = Math.ceil(distance / peck);
  const resourceAlarm = cycleResourceLimit(
    74,
    passCount * 2 + 1,
    segmentBudget
  );
  if (resourceAlarm) {
    return {
      segments: [],
      passes: 0,
      resourceLimit: true,
      alarm: resourceAlarm
    };
  }
  const retract = Math.max(0, Math.abs(pending.retract || 0));
  const segments = [];
  let current = { ...pending.start };
  const push = (kind, start, end, pass, phase, label) => {
    if (start.x === end.x && start.z === end.z) return;
    const segmentState = {
      ...state,
      motion: kind === "rapid" ? 0 : 1,
      feed
    };
    segments.push({
      ...createMotionSegment(
        index,
        `${raw.trim()} (G74 ${label} ${pass}/${passCount})`,
        segmentState,
        kind,
        [start, end],
        start,
        end
      ),
      cycle: "G74",
      cyclePass: pass,
      cyclePassCount: passCount,
      cyclePhase: phase,
      synthetic: true
    });
  };

  for (let pass = 1; pass <= passCount; pass += 1) {
    const depth = Math.min(distance, pass * peck);
    const cutEnd = {
      x: pending.start.x,
      z: pending.start.z + direction * depth
    };
    push("feed", current, cutEnd, pass, "drill-peck", "PECK");
    current = cutEnd;
    if (pass < passCount && retract > 0) {
      const retractEnd = {
        x: current.x,
        z: current.z - direction * Math.min(retract, depth)
      };
      push("rapid", current, retractEnd, pass, "drill-retract", "RETRACT");
      current = retractEnd;
    }
  }
  push(
    "rapid",
    current,
    pending.start,
    passCount,
    "drill-return",
    "RETURN"
  );
  return {
    segments,
    passes: passCount,
    end: { ...pending.start },
    feed,
    alarm: undefined
  };
}

function expandG75(
  index,
  raw,
  words,
  pending,
  state,
  segmentBudget = MAX_SYNTHETIC_CYCLE_SEGMENTS
) {
  if (
    !pending ||
    !Number.isFinite(pending.start.x) ||
    !Number.isFinite(pending.start.z)
  ) {
    return {
      segments: [],
      grooves: 0,
      pecks: 0,
      alarm: "G75 second block has no valid setup block or cycle start position."
    };
  }
  const xWord = last(words.get("X"));
  const uWord = last(words.get("U"));
  const zWord = last(words.get("Z"));
  const wWord = last(words.get("W"));
  const targetX = Number.isFinite(xWord)
    ? state.distanceMode === "incremental"
      ? pending.start.x + xWord
      : xWord + (state.localCoordinateOffset?.x || 0)
    : Number.isFinite(uWord)
      ? pending.start.x + uWord
      : undefined;
  const targetZ = Number.isFinite(zWord)
    ? state.distanceMode === "incremental"
      ? pending.start.z + zWord
      : zWord + (state.localCoordinateOffset?.z || 0)
    : Number.isFinite(wWord)
      ? pending.start.z + wWord
      : pending.start.z;
  const radialPeck = Math.abs(
    fixedCycleValue(addressText(raw, "P"), state.units) || 0
  );
  const axialStep = Math.abs(
    fixedCycleValue(addressText(raw, "Q"), state.units) || 0
  );
  const bottomRelief = Math.abs(last(words.get("R")) || 0);
  const feed = Number.isFinite(last(words.get("F")))
    ? last(words.get("F"))
    : state.feed;
  if (!Number.isFinite(targetX) || radialPeck <= 0) {
    return {
      segments: [],
      grooves: 0,
      pecks: 0,
      alarm: "G75 grooving preview requires an X/U target and a positive P radial peck."
    };
  }
  const axialDistance = Math.abs(targetZ - pending.start.z);
  if (axialDistance > 1e-7 && axialStep <= 0) {
    return {
      segments: [],
      grooves: 0,
      pecks: 0,
      alarm: "G75 grooving preview requires a positive Q axial step when Z changes."
    };
  }

  const axialDirection = Math.sign(targetZ - pending.start.z);
  const grooveCountEstimate =
    axialDistance > 1e-7
      ? Math.max(
          2,
          Math.ceil((axialDistance - 1e-7) / axialStep) + 1
        )
      : 1;
  const radialDistance = Math.abs(targetX - pending.start.x);
  const diameterPeck = radialPeck * 2;
  const pecksPerGroove = Math.ceil(radialDistance / diameterPeck);
  const resourceAlarm = cycleResourceLimit(
    75,
    grooveCountEstimate * (pecksPerGroove * 2 + 3) + 1,
    segmentBudget
  );
  if (resourceAlarm) {
    return {
      segments: [],
      grooves: 0,
      pecks: 0,
      resourceLimit: true,
      alarm: resourceAlarm
    };
  }
  const groovePositions = [pending.start.z];
  if (axialDistance > 1e-7) {
    let travelled = axialStep;
    while (travelled < axialDistance - 1e-7) {
      groovePositions.push(pending.start.z + axialDirection * travelled);
      travelled += axialStep;
    }
    groovePositions.push(targetZ);
  }
  const radialDirection = Math.sign(targetX - pending.start.x);
  const totalPecks = groovePositions.length * pecksPerGroove;
  const segments = [];
  let current = { ...pending.start };
  let globalPass = 0;
  const push = (kind, start, end, groove, phase, label) => {
    if (start.x === end.x && start.z === end.z) return;
    const segmentState = {
      ...state,
      motion: kind === "rapid" ? 0 : 1,
      feed
    };
    segments.push({
      ...createMotionSegment(
        index,
        `${raw.trim()} (G75 ${label} G${groove}/${groovePositions.length})`,
        segmentState,
        kind,
        [start, end],
        start,
        end
      ),
      cycle: "G75",
      cyclePass: globalPass,
      cyclePassCount: totalPecks,
      cycleGroove: groove,
      cycleGrooveCount: groovePositions.length,
      cyclePhase: phase,
      synthetic: true
    });
  };

  groovePositions.forEach((position, grooveIndex) => {
    const groove = grooveIndex + 1;
    const grooveStart = { x: pending.start.x, z: position };
    push("rapid", current, grooveStart, groove, "groove-position", "POSITION");
    current = grooveStart;
    let removedDiameter = 0;
    for (let peckIndex = 0; peckIndex < pecksPerGroove; peckIndex += 1) {
      globalPass += 1;
      removedDiameter = Math.min(radialDistance, removedDiameter + diameterPeck);
      const cutEnd = {
        x: pending.start.x + radialDirection * removedDiameter,
        z: position
      };
      push("g75", current, cutEnd, groove, "groove-peck", "PECK");
      current = cutEnd;
      if (peckIndex < pecksPerGroove - 1 && pending.retract > 0) {
        const retractDiameter = Math.min(
          radialDistance,
          Math.max(0, removedDiameter - pending.retract * 2)
        );
        const retractEnd = {
          x: pending.start.x + radialDirection * retractDiameter,
          z: position
        };
        push("rapid", current, retractEnd, groove, "groove-retract", "RETRACT");
        current = retractEnd;
      }
    }
    if (bottomRelief > 0 && axialDirection) {
      const reliefEnd = {
        x: current.x,
        z: current.z + axialDirection * bottomRelief
      };
      push("g75", current, reliefEnd, groove, "groove-bottom-relief", "BOTTOM RELIEF");
      current = reliefEnd;
    }
    const retractEnd = { x: pending.start.x, z: current.z };
    push("rapid", current, retractEnd, groove, "groove-return", "RETURN X");
    current = retractEnd;
  });
  push(
    "rapid",
    current,
    pending.start,
    groovePositions.length,
    "cycle-return",
    "CYCLE RETURN"
  );
  return {
    segments,
    grooves: groovePositions.length,
    pecks: totalPecks,
    end: { ...pending.start },
    feed,
    radialPeck,
    axialStep,
    bottomRelief,
    alarm: undefined
  };
}

function parseG76Setup(raw, words, state, machineParameters) {
  const packed = addressText(raw, "P");
  let finishPasses = Math.max(
    0,
    Math.trunc(fanucValue(machineParameters, 5142, 0))
  );
  let chamferTenths = Math.max(0, fanucValue(machineParameters, 5130, 0));
  let toolAngle = Math.max(0, fanucValue(machineParameters, 5143, 0));

  if (packed !== undefined && !String(packed).includes(".")) {
    const digits = String(Math.abs(Math.trunc(Number(packed))))
      .padStart(6, "0")
      .slice(-6);
    finishPasses = Number(digits.slice(0, 2));
    chamferTenths = Number(digits.slice(2, 4));
    toolAngle = Number(digits.slice(4, 6));
  }

  const minimumDepth =
    fixedCycleValue(addressText(raw, "Q"), state.units) ??
    fanucValue(machineParameters, 5140, 0);
  const finishAllowance = Number.isFinite(last(words.get("R")))
    ? Math.abs(last(words.get("R")))
    : Math.abs(fanucValue(machineParameters, 5141, 0));
  return {
    finishPasses,
    chamferTenths,
    toolAngle,
    minimumDepth: Math.abs(minimumDepth),
    finishAllowance,
    start: { x: state.x, z: state.z }
  };
}

function g76RoughDepths(targetDepth, firstDepth, minimumDepth) {
  const depths = [];
  let current = 0;
  let pass = 1;
  while (current < targetDepth - 1e-9 && pass <= 500) {
    // Cumulative depth follows Qfirst * sqrt(pass), so the radial increment
    // becomes smaller on every successive pass. Qmin only clamps an increment
    // after the calculated value actually falls below the programmed minimum.
    let next = firstDepth * Math.sqrt(pass);
    if (next - current < minimumDepth) {
      next = current + minimumDepth;
    }
    if (
      targetDepth - next < minimumDepth &&
      targetDepth - current > minimumDepth
    ) {
      next = targetDepth;
    }
    next = Math.min(next, targetDepth);
    if (next <= current + 1e-9) {
      break;
    }
    depths.push(next);
    current = next;
    pass += 1;
  }
  return depths;
}

function expandG76(
  index,
  raw,
  words,
  pending,
  state,
  machineParameters,
  segmentBudget = MAX_SYNTHETIC_CYCLE_SEGMENTS
) {
  if (!pending || !Number.isFinite(state.x) || !Number.isFinite(state.z)) {
    return {
      segments: [],
      passes: 0,
      alarm: "G76 second block has no valid first setup block or cycle start position."
    };
  }

  const xWord = last(words.get("X"));
  const uWord = last(words.get("U"));
  const zWord = last(words.get("Z"));
  const wWord = last(words.get("W"));
  const finalX = Number.isFinite(xWord)
    ? xWord + (state.localCoordinateOffset?.x || 0)
    : Number.isFinite(uWord)
      ? state.x + uWord
      : undefined;
  const endZ = Number.isFinite(zWord)
    ? zWord + (state.localCoordinateOffset?.z || 0)
    : Number.isFinite(wWord)
      ? state.z + wWord
      : undefined;
  const threadHeight = Math.abs(
    fixedCycleValue(addressText(raw, "P"), state.units)
  );
  const firstDepth = Math.abs(
    fixedCycleValue(addressText(raw, "Q"), state.units)
  );
  const pitch = Math.abs(last(words.get("F")));
  const taper = Number.isFinite(last(words.get("R")))
    ? last(words.get("R"))
    : 0;

  if (
    !Number.isFinite(finalX) ||
    !Number.isFinite(endZ) ||
    !Number.isFinite(threadHeight) ||
    threadHeight <= 0 ||
    !Number.isFinite(firstDepth) ||
    firstDepth <= 0 ||
    !Number.isFinite(pitch) ||
    pitch <= 0
  ) {
    return {
      segments: [],
      passes: 0,
      alarm: "G76 requires valid X/Z, P thread height, Q first depth, and F lead values."
    };
  }

  const finishAllowance = Math.min(
    threadHeight,
    Math.max(0, pending.finishAllowance)
  );
  const roughTarget = Math.max(0, threadHeight - finishAllowance);
  const minimumDepth = Math.max(0, pending.minimumDepth);
  const roughDepths = g76RoughDepths(
    roughTarget,
    firstDepth,
    minimumDepth
  );
  const finishPasses = Math.max(0, Math.trunc(pending.finishPasses));
  const estimatedPasses = roughDepths.length + finishPasses || 1;
  const resourceAlarm = cycleResourceLimit(
    76,
    estimatedPasses * 4,
    segmentBudget
  );
  if (resourceAlarm) {
    return {
      segments: [],
      passes: 0,
      roughPasses: 0,
      finishPasses: 0,
      resourceLimit: true,
      alarm: resourceAlarm
    };
  }
  const passes = [
    ...roughDepths.map((depth, passIndex) => ({
      depth,
      increment: depth - (roughDepths[passIndex - 1] || 0),
      finish: false
    })),
    ...Array.from({ length: finishPasses }, (_, finishIndex) => ({
      depth: threadHeight,
      increment: finishIndex === 0 ? finishAllowance : 0,
      finish: true
    }))
  ];
  if (!passes.length) {
    passes.push({ depth: threadHeight, increment: threadHeight, finish: true });
  }

  const radialDirection = finalX < state.x ? -1 : 1;
  const threadDirection = Math.sign(endZ - state.z) || -1;
  const finalStartX = finalX + taper * 2;
  const crestStartX = finalStartX - radialDirection * threadHeight * 2;
  const crestEndX = finalX - radialDirection * threadHeight * 2;
  const chamferLength = Math.min(
    Math.abs(endZ - state.z),
    Math.max(0, pending.chamferTenths) * pitch * 0.1
  );
  const halfAngle = Math.min(89, Math.max(0, pending.toolAngle / 2));
  const segments = [];
  let current = { x: state.x, z: state.z };

  const push = (kind, start, end, passIndex, depth, phase, label) => {
    if (
      !Number.isFinite(start.x) ||
      !Number.isFinite(start.z) ||
      !Number.isFinite(end.x) ||
      !Number.isFinite(end.z) ||
      (Math.abs(start.x - end.x) < 1e-9 &&
        Math.abs(start.z - end.z) < 1e-9)
    ) {
      return;
    }
    segments.push({
      line: index + 1,
      raw: `${raw.trim()} (G76 ${label})`,
      tool: state.tool,
      kind,
      motion: kind === "rapid" ? 0 : 1,
      feed: pitch,
      feedMode: "per-revolution",
      compensation: 40,
      spindleMode: state.spindleMode,
      spindleSpeed: state.spindleSpeed,
      spindleLimit: state.spindleLimit,
      units: state.units,
      points: [start, end],
      start,
      end,
      toolRadius: state.toolDefinition?.cornerRadius || 0,
      toolTip: state.toolDefinition?.tip || 0,
      toolType: "threading",
      nonCutting: false,
      cycle: "G76",
      cyclePass: passIndex,
      cyclePassCount: passes.length,
      cyclePhase: phase,
      threadDepth: depth,
      threadIncrement: passes[passIndex - 1].increment,
      threadHeight,
      threadPitch: pitch,
      threadAngle: pending.toolAngle,
      threadFinishPass: passes[passIndex - 1].finish,
      synthetic: true
    });
  };

  passes.forEach((passDefinition, passOffset) => {
    const passIndex = passOffset + 1;
    const depth = passDefinition.depth;
    const compoundShift =
      halfAngle > 0
        ? depth * Math.tan((halfAngle * Math.PI) / 180)
        : 0;
    const cutStartZ = state.z + threadDirection * compoundShift;
    const cutStartX = crestStartX + radialDirection * depth * 2;
    const cutEndX = crestEndX + radialDirection * depth * 2;
    const cutStart = { x: cutStartX, z: cutStartZ };
    push(
      "rapid",
      current,
      cutStart,
      passIndex,
      depth,
      "thread-infeed",
      `INFEED ${passIndex}/${passes.length}`
    );

    if (chamferLength > 1e-9) {
      const chamferStartZ = endZ - threadDirection * chamferLength;
      const fraction = Math.max(
        0,
        Math.min(1, (chamferStartZ - cutStartZ) / (endZ - cutStartZ))
      );
      const chamferStart = {
        x: cutStartX + (cutEndX - cutStartX) * fraction,
        z: chamferStartZ
      };
      push(
        "thread",
        cutStart,
        chamferStart,
        passIndex,
        depth,
        "thread-cut",
        `${passDefinition.finish ? "FINISH" : "CUT"} ${passIndex}/${passes.length}`
      );
      const pullout = { x: state.x, z: endZ };
      push(
        "thread-chamfer",
        chamferStart,
        pullout,
        passIndex,
        depth,
        "thread-chamfer",
        `CHAMFER ${passIndex}/${passes.length}`
      );
      current = pullout;
    } else {
      const cutEnd = { x: cutEndX, z: endZ };
      push(
        "thread",
        cutStart,
        cutEnd,
        passIndex,
        depth,
        "thread-cut",
        `${passDefinition.finish ? "FINISH" : "CUT"} ${passIndex}/${passes.length}`
      );
      const pullout = { x: state.x, z: endZ };
      push(
        "rapid",
        cutEnd,
        pullout,
        passIndex,
        depth,
        "thread-pullout",
        `PULLOUT ${passIndex}/${passes.length}`
      );
      current = pullout;
    }

    const returnPoint = { x: state.x, z: state.z };
    push(
      "rapid",
      current,
      returnPoint,
      passIndex,
      depth,
      "thread-return",
      `RETURN ${passIndex}/${passes.length}`
    );
    current = returnPoint;
  });

  return {
    segments,
    passes: passes.length,
    roughPasses: roughDepths.length,
    finishPasses,
    behavior: {
      finalX,
      endZ,
      taper,
      threadHeight,
      firstDepth,
      minimumDepth,
      finishAllowance,
      finishPasses,
      chamferTenths: pending.chamferTenths,
      chamferLength,
      toolAngle: pending.toolAngle,
      pitch,
      radialDirection: radialDirection < 0 ? "external" : "internal"
    }
  };
}

function compensatedCopy(segment, radius) {
  if (
    !Number.isFinite(radius) ||
    radius <= 0 ||
    (segment.compensation !== 41 && segment.compensation !== 42) ||
    segment.points.length < 2
  ) {
    return undefined;
  }

  const side = segment.compensation === 41 ? 1 : -1;
  const points = segment.points.map((point, index) => {
    const previous = segment.points[Math.max(0, index - 1)];
    const next = segment.points[Math.min(segment.points.length - 1, index + 1)];
    const dz = next.z - previous.z;
    const dr = next.x / 2 - previous.x / 2;
    const length = Math.hypot(dz, dr);
    if (length < 1e-9) {
      return point;
    }
    const normalZ = (-dr / length) * side;
    const normalR = (dz / length) * side;
    return {
      z: point.z + normalZ * radius,
      x: (point.x / 2 + normalR * radius) * 2
    };
  });

  return {
    ...segment,
    kind: "compensated",
    points,
    start: points[0],
    end: points[points.length - 1],
    sourceKind: segment.kind,
    toolRadius: radius,
    synthetic: true
  };
}

function physicalPoint(point) {
  return { z: point.z, r: point.x / 2 };
}

function programmedPoint(point) {
  return { z: point.z, x: point.r * 2 };
}

function pointDistance(first, second) {
  return Math.hypot(first.z - second.z, first.r - second.r);
}

function endpointTangent(segment, atEnd) {
  const points = segment.points.map(physicalPoint);
  if (atEnd) {
    for (let index = points.length - 1; index > 0; index -= 1) {
      const dz = points[index].z - points[index - 1].z;
      const dr = points[index].r - points[index - 1].r;
      const length = Math.hypot(dz, dr);
      if (length > 1e-9) {
        return { z: dz / length, r: dr / length };
      }
    }
  } else {
    for (let index = 1; index < points.length; index += 1) {
      const dz = points[index].z - points[index - 1].z;
      const dr = points[index].r - points[index - 1].r;
      const length = Math.hypot(dz, dr);
      if (length > 1e-9) {
        return { z: dz / length, r: dr / length };
      }
    }
  }
  return undefined;
}

function lineIntersection(first, firstDirection, second, secondDirection) {
  const denominator =
    firstDirection.z * secondDirection.r -
    firstDirection.r * secondDirection.z;
  if (Math.abs(denominator) < 1e-8) {
    return undefined;
  }
  const dz = second.z - first.z;
  const dr = second.r - first.r;
  const distance =
    (dz * secondDirection.r - dr * secondDirection.z) / denominator;
  return {
    z: first.z + firstDirection.z * distance,
    r: first.r + firstDirection.r * distance
  };
}

function replaceCompensatedEnd(segment, point) {
  const programmed = programmedPoint(point);
  segment.points[segment.points.length - 1] = programmed;
  segment.end = programmed;
}

function replaceCompensatedStart(segment, point) {
  const programmed = programmedPoint(point);
  segment.points[0] = programmed;
  segment.start = programmed;
}

function compensationJoin(
  previousSource,
  previousCompensated,
  source,
  compensated,
  radius
) {
  const previousTangent = endpointTangent(previousSource, true);
  const nextTangent = endpointTangent(source, false);
  if (!previousTangent || !nextTangent) {
    return undefined;
  }

  const previousEnd = physicalPoint(previousCompensated.end);
  const nextStart = physicalPoint(compensated.start);
  if (pointDistance(previousEnd, nextStart) < 1e-7) {
    const shared = {
      z: (previousEnd.z + nextStart.z) / 2,
      r: (previousEnd.r + nextStart.r) / 2
    };
    replaceCompensatedEnd(previousCompensated, shared);
    replaceCompensatedStart(compensated, shared);
    return undefined;
  }

  const cross =
    previousTangent.z * nextTangent.r -
    previousTangent.r * nextTangent.z;
  const dot =
    previousTangent.z * nextTangent.z +
    previousTangent.r * nextTangent.r;
  const side = source.compensation === 41 ? 1 : -1;
  const corner = physicalPoint(previousSource.end);

  if (Math.abs(cross) < 1e-7 && dot > 0) {
    const shared = {
      z: (previousEnd.z + nextStart.z) / 2,
      r: (previousEnd.r + nextStart.r) / 2
    };
    replaceCompensatedEnd(previousCompensated, shared);
    replaceCompensatedStart(compensated, shared);
    return undefined;
  }

  // A turn toward the selected compensation side makes the two offset paths
  // overlap. Trim or extend them to their common miter intersection.
  if (cross * side > 1e-7) {
    const intersection = lineIntersection(
      previousEnd,
      previousTangent,
      nextStart,
      nextTangent
    );
    const maximumMiter = Math.max(radius * 25, 0.01);
    if (
      intersection &&
      pointDistance(previousEnd, intersection) <= maximumMiter &&
      pointDistance(nextStart, intersection) <= maximumMiter
    ) {
      replaceCompensatedEnd(previousCompensated, intersection);
      replaceCompensatedStart(compensated, intersection);
      return undefined;
    }
  }

  // A turn away from the compensation side opens a gap. Keep the insert nose
  // tangent to the programmed corner with a circular join of the nose radius.
  const turnAngle = Math.atan2(cross, dot);
  if (cross * side < -1e-7 && Math.abs(turnAngle) > 1e-7) {
    const startAngle = Math.atan2(
      previousEnd.r - corner.r,
      previousEnd.z - corner.z
    );
    const steps = Math.max(
      4,
      Math.ceil(Math.abs(turnAngle) / (Math.PI / 24))
    );
    const points = [];
    for (let index = 0; index <= steps; index += 1) {
      const angle = startAngle + turnAngle * (index / steps);
      points.push(
        programmedPoint({
          z: corner.z + Math.cos(angle) * radius,
          r: corner.r + Math.sin(angle) * radius
        })
      );
    }
    points[0] = previousCompensated.end;
    points[points.length - 1] = compensated.start;
    return {
      ...compensated,
      line: source.line,
      raw: `G${source.compensation} CORNER ARC T${String(source.tool).padStart(2, "0")}`,
      kind: "compensated",
      points,
      start: points[0],
      end: points[points.length - 1],
      sourceKind: "corner-join",
      compensationJoin: "arc",
      synthetic: true
    };
  }

  // Degenerate or near-reversal fallback: connect the two finite offsets so
  // the overlay never leaves an unexplained gap.
  return {
    ...compensated,
    line: source.line,
    raw: `G${source.compensation} CORNER BRIDGE T${String(source.tool).padStart(2, "0")}`,
    kind: "compensated",
    points: [previousCompensated.end, compensated.start],
    start: previousCompensated.end,
    end: compensated.start,
    sourceKind: "corner-join",
    compensationJoin: "bridge",
    synthetic: true
  };
}

function imaginaryToolCenter(point, radius, tip) {
  const physical = physicalPoint(point);
  const directions = {
    1: { z: 1, r: 1 },
    2: { z: -1, r: 1 },
    3: { z: -1, r: -1 },
    4: { z: 1, r: -1 },
    5: { z: 1, r: 0 },
    6: { z: 0, r: 1 },
    7: { z: -1, r: 0 },
    8: { z: 0, r: -1 }
  };
  const direction = directions[tip];
  if (!direction) {
    return { ...point };
  }
  return programmedPoint({
    z: physical.z - direction.z * radius,
    r: physical.r - direction.r * radius
  });
}

function compensationTransition(source, start, end, phase, radius) {
  return {
    ...source,
    raw: `${phase === "startup" ? `G${source.compensation}` : "G40"} ${phase.toUpperCase()} T${String(source.tool).padStart(2, "0")}`,
    kind: "compensated",
    points: [start, end],
    start,
    end,
    sourceKind: `offset-${phase}`,
    compensationPhase: phase,
    toolRadius: radius,
    synthetic: true
  };
}

function explicitCompensationCode(segment) {
  const codes = readWords(stripComments(segment.raw)).get("G") || [];
  if (codes.includes(40)) return 40;
  if (codes.includes(41)) return 41;
  if (codes.includes(42)) return 42;
  return undefined;
}

function compensationSourcesConnect(previous, next, previousRadius, nextRadius) {
  return Boolean(
    previous &&
    next &&
    previous.tool === next.tool &&
    previous.compensation === next.compensation &&
    Math.abs(previousRadius - nextRadius) < 1e-9 &&
    pointDistance(physicalPoint(previous.end), physicalPoint(next.start)) < 1e-7
  );
}

function compensationBlockLabel(segment) {
  const match = stripComments(segment.raw).match(/\bN(\d+)\b/i);
  return match ? `N${match[1]}` : `row ${segment.line}`;
}

function detectCompensationInterference(source, compensated, radius) {
  const sourceStart = physicalPoint(source.start);
  const sourceEnd = physicalPoint(source.end);
  const compensatedStart = physicalPoint(compensated.start);
  const compensatedEnd = physicalPoint(compensated.end);
  const sourceDz = sourceEnd.z - sourceStart.z;
  const sourceDr = sourceEnd.r - sourceStart.r;
  const sourceLength = Math.hypot(sourceDz, sourceDr);
  if (sourceLength < 1e-9) {
    return undefined;
  }

  const compensatedDz = compensatedEnd.z - compensatedStart.z;
  const compensatedDr = compensatedEnd.r - compensatedStart.r;
  const forwardTravel =
    (compensatedDz * sourceDz + compensatedDr * sourceDr) / sourceLength;
  const tolerance = 1e-7;
  if (forwardTravel > tolerance) {
    return undefined;
  }

  const block = compensationBlockLabel(source);
  const state = forwardTravel < -tolerance ? "reverses" : "collapses";
  const detail =
    `offset X${compensated.start.x.toFixed(3)} Z${compensated.start.z.toFixed(3)}` +
    ` to X${compensated.end.x.toFixed(3)} Z${compensated.end.z.toFixed(3)}`;
  const issue = {
    severity: "error",
    code: forwardTravel < -tolerance
      ? "TNR_COMPENSATION_BACKTRACK"
      : "TNR_COMPENSATION_COLLAPSE",
    line: source.line,
    block,
    tool: source.tool,
    compensation: source.compensation,
    radius,
    programmedLength: sourceLength,
    forwardTravel,
    message:
      `TNR compensation interference at row ${source.line} (${block}, ` +
      `T${String(source.tool).padStart(2, "0")}): G${source.compensation} ${state} ` +
      `the tool-center path (${detail}).`
  };
  compensated.compensationInterference = issue;
  return issue;
}

function buildCompensatedSegments(segments, toolRadii, reportIssue = () => {}) {
  const compensatedSegments = [];
  let index = 0;

  while (index < segments.length) {
    const source = segments[index];
    const radius = toolRadii.get(source.tool);
    if (!compensatedCopy(source, radius)) {
      index += 1;
      continue;
    }

    const run = [source];
    let runEnd = index + 1;
    while (runEnd < segments.length) {
      const candidate = segments[runEnd];
      const candidateRadius = toolRadii.get(candidate.tool);
      if (
        !compensatedCopy(candidate, candidateRadius) ||
        !compensationSourcesConnect(
          run[run.length - 1],
          candidate,
          radius,
          candidateRadius
        )
      ) {
        break;
      }
      run.push(candidate);
      runEnd += 1;
    }

    const startupSource = run[0];
    const firstCutSource = run[1];
    const startupTarget = firstCutSource
      ? compensatedCopy(firstCutSource, radius).start
      : compensatedCopy(startupSource, radius).end;
    const startupStart = imaginaryToolCenter(
      startupSource.start,
      radius,
      startupSource.toolTip
    );
    const startup = compensationTransition(
      startupSource,
      startupStart,
      startupTarget,
      "startup",
      radius
    );
    compensatedSegments.push(startup);

    let previousSource;
    let previousCompensated;
    const compensatedPairs = [];
    for (let runIndex = 1; runIndex < run.length; runIndex += 1) {
      const cutSource = run[runIndex];
      const compensated = compensatedCopy(cutSource, radius);
      if (runIndex === 1) {
        replaceCompensatedStart(compensated, physicalPoint(startupTarget));
      }
      if (previousSource && previousCompensated) {
        const join = compensationJoin(
          previousSource,
          previousCompensated,
          cutSource,
          compensated,
          radius
        );
        if (join) {
          compensatedSegments.push(join);
        }
      }

      compensatedSegments.push(compensated);
      compensatedPairs.push({ source: cutSource, compensated });
      previousSource = cutSource;
      previousCompensated = compensated;
    }

    const lastCompensated = previousCompensated || startup;
    const cancelSource = segments[runEnd];
    const cancelRadius = cancelSource
      ? toolRadii.get(cancelSource.tool)
      : undefined;
    const hasCancelMove = Boolean(
      cancelSource &&
      explicitCompensationCode(cancelSource) === 40 &&
      cancelSource.tool === startupSource.tool &&
      Math.abs(cancelRadius - radius) < 1e-9 &&
      pointDistance(
        physicalPoint(run[run.length - 1].end),
        physicalPoint(cancelSource.start)
      ) < 1e-7
    );
    let cancelStart = lastCompensated.end;
    let cancelJoin;
    if (hasCancelMove && previousSource && previousCompensated) {
      // G40 removes the offset at the end of its move. Its travel direction
      // still defines the final active-compensation corner, so construct that
      // tangent offset long enough to join it to the preceding tool-center
      // path before transitioning to the uncompensated endpoint.
      const activeCancelSource = {
        ...cancelSource,
        compensation: startupSource.compensation
      };
      const activeCancelMove = compensatedCopy(activeCancelSource, radius);
      if (activeCancelMove) {
        cancelJoin = compensationJoin(
          previousSource,
          previousCompensated,
          activeCancelSource,
          activeCancelMove,
          radius
        );
        cancelStart = activeCancelMove.start;
      }
    }

    for (const pair of compensatedPairs) {
      const issue = detectCompensationInterference(
        pair.source,
        pair.compensated,
        radius
      );
      if (issue) {
        reportIssue(issue);
      }
    }

    if (hasCancelMove) {
      if (cancelJoin) {
        compensatedSegments.push(cancelJoin);
      }
      const cancelEnd = imaginaryToolCenter(
        cancelSource.end,
        radius,
        cancelSource.toolTip
      );
      compensatedSegments.push(
        compensationTransition(
          cancelSource,
          cancelStart,
          cancelEnd,
          "cancel",
          radius
        )
      );
      index = runEnd + 1;
    } else {
      index = runEnd;
    }
  }
  return compensatedSegments;
}

function createMotionSegment(index, raw, state, kind, points, start, end) {
  return {
    line: index + 1,
    raw: raw.trim(),
    tool: state.tool,
    kind,
    motion: state.motion,
    feed: state.feed,
    feedMode: state.feedMode,
    compensation: state.compensation,
    spindleMode: state.spindleMode,
    spindleSpeed: state.spindleSpeed,
    spindleLimit: state.spindleLimit,
    units: state.units,
    points,
    start,
    end,
    toolRadius: state.toolDefinition?.cornerRadius || 0,
    toolTip: state.toolDefinition?.tip || 0,
    toolType: state.toolDefinition?.type || "other",
    nonCutting: isNonCuttingTool(state.toolDefinition)
  };
}

function physicalDistance(points) {
  let distance = 0;
  for (let index = 1; index < points.length; index += 1) {
    const previous = points[index - 1];
    const point = points[index];
    distance += Math.hypot(point.z - previous.z, (point.x - previous.x) / 2);
  }
  return distance;
}

function estimatedSegmentSeconds(segment, rapidRate) {
  const distance = physicalDistance(segment.points);
  if (!Number.isFinite(distance) || distance <= 0) {
    return 0;
  }
  if (segment.kind === "rapid" || segment.kind === "g30") {
    return Number.isFinite(rapidRate) && rapidRate > 0
      ? (distance / rapidRate) * 60
      : undefined;
  }
  if (!Number.isFinite(segment.feed) || segment.feed <= 0) {
    return undefined;
  }
  if (segment.feedMode === "per-minute") {
    return (distance / segment.feed) * 60;
  }

  let seconds = 0;
  for (let index = 1; index < segment.points.length; index += 1) {
    const previous = segment.points[index - 1];
    const point = segment.points[index];
    const sectionDistance = Math.hypot(
      point.z - previous.z,
      (point.x - previous.x) / 2
    );
    let rpm;
    if (segment.spindleMode === "constant-rpm") {
      rpm = segment.spindleSpeed;
    } else if (segment.spindleMode === "css") {
      const diameter = Math.max(
        0.1,
        (Math.abs(previous.x) + Math.abs(point.x)) / 2
      );
      const calculated =
        segment.units === "inch"
          ? (segment.spindleSpeed * 12) / (Math.PI * diameter)
          : (segment.spindleSpeed * 1000) / (Math.PI * diameter);
      rpm =
        Number.isFinite(segment.spindleLimit) && segment.spindleLimit > 0
          ? Math.min(calculated, segment.spindleLimit)
          : calculated;
    }
    if (!Number.isFinite(rpm) || rpm <= 0) {
      return undefined;
    }
    seconds += (sectionDistance / (segment.feed * rpm)) * 60;
  }
  return seconds;
}

function parseProgram(source, options = {}) {
  const machineParameters = options.machineParameters || {};
  const machineSettings = machineParameters.settings || {};
  const numericSetting = (id, fallback) => {
    const value = Number(machineSettings[id]);
    return Number.isFinite(value) ? value : fallback;
  };
  const settings = {
    g30X: Number.isFinite(options.g30X)
      ? options.g30X
      : numericSetting("reference.g30X", 250),
    g30Z: Number.isFinite(options.g30Z)
      ? options.g30Z
      : numericSetting("reference.g30Z", 100),
    rapidRate: numericSetting("motion.rapidRate", 15000),
    turretIndexSeconds: numericSetting("turret.indexSeconds", 1.2),
    tailstockPresent:
      machineSettings["machine.tailstockPresent"] === undefined
        ? true
        : Boolean(machineSettings["machine.tailstockPresent"]),
    retractOrder:
      machineSettings["safety.retractOrder"] || "X_THEN_Z",
    initialVariables:
      options.initialVariables === undefined
        ? "#500=0,#505=0"
        : options.initialVariables
  };
  const lines = source.split(/\r?\n/);
  const segments = [];
  const tools = new Set();
  const warnings = new Set();
  const cycleAlarms = [];
  const addCycleAlarm = (alarm) => {
    warnings.add(alarm);
    cycleAlarms.push(alarm);
  };
  const variables = initialiseVariables(settings.initialVariables, (message) =>
    warnings.add(message)
  );
  const sequences = sequenceIndexMap(lines);
  const toolDefinitions = resolveToolDefinitions(source, options.toolTable);
  const toolRadii = new Map(
    [...toolDefinitions.entries()].map(([number, definition]) => [
      number,
      definition.cornerRadius
    ])
  );
  let pendingG71;
  let pendingG72;
  let pendingG73;
  let pendingG74;
  let pendingG75;
  let pendingG76;
  let g71PassCount = 0;
  let g71RoughFinishPassCount = 0;
  let g71FinishAllowance;
  let g71Behavior;
  let g72PassCount = 0;
  let g72RoughFinishPassCount = 0;
  let g72FinishAllowance;
  let g72Behavior;
  let g73PassCount = 0;
  let g73FinishAllowance;
  let g73Behavior;
  let g70FinishCount = 0;
  let g74PeckCount = 0;
  let g75GrooveCount = 0;
  let g75PeckCount = 0;
  let g76PassCount = 0;
  let g76RoughPassCount = 0;
  let g76FinishPassCount = 0;
  let g76Behavior;
  let syntheticCycleSegmentCount = 0;
  const remainingSyntheticCycleSegments = () =>
    Math.max(
      0,
      MAX_SYNTHETIC_CYCLE_SEGMENTS - syntheticCycleSegmentCount
    );
  const appendSyntheticCycleSegments = (expanded) => {
    for (const segment of expanded.segments || []) {
      segments.push(segment);
    }
    syntheticCycleSegmentCount += expanded.segments?.length || 0;
  };
  let executedBlockCount = 0;
  let jumpCount = 0;
  let executionSteps = 0;
  const lineVisits = new Map();
  const maximumExecutionSteps = numericSetting(
    "macro.maximumExecutionSteps",
    20000
  );
  const maximumLineVisits = numericSetting(
    "macro.maximumLineVisits",
    100
  );
  let turretIndexCount = 0;
  let g52ChangeCount = 0;

  const undefinedVariable = (id) => {
    warnings.add(`Macro variable #${id} is undefined; preview uses 0.`);
  };

  const jumpTo = (sequence, currentIndex) => {
    const target = sequences.get(sequence);
    if (!Number.isFinite(target)) {
      warnings.add(`GOTO${sequence} target was not found.`);
      return currentIndex + 1;
    }
    jumpCount += 1;
    return target;
  };

  const state = {
    x: undefined,
    z: undefined,
    motion: 0,
    tool: 0,
    toolDefinition: defaultToolDefinition(0),
    feed: undefined,
    units: "mm",
    plane: 18,
    distanceMode: "absolute",
    feedMode: "per-revolution",
    compensation: 40,
    spindleMode: "constant-rpm",
    spindleSpeed: undefined,
    spindleLimit: undefined,
    localCoordinateOffset: { x: 0, z: 0 },
    g30XRetracted: false,
    inG30Departure: false
  };

  let index = 0;
  while (index < lines.length && executionSteps < maximumExecutionSteps) {
    executionSteps += 1;
    const visits = (lineVisits.get(index) || 0) + 1;
    lineVisits.set(index, visits);
    if (visits > maximumLineVisits) {
      warnings.add(
        `Macro execution stopped at row ${index + 1}: loop limit (${maximumLineVisits}) reached.`
      );
      break;
    }

    const raw = lines[index];
    const macroCode = stripComments(raw).trim().toUpperCase();
    if (!macroCode || macroCode === "%") {
      index += 1;
      continue;
    }
    executedBlockCount += 1;

    const assignment = macroCode.match(
      /^(?:N\s*\d+\s*)?#(\d+)\s*=\s*(.+)$/
    );
    if (assignment) {
      const variable = Number(assignment[1]);
      try {
        const value = evaluateExpression(
          assignment[2],
          variables,
          undefinedVariable
        );
        if (variable !== 0) {
          variables.set(variable, value);
        }
      } catch (error) {
        warnings.add(`Row ${index + 1} macro assignment: ${error.message}`);
      }
      index += 1;
      continue;
    }

    const conditional = macroCode.match(
      /^(?:N\s*\d+\s*)?IF\s*(\[.*\])\s*GOTO\s*(\d+)\s*$/
    );
    if (conditional) {
      try {
        const condition = evaluateExpression(
          conditional[1],
          variables,
          undefinedVariable
        );
        index =
          condition !== 0
            ? jumpTo(Number(conditional[2]), index)
            : index + 1;
      } catch (error) {
        warnings.add(`Row ${index + 1} IF: ${error.message}`);
        index += 1;
      }
      continue;
    }

    const goTo = macroCode.match(/\bGOTO\s*(\d+)\b/);
    if (goTo) {
      index = jumpTo(Number(goTo[1]), index);
      continue;
    }

    let resolvedCode;
    try {
      resolvedCode = resolveCode(raw, variables, undefinedVariable);
    } catch (error) {
      warnings.add(`Row ${index + 1} address expression: ${error.message}`);
      index += 1;
      continue;
    }
    const words = readWords(resolvedCode);
    const gCodes = words.get("G") || [];
    const mCodes = words.get("M") || [];

    if (gCodes.includes(20)) state.units = "inch";
    if (gCodes.includes(21)) state.units = "mm";
    if (gCodes.includes(17)) state.plane = 17;
    if (gCodes.includes(18)) state.plane = 18;
    if (gCodes.includes(19)) state.plane = 19;
    if (gCodes.includes(90)) state.distanceMode = "absolute";
    if (gCodes.includes(91)) state.distanceMode = "incremental";
    if (gCodes.includes(94) || gCodes.includes(98)) state.feedMode = "per-minute";
    if (gCodes.includes(95) || gCodes.includes(99)) state.feedMode = "per-revolution";
    if (gCodes.includes(40)) state.compensation = 40;
    if (gCodes.includes(41)) state.compensation = 41;
    if (gCodes.includes(42)) state.compensation = 42;

    const speedWord = last(words.get("S"));
    if (gCodes.includes(50) && Number.isFinite(speedWord)) {
      state.spindleLimit = speedWord;
    }
    if (gCodes.includes(96)) {
      state.spindleMode = "css";
      if (Number.isFinite(speedWord)) state.spindleSpeed = speedWord;
    } else if (gCodes.includes(97)) {
      state.spindleMode = "constant-rpm";
      if (Number.isFinite(speedWord)) state.spindleSpeed = speedWord;
    } else if (Number.isFinite(speedWord) && !gCodes.includes(50)) {
      state.spindleSpeed = speedWord;
    }

    const toolWord = last(words.get("T"));
    if (Number.isFinite(toolWord)) {
      const digits = String(Math.trunc(toolWord)).padStart(4, "0");
      const nextTool = Number(digits.slice(0, -2));
      if (nextTool !== state.tool) {
        turretIndexCount += 1;
      }
      state.tool = nextTool;
      if (!toolDefinitions.has(nextTool)) {
        toolDefinitions.set(nextTool, defaultToolDefinition(nextTool));
        toolRadii.set(nextTool, 0);
      }
      state.toolDefinition = toolDefinitions.get(nextTool);
      state.g30XRetracted = false;
      tools.add(state.tool);
    }

    const feedWord = last(words.get("F"));
    if (Number.isFinite(feedWord)) {
      state.feed = feedWord;
    }

    if (gCodes.includes(52)) {
      const xOffset = last(words.get("X"));
      const zOffset = last(words.get("Z"));
      if (!Number.isFinite(xOffset) && !Number.isFinite(zOffset)) {
        warnings.add(
          `Row ${index + 1}: G52 requires at least one supported X or Z local-origin coordinate.`
        );
      } else {
        if (Number.isFinite(xOffset)) {
          state.localCoordinateOffset.x = xOffset;
        }
        if (Number.isFinite(zOffset)) {
          state.localCoordinateOffset.z = zOffset;
        }
        g52ChangeCount += 1;
        const canceled =
          Math.abs(state.localCoordinateOffset.x) <= 1e-12 &&
          Math.abs(state.localCoordinateOffset.z) <= 1e-12;
        warnings.add(
          canceled
            ? `Row ${index + 1}: G52 local coordinate system canceled with X0 Z0.`
            : `Row ${index + 1}: G52 local origin set to work coordinates X${state.localCoordinateOffset.x} Z${state.localCoordinateOffset.z}.`
        );
      }
      index += 1;
      continue;
    }

    if (gCodes.includes(30)) {
      const retractsX = words.has("U");
      const retractsZ = words.has("W");
      if (
        settings.tailstockPresent &&
        settings.retractOrder === "X_THEN_Z" &&
        retractsZ &&
        !state.g30XRetracted
      ) {
        warnings.add(
          `Row ${index + 1}: tailstock safety profile requires G30 X retraction before Z.`
        );
      }
      const start = { x: state.x, z: state.z };
      const end = {
        x: words.has("U") ? settings.g30X : state.x,
        z: words.has("W") ? settings.g30Z : state.z
      };
      if (
        Number.isFinite(start.x) &&
        Number.isFinite(start.z) &&
        Number.isFinite(end.x) &&
        Number.isFinite(end.z) &&
        (start.x !== end.x || start.z !== end.z)
      ) {
        segments.push(createMotionSegment(index, raw, state, "g30", [start, end], start, end));
      }
      state.x = end.x;
      state.z = end.z;
      if (
        Number.isFinite(state.x) &&
        Number.isFinite(state.z) &&
        state.x === settings.g30X &&
        state.z === settings.g30Z
      ) {
        state.inG30Departure = true;
      }
      if (retractsX) state.g30XRetracted = true;
      index += 1;
      continue;
    }

    if (gCodes.includes(28)) {
      state.x = undefined;
      state.z = undefined;
      state.inG30Departure = false;
      index += 1;
      continue;
    }

    if (gCodes.includes(71)) {
      state.inG30Departure = false;
      const p = last(words.get("P"));
      const q = last(words.get("Q"));
      if (!Number.isFinite(p) || !Number.isFinite(q)) {
        pendingG71 = {
          depth: Number.isFinite(last(words.get("U")))
            ? last(words.get("U"))
            : fanucValue(machineParameters, 5132, 0),
          retract: Number.isFinite(last(words.get("R")))
            ? last(words.get("R"))
            : fanucValue(machineParameters, 5133, 0),
          start: { x: state.x, z: state.z }
        };
      } else {
        const expanded = expandG71(
          lines,
          index,
          words,
          pendingG71,
          variables,
          state,
          undefinedVariable,
          machineParameters,
          remainingSyntheticCycleSegments()
        );
        appendSyntheticCycleSegments(expanded);
        g71PassCount += expanded.passes;
        g71RoughFinishPassCount += expanded.roughFinishPassCount || 0;
        if (expanded.allowance) {
          g71FinishAllowance = expanded.allowance;
        }
        g71Behavior = {
          cycleType: expanded.cycleType,
          radialDirection: expanded.radialDirection,
          fck: expanded.fck,
          rf: expanded.roughFinishSuppressed,
          nt1: expanded.nt1,
          asu: expanded.asu,
          requestedR16: expanded.requestedR16,
          effectiveR16: expanded.effectiveR16,
          dtp: expanded.dtp,
          nsp: expanded.nsp,
          alarm: expanded.alarm
        };
        if (expanded.alarm) {
          addCycleAlarm(expanded.alarm);
        } else {
          const rfBit = expanded.cycleType === "II" ? 2 : 1;
          warnings.add(
            `G71 type ${expanded.cycleType}: RF${expanded.cycleType === "II" ? 2 : 1}=${expanded.roughFinishSuppressed} ${expanded.roughFinishSuppressed ? "suppresses" : "executes"} the final rough-finishing allowance contour.`
          );
          warnings.add(
            `Applied 5107#0 ASU=${expanded.asu}, 5108#0 R16=${expanded.requestedR16}, 5108#${rfBit === 2 ? 3 : 1} ${rfBit === 2 ? `NSP=${expanded.nsp}` : `DTP=${expanded.dtp}`}.`
          );
          if (expanded.compensationCodesIgnored?.length) {
            warnings.add(
              `5106#2 NT1=1: ignored G${expanded.compensationCodesIgnored.join("/G")} in the G71 target profile.`
            );
          }
          if (expanded.profileViolationsIgnored?.length) {
            warnings.add(
              `5104#2 FCK=0: non-monotone ${expanded.profileViolationsIgnored.join("/")} profile change was not blocked.`
            );
          }
        }
        pendingG71 = undefined;
      }
      index += 1;
      continue;
    }

    if (gCodes.includes(72)) {
      state.inG30Departure = false;
      const p = last(words.get("P"));
      const q = last(words.get("Q"));
      if (!Number.isFinite(p) || !Number.isFinite(q)) {
        pendingG72 = {
          depth: Number.isFinite(last(words.get("W")))
            ? Math.abs(last(words.get("W")))
            : fanucValue(machineParameters, 5132, 0),
          retract: Number.isFinite(last(words.get("R")))
            ? Math.abs(last(words.get("R")))
            : fanucValue(machineParameters, 5133, 0),
          start: { x: state.x, z: state.z }
        };
      } else {
        const expanded = expandG72(
          lines,
          index,
          words,
          pendingG72,
          variables,
          state,
          undefinedVariable,
          machineParameters,
          remainingSyntheticCycleSegments()
        );
        appendSyntheticCycleSegments(expanded);
        g72PassCount += expanded.passes || 0;
        g72RoughFinishPassCount += expanded.roughFinishPassCount || 0;
        if (expanded.allowance) g72FinishAllowance = expanded.allowance;
        g72Behavior = {
          cycleType: expanded.cycleType,
          radialDirection: expanded.radialDirection,
          rf: expanded.roughFinishSuppressed,
          asu: expanded.asu,
          alarm: expanded.alarm
        };
        if (expanded.alarm) {
          addCycleAlarm(expanded.alarm);
        } else {
          warnings.add(
            `G72 type ${expanded.cycleType} ${expanded.radialDirection}: expanded ${expanded.passes} facing passes; RF${expanded.cycleType === "II" ? 2 : 1}=${expanded.roughFinishSuppressed} ${expanded.roughFinishSuppressed ? "suppresses" : "executes"} the final rough-finishing allowance contour.`
          );
        }
        pendingG72 = undefined;
      }
      index += 1;
      continue;
    }

    if (gCodes.includes(73)) {
      state.inG30Departure = false;
      const p = last(words.get("P"));
      const q = last(words.get("Q"));
      if (!Number.isFinite(p) || !Number.isFinite(q)) {
        pendingG73 = {
          reliefX: Number.isFinite(last(words.get("U")))
            ? last(words.get("U"))
            : fanucValue(machineParameters, 5135, 0),
          reliefZ: Number.isFinite(last(words.get("W")))
            ? last(words.get("W"))
            : fanucValue(machineParameters, 5136, 0),
          divisions: Number.isFinite(last(words.get("R")))
            ? Math.trunc(Math.abs(last(words.get("R"))))
            : Math.trunc(Math.abs(fanucValue(machineParameters, 5137, 0))),
          start: { x: state.x, z: state.z }
        };
      } else {
        const expanded = expandG73(
          lines,
          index,
          words,
          pendingG73,
          variables,
          state,
          undefinedVariable,
          remainingSyntheticCycleSegments()
        );
        appendSyntheticCycleSegments(expanded);
        g73PassCount += expanded.passes || 0;
        if (expanded.allowance) g73FinishAllowance = expanded.allowance;
        g73Behavior = {
          totalDiameterRelief: expanded.totalDiameterRelief,
          totalAxialRelief: expanded.totalAxialRelief,
          alarm: expanded.alarm
        };
        if (expanded.alarm) {
          addCycleAlarm(expanded.alarm);
        } else {
          warnings.add(
            `G73 pattern repeat: expanded ${expanded.passes} copied profile passes with X-diameter relief ${expanded.totalDiameterRelief} and Z relief ${expanded.totalAxialRelief}.`
          );
        }
        pendingG73 = undefined;
      }
      index += 1;
      continue;
    }

    if (gCodes.includes(70)) {
      const expanded = expandG70(
        lines,
        index,
        words,
        variables,
        state,
        undefinedVariable,
        remainingSyntheticCycleSegments()
      );
      if (expanded.alarm) {
        addCycleAlarm(expanded.alarm);
      } else {
        appendSyntheticCycleSegments(expanded);
        g70FinishCount += expanded.segments.length ? 1 : 0;
        state.x = expanded.end.x;
        state.z = expanded.end.z;
        state.motion = expanded.motion;
        state.feed = expanded.feed;
        state.distanceMode = expanded.distanceMode;
        state.inG30Departure = false;
        state.g30XRetracted = false;
        warnings.add(
          `G70 finish: expanded N${last(words.get("P"))}-N${last(words.get("Q"))} into ${expanded.segments.length} motion segments.`
        );
      }
      index += 1;
      continue;
    }

    if (gCodes.includes(74)) {
      const hasTarget = words.has("Z") || words.has("W");
      if (!hasTarget) {
        pendingG74 = {
          retract: Number.isFinite(last(words.get("R")))
            ? Math.abs(last(words.get("R")))
            : 0,
          start: { x: state.x, z: state.z }
        };
      } else {
        const expanded = expandG74(
          index,
          resolvedCode,
          words,
          pendingG74,
          state,
          remainingSyntheticCycleSegments()
        );
        if (expanded.alarm) {
          addCycleAlarm(expanded.alarm);
        } else {
          appendSyntheticCycleSegments(expanded);
          g74PeckCount += expanded.passes;
          state.x = expanded.end.x;
          state.z = expanded.end.z;
          state.feed = expanded.feed;
          state.inG30Departure = false;
          state.g30XRetracted = false;
          warnings.add(
            `G74 peck drilling: expanded ${expanded.passes} pecks with Q${fixedCycleValue(addressText(resolvedCode, "Q"), state.units)} and R${pendingG74.retract}.`
          );
        }
        pendingG74 = undefined;
      }
      index += 1;
      continue;
    }

    if (gCodes.includes(75)) {
      const hasTarget = words.has("X") || words.has("U");
      if (!hasTarget) {
        pendingG75 = {
          retract: Number.isFinite(last(words.get("R")))
            ? Math.abs(last(words.get("R")))
            : Math.abs(fanucValue(machineParameters, 5139, 0)),
          start: { x: state.x, z: state.z }
        };
      } else {
        const expanded = expandG75(
          index,
          resolvedCode,
          words,
          pendingG75,
          state,
          remainingSyntheticCycleSegments()
        );
        if (expanded.alarm) {
          addCycleAlarm(expanded.alarm);
        } else {
          appendSyntheticCycleSegments(expanded);
          g75GrooveCount += expanded.grooves;
          g75PeckCount += expanded.pecks;
          state.x = expanded.end.x;
          state.z = expanded.end.z;
          state.feed = expanded.feed;
          state.inG30Departure = false;
          state.g30XRetracted = false;
          warnings.add(
            `G75 grooving: expanded ${expanded.grooves} groove positions and ${expanded.pecks} radial pecks (P${expanded.radialPeck}, Q${expanded.axialStep}).`
          );
        }
        pendingG75 = undefined;
      }
      index += 1;
      continue;
    }

    if (gCodes.includes(76)) {
      const hasTarget =
        (words.has("X") || words.has("U")) &&
        (words.has("Z") || words.has("W"));
      if (!hasTarget) {
        pendingG76 = parseG76Setup(
          resolvedCode,
          words,
          state,
          machineParameters
        );
      } else {
        const expanded = expandG76(
          index,
          resolvedCode,
          words,
          pendingG76,
          state,
          machineParameters,
          remainingSyntheticCycleSegments()
        );
        appendSyntheticCycleSegments(expanded);
        g76PassCount += expanded.passes || 0;
        g76RoughPassCount += expanded.roughPasses || 0;
        g76FinishPassCount += expanded.finishPasses || 0;
        g76Behavior = expanded.behavior;
        if (expanded.alarm) {
          addCycleAlarm(expanded.alarm);
        } else {
          warnings.add(
            `G76 ${expanded.behavior.radialDirection} thread: expanded ${expanded.roughPasses} rough and ${expanded.finishPasses} finish passes at lead ${expanded.behavior.pitch}.`
          );
        }
        pendingG76 = undefined;
      }
      index += 1;
      continue;
    }

    const cycleCode = gCodes.find((value) => CYCLE_CODES.has(Math.trunc(value)));
    if (Number.isFinite(cycleCode)) {
      warnings.add(`G${Math.trunc(cycleCode)} canned-cycle motion is not expanded.`);
      index += 1;
      continue;
    }

    const explicitMotion = gCodes.find((value) => value >= 0 && value <= 3);
    if (Number.isFinite(explicitMotion)) {
      state.motion = Math.trunc(explicitMotion);
      if (state.motion !== 0) {
        state.inG30Departure = false;
        state.g30XRetracted = false;
      }
    }

    const xWord = last(words.get("X"));
    const uWord = last(words.get("U"));
    const zWord = last(words.get("Z"));
    const wWord = last(words.get("W"));
    const hasCoordinate =
      Number.isFinite(xWord) ||
      Number.isFinite(uWord) ||
      Number.isFinite(zWord) ||
      Number.isFinite(wWord);
    if (!hasCoordinate) {
      if (mCodes.includes(99) || mCodes.includes(30)) {
        break;
      }
      index += 1;
      continue;
    }

    const start = { x: state.x, z: state.z };
    let nextX = state.x;
    let nextZ = state.z;

    if (Number.isFinite(xWord)) {
      nextX =
        state.distanceMode === "incremental" && Number.isFinite(state.x)
          ? state.x + xWord
          : xWord + state.localCoordinateOffset.x;
    } else if (Number.isFinite(uWord) && Number.isFinite(state.x)) {
      nextX = state.x + uWord;
    }
    if (Number.isFinite(zWord)) {
      nextZ =
        state.distanceMode === "incremental" && Number.isFinite(state.z)
          ? state.z + zWord
          : zWord + state.localCoordinateOffset.z;
    } else if (Number.isFinite(wWord) && Number.isFinite(state.z)) {
      nextZ = state.z + wWord;
    }

    state.x = nextX;
    state.z = nextZ;
    if (
      !Number.isFinite(start.x) ||
      !Number.isFinite(start.z) ||
      !Number.isFinite(nextX) ||
      !Number.isFinite(nextZ) ||
      (start.x === nextX && start.z === nextZ)
    ) {
      index += 1;
      continue;
    }

    const end = { x: nextX, z: nextZ };
    let points = [start, end];
    let kind =
      state.motion === 0
        ? state.inG30Departure
          ? "g30"
          : "rapid"
        : "feed";
    if ((state.motion === 2 || state.motion === 3) && state.plane === 18) {
      const clockwise = state.motion === 2;
      points = interpolateArc(start, end, words, clockwise);
      kind = clockwise ? "arc-cw" : "arc-ccw";
    } else if ((state.motion === 2 || state.motion === 3) && state.plane !== 18) {
      warnings.add(`G${state.motion} outside the G18 Z-X plane is drawn as a straight line.`);
    }
    segments.push(createMotionSegment(index, raw, state, kind, points, start, end));
    if (mCodes.includes(99) || mCodes.includes(30)) {
      break;
    }
    index += 1;
  }

  if (executionSteps >= maximumExecutionSteps) {
    warnings.add(
      `Macro execution stopped after ${maximumExecutionSteps} blocks to prevent an endless loop.`
    );
  }

  let motionSeconds = 0;
  let unestimatedSegmentCount = 0;
  segments.forEach((segment, executionIndex) => {
    segment.executionIndex = executionIndex;
    const seconds = estimatedSegmentSeconds(segment, settings.rapidRate);
    segment.estimatedSeconds = Number.isFinite(seconds) ? seconds : null;
    if (Number.isFinite(seconds)) {
      motionSeconds += seconds;
    } else if (segment.kind !== "g30") {
      unestimatedSegmentCount += 1;
    }
  });

  const compensationIssues = [];
  const compensatedSegments = buildCompensatedSegments(
    segments,
    toolRadii,
    (issue) => compensationIssues.push(issue)
  );
  const errors = [...new Set([
    ...cycleAlarms,
    ...[...warnings].filter((warning) =>
      warning.startsWith(CYCLE_RESOURCE_LIMIT_PREFIX)
    ),
    ...compensationIssues
    .filter((issue) => issue.severity === "error")
    .map((issue) => issue.message)
  ])];
  for (const issue of compensationIssues) {
    warnings.add(
      `Row ${issue.line} (${issue.block}): programmed motion is ` +
      `${issue.programmedLength.toFixed(3)} ${state.units} with nose R${issue.radius.toFixed(3)}; ` +
      "the real control may reject or alter the interfering compensation vectors."
    );
  }
  if (compensatedSegments.length) {
    warnings.add(
      "The magenta G41/G42 overlay uses the configured nose radius and imaginary-tip direction; verify holder orientation, control vectors, and parameter variants."
    );
  }

  const turretSeconds = turretIndexCount * settings.turretIndexSeconds;
  const estimatedCycleSeconds = motionSeconds + turretSeconds;

  let bounds;
  for (const collection of [segments, compensatedSegments]) {
    for (const segment of collection) {
      for (const point of segment.points || []) {
        if (!Number.isFinite(point.x) || !Number.isFinite(point.z)) continue;
        if (!bounds) {
          bounds = {
            minX: point.x,
            maxX: point.x,
            minZ: point.z,
            maxZ: point.z
          };
        } else {
          bounds.minX = Math.min(bounds.minX, point.x);
          bounds.maxX = Math.max(bounds.maxX, point.x);
          bounds.minZ = Math.min(bounds.minZ, point.z);
          bounds.maxZ = Math.max(bounds.maxZ, point.z);
        }
      }
    }
  }
  bounds ||= { minX: 0, maxX: 1, minZ: 0, maxZ: 1 };

  return {
    segments,
    compensatedSegments,
    tools: [...tools].sort((a, b) => a - b),
    toolDefinitions: [...toolDefinitions.values()].sort(
      (a, b) => a.number - b.number
    ),
    toolRadii: Object.fromEntries([...toolRadii.entries()]),
    toolTable: {
      name: options.toolTable?.name || "NC comment inference",
      units: options.toolTable?.units || "mm",
      sourcePath: ""
    },
    errors,
    compensationIssues,
    warnings: [...warnings],
    bounds,
    stock: parseStock(source),
    settings,
    machine: {
      name: machineParameters.name || "Built-in preview defaults",
      control: machineParameters.control || "",
      reference: machineParameters.reference || "",
      rapidRate: settings.rapidRate,
      turretIndexSeconds: settings.turretIndexSeconds,
      tailstockPresent: settings.tailstockPresent,
      retractOrder: settings.retractOrder,
      xProgramming:
        machineSettings["axis.xProgramming"] || "diameter",
      fanuc5108Nsp: fanucBit(machineParameters, 5108, 3),
      fanucParameters: {
        fck: fanucBit(machineParameters, 5104, 2),
        rf1: fanucBit(machineParameters, 5105, 1),
        rf2: fanucBit(machineParameters, 5105, 2),
        nt1: fanucBit(machineParameters, 5106, 2),
        asu: fanucBit(machineParameters, 5107, 0),
        r16: fanucBit(machineParameters, 5108, 0),
        dtp: fanucBit(machineParameters, 5108, 1),
        nsp: fanucBit(machineParameters, 5108, 3)
      }
    },
    macroVariables: Object.fromEntries(
      [...variables.entries()].sort((a, b) => a[0] - b[0])
    ),
    meta: {
      lineCount: lines.length,
      segmentCount: segments.length,
      compensatedSegmentCount: compensatedSegments.length,
      g71PassCount,
      g71RoughFinishPassCount,
      g71FinishAllowance,
      g71Behavior,
      g72PassCount,
      g72RoughFinishPassCount,
      g72FinishAllowance,
      g72Behavior,
      g73PassCount,
      g73FinishAllowance,
      g73Behavior,
      g70FinishCount,
      g74PeckCount,
      g75GrooveCount,
      g75PeckCount,
      g76PassCount,
      g76RoughPassCount,
      g76FinishPassCount,
      g76Behavior,
      syntheticCycleSegmentCount,
      g52ChangeCount,
      finalLocalCoordinateOffset: { ...state.localCoordinateOffset },
      executedBlockCount,
      jumpCount,
      turretIndexCount,
      motionSeconds,
      turretSeconds,
      estimatedCycleSeconds,
      unestimatedSegmentCount,
      units: state.units
    }
  };
}

module.exports = {
  parseProgram,
  interpolateArc,
  toolDefinitionsForSource
};
