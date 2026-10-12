/*
 * Meimad Planner NC viewer: surfaces of the 3D voxel stock (meimad-voxel-core.js). The stock is
 * meshed in render chunks of 2×2×2 blocks (32³ voxels) with surface nets: one vertex per cell whose
 * corners differ (the mean of its crossing edge midpoints) and one quad per voxel edge between
 * material and anything else, so neighbouring chunks share their border vertices exactly and the
 * whole surface is closed. Normals come from the occupancy gradient over a 4³ neighbourhood (smooth
 * shading of curved cuts). Colours: the stock's own faces grey, faces this program cut by their depth
 * below the original stock box (cyan to violet), and in deviation view gouges red and material left
 * outside the finished part amber. A light ambient-occlusion term darkens concave corners.
 * Pure functions; loaded by the worker and the V8 test host.
 */
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory(require("./meimad-voxel-core.js"));
  else root.MeimadVoxelMesh = factory(root.MeimadVoxel);
})(typeof self !== "undefined" ? self : this, function (V) {
  "use strict";

  const BS = V.BS;
  const CHUNK = 2;              // blocks per render-chunk edge
  const CV = BS * CHUNK;        // voxels per render-chunk edge
  const PAD = 2;                // voxels read around a chunk (normals use a 4³ stencil)
  const LN = CV + 2 * PAD + 1;  // local array edge

  const toLinear = (value) => (value <= 0.04045 ? value / 12.92 : Math.pow((value + 0.055) / 1.055, 2.4));
  const STOCK = [toLinear(0.70), toLinear(0.72), toLinear(0.75)];
  const GOUGE = [toLinear(0.86), toLinear(0.16), toLinear(0.14)];
  const LEFT = [toLinear(0.95), toLinear(0.68), toLinear(0.18)];

  function hsl(h, s, l) {
    const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
    const p = 2 * l - q;
    const channel = (t) => {
      const x = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
      if (x < 1 / 6) return p + (q - p) * 6 * x;
      if (x < 1 / 2) return q;
      if (x < 2 / 3) return p + (q - p) * (2 / 3 - x) * 6;
      return p;
    };
    return [toLinear(channel(h + 1 / 3)), toLinear(channel(h)), toLinear(channel(h - 1 / 3))];
  }

  const chunkCounts = (g) => ({ cx: Math.ceil(g.nbx / CHUNK), cy: Math.ceil(g.nby / CHUNK), cz: Math.ceil(g.nbz / CHUNK) });
  const chunkKey = (g, cx, cy, cz) => {
    const n = chunkCounts(g);
    return (cz * n.cy + cy) * n.cx + cx;
  };

  // Render chunks touched by the given blocks (a block's voxels also shape the vertices of the chunks
  // around it, so its neighbours' chunks are included).
  function chunksOfBlocks(g, blocks) {
    const n = chunkCounts(g);
    const keys = new Set();
    for (const b of blocks) {
      const bx = b % g.nbx;
      const by = Math.floor(b / g.nbx) % g.nby;
      const bz = Math.floor(b / (g.nbx * g.nby));
      // A voxel shapes quads and vertices up to two voxels away, so the chunks of the neighbouring blocks.
      for (let dz = -1; dz <= 1; dz += 1) {
        for (let dy = -1; dy <= 1; dy += 1) {
          for (let dx = -1; dx <= 1; dx += 1) {
            const cx = Math.floor((bx + dx) / CHUNK);
            const cy = Math.floor((by + dy) / CHUNK);
            const cz = Math.floor((bz + dz) / CHUNK);
            if (cx < 0 || cy < 0 || cz < 0 || cx >= n.cx || cy >= n.cy || cz >= n.cz) continue;
            keys.add((cz * n.cy + cy) * n.cx + cx);
          }
        }
      }
    }
    return [...keys];
  }

  function allChunks(g) {
    const n = chunkCounts(g);
    const keys = [];
    for (let key = 0; key < n.cx * n.cy * n.cz; key += 1) keys.push(key);
    return keys;
  }

  // Whether a chunk (with its border) is uniformly one kind (all solid or all empty): no surface.
  function chunkUniform(g, cx, cy, cz) {
    let solid = -1;
    for (let bz = cz * CHUNK - 1; bz <= cz * CHUNK + CHUNK; bz += 1) {
      for (let by = cy * CHUNK - 1; by <= cy * CHUNK + CHUNK; by += 1) {
        for (let bx = cx * CHUNK - 1; bx <= cx * CHUNK + CHUNK; bx += 1) {
          let state = V.B_AIR;
          if (bx >= 0 && by >= 0 && bz >= 0 && bx < g.nbx && by < g.nby && bz < g.nbz) state = g.state[V.blockIndex(g, bx, by, bz)];
          if (state === V.B_MIX) return false;
          const value = state === V.B_MAT ? 1 : 0;
          if (solid < 0) solid = value;
          else if (solid !== value) return false;
        }
      }
    }
    return true;
  }

  // The voxel values of a chunk plus PAD voxels around it, into a reusable local array.
  let localValues = null;
  function gather(g, i0, j0, k0) {
    if (!localValues) localValues = new Uint8Array(LN * LN * LN);
    const values = localValues;
    for (let lk = 0; lk < LN; lk += 1) {
      const k = k0 - PAD + lk;
      for (let lj = 0; lj < LN; lj += 1) {
        const j = j0 - PAD + lj;
        const row = (lk * LN + lj) * LN;
        for (let li = 0; li < LN; li += 1) values[row + li] = V.voxel(g, i0 - PAD + li, j, k);
      }
    }
    return values;
  }

  function maskValue(mask, i, j, k) {
    return mask ? V.voxel(mask, i, j, k) === V.MAT : false;
  }

  // The mesh of one render chunk: { key, positions (Float32), normals (Int8, normalized), colors (Uint8,
  // normalized, linear), index (Uint16 or Uint32) } or null.
  // view: { deviation?: boolean, target?: mask, palette?: "depth" | "plain" }.
  function meshChunk(g, key, view) {
    const n = chunkCounts(g);
    const cx = key % n.cx;
    const cy = Math.floor(key / n.cx) % n.cy;
    const cz = Math.floor(key / (n.cx * n.cy));
    if (chunkUniform(g, cx, cy, cz)) return null;
    const i0 = cx * CV, j0 = cy * CV, k0 = cz * CV;
    const values = gather(g, i0, j0, k0);
    const at = (li, lj, lk) => values[(lk * LN + lj) * LN + li];
    const solid = (li, lj, lk) => values[(lk * LN + lj) * LN + li] === V.MAT;
    const cell = g.cell;
    const target = view && view.deviation ? view.target : null;
    const box = g.stockBox;
    const range = Math.max(1e-6, Math.min(box.maxX - box.minX, box.maxY - box.minY, box.maxZ - box.minZ) / 2);
    // Vertex index per cell (cells with min corner local li in [PAD-1, PAD+CV)).
    const CN = CV + 1;
    const cellIndex = new Int32Array(CN * CN * CN).fill(-1);
    const positions = [];
    const normals = [];
    const colors = [];
    const index = [];
    const vertexOf = (li, lj, lk) => {
      // li etc. are local voxel coordinates of the cell's min corner.
      const ci = li - (PAD - 1), cj = lj - (PAD - 1), ck = lk - (PAD - 1);
      const slot = (ck * CN + cj) * CN + ci;
      const existing = cellIndex[slot];
      if (existing >= 0) return existing;
      // Mean of the crossing edge midpoints.
      let sx = 0, sy = 0, sz = 0, count = 0;
      for (let e = 0; e < 12; e += 1) {
        const axis = e >> 2;
        const u = e & 1, w = (e >> 1) & 1;
        let ax, ay, az, bx, by, bz;
        if (axis === 0) { ax = 0; ay = u; az = w; bx = 1; by = u; bz = w; }
        else if (axis === 1) { ax = u; ay = 0; az = w; bx = u; by = 1; bz = w; }
        else { ax = u; ay = w; az = 0; bx = u; by = w; bz = 1; }
        if (solid(li + ax, lj + ay, lk + az) !== solid(li + bx, lj + by, lk + bz)) {
          sx += (ax + bx) / 2; sy += (ay + by) / 2; sz += (az + bz) / 2;
          count += 1;
        }
      }
      if (!count) { sx = 0.5; sy = 0.5; sz = 0.5; count = 1; }
      const gi = i0 - PAD + li, gj = j0 - PAD + lj, gk = k0 - PAD + lk;
      const x = g.ox + (gi + 0.5 + sx / count) * cell;
      const y = g.oy + (gj + 0.5 + sy / count) * cell;
      const z = g.oz + (gk + 0.5 + sz / count) * cell;
      // Normal: from material toward air over the 4³ stencil around the cell; ambient occlusion from
      // how much of that stencil is material.
      let nx = 0, ny = 0, nz = 0, filled = 0, cutNear = false, gouge = false, left = false;
      for (let dk = -1; dk <= 2; dk += 1) {
        for (let dj = -1; dj <= 2; dj += 1) {
          for (let di = -1; di <= 2; di += 1) {
            const value = at(li + di, lj + dj, lk + dk);
            if (value === V.MAT) {
              const wx = di - 0.5, wy = dj - 0.5, wz = dk - 0.5;
              const weight = 1 / (0.75 + wx * wx + wy * wy + wz * wz);
              nx -= wx * weight; ny -= wy * weight; nz -= wz * weight;
              filled += 1;
            }
            if (di >= 0 && di <= 1 && dj >= 0 && dj <= 1 && dk >= 0 && dk <= 1) {
              if (value === V.CUT) {
                cutNear = true;
                if (target && maskValue(target, gi + di, gj + dj, gk + dk)) gouge = true;
              } else if (value === V.MAT && target && !maskValue(target, gi + di, gj + dj, gk + dk)) {
                left = true;
              }
            }
          }
        }
      }
      const length = Math.hypot(nx, ny, nz) || 1;
      let color;
      if (gouge) color = GOUGE;
      else if (left) color = LEFT;
      else if (cutNear) {
        const depth = Math.max(0, Math.min(x - box.minX, box.maxX - x, y - box.minY, box.maxY - y, z - box.minZ, box.maxZ - z));
        const t = Math.min(1, depth / range);
        color = hsl((190 + 85 * t) / 360, 0.42, 0.74 - 0.1 * t);
      } else color = STOCK;
      // 64 stencil voxels: a flat face has about half of them material.
      const occlusion = Math.max(0.55, Math.min(1, 1.25 - filled / 64));
      const vertex = positions.length / 3;
      positions.push(x, y, z);
      normals.push(nx / length, ny / length, nz / length);
      colors.push(color[0] * occlusion, color[1] * occlusion, color[2] * occlusion);
      cellIndex[slot] = vertex;
      return vertex;
    };
    // Quads: every edge from voxel A=(i,j,k) to its +axis neighbour B where exactly one is material,
    // for A in this chunk.
    for (let lk = PAD; lk < PAD + CV; lk += 1) {
      for (let lj = PAD; lj < PAD + CV; lj += 1) {
        for (let li = PAD; li < PAD + CV; li += 1) {
          const a = solid(li, lj, lk);
          for (let axis = 0; axis < 3; axis += 1) {
            const bi = li + (axis === 0 ? 1 : 0), bj = lj + (axis === 1 ? 1 : 0), bk = lk + (axis === 2 ? 1 : 0);
            const b = solid(bi, bj, bk);
            if (a === b) continue;
            // The four cells around the edge, in the plane of the two other axes (cyclic order).
            let c00, c10, c11, c01;
            if (axis === 0) {
              c00 = vertexOf(li, lj - 1, lk - 1); c10 = vertexOf(li, lj, lk - 1); c11 = vertexOf(li, lj, lk); c01 = vertexOf(li, lj - 1, lk);
            } else if (axis === 1) {
              c00 = vertexOf(li - 1, lj, lk - 1); c10 = vertexOf(li - 1, lj, lk); c11 = vertexOf(li, lj, lk); c01 = vertexOf(li, lj, lk - 1);
            } else {
              c00 = vertexOf(li - 1, lj - 1, lk); c10 = vertexOf(li, lj - 1, lk); c11 = vertexOf(li, lj, lk); c01 = vertexOf(li - 1, lj, lk);
            }
            // Counter-clockwise seen from the air side.
            if (a) index.push(c00, c10, c11, c00, c11, c01);
            else index.push(c00, c11, c10, c00, c01, c11);
          }
        }
      }
    }
    if (!index.length) return null;
    // Normals and colours as normalized bytes (a quarter of the memory of floats; the 7 M triangle
    // stock of a long 5-axis program stays within reach of an office PC).
    const normalBytes = new Int8Array(normals.length);
    for (let n = 0; n < normals.length; n += 1) normalBytes[n] = Math.max(-127, Math.min(127, Math.round(normals[n] * 127)));
    const colorBytes = new Uint8Array(colors.length);
    for (let n = 0; n < colors.length; n += 1) colorBytes[n] = Math.max(0, Math.min(255, Math.round(colors[n] * 255)));
    const vertexCount = positions.length / 3;
    return {
      key,
      positions: Float32Array.from(positions),
      normals: normalBytes,
      colors: colorBytes,
      index: vertexCount < 65536 ? Uint16Array.from(index) : Uint32Array.from(index)
    };
  }

  // Flat triangle positions of the whole stock (STL export).
  function triangles(g) {
    const parts = [];
    let total = 0;
    for (const key of allChunks(g)) {
      const mesh = meshChunk(g, key, null);
      if (!mesh) continue;
      parts.push(mesh);
      total += mesh.index.length * 3;
    }
    const out = new Float32Array(total);
    let offset = 0;
    for (const mesh of parts) {
      for (let n = 0; n < mesh.index.length; n += 1) {
        const v = mesh.index[n] * 3;
        out[offset] = mesh.positions[v];
        out[offset + 1] = mesh.positions[v + 1];
        out[offset + 2] = mesh.positions[v + 2];
        offset += 3;
      }
    }
    return out;
  }

  return { CHUNK, CV, chunkCounts, chunkKey, chunksOfBlocks, allChunks, meshChunk, triangles };
});
