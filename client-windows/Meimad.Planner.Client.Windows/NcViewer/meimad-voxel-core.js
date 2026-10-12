/*
 * Meimad Planner NC viewer: the 3D stock simulation core (pure functions, no DOM). The stock is a real
 * 3D volume: a sparse voxel grid of 16³ blocks (uniform blocks hold no voxel array), each voxel
 * material, air outside the original stock, or cut by this program. Tools are parametric solids of
 * revolution built from the Tool Room's drawing (ToolShapeGeometry segments) or from the tool table:
 * a cutting profile removes material, the whole assembly (shank, holder, flange) is the collision
 * profile. A motion is cut as exact translation sweeps of convex tool pieces (the swept volume of a
 * convex solid moved along a straight line is convex), so long moves cannot tunnel; a turning tool
 * axis is split into sub-sweeps short enough that no tool point moves more than half a cell between
 * orientations. Loaded by the page, the material worker (importScripts) and the V8 test host.
 *
 * Coordinates are millimetres in the part frame (the frame of the toolpath points). The tool axis
 * points from the tip toward the spindle; s is the distance from the tip along it.
 */
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory();
  else root.MeimadVoxel = factory();
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  const BS = 16;            // voxels per block edge
  const BS3 = BS * BS * BS;
  const BR = 4;             // voxels per brick edge (bricks subdivide a block for the sweep tests)
  const AIR = 0;            // outside the original stock
  const MAT = 1;            // material
  const CUT = 2;            // removed by this program
  const B_AIR = 0, B_MAT = 1, B_CUT = 2, B_MIX = 3; // block states: uniform air / material / cut, or mixed (has a voxel array)
  const EPS = 1e-9;
  const DEG = Math.PI / 180;

  // ----- grid ------------------------------------------------------------------------------------

  // A grid covering `bounds` ({minX..maxZ}, mm) with `cell` mm voxels and one block of air around it,
  // so every surface closes. `limitBlocks` caps the block count (the resolution is raised to fit).
  function createGrid(bounds, cell, limitBlocks) {
    let size = Math.max(0.01, Number(cell) || 1);
    const spanX = Math.max(EPS, bounds.maxX - bounds.minX);
    const spanY = Math.max(EPS, bounds.maxY - bounds.minY);
    const spanZ = Math.max(EPS, bounds.maxZ - bounds.minZ);
    const limit = Number(limitBlocks) > 0 ? Number(limitBlocks) : 4000000;
    let nbx, nby, nbz;
    for (;;) {
      nbx = Math.ceil(spanX / (size * BS)) + 2;
      nby = Math.ceil(spanY / (size * BS)) + 2;
      nbz = Math.ceil(spanZ / (size * BS)) + 2;
      if (nbx * nby * nbz <= limit) break;
      size *= 1.25;
    }
    const nb = nbx * nby * nbz;
    return {
      cell: size,
      ox: bounds.minX - BS * size,
      oy: bounds.minY - BS * size,
      oz: bounds.minZ - BS * size,
      nbx, nby, nbz,
      nx: nbx * BS, ny: nby * BS, nz: nbz * BS,
      state: new Uint8Array(nb),
      slot: new Int32Array(nb).fill(-1),
      pool: [],
      free: [],
      dirty: new Uint8Array(nb),
      dirtyList: [],
      // Material voxels per mixed block (a mixed block with none is skipped by the sweeps).
      mat: new Int32Array(nb)
    };
  }

  const blockIndex = (g, bx, by, bz) => (bz * g.nby + by) * g.nbx + bx;

  function allocate(g, b, fill) {
    let slot = g.free.length ? g.free.pop() : -1;
    if (slot < 0) {
      slot = g.pool.length;
      g.pool.push(new Uint8Array(BS3));
    }
    g.pool[slot].fill(fill);
    g.mat[b] = fill === MAT ? BS3 : 0;
    g.slot[b] = slot;
    g.state[b] = B_MIX;
    return g.pool[slot];
  }

  function release(g, b, state) {
    const slot = g.slot[b];
    if (slot >= 0) g.free.push(slot);
    g.slot[b] = -1;
    g.state[b] = state;
  }

  // A mixed block whose voxels became uniform goes back to a uniform state.
  function compact(g, b) {
    if (g.state[b] !== B_MIX) return;
    const data = g.pool[g.slot[b]];
    const first = data[0];
    for (let index = 1; index < BS3; index += 1) if (data[index] !== first) return;
    release(g, b, first === MAT ? B_MAT : first === CUT ? B_CUT : B_AIR);
  }

  function markDirty(g, b) {
    if (g.dirty[b]) return;
    g.dirty[b] = 1;
    g.dirtyList.push(b);
  }

  function takeDirty(g) {
    const list = g.dirtyList;
    g.dirtyList = [];
    for (const b of list) g.dirty[b] = 0;
    return list;
  }

  // The voxel value at grid coordinates (outside the grid: air).
  function voxel(g, i, j, k) {
    if (i < 0 || j < 0 || k < 0 || i >= g.nx || j >= g.ny || k >= g.nz) return AIR;
    const b = blockIndex(g, i >> 4, j >> 4, k >> 4);
    const state = g.state[b];
    if (state === B_MIX) return g.pool[g.slot[b]][((k & 15) * BS + (j & 15)) * BS + (i & 15)];
    return state === B_MAT ? MAT : state === B_CUT ? CUT : AIR;
  }

  // ----- filling a grid from column intervals ----------------------------------------------------

  // Fills material from `columns(x, y)`: sorted [lo, hi, lo, hi, ...] z intervals (or null) of the
  // vertical line through the voxel centres at (x, y). A voxel is material when its centre lies inside.
  function fillColumns(g, columns) {
    const cell = g.cell;
    const ranges = new Array(BS * BS);
    for (let by = 0; by < g.nby; by += 1) {
      for (let bx = 0; bx < g.nbx; bx += 1) {
        let any = false;
        for (let ly = 0; ly < BS; ly += 1) {
          const y = g.oy + ((by << 4) + ly + 0.5) * cell;
          for (let lx = 0; lx < BS; lx += 1) {
            const x = g.ox + ((bx << 4) + lx + 0.5) * cell;
            const list = columns(x, y);
            // As voxel k ranges: [k0, k1) per interval.
            let kr = null;
            if (list && list.length) {
              kr = [];
              for (let p = 0; p + 1 < list.length; p += 2) {
                const k0 = Math.max(0, Math.ceil((list[p] - g.oz) / cell - 0.5));
                const k1 = Math.min(g.nz, Math.floor((list[p + 1] - g.oz) / cell - 0.5) + 1);
                if (k1 > k0) kr.push(k0, k1);
              }
              if (kr.length) any = true;
              else kr = null;
            }
            ranges[ly * BS + lx] = kr;
          }
        }
        if (!any) continue;
        for (let bz = 0; bz < g.nbz; bz += 1) {
          const z0 = bz << 4;
          const z1 = z0 + BS;
          let full = true;
          let none = true;
          for (let c = 0; c < BS * BS && (full || none); c += 1) {
            const kr = ranges[c];
            let covered = 0;
            if (kr) {
              for (let p = 0; p < kr.length; p += 2) {
                const a = Math.max(z0, kr[p]);
                const e = Math.min(z1, kr[p + 1]);
                if (e > a) covered += e - a;
              }
            }
            if (covered < BS) full = false;
            if (covered > 0) none = false;
          }
          const b = blockIndex(g, bx, by, bz);
          if (none) continue;
          if (full) {
            if (g.state[b] === B_MIX) release(g, b, B_MAT);
            else g.state[b] = B_MAT;
            continue;
          }
          const data = g.state[b] === B_MIX ? g.pool[g.slot[b]] : allocate(g, b, g.state[b] === B_MAT ? MAT : AIR);
          for (let c = 0; c < BS * BS; c += 1) {
            const kr = ranges[c];
            if (!kr) continue;
            const ly = (c / BS) | 0;
            const lx = c % BS;
            for (let p = 0; p < kr.length; p += 2) {
              const a = Math.max(z0, kr[p]);
              const e = Math.min(z1, kr[p + 1]);
              for (let k = a; k < e; k += 1) data[((k - z0) * BS + ly) * BS + lx] = MAT;
            }
          }
          let material = 0;
          for (let index = 0; index < BS3; index += 1) if (data[index] === MAT) material += 1;
          g.mat[b] = material;
          compact(g, b);
        }
      }
    }
  }

  // ----- stock ------------------------------------------------------------------------------------

  // stock: { minX..maxZ, cylinder?: { cx, cy, radius, innerRadius } } (a box or a round blank), or
  // { positions } (a closed STL in the part frame). `extent` widens the grid (fixtures, part, toolpath).
  function createStock(stock, resolution, extent, limitBlocks) {
    const box = stock.positions ? bounds(stock.positions) : stock;
    if (!box) throw new Error("The stock has no geometry.");
    const all = union(box, extent);
    const g = createGrid(all, resolution, limitBlocks);
    g.kind = "voxel";
    g.stockBox = { minX: box.minX, maxX: box.maxX, minY: box.minY, maxY: box.maxY, minZ: box.minZ, maxZ: box.maxZ };
    if (stock.positions) {
      fillColumns(g, meshColumns(stock.positions, g));
    } else if (stock.cylinder && stock.cylinder.radius > 0) {
      const { cx, cy, radius } = stock.cylinder;
      const inner = Math.max(0, stock.cylinder.innerRadius || 0);
      fillColumns(g, (x, y) => {
        const d = Math.hypot(x - cx, y - cy);
        return d <= radius && d >= inner ? [box.minZ, box.maxZ] : null;
      });
    } else {
      fillColumns(g, (x, y) => (x >= box.minX && x <= box.maxX && y >= box.minY && y <= box.maxY ? [box.minZ, box.maxZ] : null));
    }
    g.initialMaterial = countMaterial(g);
    g.removed = 0;
    g.gouged = 0;
    takeDirty(g);
    return g;
  }

  // A solid mask on the same lattice as `g` (fixtures, the finished part): 1 inside the closed mesh.
  function createMask(g, positions) {
    const m = {
      cell: g.cell, ox: g.ox, oy: g.oy, oz: g.oz, nbx: g.nbx, nby: g.nby, nbz: g.nbz, nx: g.nx, ny: g.ny, nz: g.nz,
      state: new Uint8Array(g.state.length), slot: new Int32Array(g.state.length).fill(-1), pool: [], free: [],
      dirty: new Uint8Array(g.state.length), dirtyList: [], mat: new Int32Array(g.state.length)
    };
    if (positions && positions.length >= 9) fillColumns(m, meshColumns(positions, m));
    m.dirtyList = [];
    m.dirty.fill(0);
    return m;
  }

  // Column crossings of a closed triangle mesh: the z of every triangle crossing each voxel-centre line.
  function meshColumns(positions, g) {
    const cell = g.cell;
    const crossings = new Map();
    for (let t = 0; t + 8 < positions.length; t += 9) {
      const ax = positions[t], ay = positions[t + 1], az = positions[t + 2];
      const bx = positions[t + 3], by = positions[t + 4], bz = positions[t + 5];
      const cx = positions[t + 6], cy = positions[t + 7], cz = positions[t + 8];
      const det = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
      if (Math.abs(det) < 1e-14) continue;
      const i0 = Math.max(0, Math.ceil((Math.min(ax, bx, cx) - g.ox) / cell - 0.5));
      const i1 = Math.min(g.nx - 1, Math.floor((Math.max(ax, bx, cx) - g.ox) / cell - 0.5));
      const j0 = Math.max(0, Math.ceil((Math.min(ay, by, cy) - g.oy) / cell - 0.5));
      const j1 = Math.min(g.ny - 1, Math.floor((Math.max(ay, by, cy) - g.oy) / cell - 0.5));
      for (let j = j0; j <= j1; j += 1) {
        const y = g.oy + (j + 0.5) * cell;
        for (let i = i0; i <= i1; i += 1) {
          const x = g.ox + (i + 0.5) * cell;
          const l0 = ((bx - x) * (cy - y) - (cx - x) * (by - y)) / det;
          const l1 = ((cx - x) * (ay - y) - (ax - x) * (cy - y)) / det;
          const l2 = 1 - l0 - l1;
          if (l0 < -1e-9 || l1 < -1e-9 || l2 < -1e-9) continue;
          const key = j * g.nx + i;
          let list = crossings.get(key);
          if (!list) crossings.set(key, (list = []));
          list.push(l0 * az + l1 * bz + l2 * cz);
        }
      }
    }
    return (x, y) => {
      const i = Math.round((x - g.ox) / cell - 0.5);
      const j = Math.round((y - g.oy) / cell - 0.5);
      const list = crossings.get(j * g.nx + i);
      if (!list || list.length < 2) return null;
      list.sort((p, q) => p - q);
      const z = [];
      for (const value of list) if (!z.length || value - z[z.length - 1] > 1e-6) z.push(value);
      if (z.length < 2) return null;
      if (z.length % 2) return [z[0], z[z.length - 1]]; // an open or touching mesh: its full extent
      return z;
    };
  }

  function countMaterial(g) {
    let count = 0;
    for (let b = 0; b < g.state.length; b += 1) {
      const state = g.state[b];
      if (state === B_MAT) count += BS3;
      else if (state === B_MIX) {
        const data = g.pool[g.slot[b]];
        for (let index = 0; index < BS3; index += 1) if (data[index] === MAT) count += 1;
      }
    }
    return count;
  }

  function bounds(positions) {
    if (!positions || positions.length < 9) return undefined;
    const box = { minX: Infinity, maxX: -Infinity, minY: Infinity, maxY: -Infinity, minZ: Infinity, maxZ: -Infinity };
    for (let index = 0; index + 2 < positions.length; index += 3) {
      const x = positions[index], y = positions[index + 1], z = positions[index + 2];
      if (x < box.minX) box.minX = x;
      if (x > box.maxX) box.maxX = x;
      if (y < box.minY) box.minY = y;
      if (y > box.maxY) box.maxY = y;
      if (z < box.minZ) box.minZ = z;
      if (z > box.maxZ) box.maxZ = z;
    }
    return box;
  }

  function union(box, extent) {
    if (!extent) return box;
    return {
      minX: Math.min(box.minX, extent.minX), maxX: Math.max(box.maxX, extent.maxX),
      minY: Math.min(box.minY, extent.minY), maxY: Math.max(box.maxY, extent.maxY),
      minZ: Math.min(box.minZ, extent.minZ), maxZ: Math.max(box.maxZ, extent.maxZ)
    };
  }

  // ----- tool profiles ----------------------------------------------------------------------------

  // A tool is a stack of convex pieces of revolution along its axis (s from the tip):
  //   { kind: "frustum", s0, s1, r0, r1 }  radius r0 at s0 to r1 at s1 (cylinder, cone, chamfer)
  //   { kind: "sphere", c, r }             a ball centred c along the axis
  // profile = { cutting: [pieces], collision: [pieces], radius, length, notes: [text], estimated }

  const frustum = (s0, s1, r0, r1) => ({ kind: "frustum", s0: Math.min(s0, s1), s1: Math.max(s0, s1), r0: s0 <= s1 ? r0 : r1, r1: s0 <= s1 ? r1 : r0 });
  const sphere = (c, r) => ({ kind: "sphere", c, r });

  // A bull-nose corner (radius rc at the tip of a tool of radius R) as frusta inscribed in the torus.
  function cornerPieces(radius, corner, s0) {
    const pieces = [];
    const rc = Math.min(corner, radius);
    const flat = radius - rc;
    const steps = 6;
    for (let n = 0; n < steps; n += 1) {
      const a0 = (n / steps) * Math.PI / 2;
      const a1 = ((n + 1) / steps) * Math.PI / 2;
      // Angle from the bottom: s = rc - rc cos(a), r = flat + rc sin(a).
      pieces.push(frustum(s0 + rc - rc * Math.cos(a0), s0 + rc - rc * Math.cos(a1), flat + rc * Math.sin(a0), flat + rc * Math.sin(a1)));
    }
    return pieces;
  }

  // The profile from the tool table / engine definition (no Tool Room drawing): the whole stick-out cuts
  // (its flute length is unknown) and there is no holder to check.
  function profileFromDefinition(tool) {
    const radius = Math.max(0, Number(tool.radius) || 0);
    const type = String(tool.type || "end-mill");
    const length = Number(tool.length) > 0 ? Number(tool.length) : Math.max(100, radius * 8);
    const notes = [];
    if (!(Number(tool.length) > 0)) notes.push(`length estimated ${round(length)} mm`);
    notes.push("flute length unknown: the whole stick-out cuts");
    notes.push("holder unknown: no holder collision check");
    const cutting = [];
    if (radius <= 0) {
      notes.push("no diameter");
    } else if (type === "ball-mill") {
      cutting.push(sphere(radius, radius), frustum(radius, length, radius, radius));
    } else if (type === "bull-nose-mill" && tool.cornerRadius > 0) {
      const rc = Math.min(tool.cornerRadius, radius);
      cutting.push(...cornerPieces(radius, rc, 0), frustum(rc, length, radius, radius));
    } else if (/drill|spot|center/.test(type)) {
      const angle = Number(tool.pointAngle) > 0 ? Number(tool.pointAngle) : 118;
      const height = radius / Math.tan(Math.min(179, Math.max(1, angle)) / 2 * DEG);
      cutting.push(frustum(0, height, 0, radius), frustum(height, length, radius, radius));
    } else if (type === "chamfer-mill" || type === "engraver" || type === "countersink") {
      const angle = Number(tool.taperAngle) > 0 ? Number(tool.taperAngle) : 45;
      const height = radius / Math.tan(Math.min(89, Math.max(1, angle)) * DEG);
      cutting.push(frustum(0, height, 0, radius), frustum(height, length, radius, radius));
    } else {
      cutting.push(frustum(0, length, radius, radius));
    }
    return finish({ cutting, collision: [], notes, estimated: true, source: "tool table" });
  }

  const CUTTING_KINDS = new Set(["CUTTER", "BALL", "POINT", "CONE", "DISC", "DOVETAIL", "BLADE", "INSERT"]);

  // The profile from the Tool Room's drawing (ToolShapeGeometry segments from the gauge line down:
  // { kind, top, height, diameter, tipAngle, cornerRadius, isDefault, topDiameter }, totalLength).
  // Segments above the gauge line (taper, pull stud) sit inside the spindle and are left out.
  function profileFromAssembly(assembly) {
    const total = Number(assembly.totalLength) || 0;
    const segments = (assembly.segments || []).map((segment) => ({
      kind: String(segment.kind || "").toUpperCase(),
      top: Number(segment.top) || 0,
      height: Math.max(0, Number(segment.height) || 0),
      radius: Math.max(0, (Number(segment.diameter) || 0) / 2),
      tipAngle: Number(segment.tipAngle) || 0,
      corner: Math.max(0, Number(segment.cornerRadius) || 0),
      isDefault: Boolean(segment.isDefault),
      label: segment.label || ""
    }));
    const cutting = [];
    const collision = [];
    const notes = [];
    let estimated = false;
    // The lowest cutting segment: cylinders below it (a centre drill's pilot) cut too.
    let lowestCutTop = Infinity;
    for (const segment of segments) if (CUTTING_KINDS.has(segment.kind)) lowestCutTop = Math.min(lowestCutTop, total - segment.top);
    for (const segment of segments) {
      if (segment.top < 0 || segment.kind === "GAP" || segment.kind === "TAPER" || segment.height <= 0) continue;
      const sTop = total - segment.top;
      const sBottom = sTop - segment.height;
      const r = segment.radius;
      if (r <= 0) continue;
      if (segment.isDefault) {
        estimated = true;
        notes.push(`${segment.label || segment.kind} size estimated`);
      }
      let pieces;
      switch (segment.kind) {
        case "BALL":
          pieces = [sphere(sBottom + r, r), frustum(sBottom + r, sTop, r, r)];
          break;
        case "POINT":
          pieces = [frustum(sBottom, sTop, 0, r)];
          break;
        case "CONE": {
          pieces = [frustum(sBottom, sTop, 0, r)];
          break;
        }
        case "DOVETAIL": {
          const angle = segment.tipAngle > 0 ? segment.tipAngle : 45;
          const narrow = Math.max(r * 0.3, r - segment.height * Math.tan(Math.min(80, angle) * DEG));
          pieces = [frustum(sBottom, sTop, r, narrow)];
          break;
        }
        case "SPHERE":
          pieces = [sphere(sBottom + Math.min(r, segment.height / 2), r)];
          break;
        case "CUTTER":
          pieces = segment.corner > 0 && sBottom <= 1e-6
            ? [...cornerPieces(r, segment.corner, sBottom), frustum(sBottom + Math.min(r, segment.corner), sTop, r, r)]
            : [frustum(sBottom, sTop, r, r)];
          break;
        default:
          pieces = [frustum(sBottom, sTop, r, r)];
          break;
      }
      const cuts = CUTTING_KINDS.has(segment.kind) || (segment.kind === "CYLINDER" && sTop <= lowestCutTop + 1e-6 && lowestCutTop < Infinity)
        || (segment.kind === "SPHERE" && assembly.shapeType !== "PROBE");
      (cuts ? cutting : collision).push(...pieces);
    }
    if (!cutting.length) notes.push("no cutting segment");
    if (!collision.length) notes.push("no holder or shank described");
    return finish({ cutting, collision, notes, estimated, source: "Tool Room" });
  }

  function finish(profile) {
    let radius = 0;
    let length = 0;
    let reach = 0;
    for (const piece of profile.cutting) {
      radius = Math.max(radius, pieceRadius(piece));
      length = Math.max(length, pieceTop(piece));
    }
    for (const piece of profile.collision) reach = Math.max(reach, pieceTop(piece));
    profile.radius = radius;
    profile.length = length;
    profile.reach = Math.max(length, reach);
    profile.collisionRadius = profile.collision.reduce((value, piece) => Math.max(value, pieceRadius(piece)), 0);
    return profile;
  }

  const pieceRadius = (piece) => (piece.kind === "sphere" ? piece.r : Math.max(piece.r0, piece.r1));
  const pieceTop = (piece) => (piece.kind === "sphere" ? piece.c + piece.r : piece.s1);
  const pieceBottom = (piece) => (piece.kind === "sphere" ? piece.c - piece.r : piece.s0);
  const round = (value) => Math.round(value * 1000) / 1000;

  // ----- sweeps -----------------------------------------------------------------------------------

  // A straight translation of the tool tip from a to b with a fixed axis u (unit, tip -> spindle).
  function sweep(a, b, u) {
    const dx = b.x - a.x, dy = b.y - a.y, dz = b.z - a.z;
    // The tool relative to a point moves by v = -d.
    const vx = -dx, vy = -dy, vz = -dz;
    const vv = vx * vx + vy * vy + vz * vz;
    const vu = vx * u.x + vy * u.y + vz * u.z;
    return { ax: a.x, ay: a.y, az: a.z, dx, dy, dz, vx, vy, vz, vv, vu, A: Math.max(0, vv - vu * vu), ux: u.x, uy: u.y, uz: u.z };
  }

  // Minimum of q(t) = p t² + l t + c over [lo, hi] is <= 0.
  function quadraticReaches(p, l, c, lo, hi) {
    if (hi < lo) return false;
    let best = Math.min(c + l * lo + p * lo * lo, c + l * hi + p * hi * hi);
    if (p > 1e-15) {
      const t = -l / (2 * p);
      if (t > lo && t < hi) best = Math.min(best, c + l * t + p * t * t);
    }
    return best <= 1e-12;
  }

  // Whether the point at w = p - a (wu = w·u, wv = w·v, ww = |w|²) lies inside the sweep of one piece.
  function insidePiece(sw, piece, wu, wv, ww) {
    if (piece.kind === "sphere") {
      const c = piece.c;
      const l = 2 * (wv - c * sw.vu);
      const k = ww - 2 * c * wu + c * c - piece.r * piece.r;
      return quadraticReaches(sw.vv, l, k, 0, 1);
    }
    // s(t) = wu + t vu must lie in [s0, s1].
    let lo = 0;
    let hi = 1;
    const vu = sw.vu;
    if (Math.abs(vu) < 1e-12) {
      if (wu < piece.s0 - 1e-9 || wu > piece.s1 + 1e-9) return false;
    } else {
      let t0 = (piece.s0 - wu) / vu;
      let t1 = (piece.s1 - wu) / vu;
      if (t0 > t1) { const swap = t0; t0 = t1; t1 = swap; }
      lo = Math.max(lo, t0);
      hi = Math.min(hi, t1);
      if (hi < lo) return false;
    }
    const span = piece.s1 - piece.s0;
    const k = span > 1e-12 ? (piece.r1 - piece.r0) / span : 0;
    // R(t) = ra + rb t, ρ²(t) = A t² + D t + F.
    const ra = piece.r0 + k * (wu - piece.s0);
    const rb = k * vu;
    const D = 2 * (wv - wu * vu);
    const F = Math.max(0, ww - wu * wu);
    return quadraticReaches(sw.A - rb * rb, D - 2 * ra * rb, F - ra * ra, lo, hi);
  }

  function insideAny(sw, pieces, x, y, z) {
    const wx = x - sw.ax, wy = y - sw.ay, wz = z - sw.az;
    const wu = wx * sw.ux + wy * sw.uy + wz * sw.uz;
    const wv = wx * sw.vx + wy * sw.vy + wz * sw.vz;
    const ww = wx * wx + wy * wy + wz * wz;
    for (let p = 0; p < pieces.length; p += 1) if (insidePiece(sw, pieces[p], wu, wv, ww)) return true;
    return false;
  }

  // Whether all 8 corners of the box [x0,x1]×[y0,y1]×[z0,z1] lie inside the sweep of one piece
  // (the sweep of a convex piece is convex, so the whole box is then inside).
  function boxInside(sw, pieces, x0, x1, y0, y1, z0, z1) {
    for (let p = 0; p < pieces.length; p += 1) {
      const piece = pieces[p];
      let all = true;
      for (let corner = 0; corner < 8 && all; corner += 1) {
        const x = corner & 1 ? x1 : x0;
        const y = corner & 2 ? y1 : y0;
        const z = corner & 4 ? z1 : z0;
        const wx = x - sw.ax, wy = y - sw.ay, wz = z - sw.az;
        if (!insidePiece(sw, piece, wx * sw.ux + wy * sw.uy + wz * sw.uz, wx * sw.vx + wy * sw.vy + wz * sw.vz, wx * wx + wy * wy + wz * wz)) all = false;
      }
      if (all) return true;
    }
    return false;
  }

  function segmentDistance(x, y, z, ax, ay, az, bx, by, bz) {
    const dx = bx - ax, dy = by - ay, dz = bz - az;
    const len = dx * dx + dy * dy + dz * dz;
    let t = len > 0 ? ((x - ax) * dx + (y - ay) * dy + (z - az) * dz) / len : 0;
    t = Math.max(0, Math.min(1, t));
    const rx = x - ax - t * dx, ry = y - ay - t * dy, rz = z - az - t * dz;
    return Math.sqrt(rx * rx + ry * ry + rz * rz);
  }

  // The s range and largest radius of a list of pieces.
  function extentOf(pieces) {
    let sLo = Infinity, sHi = -Infinity, r = 0;
    for (const piece of pieces) {
      sLo = Math.min(sLo, pieceBottom(piece));
      sHi = Math.max(sHi, pieceTop(piece));
      r = Math.max(r, pieceRadius(piece));
    }
    return { sLo, sHi, r };
  }

  // ----- applying a sweep -------------------------------------------------------------------------

  // The pieces of one sweep as flat numbers: [kind (0 frustum, 1 sphere), s0, s1, r0, slope, c, r²].
  function compilePieces(pieces) {
    const out = new Float64Array(pieces.length * 7);
    pieces.forEach((piece, n) => {
      const o = n * 7;
      if (piece.kind === "sphere") {
        out[o] = 1; out[o + 5] = piece.c; out[o + 6] = piece.r * piece.r;
      } else {
        const span = piece.s1 - piece.s0;
        out[o + 1] = piece.s0; out[o + 2] = piece.s1; out[o + 3] = piece.r0;
        out[o + 4] = span > 1e-12 ? (piece.r1 - piece.r0) / span : 0;
      }
    });
    return out;
  }

  // Whether the point with w = p - a (wu = w·u, wv = w·v, ww = |w|²) is inside the sweep of a compiled piece list.
  function insideCompiled(P, count, vu, A, vv, wu, wv, ww) {
    for (let p = 0; p < count; p += 1) {
      const o = p * 7;
      if (P[o] === 1) {
        const c = P[o + 5];
        if (quadraticReaches(vv, 2 * (wv - c * vu), ww - 2 * c * wu + c * c - P[o + 6], 0, 1)) return true;
        continue;
      }
      const s0 = P[o + 1], s1 = P[o + 2];
      let lo = 0, hi = 1;
      if (vu > -1e-12 && vu < 1e-12) {
        if (wu < s0 - 1e-9 || wu > s1 + 1e-9) continue;
      } else {
        let t0 = (s0 - wu) / vu;
        let t1 = (s1 - wu) / vu;
        if (t0 > t1) { const swap = t0; t0 = t1; t1 = swap; }
        if (t0 > lo) lo = t0;
        if (t1 < hi) hi = t1;
        if (hi < lo) continue;
      }
      const k = P[o + 4];
      const ra = P[o + 3] + k * (wu - s0);
      const rb = k * vu;
      const F = ww - wu * wu;
      if (quadraticReaches(A - rb * rb, 2 * (wv - wu * vu) - 2 * ra * rb, (F > 0 ? F : 0) - ra * ra, lo, hi)) return true;
    }
    return false;
  }

  // The axis sheet of a sweep (the parallelogram the tool axis segment [sLo, sHi] sweeps), precomputed.
  function sheetOf(sw, sLo, sHi) {
    const px = sw.ax + sw.ux * sLo, py = sw.ay + sw.uy * sLo, pz = sw.az + sw.uz * sLo;
    const ex = sw.ux * (sHi - sLo), ey = sw.uy * (sHi - sLo), ez = sw.uz * (sHi - sLo);
    const fx = sw.dx, fy = sw.dy, fz = sw.dz;
    const ee = ex * ex + ey * ey + ez * ez, ff = fx * fx + fy * fy + fz * fz, ef = ex * fx + ey * fy + ez * fz;
    const det = ee * ff - ef * ef;
    return { px, py, pz, ex, ey, ez, fx, fy, fz, ee, ff, ef, det, planar: det > 1e-12 * Math.max(1, ee * ff) };
  }

  function sheetDistance(S, x, y, z) {
    const qx = x - S.px, qy = y - S.py, qz = z - S.pz;
    if (S.planar) {
      const qe = qx * S.ex + qy * S.ey + qz * S.ez;
      const qf = qx * S.fx + qy * S.fy + qz * S.fz;
      const s = (qe * S.ff - qf * S.ef) / S.det;
      const t = (qf * S.ee - qe * S.ef) / S.det;
      if (s >= 0 && s <= 1 && t >= 0 && t <= 1) {
        const rx = qx - s * S.ex - t * S.fx, ry = qy - s * S.ey - t * S.fy, rz = qz - s * S.ez - t * S.fz;
        return Math.sqrt(rx * rx + ry * ry + rz * rz);
      }
    }
    return Math.min(
      segmentDistance(qx, qy, qz, 0, 0, 0, S.ex, S.ey, S.ez),
      segmentDistance(qx, qy, qz, S.fx, S.fy, S.fz, S.fx + S.ex, S.fy + S.ey, S.fz + S.ez),
      segmentDistance(qx, qy, qz, 0, 0, 0, S.fx, S.fy, S.fz),
      segmentDistance(qx, qy, qz, S.ex, S.ey, S.ez, S.ex + S.fx, S.ey + S.fy, S.ez + S.fz));
  }

  // Kept for callers outside the hot path.
  function axisSheetDistance(sw, sLo, sHi, x, y, z) {
    return sheetDistance(sheetOf(sw, sLo, sHi), x, y, z);
  }

  const MODE_COUNT = 0;
  const MODE_REMOVE = 1;

  // Removes (MODE_REMOVE) or counts (MODE_COUNT, stopping at `limit`) the material voxels of `g` inside
  // one sweep of `pieces`. Removal marks the changed blocks dirty and, with a target mask, counts the
  // removed voxels that belong to the finished part (gouges). Returns the voxel count.
  function applySweep(g, sw, pieces, mode, limit, target) {
    if (!pieces.length) return 0;
    const { sLo, sHi, r } = extentOf(pieces);
    const P = compilePieces(pieces);
    const n = pieces.length;
    const S = sheetOf(sw, sLo, sHi);
    const cell = g.cell;
    const vu = sw.vu, A = sw.A, vv = sw.vv;
    const ux = sw.ux, uy = sw.uy, uz = sw.uz, vx = sw.vx, vy = sw.vy, vz = sw.vz;
    const ax = sw.ax, ay = sw.ay, az = sw.az;
    const cap = mode === MODE_COUNT && Number(limit) > 0 ? Number(limit) : Infinity;
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity, minZ = Infinity, maxZ = -Infinity;
    for (let corner = 0; corner < 4; corner += 1) {
      const s = corner & 1 ? sHi : sLo;
      const t = corner & 2 ? 1 : 0;
      const x = ax + sw.dx * t + ux * s, y = ay + sw.dy * t + uy * s, z = az + sw.dz * t + uz * s;
      if (x < minX) minX = x; if (x > maxX) maxX = x;
      if (y < minY) minY = y; if (y > maxY) maxY = y;
      if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
    }
    const span = cell * BS;
    const bx0 = Math.max(0, Math.floor((minX - r - g.ox) / span)), bx1 = Math.min(g.nbx - 1, Math.floor((maxX + r - g.ox) / span));
    const by0 = Math.max(0, Math.floor((minY - r - g.oy) / span)), by1 = Math.min(g.nby - 1, Math.floor((maxY + r - g.oy) / span));
    const bz0 = Math.max(0, Math.floor((minZ - r - g.oz) / span)), bz1 = Math.min(g.nbz - 1, Math.floor((maxZ + r - g.oz) / span));
    const blockHalf = Math.sqrt(3) * cell * (BS - 1) / 2;
    const brickHalf = Math.sqrt(3) * cell * (BR - 1) / 2;
    let count = 0;
    let gouged = 0;
    const vertical = ux > -1e-9 && ux < 1e-9 && uy > -1e-9 && uy < 1e-9 && uz > 0;
    const dd2 = sw.dx * sw.dx + sw.dy * sw.dy;
    const r2 = r * r;
    // Whether every corner of a box is inside the sweep of one piece (convex: then the whole box is).
    const boxIn = (x0, x1, y0, y1, z0, z1) => {
      const cx = (x0 + x1) / 2 - ax, cy = (y0 + y1) / 2 - ay, cz = (z0 + z1) / 2 - az;
      if (!insideCompiled(P, n, vu, A, vv, cx * ux + cy * uy + cz * uz, cx * vx + cy * vy + cz * vz, cx * cx + cy * cy + cz * cz)) return false;
      for (let p = 0; p < n; p += 1) {
        const one = P.subarray(p * 7, p * 7 + 7);
        let all = true;
        for (let corner = 0; corner < 8 && all; corner += 1) {
          const wx = (corner & 1 ? x1 : x0) - ax, wy = (corner & 2 ? y1 : y0) - ay, wz = (corner & 4 ? z1 : z0) - az;
          if (!insideCompiled(one, 1, vu, A, vv, wx * ux + wy * uy + wz * uz, wx * vx + wy * vy + wz * vz, wx * wx + wy * wy + wz * wz)) all = false;
        }
        if (all) return true;
      }
      return false;
    };
    const targetAt = (b, index) => {
      const ts = target.state[b];
      return ts === B_MAT || (ts === B_MIX && target.pool[target.slot[b]][index] === MAT);
    };
    for (let bz = bz0; bz <= bz1; bz += 1) {
      for (let by = by0; by <= by1; by += 1) {
        for (let bx = bx0; bx <= bx1; bx += 1) {
          const b = (bz * g.nby + by) * g.nbx + bx;
          const state = g.state[b];
          if (state !== B_MIX && state !== B_MAT) continue;
          if (state === B_MIX && g.mat[b] === 0) continue;
          const x0 = g.ox + ((bx << 4) + 0.5) * cell, x1 = x0 + (BS - 1) * cell;
          const y0 = g.oy + ((by << 4) + 0.5) * cell, y1 = y0 + (BS - 1) * cell;
          const z0 = g.oz + ((bz << 4) + 0.5) * cell, z1 = z0 + (BS - 1) * cell;
          if (sheetDistance(S, (x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2) > r + blockHalf) continue;
          let data = state === B_MIX ? g.pool[g.slot[b]] : null;
          if (boxIn(x0, x1, y0, y1, z0, z1)) {
            if (!data) {
              count += BS3;
              if (mode === MODE_COUNT) { if (count >= cap) return count; continue; }
              if (target) {
                const ts = target.state[b];
                if (ts === B_MAT) gouged += BS3;
                else if (ts === B_MIX) { const td = target.pool[target.slot[b]]; for (let index = 0; index < BS3; index += 1) if (td[index] === MAT) gouged += 1; }
              }
              g.state[b] = B_CUT;
              markDirty(g, b);
              continue;
            }
            let here = 0;
            for (let index = 0; index < BS3; index += 1) {
              if (data[index] !== MAT) continue;
              here += 1;
              if (mode === MODE_REMOVE) {
                data[index] = CUT;
                if (target && targetAt(b, index)) gouged += 1;
              }
            }
            count += here;
            if (mode === MODE_COUNT && count >= cap) return count;
            if (here && mode === MODE_REMOVE) {
              g.mat[b] -= here;
              markDirty(g, b);
            }
            continue;
          }
          let touched = false;
          if (vertical) {
            // A vertical tool: a voxel column whose (x, y) never comes within rMax of the tip's path is
            // skipped whole, and the others are tested only over the z range the tool can reach there.
            for (let ly = 0; ly < BS; ly += 1) {
              const py = y0 + ly * cell - ay;
              for (let lx = 0; lx < BS; lx += 1) {
                const px = x0 + lx * cell - ax;
                // t where |p - t·d| <= rMax in the plane.
                let t0 = 0, t1 = 1;
                const pp = px * px + py * py;
                if (dd2 > 1e-18) {
                  const pd = px * sw.dx + py * sw.dy;
                  const disc = pd * pd - dd2 * (pp - r2);
                  if (disc < 0) continue;
                  const root = Math.sqrt(disc);
                  t0 = Math.max(0, (pd - root) / dd2);
                  t1 = Math.min(1, (pd + root) / dd2);
                  if (t1 < t0) continue;
                } else if (pp > r2) continue;
                const zA = az + Math.min(t0 * sw.dz, t1 * sw.dz) + sLo;
                const zB = az + Math.max(t0 * sw.dz, t1 * sw.dz) + sHi;
                const k0 = Math.max(0, Math.ceil((zA - z0) / cell - 1e-9));
                const k1 = Math.min(BS - 1, Math.floor((zB - z0) / cell + 1e-9));
                for (let lz = k0; lz <= k1; lz += 1) {
                  const index = (lz * BS + ly) * BS + lx;
                  if (data && data[index] !== MAT) continue;
                  const wz = z0 + lz * cell - az;
                  if (!insideCompiled(P, n, vu, A, vv, wz, px * vx + py * vy + wz * vz, pp + wz * wz)) continue;
                  count += 1;
                  if (mode === MODE_COUNT) { if (count >= cap) return count; continue; }
                  if (!data) data = allocate(g, b, MAT);
                  data[index] = CUT;
                  g.mat[b] -= 1;
                  touched = true;
                  if (target && targetAt(b, index)) gouged += 1;
                }
              }
            }
            if (touched) markDirty(g, b);
            continue;
          }
          for (let rz = 0; rz < BS; rz += BR) {
            for (let ry = 0; ry < BS; ry += BR) {
              for (let rx = 0; rx < BS; rx += BR) {
                const cx0 = x0 + rx * cell, cy0 = y0 + ry * cell, cz0 = z0 + rz * cell;
                const cx1 = cx0 + (BR - 1) * cell, cy1 = cy0 + (BR - 1) * cell, cz1 = cz0 + (BR - 1) * cell;
                // A brick of a mixed block with no material is skipped before any geometry.
                if (data) {
                  let any = false;
                  for (let lz = rz; lz < rz + BR && !any; lz += 1) {
                    for (let ly = ry; ly < ry + BR && !any; ly += 1) {
                      const row = (lz * BS + ly) * BS + rx;
                      if (data[row] === MAT || data[row + 1] === MAT || data[row + 2] === MAT || data[row + 3] === MAT) any = true;
                    }
                  }
                  if (!any) continue;
                }
                if (sheetDistance(S, (cx0 + cx1) / 2, (cy0 + cy1) / 2, (cz0 + cz1) / 2) > r + brickHalf) continue;
                const all = boxIn(cx0, cx1, cy0, cy1, cz0, cz1);
                for (let lz = rz; lz < rz + BR; lz += 1) {
                  const wz = z0 + lz * cell - az;
                  for (let ly = ry; ly < ry + BR; ly += 1) {
                    const wy = y0 + ly * cell - ay;
                    let wx = cx0 - ax;
                    const base = (lz * BS + ly) * BS;
                    for (let lx = rx; lx < rx + BR; lx += 1, wx += cell) {
                      const index = base + lx;
                      if (data && data[index] !== MAT) continue;
                      if (!all && !insideCompiled(P, n, vu, A, vv, wx * ux + wy * uy + wz * uz, wx * vx + wy * vy + wz * vz, wx * wx + wy * wy + wz * wz)) continue;
                      count += 1;
                      if (mode === MODE_COUNT) { if (count >= cap) return count; continue; }
                      if (!data) data = allocate(g, b, MAT);
                      data[index] = CUT;
                      g.mat[b] -= 1;
                      touched = true;
                      if (target && targetAt(b, index)) gouged += 1;
                    }
                  }
                }
              }
            }
          }
          if (touched) markDirty(g, b);
        }
      }
    }
    if (mode === MODE_REMOVE) {
      g.removed += count;
      if (gouged) g.gouged += gouged;
      g.lastGouged = gouged;
    }
    return count;
  }

  // Removes the material inside one sweep of the cutting pieces; with a target mask (the finished
  // part) the removed part voxels are counted as gouges (g.gouged, g.lastGouged). Returns the voxels removed.
  function removeSweep(g, sw, pieces, ctx) {
    return applySweep(g, sw, pieces, MODE_REMOVE, 0, ctx && ctx.target);
  }

  // Number of material voxels of `g` (stock or a mask) inside one sweep of `pieces`, stopping at `limit`.
  function countSweep(g, sw, pieces, limit) {
    return applySweep(g, sw, pieces, MODE_COUNT, limit, null);
  }

  // ----- motions ----------------------------------------------------------------------------------

  function normalize(v) {
    const length = Math.hypot(v.x, v.y, v.z);
    return length > 1e-12 ? { x: v.x / length, y: v.y / length, z: v.z / length } : { x: 0, y: 0, z: 1 };
  }

  function slerp(a, b, t) {
    if (t <= 0) return a;
    if (t >= 1) return b;
    const dot = Math.max(-1, Math.min(1, a.x * b.x + a.y * b.y + a.z * b.z));
    if (dot > 0.9999) return normalize({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t, z: a.z + (b.z - a.z) * t });
    if (dot < -0.9999) return t < 0.5 ? a : b;
    const theta = Math.acos(dot);
    const s = Math.sin(theta);
    const wa = Math.sin((1 - t) * theta) / s;
    const wb = Math.sin(t * theta) / s;
    return normalize({ x: wa * a.x + wb * b.x, y: wa * a.y + wb * b.y, z: wa * a.z + wb * b.z });
  }

  const UP = Object.freeze({ x: 0, y: 0, z: 1 });

  // The sweeps of one motion: points [{x,y,z}], axes per point (or one axis, or none = +Z). A piece
  // whose axis turns is split so no point of the tool (up to `reach` from the tip) moves more than
  // `maxStep` sideways between orientations (the orientation is interpolated on the great circle).
  // Returns [{ a, b, u }] with `samples` (poses visited) for the journal.
  function motionSweeps(points, axes, reach, maxStep) {
    const result = [];
    if (!points || !points.length) return result;
    const axisAt = (index) => {
      const value = Array.isArray(axes) ? axes[Math.min(index, axes.length - 1)] : axes;
      return value && Number.isFinite(value.x) && Number.isFinite(value.y) && Number.isFinite(value.z) ? normalize(value) : UP;
    };
    if (points.length === 1) {
      const u = axisAt(0);
      result.push({ a: points[0], b: points[0], u });
      return result;
    }
    for (let index = 1; index < points.length; index += 1) {
      const a = points[index - 1];
      const b = points[index];
      const ua = axisAt(index - 1);
      const ub = axisAt(index);
      const turn = Math.acos(Math.max(-1, Math.min(1, ua.x * ub.x + ua.y * ub.y + ua.z * ub.z)));
      const count = turn > 1e-9 ? Math.max(1, Math.ceil(turn * Math.max(1, reach) / Math.max(1e-6, maxStep))) : 1;
      for (let n = 0; n < count; n += 1) {
        const t0 = n / count;
        const t1 = (n + 1) / count;
        result.push({
          a: { x: a.x + (b.x - a.x) * t0, y: a.y + (b.y - a.y) * t0, z: a.z + (b.z - a.z) * t0 },
          b: { x: a.x + (b.x - a.x) * t1, y: a.y + (b.y - a.y) * t1, z: a.z + (b.z - a.z) * t1 },
          u: count === 1 ? (turn > 1e-9 ? slerp(ua, ub, 0.5) : ua) : slerp(ua, ub, (n + 0.5) / count)
        });
      }
    }
    return result;
  }

  // ----- checkpoints --------------------------------------------------------------------------------

  // A compact copy of the stock state (mixed blocks packed at 2 bits per voxel) to seek backwards.
  function snapshot(g) {
    const mixed = [];
    for (let b = 0; b < g.state.length; b += 1) if (g.state[b] === B_MIX) mixed.push(b);
    const packed = new Uint8Array(mixed.length * (BS3 / 4));
    mixed.forEach((b, n) => {
      const data = g.pool[g.slot[b]];
      const base = n * (BS3 / 4);
      for (let index = 0; index < BS3; index += 4) {
        packed[base + (index >> 2)] = data[index] | (data[index + 1] << 2) | (data[index + 2] << 4) | (data[index + 3] << 6);
      }
    });
    return { state: g.state.slice(), mixed: Int32Array.from(mixed), packed, removed: g.removed, gouged: g.gouged };
  }

  function restore(g, snap) {
    for (let b = 0; b < g.state.length; b += 1) if (g.state[b] === B_MIX) release(g, b, B_AIR);
    g.state.set(snap.state);
    for (let b = 0; b < g.state.length; b += 1) if (g.state[b] === B_MIX) g.state[b] = B_AIR; // set below
    snap.mixed.forEach((b, n) => {
      const data = allocate(g, b, 0);
      const base = n * (BS3 / 4);
      for (let index = 0; index < BS3; index += 4) {
        const byte = snap.packed[base + (index >> 2)];
        data[index] = byte & 3;
        data[index + 1] = (byte >> 2) & 3;
        data[index + 2] = (byte >> 4) & 3;
        data[index + 3] = (byte >> 6) & 3;
      }
      let material = 0;
      for (let index = 0; index < BS3; index += 1) if (data[index] === MAT) material += 1;
      g.mat[b] = material;
    });
    g.removed = snap.removed;
    g.gouged = snap.gouged;
    g.dirtyList = [];
    g.dirty.fill(0);
    for (let b = 0; b < g.state.length; b += 1) if (g.state[b] !== B_AIR) markDirty(g, b);
  }

  const snapshotBytes = (snap) => snap.state.length + snap.packed.length + snap.mixed.length * 4;

  return {
    BS, AIR, MAT, CUT, B_AIR, B_MAT, B_CUT, B_MIX,
    createGrid, createStock, createMask, fillColumns, meshColumns, countMaterial, bounds, union,
    blockIndex, voxel, takeDirty, markDirty, compact,
    profileFromDefinition, profileFromAssembly, frustum, sphere, extentOf,
    sweep, insideAny, insidePiece, boxInside, axisSheetDistance, sheetOf, sheetDistance, applySweep, removeSweep, countSweep,
    allocateBlock: allocate,
    motionSweeps, slerp, normalize,
    snapshot, restore, snapshotBytes
  };
});
