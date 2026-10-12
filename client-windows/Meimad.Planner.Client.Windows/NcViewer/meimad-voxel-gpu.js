/*
 * Meimad Planner NC viewer: WebGPU backend of the 3D stock simulation (meimad-voxel-core.js). The CPU
 * engine stays the reference: this backend runs exactly its stock operations (removal by the cutting
 * profile, counts of the holder or a rapid in the material) for a batch of motions in one compute pass.
 *   - The CPU picks the blocks every operation can reach (the same culling as the CPU engine) and gives
 *     each block its operations in program order; a uniform material block becomes a voxel array first.
 *   - The GPU keeps a mirror of the voxel arrays (2 bits per voxel). One workgroup takes one block;
 *     every material voxel is tested against the block's operations in order: a count operation counts
 *     it while it is still material, the first removal containing it removes it (and counts it as a
 *     gouge when the finished part has it). So the result is the program-order result of the CPU.
 *   - Changed blocks are read back into the CPU arrays (meshing, checkpoints and the static checks
 *     read them there).
 * Differences from the CPU engine come only from float32 arithmetic at voxel centres lying on a tool
 * surface; selfCheck() compares both on a test scene before the backend is used.
 */
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory(require("./meimad-voxel-core.js"));
  else root.MeimadVoxelGpu = factory(root.MeimadVoxel);
})(typeof self !== "undefined" ? self : this, function (V) {
  "use strict";

  const BS = V.BS;
  const BS3 = BS * BS * BS;
  const WORDS = BS3 / 16;      // 2 bits per voxel
  const TARGET_WORDS = BS3 / 32; // 1 bit per voxel
  const OP_FLOATS = 20;
  const MAX_WIDTH = 65535;

  const SHADER = /* wgsl */ `
struct Op { a: vec4f, d: vec4f, u: vec4f, k: vec4f, p: vec4u };
struct Piece { x: vec4f, y: vec4f };
struct Item { o: vec4f, i: vec4i };
struct Params { cell: f32, count: u32, width: u32, flags: u32 };

@group(0) @binding(0) var<storage, read_write> pool: array<u32>;
@group(0) @binding(1) var<storage, read> ops: array<Op>;
@group(0) @binding(2) var<storage, read> pieces: array<Piece>;
@group(0) @binding(3) var<storage, read> items: array<Item>;
@group(0) @binding(4) var<storage, read> opIndex: array<u32>;
@group(0) @binding(5) var<storage, read_write> counters: array<atomic<u32>>;
@group(0) @binding(6) var<storage, read> targetPool: array<u32>;
@group(0) @binding(7) var<uniform> params: Params;
@group(0) @binding(8) var<storage, read_write> changed: array<atomic<u32>>;

fn reaches(p: f32, l: f32, c: f32, lo: f32, hi: f32) -> bool {
  if (hi < lo) { return false; }
  var best = min(c + l * lo + p * lo * lo, c + l * hi + p * hi * hi);
  if (p > 1e-12) {
    let t = -l / (2.0 * p);
    if (t > lo && t < hi) { best = min(best, c + l * t + p * t * t); }
  }
  return best <= 1e-6;
}

// No break or continue in these loops: a D3D shader compiler (Intel, 2026) mis-ordered the operations
// of a loop that left early around atomics, so every loop runs to its end and uses flags instead.
fn insideOp(op: Op, w: vec3f) -> bool {
  let u = op.u.xyz;
  let v = -op.d.xyz;
  let vv = op.k.x;
  let vu = op.k.y;
  let A = op.k.z;
  let wu = dot(w, u);
  let wp = w - wu * u;
  let F = dot(wp, wp);
  let D = 2.0 * dot(wp, v);
  var inside = false;
  for (var n = 0u; n < op.p.y; n = n + 1u) {
    if (!inside) {
      let pc = pieces[op.p.x + n];
      if (pc.x.x > 0.5) {
        let c = pc.y.y;
        let q = w - c * u;
        inside = reaches(vv, 2.0 * dot(q, v), dot(q, q) - pc.y.z, 0.0, 1.0);
      } else {
        let s0 = pc.x.y;
        let s1 = pc.x.z;
        var lo = 0.0;
        var hi = 1.0;
        var possible = true;
        if (abs(vu) < 1e-9) {
          possible = wu >= s0 - 1e-6 && wu <= s1 + 1e-6;
        } else {
          var t0 = (s0 - wu) / vu;
          var t1 = (s1 - wu) / vu;
          if (t0 > t1) { let swap = t0; t0 = t1; t1 = swap; }
          lo = max(lo, t0);
          hi = min(hi, t1);
          possible = hi >= lo;
        }
        if (possible) {
          let k = pc.y.x;
          let ra = pc.x.w + k * (wu - s0);
          let rb = k * vu;
          inside = reaches(A - rb * rb, D - 2.0 * ra * rb, F - ra * ra, lo, hi);
        }
      }
    }
  }
  return inside;
}

fn targetInside(item: Item, vi: u32) -> bool {
  if (item.i.y == -2) { return true; }
  if (item.i.y < 0) { return false; }
  let word = targetPool[u32(item.i.y) * ${TARGET_WORDS}u + (vi >> 5u)];
  return ((word >> (vi & 31u)) & 1u) == 1u;
}

@compute @workgroup_size(64)
fn main(@builtin(workgroup_id) wg: vec3u, @builtin(local_invocation_index) t: u32) {
  let itemIndex = wg.x + wg.y * params.width;
  if (itemIndex >= params.count) { return; }
  let item = items[itemIndex];
  let slot = u32(item.i.x);
  let opStart = u32(item.i.z);
  let opCount = u32(item.i.w);
  var any = false;
  for (var q = 0u; q < 4u; q = q + 1u) {
    let wordIndex = t * 4u + q;
    let base = slot * ${WORDS}u + wordIndex;
    var word = pool[base];
    var dirty = false;
    for (var b = 0u; b < 16u; b = b + 1u) {
      if (((word >> (b * 2u)) & 3u) == 1u) {
        let vi = wordIndex * 16u + b;
        let p = item.o.xyz + vec3f(f32(vi & 15u), f32((vi >> 4u) & 15u), f32(vi >> 8u)) * params.cell;
        // The first removal containing the voxel removes it; count ops before it see material.
        var removedBy = 0xffffffffu;
        for (var n = 0u; n < opCount; n = n + 1u) {
          if (removedBy == 0xffffffffu) {
            let opi = opIndex[opStart + n];
            let op = ops[opi];
            if (insideOp(op, p - op.a.xyz)) {
              if (op.k.w < 0.5) {
                removedBy = opi;
              } else {
                atomicAdd(&counters[opi * 3u + 2u], 1u);
              }
            }
          }
        }
        if (removedBy != 0xffffffffu) {
          word = (word & ~(3u << (b * 2u))) | (2u << (b * 2u));
          dirty = true;
          atomicAdd(&counters[removedBy * 3u], 1u);
          if (targetInside(item, vi)) { atomicAdd(&counters[removedBy * 3u + 1u], 1u); }
        }
      }
    }
    if (dirty) {
      pool[base] = word;
      any = true;
    }
  }
  if (any) { atomicStore(&changed[itemIndex], 1u); }
}
`;

  // Builds the backend for `grid` (and the finished-part mask `target`, or null). Resolves
  // { backend } or { error } (no WebGPU, no adapter, a device failure).
  async function create(grid, target, options) {
    const gpu = typeof navigator !== "undefined" ? navigator.gpu : undefined;
    if (!gpu) return { error: "WebGPU is not available" };
    let adapter;
    try {
      adapter = await gpu.requestAdapter({ powerPreference: "high-performance" });
    } catch (error) {
      return { error: `No WebGPU adapter (${error.message})` };
    }
    if (!adapter) return { error: "No WebGPU adapter" };
    const limits = adapter.limits;
    let device;
    try {
      device = await adapter.requestDevice({
        requiredLimits: {
          maxStorageBufferBindingSize: limits.maxStorageBufferBindingSize,
          maxBufferSize: limits.maxBufferSize,
          maxStorageBuffersPerShaderStage: Math.min(limits.maxStorageBuffersPerShaderStage, 10)
        }
      });
    } catch (error) {
      return { error: `The WebGPU device could not be created (${error.message})` };
    }
    let lost = null;
    device.lost.then((info) => { lost = info.message || "device lost"; });
    const module_ = device.createShaderModule({ code: SHADER });
    const info = await module_.getCompilationInfo?.();
    const errors = (info?.messages || []).filter((message) => message.type === "error");
    if (errors.length) return { error: `The WebGPU shader did not compile: ${errors.map((message) => message.message).join("; ")}` };
    const pipeline = device.createComputePipeline({ layout: "auto", compute: { module: module_, entryPoint: "main" } });
    const maxSlots = Math.floor(Math.min(limits.maxStorageBufferBindingSize, limits.maxBufferSize) / (WORDS * 4));
    const STORAGE = GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST | GPUBufferUsage.COPY_SRC;

    let poolBuffer = null;
    let poolSlots = 0;
    let stale = new Set();      // CPU slots the GPU mirror does not have yet
    const adapterInfo = adapter.info || {};

    // The finished part as 1 bit per voxel, uploaded once (it never changes).
    let targetBuffer;
    {
      const slots = target ? target.pool.length : 0;
      const words = new Uint32Array(Math.max(1, slots * TARGET_WORDS));
      if (target) {
        for (let slot = 0; slot < slots; slot += 1) {
          const data = target.pool[slot];
          for (let index = 0; index < BS3; index += 1) if (data[index] === V.MAT) words[slot * TARGET_WORDS + (index >> 5)] |= 1 << (index & 31);
        }
      }
      targetBuffer = device.createBuffer({ size: words.byteLength, usage: STORAGE });
      device.queue.writeBuffer(targetBuffer, 0, words);
    }

    function ensurePool(slots) {
      if (slots <= poolSlots && poolBuffer) return;
      if (slots > maxSlots) throw new Error(`The stock needs ${slots} GPU blocks; this GPU holds ${maxSlots}.`);
      const next = Math.min(maxSlots, Math.max(slots, Math.ceil(poolSlots * 1.5), 1024));
      const buffer = device.createBuffer({ size: next * WORDS * 4, usage: STORAGE });
      if (poolBuffer) {
        const encoder = device.createCommandEncoder();
        encoder.copyBufferToBuffer(poolBuffer, 0, buffer, 0, poolSlots * WORDS * 4);
        device.queue.submit([encoder.finish()]);
        poolBuffer.destroy();
      }
      poolBuffer = buffer;
      poolSlots = next;
    }

    const packSlot = (data, out, offset) => {
      for (let word = 0; word < WORDS; word += 1) {
        let value = 0;
        const base = word * 16;
        for (let b = 0; b < 16; b += 1) value |= data[base + b] << (b * 2);
        out[offset + word] = value >>> 0;
      }
    };

    function invalidate() {
      stale = new Set();
      for (let slot = 0; slot < grid.pool.length; slot += 1) stale.add(slot);
    }
    invalidate();

    // The blocks every op can reach, with the ops of each block in order. Uniform material blocks
    // become voxel arrays (for the GPU to work on).
    function cull(ops) {
      const byBlock = new Map();
      const cell = grid.cell;
      const span = cell * BS;
      const blockHalf = Math.sqrt(3) * cell * (BS - 1) / 2;
      for (let o = 0; o < ops.length; o += 1) {
        const op = ops[o];
        const { sLo, sHi, r } = V.extentOf(op.pieces);
        const sw = op.sw;
        const sheet = V.sheetOf(sw, sLo, sHi);
        let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity, minZ = Infinity, maxZ = -Infinity;
        for (let corner = 0; corner < 4; corner += 1) {
          const s = corner & 1 ? sHi : sLo;
          const t = corner & 2 ? 1 : 0;
          const x = sw.ax + sw.dx * t + sw.ux * s, y = sw.ay + sw.dy * t + sw.uy * s, z = sw.az + sw.dz * t + sw.uz * s;
          if (x < minX) minX = x; if (x > maxX) maxX = x;
          if (y < minY) minY = y; if (y > maxY) maxY = y;
          if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
        }
        const bx0 = Math.max(0, Math.floor((minX - r - grid.ox) / span)), bx1 = Math.min(grid.nbx - 1, Math.floor((maxX + r - grid.ox) / span));
        const by0 = Math.max(0, Math.floor((minY - r - grid.oy) / span)), by1 = Math.min(grid.nby - 1, Math.floor((maxY + r - grid.oy) / span));
        const bz0 = Math.max(0, Math.floor((minZ - r - grid.oz) / span)), bz1 = Math.min(grid.nbz - 1, Math.floor((maxZ + r - grid.oz) / span));
        for (let bz = bz0; bz <= bz1; bz += 1) {
          for (let by = by0; by <= by1; by += 1) {
            for (let bx = bx0; bx <= bx1; bx += 1) {
              const b = (bz * grid.nby + by) * grid.nbx + bx;
              const state = grid.state[b];
              if (state !== V.B_MAT && !(state === V.B_MIX && grid.mat[b] > 0)) continue;
              const cx = grid.ox + ((bx << 4) + 0.5) * cell + (BS - 1) * cell / 2;
              const cy = grid.oy + ((by << 4) + 0.5) * cell + (BS - 1) * cell / 2;
              const cz = grid.oz + ((bz << 4) + 0.5) * cell + (BS - 1) * cell / 2;
              if (V.sheetDistance(sheet, cx, cy, cz) > r + blockHalf) continue;
              if (state === V.B_MAT) {
                V.allocateBlock(grid, b, V.MAT);
                stale.add(grid.slot[b]);
              }
              let list = byBlock.get(b);
              if (!list) byBlock.set(b, (list = []));
              list.push(o);
            }
          }
        }
      }
      return byBlock;
    }

    // Runs the stock operations of `ops` (program order) and writes their results into them:
    // count ops get `result`, removal ops `removed` and `gouged`.
    async function execute(ops) {
      if (lost) throw new Error(`The GPU stopped (${lost}).`);
      const byBlock = cull(ops);
      const blocks = [...byBlock.keys()];
      for (const op of ops) {
        if (op.kind === "count") op.result = 0;
        else { op.removed = 0; op.gouged = 0; }
      }
      if (!blocks.length) return;
      ensurePool(grid.pool.length);
      // Upload the voxel arrays the GPU does not have.
      const uploads = blocks.map((b) => grid.slot[b]).filter((slot) => stale.has(slot));
      if (uploads.length) {
        const words = new Uint32Array(WORDS);
        for (const slot of uploads) {
          packSlot(grid.pool[slot], words, 0);
          device.queue.writeBuffer(poolBuffer, slot * WORDS * 4, words);
          stale.delete(slot);
        }
      }
      // Ops, pieces, items and per-item op lists.
      const opData = new ArrayBuffer(ops.length * OP_FLOATS * 4);
      const opFloats = new Float32Array(opData);
      const opInts = new Uint32Array(opData);
      const pieceStart = new Map();
      const pieceList = [];
      ops.forEach((op, o) => {
        let start = pieceStart.get(op.pieces);
        if (start === undefined) {
          start = pieceList.length;
          pieceStart.set(op.pieces, start);
          pieceList.push(...op.pieces);
        }
        const sw = op.sw;
        const base = o * OP_FLOATS;
        opFloats[base] = sw.ax - grid.ox; opFloats[base + 1] = sw.ay - grid.oy; opFloats[base + 2] = sw.az - grid.oz;
        opFloats[base + 4] = sw.dx; opFloats[base + 5] = sw.dy; opFloats[base + 6] = sw.dz;
        opFloats[base + 8] = sw.ux; opFloats[base + 9] = sw.uy; opFloats[base + 10] = sw.uz;
        opFloats[base + 12] = sw.vv; opFloats[base + 13] = sw.vu; opFloats[base + 14] = sw.A; opFloats[base + 15] = op.kind === "count" ? 1 : 0;
        opInts[base + 16] = start; opInts[base + 17] = op.pieces.length;
      });
      const pieceData = new Float32Array(Math.max(1, pieceList.length) * 8);
      pieceList.forEach((piece, n) => {
        const base = n * 8;
        if (piece.kind === "sphere") {
          pieceData[base] = 1; pieceData[base + 5] = piece.c; pieceData[base + 6] = piece.r * piece.r;
        } else {
          const span = piece.s1 - piece.s0;
          pieceData[base + 1] = piece.s0; pieceData[base + 2] = piece.s1; pieceData[base + 3] = piece.r0;
          pieceData[base + 4] = span > 1e-12 ? (piece.r1 - piece.r0) / span : 0;
        }
      });
      const itemData = new ArrayBuffer(blocks.length * 32);
      const itemFloats = new Float32Array(itemData);
      const itemInts = new Int32Array(itemData);
      const indexList = [];
      blocks.forEach((b, n) => {
        const bx = b % grid.nbx, by = Math.floor(b / grid.nbx) % grid.nby, bz = Math.floor(b / (grid.nbx * grid.nby));
        const base = n * 8;
        itemFloats[base] = ((bx << 4) + 0.5) * grid.cell;
        itemFloats[base + 1] = ((by << 4) + 0.5) * grid.cell;
        itemFloats[base + 2] = ((bz << 4) + 0.5) * grid.cell;
        itemInts[base + 4] = grid.slot[b];
        const ts = target ? target.state[b] : V.B_AIR;
        itemInts[base + 5] = ts === V.B_MAT ? -2 : ts === V.B_MIX ? target.slot[b] : -1;
        const list = byBlock.get(b);
        itemInts[base + 6] = indexList.length;
        itemInts[base + 7] = list.length;
        for (const o of list) indexList.push(o);
      });
      const indexData = Uint32Array.from(indexList.length ? indexList : [0]);
      const counterBytes = ops.length * 3 * 4;
      const changedBytes = blocks.length * 4;
      const make = (data, usage) => {
        const buffer = device.createBuffer({ size: Math.max(16, data.byteLength), usage });
        device.queue.writeBuffer(buffer, 0, data);
        return buffer;
      };
      const opBuffer = make(opFloats, GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST);
      const pieceBuffer = make(pieceData, GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST);
      const itemBuffer = make(itemFloats, GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST);
      const indexBuffer = make(indexData, GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST);
      const counterBuffer = make(new Uint32Array(ops.length * 3), STORAGE);
      const changedBuffer = make(new Uint32Array(blocks.length), STORAGE);
      const width = Math.min(blocks.length, MAX_WIDTH);
      const paramData = new ArrayBuffer(16);
      new Float32Array(paramData, 0, 1)[0] = grid.cell;
      new Uint32Array(paramData, 4, 3).set([blocks.length, MAX_WIDTH, 0]);
      const paramBuffer = make(new Uint8Array(paramData), GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
      const bind = device.createBindGroup({
        layout: pipeline.getBindGroupLayout(0),
        entries: [poolBuffer, opBuffer, pieceBuffer, itemBuffer, indexBuffer, counterBuffer, targetBuffer, paramBuffer, changedBuffer]
          .map((buffer, binding) => ({ binding, resource: { buffer } }))
      });
      const blockBytes = WORDS * 4;
      const readBytes = counterBytes + changedBytes + blocks.length * blockBytes;
      const read = device.createBuffer({ size: readBytes, usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST });
      const encoder = device.createCommandEncoder();
      const pass = encoder.beginComputePass();
      pass.setPipeline(pipeline);
      pass.setBindGroup(0, bind);
      pass.dispatchWorkgroups(width, Math.ceil(blocks.length / MAX_WIDTH));
      pass.end();
      encoder.copyBufferToBuffer(counterBuffer, 0, read, 0, counterBytes);
      encoder.copyBufferToBuffer(changedBuffer, 0, read, counterBytes, changedBytes);
      blocks.forEach((b, n) => encoder.copyBufferToBuffer(poolBuffer, grid.slot[b] * blockBytes, read, counterBytes + changedBytes + n * blockBytes, blockBytes));
      device.queue.submit([encoder.finish()]);
      try {
        await read.mapAsync(GPUMapMode.READ);
        const mapped = read.getMappedRange();
        const counters = new Uint32Array(mapped, 0, ops.length * 3);
        const flags = new Uint32Array(mapped, counterBytes, blocks.length);
        const voxels = new Uint32Array(mapped, counterBytes + changedBytes);
        ops.forEach((op, o) => {
          if (op.kind === "count") op.result = counters[o * 3 + 2];
          else {
            op.removed = counters[o * 3];
            op.gouged = counters[o * 3 + 1];
            grid.removed += op.removed;
            grid.gouged += op.gouged;
          }
        });
        blocks.forEach((b, n) => {
          if (!flags[n]) return;
          const data = grid.pool[grid.slot[b]];
          let material = 0;
          const base = n * WORDS;
          for (let word = 0; word < WORDS; word += 1) {
            const value = voxels[base + word];
            const at = word * 16;
            for (let bit = 0; bit < 16; bit += 1) {
              const v = (value >>> (bit * 2)) & 3;
              data[at + bit] = v;
              if (v === V.MAT) material += 1;
            }
          }
          grid.mat[b] = material;
          V.markDirty(grid, b);
          const slot = grid.slot[b];
          V.compact(grid, b);
          // A block that became uniform freed its slot; the slot's next owner is uploaded again.
          if (grid.slot[b] !== slot) stale.add(slot);
        });
        read.unmap();
      } finally {
        for (const buffer of [opBuffer, pieceBuffer, itemBuffer, indexBuffer, counterBuffer, changedBuffer, paramBuffer, read]) buffer.destroy();
      }
    }

    const backend = {
      execute,
      invalidate,
      maxOps: Number(options && options.maxOps) > 0 ? Number(options.maxOps) : 4096,
      info: `${adapterInfo.vendor || "GPU"} ${adapterInfo.architecture || ""}`.trim(),
      destroy() {
        poolBuffer?.destroy();
        targetBuffer?.destroy();
        device.destroy();
      }
    };
    return { backend };
  }

  // Runs a test scene on the CPU and on the GPU and compares the stocks voxel by voxel. Resolves
  // { ok, mismatch, removed, ms } (mismatch: voxels that differ, as a share of the voxels removed).
  async function selfCheck(options) {
    const scene = () => {
      const g = V.createStock({ minX: 0, maxX: 40, minY: 0, maxY: 40, minZ: -20, maxZ: 0 }, 0.4);
      const ops = [];
      const end = V.profileFromDefinition({ radius: 4, type: "end-mill", length: 40 });
      const ball = V.profileFromDefinition({ radius: 3, type: "ball-mill", length: 40 });
      const bull = V.profileFromDefinition({ radius: 4, type: "bull-nose-mill", cornerRadius: 1.5, length: 40 });
      const drill = V.profileFromDefinition({ radius: 2.5, type: "drill", length: 40 });
      // Off the voxel lattice, so few voxel centres lie exactly on a tool surface (where float32 and
      // float64 may decide differently).
      const jitter = (point) => ({ x: point.x + 0.0137, y: point.y - 0.0091, z: point.z + 0.0063 });
      const add = (profile, points, axes) => {
        for (const s of V.motionSweeps(points.map(jitter), axes, profile.reach, g.cell / 2)) ops.push({ kind: "cut", sw: V.sweep(s.a, s.b, s.u), pieces: profile.cutting });
      };
      for (let n = 0; n < 6; n += 1) add(end, [{ x: -5, y: 4 + n * 5, z: -2 - n * 0.7 }, { x: 45, y: 4 + n * 5, z: -2 - n * 0.7 }]);
      add(ball, [{ x: 5, y: 5, z: -3 }, { x: 35, y: 35, z: -9 }]);
      add(bull, [{ x: 10, y: -5, z: -12 }, { x: 10, y: 45, z: -12 }], { x: Math.SQRT1_2, y: 0, z: Math.SQRT1_2 });
      add(drill, [{ x: 30, y: 10, z: 5 }, { x: 30, y: 10, z: -15 }]);
      add(end, [{ x: 5, y: 20, z: -6 }, { x: 35, y: 20, z: -6 }], [{ x: 0, y: 0, z: 1 }, { x: 1, y: 0, z: 0 }]);
      // A count op (a holder) after the cuts: must only see the material still there.
      ops.push({ kind: "count", sw: V.sweep(jitter({ x: 36, y: 36, z: -4 }), jitter({ x: 39, y: 36, z: -4 }), { x: 0, y: 0, z: 1 }), pieces: end.cutting });
      return { g, ops };
    };
    const started = Date.now();
    const cpu = scene();
    for (const op of cpu.ops) {
      if (op.kind === "count") op.result = V.countSweep(cpu.g, op.sw, op.pieces, 0);
      else op.removed = V.removeSweep(cpu.g, op.sw, op.pieces, null);
    }
    const gpu = scene();
    const made = await create(gpu.g, null, options);
    if (made.error) return { ok: false, error: made.error };
    try {
      await made.backend.execute(gpu.ops);
    } catch (error) {
      made.backend.destroy();
      return { ok: false, error: error.message };
    }
    let differ = 0;
    for (let k = 0; k < cpu.g.nz; k += 1) {
      for (let j = 0; j < cpu.g.ny; j += 1) {
        for (let i = 0; i < cpu.g.nx; i += 1) if (V.voxel(cpu.g, i, j, k) !== V.voxel(gpu.g, i, j, k)) differ += 1;
      }
    }
    const removed = Math.max(1, cpu.g.removed);
    const countCpu = cpu.ops[cpu.ops.length - 1].result;
    const countGpu = gpu.ops[gpu.ops.length - 1].result;
    made.backend.destroy();
    const mismatch = differ / removed;
    return {
      ok: mismatch <= 0.002 && Math.abs(countCpu - countGpu) <= Math.max(2, countCpu * 0.01),
      mismatch, removed: cpu.g.removed, gpuRemoved: gpu.g.removed, countCpu, countGpu, ms: Date.now() - started, info: made.backend.info
    };
  }

  return { create, selfCheck, SHADER };
});
