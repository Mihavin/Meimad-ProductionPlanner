/*
 * Compact transport of a parsed preview model between the parser (VS Code
 * extension host, desktop parse worker) and the preview page.
 *
 * Sending the model object as is costs a structured clone (or JSON) of every
 * segment object with its ~30 named fields and every {x, y, z} point: tens of
 * megabytes for a large CAM program, and a long pause in the page that
 * receives it. Here segment lists are stored by column (one array per field),
 * points as flat rounded number arrays, start/end are rebuilt from the points,
 * and the whole model travels as a single JSON string, which is copied cheaply
 * and parsed quickly.
 */
(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.CncModelTransport = factory();
  }
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  const FORMAT = "cnc-model-columns-1";
  const SEGMENT_LISTS = ["segments", "compensatedSegments"];
  const DERIVED = new Set(["start", "end"]);
  const POINT_SCALE = 1e5;

  function round(value) {
    return typeof value === "number" && Number.isFinite(value)
      ? Math.round(value * POINT_SCALE) / POINT_SCALE
      : value ?? null;
  }

  function isPlainNumberRecord(value) {
    if (!value || typeof value !== "object" || Array.isArray(value)) return false;
    for (const key in value) {
      const entry = value[key];
      if (entry !== undefined && entry !== null && typeof entry !== "number") return false;
    }
    return true;
  }

  function packPoints(points, pointKeys) {
    if (!Array.isArray(points)) return null;
    const flat = new Array(points.length * pointKeys.length);
    let index = 0;
    for (const point of points) {
      for (const key of pointKeys) flat[index++] = round(point?.[key]);
    }
    return flat;
  }

  function unpackPoints(flat, pointKeys) {
    if (!Array.isArray(flat)) return flat;
    const width = pointKeys.length;
    const points = new Array(flat.length / width);
    for (let index = 0, offset = 0; index < points.length; index += 1, offset += width) {
      const point = {};
      for (let key = 0; key < width; key += 1) {
        const value = flat[offset + key];
        if (value !== null) point[pointKeys[key]] = value;
      }
      points[index] = point;
    }
    return points;
  }

  // Columns: one array per field. Fields whose values are records of numbers
  // (rotary: {a0, b0, ...}) become one column per member ("rotary.b0").
  function packSegments(list) {
    const count = list.length;
    const keys = new Set();
    const records = new Map();
    const pointKeys = new Set();
    for (const segment of list) {
      for (const key in segment) {
        if (DERIVED.has(key) || segment[key] === undefined) continue;
        keys.add(key);
        const value = segment[key];
        if (key === "points") {
          if (Array.isArray(value)) {
            for (const point of value) for (const member in point) pointKeys.add(member);
          }
        } else if (isPlainNumberRecord(value)) {
          if (!records.has(key)) records.set(key, new Set());
          for (const member in value) records.get(key).add(member);
        } else if (value !== null && typeof value === "object") {
          records.set(key, null); // mixed or nested: keep whole values
        }
      }
    }
    const pointOrder = [...pointKeys];
    const columns = {};
    const nulls = {};
    const encodings = {};
    for (const key of keys) {
      const members = records.get(key);
      if (key === "points") {
        columns.points = list.map((segment) => packPoints(segment.points, pointOrder));
      } else if (members) {
        for (const member of members) {
          columns[`${key}.${member}`] = list.map((segment) => {
            const value = segment[key]?.[member];
            return value === undefined ? null : value;
          });
        }
        columns[key] = undefined;
        const present = list.map((segment) => (segment[key] && typeof segment[key] === "object" ? 1 : 0));
        if (present.some((flag) => !flag)) columns[`${key}.`] = present;
      } else {
        const column = new Array(count);
        const explicitNulls = [];
        for (let index = 0; index < count; index += 1) {
          const value = list[index][key];
          if (value === undefined) column[index] = null;
          else {
            if (value === null) explicitNulls.push(index);
            column[index] = value;
          }
        }
        const encoded = encodeColumn(column);
        columns[key] = encoded.column;
        if (encoded.encoding) encodings[key] = encoded.encoding;
        if (explicitNulls.length) nulls[key] = explicitNulls;
      }
    }
    for (const key of Object.keys(columns)) {
      if (columns[key] === undefined) delete columns[key];
      else if (key.includes(".")) columns[key] = columns[key].map(roundNumber);
    }
    return { count, pointKeys: pointOrder, columns, nulls, encodings };
  }

  function roundNumber(value) {
    return typeof value === "number" && Number.isFinite(value) && !Number.isInteger(value)
      ? Math.round(value * 1e6) / 1e6
      : value;
  }

  // Short string columns become dictionary indexes, booleans 0/1, and
  // decimals are rounded to 1e-6; null (absent) stays null.
  function encodeColumn(column) {
    let strings = 0;
    let booleans = 0;
    let numbers = 0;
    let others = 0;
    for (const value of column) {
      if (value === null) continue;
      if (typeof value === "string") strings += 1;
      else if (typeof value === "boolean") booleans += 1;
      else if (typeof value === "number") numbers += 1;
      else others += 1;
    }
    if (others) return { column };
    if (booleans && !strings && !numbers) {
      return { column: column.map((value) => (value === null ? null : value ? 1 : 0)), encoding: "bool" };
    }
    if (numbers && !strings && !booleans) return { column: column.map(roundNumber) };
    if (strings && !booleans && !numbers) {
      const dictionary = new Map();
      for (const value of column) {
        if (value !== null && !dictionary.has(value)) dictionary.set(value, dictionary.size);
        if (dictionary.size > 1024) return { column };
      }
      return {
        column: column.map((value) => (value === null ? null : dictionary.get(value))),
        encoding: { dictionary: [...dictionary.keys()] }
      };
    }
    return { column };
  }

  function decodeColumn(column, encoding) {
    if (!encoding) return column;
    if (encoding === "bool") return column.map((value) => (value === null ? null : value === 1));
    if (encoding.dictionary) return column.map((value) => (value === null ? null : encoding.dictionary[value]));
    return column;
  }

  function unpackSegments(packed) {
    const { count, pointKeys, nulls = {}, encodings = {} } = packed;
    const columns = {};
    for (const [name, column] of Object.entries(packed.columns)) columns[name] = decodeColumn(column, encodings[name]);
    const segments = new Array(count);
    for (let index = 0; index < count; index += 1) segments[index] = {};
    const recordKeys = new Map();
    for (const name of Object.keys(columns)) {
      const dot = name.indexOf(".");
      if (dot < 0) continue;
      const key = name.slice(0, dot);
      const member = name.slice(dot + 1);
      if (!recordKeys.has(key)) recordKeys.set(key, []);
      if (member) recordKeys.get(key).push(member);
    }
    for (const name of Object.keys(columns)) {
      if (name.includes(".")) continue;
      const column = columns[name];
      if (name === "points") {
        for (let index = 0; index < count; index += 1) {
          const points = unpackPoints(column[index], pointKeys);
          if (points === null) continue;
          const segment = segments[index];
          segment.points = points;
          if (points.length) {
            segment.start = points[0];
            segment.end = points[points.length - 1];
          }
        }
        continue;
      }
      for (let index = 0; index < count; index += 1) {
        const value = column[index];
        if (value !== null) segments[index][name] = value;
      }
      for (const index of nulls[name] || []) segments[index][name] = null;
    }
    for (const [key, members] of recordKeys) {
      const present = columns[`${key}.`];
      const memberColumns = members.map((member) => columns[`${key}.${member}`]);
      for (let index = 0; index < count; index += 1) {
        if (present && !present[index]) continue;
        const record = {};
        for (let member = 0; member < members.length; member += 1) {
          const value = memberColumns[member][index];
          if (value !== null) record[members[member]] = value;
        }
        segments[index][key] = record;
      }
    }
    return segments;
  }

  // Model -> JSON string.
  function stringify(model) {
    const packed = { format: FORMAT, model: {}, lists: {} };
    for (const key in model) {
      if (SEGMENT_LISTS.includes(key) && Array.isArray(model[key])) {
        packed.lists[key] = packSegments(model[key]);
      } else {
        packed.model[key] = model[key];
      }
    }
    return JSON.stringify(packed);
  }

  // JSON string (or parsed object) -> model.
  function parse(text) {
    const packed = typeof text === "string" ? JSON.parse(text) : text;
    if (!packed || packed.format !== FORMAT) {
      throw new Error("Unknown preview model format");
    }
    const model = packed.model || {};
    for (const [key, list] of Object.entries(packed.lists || {})) {
      model[key] = unpackSegments(list);
    }
    return model;
  }

  return { FORMAT, parse, stringify, packSegments, unpackSegments };
});
