"use strict";

function tokenise(expression) {
  const tokens = [];
  let index = 0;

  while (index < expression.length) {
    const character = expression[index];
    if (/\s/.test(character)) {
      index += 1;
      continue;
    }
    if (character === "#" && expression[index + 1] === "[") {
      // Indirect reference: #[<expression>] reads the variable whose number
      // the bracketed expression evaluates to.
      tokens.push({ type: "indirect" });
      index += 1;
      continue;
    }
    if (character === "#") {
      const match = expression.slice(index).match(/^#(\d+)(\.\d+)?/);
      if (!match) {
        throw new Error(`Invalid variable at column ${index + 1}`);
      }
      // Haas NGC parameter variables carry a decimal part (#30003.078).
      const decimal = match[2] && Number(match[1]) >= 30000 && Number(match[1]) <= 39999;
      tokens.push({ type: "variable", value: Number(decimal ? match[1] + match[2] : match[1]) });
      index += decimal ? match[0].length : match[1].length + 1;
      continue;
    }
    if (/\d|\./.test(character)) {
      const match = expression
        .slice(index)
        .match(/^(?:\d+(?:\.\d*)?|\.\d+)(?:E[+-]?\d+)?/i);
      if (!match) {
        throw new Error(`Invalid number at column ${index + 1}`);
      }
      tokens.push({ type: "number", value: Number(match[0]) });
      index += match[0].length;
      continue;
    }
    if (/[A-Z_]/i.test(character)) {
      const match = expression.slice(index).match(/^[A-Z_]+/i);
      tokens.push({ type: "name", value: match[0].toUpperCase() });
      index += match[0].length;
      continue;
    }
    if ("+-*/[]()".includes(character)) {
      tokens.push({
        type: character === "[" || character === "("
          ? "open"
          : character === "]" || character === ")"
            ? "close"
            : "operator",
        value: character
      });
      index += 1;
      continue;
    }
    if (character === ",") {
      // Separates function arguments: ATAN[#1,#2], POW[#1,2], PRM[1401,1].
      tokens.push({ type: "comma", value: "," });
      index += 1;
      continue;
    }
    throw new Error(`Unsupported character "${character}" at column ${index + 1}`);
  }

  return tokens;
}

function fanucRound(value) {
  return Math.sign(value) * Math.floor(Math.abs(value) + 0.5);
}

function applyFunction(name, value) {
  const radians = (value * Math.PI) / 180;
  switch (name) {
    case "ABS":
      return Math.abs(value);
    case "SQRT":
      return Math.sqrt(value);
    case "FIX":
      return Math.trunc(value);
    case "FUP":
      return Math.sign(value) * Math.ceil(Math.abs(value));
    case "ROUND":
      return fanucRound(value);
    case "SIN":
      return Math.sin(radians);
    case "COS":
      return Math.cos(radians);
    case "TAN":
      return Math.tan(radians);
    case "ASIN":
      return (Math.asin(value) * 180) / Math.PI;
    case "ACOS":
      return (Math.acos(value) * 180) / Math.PI;
    case "ATAN":
      return (Math.atan(value) * 180) / Math.PI;
    case "EXP":
      return Math.exp(value);
    case "LN":
      return Math.log(value);
    case "ADP":
      // Add decimal point: the value is already a real number here.
      return value;
    case "BIN":
      return binaryFromBcd(value);
    case "BCD":
      return bcdFromBinary(value);
    default:
      throw new Error(`Unsupported macro function ${name}`);
  }
}

function binaryFromBcd(value) {
  let result = 0;
  let scale = 1;
  for (let rest = Math.trunc(value); rest > 0; rest = Math.floor(rest / 16)) {
    result += (rest % 16) * scale;
    scale *= 10;
  }
  return result;
}

function bcdFromBinary(value) {
  let result = 0;
  let scale = 1;
  for (let rest = Math.trunc(value); rest > 0; rest = Math.floor(rest / 10)) {
    result += (rest % 10) * scale;
    scale *= 16;
  }
  return result;
}

// Functions with more than one argument (FANUC Series 30i forms).
function applyFunctionArguments(name, values) {
  switch (name) {
    case "ATAN":
      return degreesAtan2(values[0], values[1]);
    case "POW":
      return Math.pow(values[0], values[1]);
    case "MIN":
      return Math.min(values[0], values[1]);
    case "MAX":
      return Math.max(values[0], values[1]);
    default:
      throw new Error(`Macro function ${name} does not take ${values.length} arguments`);
  }
}

function degreesAtan2(y, x) {
  const degrees = (Math.atan2(y, x) * 180) / Math.PI;
  return degrees < 0 ? degrees + 360 : degrees;
}

// Evaluates a FANUC Macro B / Haas NGC expression.
//
// options.vacant: undefined variables and #0 evaluate to null ("vacant")
//   instead of 0. Vacant behaves as 0 in arithmetic, differs from 0 in EQ/NE,
//   and a bare vacant operand stays vacant so `#2=#1` copies the vacancy. The
//   result may then be null. onUndefined is not called in this mode.
// options.bitwise: AND/OR/XOR work on the integer bits of their operands, as
//   on the Haas control, instead of as boolean operators.
function evaluateExpression(expression, variables, onUndefined, options = {}) {
  const tokens = tokenise(String(expression).toUpperCase());
  const vacantAware = Boolean(options.vacant);
  const bitwise = Boolean(options.bitwise);
  let position = 0;

  const peek = () => tokens[position];
  const take = () => tokens[position++];
  const takeName = (name) => {
    const token = peek();
    if (token && token.type === "name" && token.value === name) {
      position += 1;
      return true;
    }
    return false;
  };
  const number = (value) => (value === null ? 0 : value);

  function bracketed(label) {
    const open = take();
    if (!open || open.type !== "open") {
      throw new Error(`Expected [ after ${label}`);
    }
    const value = logicalOr();
    const close = take();
    if (!close || close.type !== "close") {
      throw new Error(`Unclosed ${label}`);
    }
    return value;
  }

  function readVariable(id) {
    if (id === 0) {
      return vacantAware ? null : 0;
    }
    const value = variables.get(id);
    if (Number.isFinite(value)) {
      return value;
    }
    if (vacantAware) {
      return null;
    }
    if (onUndefined) {
      onUndefined(id);
    }
    return 0;
  }

  function primary() {
    const token = take();
    if (!token) {
      throw new Error("Incomplete macro expression");
    }
    if (token.type === "number") {
      return token.value;
    }
    if (token.type === "variable") {
      return readVariable(token.value);
    }
    if (token.type === "indirect") {
      const id = Math.round(number(bracketed("indirect variable #[")));
      if (!Number.isSafeInteger(id) || id < 0) {
        throw new Error(`Invalid indirect variable number ${id}`);
      }
      return readVariable(id);
    }
    if (token.type === "open") {
      const value = logicalOr();
      const close = take();
      if (!close || close.type !== "close") {
        throw new Error("Unclosed macro expression bracket");
      }
      return value;
    }
    if (token.type === "name") {
      const next = peek();
      if (!next || next.type !== "open") {
        throw new Error(`Expected [ after macro function ${token.value}`);
      }
      take();
      const args = [logicalOr()];
      while (peek()?.type === "comma") {
        take();
        args.push(logicalOr());
      }
      const close = take();
      if (!close || close.type !== "close") {
        throw new Error(`Unclosed ${token.value} function`);
      }
      // Control-specific functions (FANUC PRM[] reads machine parameters).
      const custom = options.functions?.[token.value];
      if (custom) {
        return custom(args.map(number));
      }
      if (args.length > 1) {
        return applyFunctionArguments(token.value, args.map(number));
      }
      const value = number(args[0]);
      // FANUC two-argument arctangent: ATAN[y]/[x], 0 to 360 degrees.
      if (
        token.value === "ATAN" &&
        peek()?.type === "operator" && peek().value === "/" &&
        tokens[position + 1]?.type === "open"
      ) {
        take();
        const divisor = number(bracketed("ATAN divisor"));
        return degreesAtan2(value, divisor);
      }
      return applyFunction(token.value, value);
    }
    throw new Error("Expected a macro value");
  }

  function unary() {
    const token = peek();
    if (token && token.type === "operator" && (token.value === "+" || token.value === "-")) {
      take();
      const value = unary();
      if (value === null) {
        return 0;
      }
      return token.value === "-" ? -value : value;
    }
    return primary();
  }

  function multiply() {
    let value = unary();
    while (true) {
      const token = peek();
      const operator =
        token && token.type === "operator" && (token.value === "*" || token.value === "/")
          ? token.value
          : token && token.type === "name" && token.value === "MOD"
            ? token.value
            : undefined;
      if (!operator) {
        break;
      }
      take();
      const right = number(unary());
      value = number(value);
      if (operator === "*") value *= right;
      if (operator === "/") value /= right;
      if (operator === "MOD") value %= right;
    }
    return value;
  }

  function add() {
    let value = multiply();
    while (true) {
      const token = peek();
      if (!token || token.type !== "operator" || (token.value !== "+" && token.value !== "-")) {
        break;
      }
      take();
      const right = number(multiply());
      value = token.value === "+" ? number(value) + right : number(value) - right;
    }
    return value;
  }

  function comparison() {
    let value = add();
    const operators = new Set(["EQ", "NE", "GT", "GE", "LT", "LE"]);
    const token = peek();
    if (!token || token.type !== "name" || !operators.has(token.value)) {
      return value;
    }
    take();
    const right = add();
    if (token.value === "EQ" || token.value === "NE") {
      // Vacant equals only vacant, so [#1 EQ #0] tests for an unset variable.
      return (token.value === "EQ") === (value === right) ? 1 : 0;
    }
    const left = number(value);
    const other = number(right);
    if (token.value === "GT") return left > other ? 1 : 0;
    if (token.value === "GE") return left >= other ? 1 : 0;
    if (token.value === "LT") return left < other ? 1 : 0;
    return left <= other ? 1 : 0;
  }

  function combine(operator, left, right) {
    const a = number(left);
    const b = number(right);
    if (bitwise) {
      const x = Math.trunc(a);
      const y = Math.trunc(b);
      if (operator === "AND") return x & y;
      if (operator === "OR") return x | y;
      return x ^ y;
    }
    if (operator === "AND") return a !== 0 && b !== 0 ? 1 : 0;
    if (operator === "OR") return a !== 0 || b !== 0 ? 1 : 0;
    return Boolean(a) !== Boolean(b) ? 1 : 0;
  }

  function logicalAnd() {
    let value = comparison();
    while (takeName("AND")) {
      value = combine("AND", value, comparison());
    }
    return value;
  }

  function logicalOr() {
    let value = logicalAnd();
    while (true) {
      if (takeName("OR")) {
        value = combine("OR", value, logicalAnd());
      } else if (takeName("XOR")) {
        value = combine("XOR", value, logicalAnd());
      } else {
        break;
      }
    }
    return value;
  }

  const result = logicalOr();
  if (position !== tokens.length) {
    throw new Error(`Unexpected token ${tokens[position].value ?? tokens[position].type}`);
  }
  if (result === null && vacantAware) {
    return null;
  }
  if (!Number.isFinite(result)) {
    throw new Error("Macro expression did not produce a finite number");
  }
  return result;
}

function matchingBracket(text, start) {
  let depth = 0;
  for (let index = start; index < text.length; index += 1) {
    if (text[index] === "[") depth += 1;
    if (text[index] === "]") {
      depth -= 1;
      if (depth === 0) return index;
    }
  }
  return -1;
}

function expandAddressExpressions(code, variables, onUndefined) {
  let expanded = "";
  let index = 0;
  while (index < code.length) {
    const character = code[index];
    if (/[A-Z]/.test(character)) {
      let cursor = index + 1;
      while (/\s/.test(code[cursor] || "")) cursor += 1;
      if (code[cursor] === "[") {
        const end = matchingBracket(code, cursor);
        if (end < 0) {
          throw new Error(`Unclosed address expression after ${character}`);
        }
        const value = evaluateExpression(
          code.slice(cursor, end + 1),
          variables,
          onUndefined
        );
        expanded += `${character}${value}`;
        index = end + 1;
        continue;
      }
    }
    expanded += character;
    index += 1;
  }

  return expanded.replace(/#(\d+)/g, (full, id) => {
    const variable = Number(id);
    const value = variable === 0 ? 0 : variables.get(variable);
    if (!Number.isFinite(value)) {
      if (onUndefined) {
        onUndefined(variable);
      }
      return "0";
    }
    return String(value);
  });
}

function initialiseVariables(initialValues, onError) {
  const variables = new Map();
  if (initialValues instanceof Map) {
    for (const [id, value] of initialValues) {
      if (Number.isFinite(Number(id)) && Number.isFinite(Number(value)) && Number(id) !== 0) {
        variables.set(Number(id), Number(value));
      }
    }
    return variables;
  }
  if (initialValues && typeof initialValues === "object") {
    for (const [id, value] of Object.entries(initialValues)) {
      if (Number.isFinite(Number(id)) && Number.isFinite(Number(value)) && Number(id) !== 0) {
        variables.set(Number(id), Number(value));
      }
    }
    return variables;
  }

  const source = String(initialValues || "");
  const assignments = source.split(/[,\r\n;]+/).map((value) => value.trim()).filter(Boolean);
  for (const assignment of assignments) {
    const match = assignment.match(/^#?(\d+)\s*=\s*(.+)$/);
    if (!match || Number(match[1]) === 0) {
      if (onError) onError(`Invalid initial macro value: ${assignment}`);
      continue;
    }
    try {
      variables.set(
        Number(match[1]),
        evaluateExpression(match[2], variables)
      );
    } catch (error) {
      if (onError) onError(`Initial macro ${assignment}: ${error.message}`);
    }
  }
  return variables;
}

module.exports = {
  evaluateExpression,
  expandAddressExpressions,
  initialiseVariables
};
