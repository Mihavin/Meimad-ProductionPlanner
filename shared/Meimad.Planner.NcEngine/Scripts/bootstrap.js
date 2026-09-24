"use strict";

// Minimal CommonJS runtime for the vendored Chevalier NC engine
// (third_party/chevalier-nc-viewer) inside a ClearScript V8 engine.
//
// The engine was written for Node.js. It needs only require(), the win32 flavour of
// node:path, a read-only subset of node:fs, and Buffer.alloc for one header read. They are
// provided here so the vendored files stay byte-for-byte identical to upstream. Every file
// access goes through the host object __meimadHost, which only answers for folders the .NET
// host allow-listed (the engine folder, and on the client the program and memory folders).
(function (global) {
  const host = global.__meimadHost;
  const SEP = "\\";

  // ----- node:path (win32) ------------------------------------------------------------------

  const isSeparator = (character) => character === "\\" || character === "/";

  function splitRoot(value) {
    const text = String(value);
    if (text.length >= 2 && isSeparator(text[0]) && isSeparator(text[1])) {
      const unc = /^[\\/]{2}([^\\/]+)[\\/]+([^\\/]+)(?:[\\/]+|$)/.exec(text);
      if (unc) {
        return { device: `\\\\${unc[1]}\\${unc[2]}`, rooted: true, rest: text.slice(unc[0].length) };
      }
    }
    if (/^[a-zA-Z]:/.test(text)) {
      const rooted = text.length > 2 && isSeparator(text[2]);
      return { device: text.slice(0, 2), rooted, rest: text.slice(rooted ? 3 : 2) };
    }
    if (text.length && isSeparator(text[0])) {
      return { device: "", rooted: true, rest: text.replace(/^[\\/]+/, "") };
    }
    return { device: "", rooted: false, rest: text };
  }

  function normalizeSegments(rest, allowAboveRoot) {
    const segments = [];
    for (const part of String(rest).split(/[\\/]+/)) {
      if (!part || part === ".") continue;
      if (part === "..") {
        if (segments.length && segments[segments.length - 1] !== "..") segments.pop();
        else if (allowAboveRoot) segments.push("..");
      } else {
        segments.push(part);
      }
    }
    return segments;
  }

  function normalize(value) {
    const text = String(value);
    if (!text) return ".";
    const root = splitRoot(text);
    const trailing = isSeparator(text[text.length - 1]);
    let tail = normalizeSegments(root.rest, !root.rooted).join(SEP);
    if (!tail && !root.rooted) tail = ".";
    if (tail && trailing && tail !== ".") tail += SEP;
    return root.device + (root.rooted ? SEP : "") + tail;
  }

  function isAbsolute(value) {
    const root = splitRoot(String(value));
    return root.rooted;
  }

  function join(...parts) {
    const joined = parts.map(String).filter((part) => part.length > 0).join(SEP);
    return joined ? normalize(joined) : ".";
  }

  function resolve(...parts) {
    let device = "";
    let tail = "";
    let absolute = false;
    for (let index = parts.length - 1; index >= -1; index -= 1) {
      const value = index >= 0 ? String(parts[index]) : String(host.currentDirectory());
      if (!value) continue;
      const root = splitRoot(value);
      if (root.device) {
        if (device && device.toLowerCase() !== root.device.toLowerCase()) continue;
        if (!device) device = root.device;
      }
      if (!absolute) {
        tail = tail ? `${root.rest}${SEP}${tail}` : root.rest;
        absolute = root.rooted;
      }
      if (absolute && device) break;
    }
    if (!device) device = splitRoot(String(host.currentDirectory())).device;
    return device + SEP + normalizeSegments(tail, false).join(SEP);
  }

  function trimTrailingSeparators(value) {
    const root = splitRoot(value);
    const prefix = value.slice(0, value.length - root.rest.length);
    return prefix + root.rest.replace(/[\\/]+$/, "");
  }

  function dirname(value) {
    const text = trimTrailingSeparators(String(value));
    const root = splitRoot(text);
    const prefix = root.device + (root.rooted ? SEP : "");
    const index = Math.max(root.rest.lastIndexOf("\\"), root.rest.lastIndexOf("/"));
    if (index < 0) return prefix || ".";
    return prefix + root.rest.slice(0, index).replace(/[\\/]+$/, "");
  }

  function basename(value, extension) {
    const text = trimTrailingSeparators(String(value));
    const root = splitRoot(text);
    const index = Math.max(root.rest.lastIndexOf("\\"), root.rest.lastIndexOf("/"));
    const name = index < 0 ? root.rest : root.rest.slice(index + 1);
    if (typeof extension === "string" && extension && name !== extension && name.endsWith(extension)) {
      return name.slice(0, name.length - extension.length);
    }
    return name;
  }

  function extname(value) {
    const name = basename(value);
    const index = name.lastIndexOf(".");
    return index <= 0 ? "" : name.slice(index);
  }

  function relative(from, to) {
    const source = resolve(from);
    const target = resolve(to);
    if (source.toLowerCase() === target.toLowerCase()) return "";
    const sourceRoot = splitRoot(source);
    const targetRoot = splitRoot(target);
    if (sourceRoot.device.toLowerCase() !== targetRoot.device.toLowerCase()) return target;
    const sourceParts = normalizeSegments(sourceRoot.rest, false);
    const targetParts = normalizeSegments(targetRoot.rest, false);
    let common = 0;
    while (common < sourceParts.length && common < targetParts.length &&
      sourceParts[common].toLowerCase() === targetParts[common].toLowerCase()) {
      common += 1;
    }
    return [
      ...sourceParts.slice(common).map(() => ".."),
      ...targetParts.slice(common)
    ].join(SEP);
  }

  const path = Object.freeze({
    sep: SEP,
    delimiter: ";",
    basename,
    dirname,
    extname,
    isAbsolute,
    join,
    normalize,
    relative,
    resolve
  });

  // ----- node:fs (read-only subset) ---------------------------------------------------------

  function notFound(filePath) {
    const error = new Error(`ENOENT: no such file or directory, '${filePath}'`);
    error.code = "ENOENT";
    return error;
  }

  function statOf(filePath) {
    const json = String(host.stat(String(filePath)) || "");
    if (!json) throw notFound(filePath);
    const value = JSON.parse(json);
    return {
      size: value.size,
      mtimeMs: value.mtimeMs,
      isFile: () => value.file === true,
      isDirectory: () => value.directory === true
    };
  }

  const fs = Object.freeze({
    existsSync: (filePath) => Boolean(host.exists(String(filePath))),
    statSync: statOf,
    readFileSync(filePath) {
      const target = String(filePath);
      if (!host.exists(target)) throw notFound(target);
      return String(host.readText(target));
    },
    readdirSync(folder, options) {
      const json = String(host.listDirectory(String(folder)) || "");
      if (!json) throw notFound(folder);
      const entries = JSON.parse(json);
      if (options && options.withFileTypes) {
        return entries.map((entry) => ({
          name: entry.name,
          isFile: () => entry.directory !== true,
          isDirectory: () => entry.directory === true
        }));
      }
      return entries.map((entry) => entry.name);
    },
    openSync(filePath) {
      const target = String(filePath);
      if (!host.exists(target)) throw notFound(target);
      return { path: target };
    },
    readSync(handle, buffer, _offset, length) {
      const text = String(host.readPrefix(handle.path, Number(length) || 0) || "");
      buffer.text = text;
      return text.length;
    },
    closeSync() {}
  });

  // Only subprograms.js uses Buffer: Buffer.alloc(n), fs.readSync into it, then
  // buffer.subarray(0, length).toString("utf8").
  class HeaderBuffer {
    constructor() { this.text = ""; }
    subarray(start, end) {
      const view = new HeaderBuffer();
      view.text = this.text.slice(start, end);
      return view;
    }
    toString() { return this.text; }
  }
  const Buffer = Object.freeze({ alloc: () => new HeaderBuffer() });

  // ----- CommonJS modules -------------------------------------------------------------------

  const factories = new Map();
  const cache = new Map();
  const builtins = new Map([
    ["node:path", path], ["path", path],
    ["node:fs", fs], ["fs", fs]
  ]);

  const key = (filename) => normalize(filename).toLowerCase();

  function register(filename, factory) {
    factories.set(key(filename), { filename: normalize(filename), factory });
  }

  function load(filename) {
    const id = key(filename);
    if (cache.has(id)) return cache.get(id).exports;
    const entry = factories.get(id);
    if (!entry) throw new Error(`Cannot find module '${filename}'`);
    const module = { exports: {}, filename: entry.filename, loaded: false };
    cache.set(id, module);
    try {
      entry.factory.call(module.exports, module.exports, requireFrom(dirname(entry.filename)), module,
        entry.filename, dirname(entry.filename));
    } catch (error) {
      cache.delete(id);
      throw error;
    }
    module.loaded = true;
    return module.exports;
  }

  function requireFrom(directory) {
    return function require(request) {
      const name = String(request);
      if (builtins.has(name)) return builtins.get(name);
      if (!name.startsWith(".") && !isAbsolute(name)) {
        throw new Error(`Module '${name}' is not available in the Meimad NC engine host.`);
      }
      const target = resolve(directory, name);
      if (factories.has(key(target))) return load(target);
      if (factories.has(key(`${target}.js`))) return load(`${target}.js`);
      throw new Error(`Cannot find module '${name}' from '${directory}'`);
    };
  }

  const noop = () => {};
  if (typeof global.console === "undefined") {
    global.console = Object.freeze({ log: noop, info: noop, warn: noop, error: noop, debug: noop });
  }
  global.Buffer = Buffer;
  global.__meimadModules = Object.freeze({
    register,
    require: (filename) => load(filename),
    path,
    fs
  });
})(this);
