"use strict";

const fs = require("node:fs");
const path = require("node:path");

const MAX_SUBPROGRAM_BYTES = 32 * 1024 * 1024;
const EXTENSIONS = [".nc", ".NC", ".tap", ".TAP", ".cnc", ".CNC", ".txt", ""];
const MEMORY_EXTENSIONS = new Set(["", ".nc", ".tap", ".cnc", ".ngc", ".txt", ".min", ".mpf", ".spf"]);
const MEMORY_MAX_FILES = 5000;
const MEMORY_MAX_DEPTH = 6;
const HEADER_BYTES = 16 * 1024;
const MEMORY_CACHE_MS = 3000;

const memoryCache = new Map();

function readFileIfSmall(filePath) {
  const stat = fs.statSync(filePath);
  if (!stat.isFile() || stat.size > MAX_SUBPROGRAM_BYTES) return undefined;
  return fs.readFileSync(filePath, "utf8");
}

// The O number a program file declares: its first code line, after "%" and
// comment-only lines.
function headerProgramNumber(text) {
  for (const raw of String(text).split(/\r?\n/)) {
    const code = raw.replace(/\([^)]*\)?/g, " ").replace(/;.*$/, "").trim();
    if (!code || code === "%") continue;
    const match = code.match(/^O\s*(\d+)/i) || code.match(/^:\s*(\d+)/);
    return match ? Number(match[1]) : undefined;
  }
  return undefined;
}

function readHeader(filePath) {
  let handle;
  try {
    handle = fs.openSync(filePath, "r");
    const buffer = Buffer.alloc(HEADER_BYTES);
    const length = fs.readSync(handle, buffer, 0, HEADER_BYTES, 0);
    return buffer.subarray(0, length).toString("utf8");
  } catch {
    return "";
  } finally {
    if (handle !== undefined) fs.closeSync(handle);
  }
}

// Indexes a machine memory folder the way the control's Memory drive lists
// it: by the O number in each program's header, falling back to a leading
// number in the file name ("O09810_...nc", "9013_5ax.nc"). Files in the
// folder itself win over subfolders, then alphabetical order.
function indexMemoryFolder(root) {
  const numbers = new Map();
  const names = new Map();
  const duplicates = [];
  let fileCount = 0;
  const visit = (folder, depth) => {
    let entries;
    try {
      entries = fs.readdirSync(folder, { withFileTypes: true });
    } catch {
      return;
    }
    entries.sort((a, b) => a.name.localeCompare(b.name, "en", { numeric: true }));
    const subfolders = [];
    for (const entry of entries) {
      if (entry.name.startsWith(".")) continue;
      const filePath = path.join(folder, entry.name);
      if (entry.isDirectory()) {
        if (depth < MEMORY_MAX_DEPTH && entry.name !== "node_modules") subfolders.push(filePath);
        continue;
      }
      if (!entry.isFile() || !MEMORY_EXTENSIONS.has(path.extname(entry.name).toLowerCase())) continue;
      if (fileCount >= MEMORY_MAX_FILES) return;
      fileCount += 1;
      if (!names.has(entry.name.toLowerCase())) names.set(entry.name.toLowerCase(), filePath);
      const number = headerProgramNumber(readHeader(filePath)) ??
        (entry.name.match(/^O?(\d+)/i) ? Number(entry.name.match(/^O?(\d+)/i)[1]) : undefined);
      if (!Number.isFinite(number)) continue;
      if (numbers.has(number)) duplicates.push({ number, path: filePath, kept: numbers.get(number) });
      else numbers.set(number, filePath);
    }
    for (const subfolder of subfolders) visit(subfolder, depth + 1);
  };
  visit(root, 0);
  return { root, numbers, names, duplicates, fileCount };
}

function memoryIndex(root) {
  const cached = memoryCache.get(root);
  if (cached && Date.now() - cached.builtAt < MEMORY_CACHE_MS) return cached.index;
  const index = indexMemoryFolder(root);
  memoryCache.set(root, { index, builtAt: Date.now() });
  return index;
}

function insideFolder(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative && !relative.startsWith("..") && !path.isAbsolute(relative);
}

// Resolves M98/G65 targets that are not in the main file. Haas looks in the
// main program's folder first, then in the Setting 251/252 search location;
// memoryFolders stand in for that location (the machine's Memory drive). A
// path in the call is reduced to its file name in the program's folder, and
// never leaves a memory folder.
function createSubprogramResolver(directory, options = {}) {
  const folder = directory ? path.resolve(directory) : undefined;
  const memoryFolders = (Array.isArray(options.memoryFolders) ? options.memoryFolders : [])
    .filter((entry) => typeof entry === "string" && entry.trim())
    .map((entry) => path.resolve(entry.trim()))
    .filter((entry, index, all) => all.indexOf(entry) === index);
  if (!folder && !memoryFolders.length) return undefined;
  let listing;
  const files = () => {
    if (!listing) {
      try {
        listing = new Map(fs.readdirSync(folder).map((name) => [name.toLowerCase(), name]));
      } catch {
        listing = new Map();
      }
    }
    return listing;
  };
  const fromFolder = (target) => {
    if (!folder) return undefined;
    const candidates = [];
    if (typeof target === "number") {
      const number = String(Math.trunc(target));
      const stems = [
        `O${number.padStart(5, "0")}`,
        `O${number.padStart(4, "0")}`,
        `O${number}`,
        number.padStart(5, "0"),
        number
      ];
      for (const stem of stems) {
        for (const extension of EXTENSIONS) candidates.push(`${stem}${extension}`);
      }
    } else {
      const name = path.basename(String(target).replace(/\\/g, "/"));
      if (name) candidates.push(name);
    }
    const available = files();
    for (const candidate of candidates) {
      const actual = available.get(candidate.toLowerCase());
      if (!actual) continue;
      const filePath = path.join(folder, actual);
      const source = readFileIfSmall(filePath);
      if (source !== undefined) return { name: actual, source, path: filePath, location: "folder" };
    }
    return undefined;
  };
  const fromMemory = (target) => {
    for (const root of memoryFolders) {
      const index = memoryIndex(root);
      let filePath;
      if (typeof target === "number") {
        filePath = index.numbers.get(Math.trunc(target));
      } else {
        // G65 (/Memory/Folder/NAME.nc): a path under the memory root first,
        // then the file name anywhere in the memory folder.
        const relative = String(target).replace(/\\/g, "/").replace(/^\/+/, "").replace(/^memory\//i, "");
        const direct = path.resolve(root, relative);
        if (insideFolder(root, direct) && fs.existsSync(direct)) filePath = direct;
        else filePath = index.names.get(path.basename(relative).toLowerCase());
      }
      if (!filePath) continue;
      const source = readFileIfSmall(filePath);
      if (source === undefined) continue;
      return {
        name: path.basename(filePath),
        source,
        path: filePath,
        location: "memory",
        memoryRoot: root
      };
    }
    return undefined;
  };
  const resolve = (target) => fromFolder(target) || fromMemory(target);
  resolve.memoryFolders = memoryFolders;
  return resolve;
}

module.exports = {
  createSubprogramResolver,
  headerProgramNumber,
  indexMemoryFolder
};
