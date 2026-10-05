"use strict";

// Program files of controls whose extension the vendored resolver (src/subprograms.js) does not
// read: Mazak EIA/ISO programs saved by a MAZATROL MATRIX or SMOOTH control as "<work no.>.EIA".
// The vendored resolver runs first, unchanged; a call it does not find is looked up here with
// its rules: in the program's folder by file name (O1001.EIA, 1001.EIA), then in every searched
// folder and its subfolders by the O number the file declares, else a leading number in its file
// name. A name target (G65 <path>) is a file name, with or without the extension.

const fs = require("node:fs");
const path = require("node:path");
const { headerProgramNumber } = require("../src/subprograms");

const EXTENSIONS = [".eia"];
const MAX_PROGRAM_BYTES = 32 * 1024 * 1024;
const MEMORY_MAX_FILES = 5000;
const MEMORY_MAX_DEPTH = 6;
const HEADER_BYTES = 16 * 1024;
const MEMORY_CACHE_MS = 3000;

const memoryCache = new Map();

const isProgramFile = (name) => EXTENSIONS.includes(path.extname(name).toLowerCase());

function readIfSmall(filePath) {
  try {
    const stat = fs.statSync(filePath);
    if (!stat.isFile() || stat.size > MAX_PROGRAM_BYTES) return undefined;
    return String(fs.readFileSync(filePath, "utf8"));
  } catch {
    return undefined;
  }
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

// The folder's program files of these extensions, by declared number and by file name: files
// in the folder itself win over subfolders, then alphabetical order (as the vendored index).
function indexFolder(root) {
  const numbers = new Map();
  const names = new Map();
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
      if (!entry.isFile() || !isProgramFile(entry.name)) continue;
      if (fileCount >= MEMORY_MAX_FILES) return;
      fileCount += 1;
      const lower = entry.name.toLowerCase();
      if (!names.has(lower)) names.set(lower, filePath);
      const stem = lower.slice(0, -path.extname(lower).length);
      if (!names.has(stem)) names.set(stem, filePath);
      const fromName = entry.name.match(/^O?(\d+)/i);
      const number = headerProgramNumber(readHeader(filePath)) ?? (fromName ? Number(fromName[1]) : undefined);
      if (Number.isFinite(number) && !numbers.has(number)) numbers.set(number, filePath);
    }
    for (const subfolder of subfolders) visit(subfolder, depth + 1);
  };
  visit(root, 0);
  return { numbers, names };
}

function folderIndex(root) {
  const cached = memoryCache.get(root);
  if (cached && Date.now() - cached.builtAt < MEMORY_CACHE_MS) return cached.index;
  const index = indexFolder(root);
  memoryCache.set(root, { index, builtAt: Date.now() });
  return index;
}

function found(filePath, location, memoryRoot) {
  const source = readIfSmall(filePath);
  if (source === undefined) return undefined;
  return { name: path.basename(filePath), source, path: filePath, location, ...(memoryRoot ? { memoryRoot } : {}) };
}

// The program's folder by file name: the numbered stems the vendored resolver tries, or the
// called name, with each extension.
function fromFolder(folder, target) {
  if (!folder) return undefined;
  let listing;
  try {
    listing = new Map(fs.readdirSync(folder).map((name) => [name.toLowerCase(), name]));
  } catch {
    return undefined;
  }
  const stems = [];
  if (typeof target === "number") {
    const number = String(Math.trunc(target));
    stems.push(`O${number.padStart(5, "0")}`, `O${number.padStart(4, "0")}`, `O${number}`, number.padStart(5, "0"), number);
  } else {
    const name = path.basename(String(target).replace(/\\/g, "/"));
    if (name && !path.extname(name)) stems.push(name);
  }
  for (const stem of stems) {
    for (const extension of EXTENSIONS) {
      const actual = listing.get(`${stem}${extension}`.toLowerCase());
      if (!actual) continue;
      const result = found(path.join(folder, actual), "folder");
      if (result) return result;
    }
  }
  return undefined;
}

function fromMemory(memoryFolders, target) {
  for (const root of memoryFolders) {
    const index = folderIndex(root);
    const filePath = typeof target === "number"
      ? index.numbers.get(Math.trunc(target))
      : index.names.get(path.basename(String(target).replace(/\\/g, "/")).toLowerCase());
    if (!filePath) continue;
    const result = found(filePath, "memory", root);
    if (result) return result;
  }
  return undefined;
}

// The vendored resolver, then these extensions in the same folders. The resolver runs as a method
// of the interpreter that calls it, so `this` is passed through.
function withProgramExtensions(resolver, directory) {
  if (typeof resolver !== "function") return resolver;
  const folder = typeof directory === "string" && directory.trim() ? path.resolve(directory) : undefined;
  const memoryFolders = Array.isArray(resolver.memoryFolders) ? resolver.memoryFolders : [];
  const resolve = function (target) {
    return resolver.call(this, target) || fromFolder(folder, target) || fromMemory(memoryFolders, target);
  };
  for (const key of Object.keys(resolver)) resolve[key] = resolver[key];
  return resolve;
}

module.exports = { EXTENSIONS, withProgramExtensions };
