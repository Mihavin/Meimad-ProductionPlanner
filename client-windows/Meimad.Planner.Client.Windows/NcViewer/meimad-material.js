/*
 * Meimad Planner material removal core (pure functions, no DOM): a column stock for milling
 * (per grid point the topmost slab of material plus any undercut slabs below it; a vertical
 * tool lowers the top along its swept capsule, a tilted tool is stamped as a solid of
 * revolution along the motion) and an outer / inner radius profile per Z column for turning
 * (cut by the nose circle and the insert's body side), plus STL reading and writing. Loaded by
 * the page, by the simulation worker (importScripts) and by the V8 test host.
 */
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory();
  else root.MeimadMaterial = factory();
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  const EPSILON = 1e-9;
  // Marker for grid points without material; every real height is at or above the bottom.
  const EMPTY = -1e30;
  const isEmpty = (value) => value < -1e29;

  function clampResolution(value, span) {
    const fallback = Math.max(0.05, Math.min(5, span / 300));
    const resolution = Number(value);
    return Number.isFinite(resolution) && resolution > 0 ? Math.max(0.02, Math.min(10, resolution)) : fallback;
  }

  function emptyDirty() {
    return { min: Infinity, max: -Infinity };
  }

  function takeDirty(stock) {
    const dirty = stock.dirty;
    stock.dirty = emptyDirty();
    return Number.isFinite(dirty.min) ? dirty : undefined;
  }

  // ----- mill stock: columns of material ---------------------------------------------------

  // Every grid point holds one column of material: its topmost slab [base, top] (top = EMPTY for
  // points outside a round blank; a column cut through keeps a zero-thickness slab at the bottom)
  // and, below it, any slabs an undercut left behind (stock.extras: column index -> [lo, hi,
  // lo, hi, ...] ascending). A tool along +Z lowers the top exactly (2.5D); a tilted tool
  // (3+2 positions, simultaneous rotary moves, cutting from below) is stamped along its motion
  // as a solid of revolution whose intersection with every column is subtracted.

  const VERTICAL = 0.9999; // cos 0.8 degrees: closer to +Z than this counts as a vertical tool
  const DEGREE = Math.PI / 180;

  // stock: { minX, maxX, minY, maxY, minZ, maxZ, cylinder?: { cx, cy, radius, innerRadius } }.
  function createMillStock(stock, resolution) {
    const spanX = Math.max(EPSILON, stock.maxX - stock.minX);
    const spanY = Math.max(EPSILON, stock.maxY - stock.minY);
    const cell = clampResolution(resolution, Math.max(spanX, spanY));
    const nx = Math.max(2, Math.min(2048, Math.ceil(spanX / cell) + 1));
    const ny = Math.max(2, Math.min(2048, Math.ceil(spanY / cell) + 1));
    const top = new Float32Array(nx * ny);
    top.fill(stock.maxZ);
    const base = new Float32Array(nx * ny);
    base.fill(stock.minZ);
    const result = {
      kind: "mill",
      cell,
      nx,
      ny,
      originX: stock.minX,
      originY: stock.minY,
      bottom: stock.minZ,
      topZ: stock.maxZ,
      top,
      base,
      extras: new Map(),
      undercut: false,
      masked: false,
      throughCut: 0,
      dirty: emptyDirty()
    };
    // A round blank: grid points outside the cylinder (or inside its bore) hold no material.
    if (stock.cylinder && stock.cylinder.radius > 0) {
      const { cx, cy, radius } = stock.cylinder;
      const inner = Math.max(0, stock.cylinder.innerRadius || 0);
      for (let iy = 0; iy < ny; iy += 1) {
        for (let ix = 0; ix < nx; ix += 1) {
          const d = Math.hypot(stock.minX + ix * cell - cx, stock.minY + iy * cell - cy);
          if (d > radius + EPSILON || d < inner - EPSILON) top[iy * nx + ix] = EMPTY;
        }
      }
      result.masked = true;
    }
    return result;
  }

  function markDirty(stock, ix) {
    if (ix < stock.dirty.min) stock.dirty.min = ix;
    if (ix > stock.dirty.max) stock.dirty.max = ix;
  }

  // Removes [zlo, zhi] from the slabs of an interval list [lo, hi, lo, hi, ...].
  function subtractFromList(list, zlo, zhi) {
    let changed = false;
    for (let k = list.length - 2; k >= 0; k -= 2) {
      const lo = list[k];
      const hi = list[k + 1];
      if (zlo >= hi - EPSILON || zhi <= lo + EPSILON) continue;
      changed = true;
      if (zlo <= lo + EPSILON && zhi >= hi - EPSILON) list.splice(k, 2);
      else if (zhi >= hi - EPSILON) list[k + 1] = zlo;
      else if (zlo <= lo + EPSILON) list[k] = zhi;
      else list.splice(k + 1, 0, zlo, zhi); // [lo, zlo] and [zhi, hi]
    }
    return changed;
  }

  // Removes the material between zlo and zhi from one column.
  function subtractColumn(stock, index, ix, zlo, zhi) {
    let top = stock.top[index];
    if (isEmpty(top) || !(zhi > zlo + EPSILON)) return false;
    let base = stock.base[index];
    let changed = false;
    // The topmost slab; after it is removed, the slab that takes its place.
    for (;;) {
      if (top - base <= EPSILON) break; // nothing left in this column
      if (zlo >= top - EPSILON || zhi <= base + EPSILON) break;
      changed = true;
      if (zlo <= base + EPSILON && zhi >= top - EPSILON) {
        const list = stock.extras.get(index);
        if (list && list.length) {
          top = list.pop();
          base = list.pop();
          if (!list.length) stock.extras.delete(index);
          continue;
        }
        if (top > stock.bottom + EPSILON) stock.throughCut += 1;
        top = stock.bottom;
        base = stock.bottom;
        break;
      }
      if (zhi >= top - EPSILON) {
        top = zlo; // cut from above
      } else if (zlo <= base + EPSILON) {
        base = zhi; // cut from below
      } else {
        // A slot through the slab: the part below becomes the highest undercut slab.
        let list = stock.extras.get(index);
        if (!list) {
          list = [];
          stock.extras.set(index, list);
        }
        list.push(base, zlo);
        base = zhi;
      }
      break;
    }
    const list = stock.extras.get(index);
    if (list) {
      if (subtractFromList(list, zlo, zhi)) changed = true;
      if (!list.length) stock.extras.delete(index);
    }
    if (changed) {
      stock.top[index] = top;
      stock.base[index] = base;
      if (base > stock.bottom + EPSILON || stock.extras.has(index)) stock.undercut = true;
      markDirty(stock, ix);
    }
    return changed;
  }

  // Height of the cutting edge above the tip at radial distance d from the tool axis.
  function edgeHeight(tool, d) {
    const radius = Math.max(tool.radius || 0, 0);
    if (d > radius + EPSILON) return Infinity;
    const type = tool.type || "end-mill";
    if (type === "ball-mill") {
      return radius - Math.sqrt(Math.max(0, radius * radius - d * d));
    }
    if (type === "bull-nose-mill" && tool.cornerRadius > 0) {
      const corner = Math.min(tool.cornerRadius, radius);
      const flat = radius - corner;
      if (d <= flat) return 0;
      const inner = d - flat;
      return corner - Math.sqrt(Math.max(0, corner * corner - inner * inner));
    }
    if (/drill|spot|center/.test(type)) {
      const angle = Number.isFinite(tool.pointAngle) && tool.pointAngle > 0 ? tool.pointAngle : 118;
      const half = Math.min(179, Math.max(1, angle)) / 2 * Math.PI / 180;
      return d / Math.tan(half);
    }
    if (type === "chamfer-mill" || type === "engraver") {
      const angle = Number.isFinite(tool.taperAngle) && tool.taperAngle > 0 ? tool.taperAngle : 45;
      return d / Math.tan(Math.min(89, Math.max(1, angle)) * Math.PI / 180);
    }
    return 0;
  }

  function isFlat(tool) {
    const type = tool.type || "end-mill";
    return !(type === "ball-mill" || (type === "bull-nose-mill" && tool.cornerRadius > 0)
      || /drill|spot|center/.test(type) || type === "chamfer-mill" || type === "engraver");
  }

  // A vertical tool: everything above the cutting edge goes.
  function lower(stock, index, ix, edge) {
    const current = stock.top[index];
    if (isEmpty(current)) return false;
    const limited = Math.max(stock.bottom, edge);
    if (limited >= current - EPSILON) return false;
    if (limited > stock.base[index] + EPSILON) {
      stock.top[index] = limited;
      markDirty(stock, ix);
      return true;
    }
    return subtractColumn(stock, index, ix, limited, Infinity);
  }

  // One straight motion of the tip from a to b: every grid point within the swept capsule takes
  // the lowest height of the cutting edge over the part of the motion that covers it.
  function cutMillPiece(stock, a, b, tool) {
    const radius = Math.max(tool.radius || 0, stock.cell * 0.5);
    const shaped = { ...tool, radius };
    const flat = isFlat(tool);
    const cell = stock.cell;
    const ix0 = Math.max(0, Math.floor((Math.min(a.x, b.x) - radius - stock.originX) / cell));
    const ix1 = Math.min(stock.nx - 1, Math.ceil((Math.max(a.x, b.x) + radius - stock.originX) / cell));
    const iy0 = Math.max(0, Math.floor((Math.min(a.y, b.y) - radius - stock.originY) / cell));
    const iy1 = Math.min(stock.ny - 1, Math.ceil((Math.max(a.y, b.y) + radius - stock.originY) / cell));
    if (ix1 < ix0 || iy1 < iy0) return false;
    const dx = b.x - a.x;
    const dy = b.y - a.y;
    const dz = b.z - a.z;
    const length2 = dx * dx + dy * dy;
    let changed = false;
    for (let iy = iy0; iy <= iy1; iy += 1) {
      const py = stock.originY + iy * cell;
      for (let ix = ix0; ix <= ix1; ix += 1) {
        const px = stock.originX + ix * cell;
        const index = iy * stock.nx + ix;
        if (isEmpty(stock.top[index])) continue;
        // Parameter interval [t1, t2] of the motion during which the tool covers this point.
        let t1;
        let t2;
        let tClosest;
        if (length2 < 1e-18) {
          const d0 = Math.hypot(px - a.x, py - a.y);
          if (d0 > radius + EPSILON) continue;
          t1 = 0; t2 = 1; tClosest = 0;
        } else {
          const ex = a.x - px;
          const ey = a.y - py;
          const bq = 2 * (ex * dx + ey * dy);
          const cq = ex * ex + ey * ey - radius * radius;
          const disc = bq * bq - 4 * length2 * cq;
          if (disc < 0) continue;
          const root = Math.sqrt(disc);
          t1 = (-bq - root) / (2 * length2);
          t2 = (-bq + root) / (2 * length2);
          if (t2 < 0 || t1 > 1) continue;
          t1 = Math.max(0, t1);
          t2 = Math.min(1, t2);
          tClosest = Math.max(t1, Math.min(t2, -(ex * dx + ey * dy) / length2));
        }
        let edge;
        if (flat) {
          edge = Math.min(a.z + dz * t1, a.z + dz * t2);
        } else {
          edge = Infinity;
          for (const t of [tClosest, t1, t2, (t1 + tClosest) / 2, (tClosest + t2) / 2]) {
            const d = Math.hypot(px - (a.x + dx * t), py - (a.y + dy * t));
            const rise = edgeHeight(shaped, d);
            if (rise === Infinity) continue;
            const candidate = a.z + dz * t + rise;
            if (candidate < edge) edge = candidate;
          }
          if (edge === Infinity) continue;
        }
        if (lower(stock, index, ix, edge)) changed = true;
      }
    }
    return changed;
  }

  // ----- tilted tools: solids of revolution stamped along the motion -------------------------

  // The tool as convex parts along its axis (s measured from the tip toward the spindle):
  // cylinders { s0, s1, radius }, the ball of a ball mill { center, radius } and the cone of a
  // drill or chamfer mill { k = radius per unit s, s1 }. A column meets their union in one interval.
  function toolParts(tool) {
    const radius = Math.max(tool.radius || 0, 0);
    const length = Number.isFinite(tool.length) && tool.length > 0 ? tool.length : Math.max(100, radius * 8);
    const type = tool.type || "end-mill";
    const parts = [];
    if (type === "ball-mill" && radius > 0) {
      parts.push({ kind: "sphere", center: radius, radius });
      parts.push({ kind: "cylinder", s0: radius, s1: length, radius });
    } else if (type === "bull-nose-mill" && tool.cornerRadius > 0 && radius > 0) {
      const corner = Math.min(tool.cornerRadius, radius);
      const flat = radius - corner;
      if (flat > EPSILON) parts.push({ kind: "cylinder", s0: 0, s1: length, radius: flat });
      for (const degrees of [30, 60]) {
        const angle = degrees * DEGREE;
        parts.push({ kind: "cylinder", s0: corner * (1 - Math.cos(angle)), s1: length, radius: flat + corner * Math.sin(angle) });
      }
      parts.push({ kind: "cylinder", s0: corner, s1: length, radius });
    } else if ((/drill|spot|center/.test(type) || type === "chamfer-mill" || type === "engraver") && radius > 0) {
      const degrees = /drill|spot|center/.test(type)
        ? (Number.isFinite(tool.pointAngle) && tool.pointAngle > 0 ? tool.pointAngle : 118) / 2
        : Number.isFinite(tool.taperAngle) && tool.taperAngle > 0 ? tool.taperAngle : 45;
      const k = Math.tan(Math.min(89, Math.max(1, degrees)) * DEGREE);
      const height = radius / k;
      parts.push({ kind: "cone", k, s1: height });
      parts.push({ kind: "cylinder", s0: height, s1: length, radius });
    } else {
      parts.push({ kind: "cylinder", s0: 0, s1: length, radius: Math.max(radius, EPSILON) });
    }
    return { parts, radius, length };
  }

  // Interval of a column inside one tool part, written to stampLo/stampHi.
  //   A z^2 + D z + F0: squared distance of the column point (z) from the tool axis;
  //   s0 + uz z: its position along the axis (from the tip); w0z: column reference minus tip z.
  let stampLo = 0;
  let stampHi = 0;

  function axialClip(s0, uz, sLow, sHigh, z1, z2) {
    if (uz > 1e-9) {
      z1 = Math.max(z1, (sLow - s0) / uz);
      z2 = Math.min(z2, (sHigh - s0) / uz);
    } else if (uz < -1e-9) {
      z1 = Math.max(z1, (sHigh - s0) / uz);
      z2 = Math.min(z2, (sLow - s0) / uz);
    } else if (s0 < sLow - EPSILON || s0 > sHigh + EPSILON) {
      return false;
    }
    if (!(z2 > z1 + EPSILON)) return false;
    stampLo = z1;
    stampHi = z2;
    return true;
  }

  function partInterval(part, A, D, F0, s0, uz, w0z) {
    if (part.kind === "cylinder") {
      const r2 = part.radius * part.radius;
      let z1 = -Infinity;
      let z2 = Infinity;
      if (A > 1e-9) {
        const disc = D * D - 4 * A * (F0 - r2);
        if (disc < 0) return false;
        const root = Math.sqrt(disc);
        z1 = (-D - root) / (2 * A);
        z2 = (-D + root) / (2 * A);
      } else if (F0 > r2) {
        return false;
      }
      return axialClip(s0, uz, part.s0, part.s1, z1, z2);
    }
    if (part.kind === "sphere") {
      // |column point - ball centre|^2 <= r^2, the centre sitting `center` up the axis.
      const b = 2 * (w0z - part.center * uz);
      const c0 = F0 + s0 * s0 - 2 * part.center * s0 + part.center * part.center - part.radius * part.radius;
      const disc = b * b - 4 * c0;
      if (disc < 0) return false;
      const root = Math.sqrt(disc);
      stampLo = (-b - root) / 2;
      stampHi = (-b + root) / 2;
      return stampHi > stampLo + EPSILON;
    }
    // Cone from the tip: distance from the axis <= k * s for 0 <= s <= s1.
    const k2 = part.k * part.k;
    if (Math.abs(uz) <= 1e-9) {
      if (s0 < -EPSILON || s0 > part.s1 + EPSILON) return false;
      return partInterval({ kind: "cylinder", radius: Math.max(part.k * Math.max(s0, 0), EPSILON), s0: -Infinity, s1: Infinity }, A, D, F0, s0, uz, w0z);
    }
    const a = A - k2 * uz * uz;
    const b = D - 2 * k2 * s0 * uz;
    const c0 = F0 - k2 * s0 * s0;
    // z range of 0 <= s <= s1.
    const zs1 = uz > 0 ? -s0 / uz : (part.s1 - s0) / uz;
    const zs2 = uz > 0 ? (part.s1 - s0) / uz : -s0 / uz;
    if (Math.abs(a) > 1e-9) {
      const disc = b * b - 4 * a * c0;
      if (disc < 0) {
        if (a > 0) return false;
        return axialClip(s0, uz, 0, part.s1, -Infinity, Infinity);
      }
      const root = Math.sqrt(disc);
      const r1 = Math.min((-b - root) / (2 * a), (-b + root) / (2 * a));
      const r2 = Math.max((-b - root) / (2 * a), (-b + root) / (2 * a));
      if (a > 0) return axialClip(s0, uz, 0, part.s1, r1, r2);
      // Outside the roots: the nappe of the cone on the tip's side is one of the two rays.
      let lo = Infinity;
      let hi = -Infinity;
      if (Math.min(r1, zs2) > zs1 + EPSILON) { lo = zs1; hi = Math.min(r1, zs2); }
      if (zs2 > Math.max(r2, zs1) + EPSILON) { lo = Math.min(lo, Math.max(r2, zs1)); hi = zs2; }
      if (!(hi > lo + EPSILON)) return false;
      stampLo = lo;
      stampHi = hi;
      return true;
    }
    if (Math.abs(b) > 1e-12) {
      const zb = -c0 / b;
      return b > 0 ? axialClip(s0, uz, 0, part.s1, -Infinity, zb) : axialClip(s0, uz, 0, part.s1, zb, Infinity);
    }
    return c0 <= 0 && axialClip(s0, uz, 0, part.s1, -Infinity, Infinity);
  }

  // Subtracts the tool at tip c with axis u (unit, tip toward spindle) from every column it meets.
  function stampTool(stock, c, u, shape) {
    const { parts, radius, length } = shape;
    const cell = stock.cell;
    const uz = Math.max(-1, Math.min(1, u.z));
    const A = Math.max(0, 1 - uz * uz);
    const sin = Math.sqrt(A);
    const ex = length * u.x;
    const ey = length * u.y;
    const ix0 = Math.max(0, Math.floor((Math.min(c.x, c.x + ex) - radius - stock.originX) / cell));
    const ix1 = Math.min(stock.nx - 1, Math.ceil((Math.max(c.x, c.x + ex) + radius - stock.originX) / cell));
    const iy0 = Math.max(0, Math.floor((Math.min(c.y, c.y + ey) - radius - stock.originY) / cell));
    const iy1 = Math.min(stock.ny - 1, Math.ceil((Math.max(c.y, c.y + ey) + radius - stock.originY) / cell));
    if (ix1 < ix0 || iy1 < iy0) return false;
    const zMin = c.z + Math.min(0, length * uz) - radius * sin - EPSILON;
    const zMax = c.z + Math.max(0, length * uz) + radius * sin + EPSILON;
    const w0z = -c.z;
    let changed = false;
    for (let iy = iy0; iy <= iy1; iy += 1) {
      const w0y = stock.originY + iy * cell - c.y;
      for (let ix = ix0; ix <= ix1; ix += 1) {
        const index = iy * stock.nx + ix;
        const top = stock.top[index];
        if (isEmpty(top) || top <= zMin) continue;
        if (stock.base[index] >= zMax && !stock.extras.has(index)) continue;
        const w0x = stock.originX + ix * cell - c.x;
        const s0 = w0x * u.x + w0y * u.y + w0z * uz;
        const F0 = Math.max(0, w0x * w0x + w0y * w0y + w0z * w0z - s0 * s0);
        const D = 2 * (w0z - uz * s0);
        let lo = Infinity;
        let hi = -Infinity;
        for (let p = 0; p < parts.length; p += 1) {
          if (!partInterval(parts[p], A, D, F0, s0, uz, w0z)) continue;
          if (stampLo < lo) lo = stampLo;
          if (stampHi > hi) hi = stampHi;
        }
        if (hi > lo + EPSILON && subtractColumn(stock, index, ix, lo, hi)) changed = true;
      }
    }
    return changed;
  }

  function normalizeAxis(axis) {
    const length = Math.hypot(axis.x, axis.y, axis.z);
    return length > 1e-12 ? { x: axis.x / length, y: axis.y / length, z: axis.z / length } : { x: 0, y: 0, z: 1 };
  }

  // Tool axis between two orientations (great-circle interpolation).
  function slerpAxis(a, b, t) {
    if (t <= 0) return a;
    if (t >= 1) return b;
    const dot = Math.max(-1, Math.min(1, a.x * b.x + a.y * b.y + a.z * b.z));
    if (dot > 0.9999) return normalizeAxis({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t, z: a.z + (b.z - a.z) * t });
    if (dot < -0.9999) return t < 0.5 ? a : b;
    const theta = Math.acos(dot);
    const sinTheta = Math.sin(theta);
    const wa = Math.sin((1 - t) * theta) / sinTheta;
    const wb = Math.sin(t * theta) / sinTheta;
    return normalizeAxis({ x: wa * a.x + wb * b.x, y: wa * a.y + wb * b.y, z: wa * a.z + wb * b.z });
  }

  // One motion of a tilted tool: stamps close enough that the scallops between them stay
  // below an eighth of a cell on the tool's side and half a cell along its axis.
  function cutTiltedPiece(stock, a, b, ua, ub, shape, includeStart) {
    const cell = stock.cell;
    const dx = b.x - a.x;
    const dy = b.y - a.y;
    const dz = b.z - a.z;
    const length = Math.hypot(dx, dy, dz);
    const mean = normalizeAxis({ x: ua.x + ub.x, y: ua.y + ub.y, z: ua.z + ub.z });
    const axial = length > 0 ? Math.abs((dx * mean.x + dy * mean.y + dz * mean.z) / length) : 0;
    let step = Math.sqrt(Math.max(cell, shape.radius * cell));
    if (axial > 0.02) step = Math.min(step, cell / (2 * axial));
    step = Math.max(step, cell * 0.25);
    const turn = Math.acos(Math.max(-1, Math.min(1, ua.x * ub.x + ua.y * ub.y + ua.z * ub.z)));
    const count = Math.max(1, Math.ceil(length / step), Math.ceil(turn / (2 * DEGREE)));
    let changed = false;
    for (let k = includeStart ? 0 : 1; k <= count; k += 1) {
      const t = k / count;
      const c = { x: a.x + dx * t, y: a.y + dy * t, z: a.z + dz * t };
      if (stampTool(stock, c, slerpAxis(ua, ub, t), shape)) changed = true;
    }
    return changed;
  }

  // Cuts one motion: points [{x, y, z}, ...] of the tool tip. `axis` is the tool axis in the
  // stock's frame (unit vector from the tip toward the spindle): undefined or +Z for a vertical
  // tool, one vector for a fixed tilt, or one vector per point when the axis turns during the move.
  function cutMillSegment(stock, points, tool, axis) {
    if (!points || points.length === 0) return false;
    const perPoint = Array.isArray(axis) ? axis : undefined;
    const fixed = perPoint ? undefined : axis;
    const axisAt = (index) => {
      const value = perPoint ? perPoint[Math.min(index, perPoint.length - 1)] : fixed;
      return value && Number.isFinite(value.x) && Number.isFinite(value.y) && Number.isFinite(value.z) ? normalizeAxis(value) : undefined;
    };
    const vertical = (u) => !u || u.z >= VERTICAL;
    let shape;
    const tilted = (a, b, ua, ub, first) => {
      if (!shape) shape = toolParts(tool);
      return cutTiltedPiece(stock, a, b, ua || { x: 0, y: 0, z: 1 }, ub || { x: 0, y: 0, z: 1 }, shape, first);
    };
    if (points.length === 1) {
      const u = axisAt(0);
      return vertical(u) ? cutMillPiece(stock, points[0], points[0], tool) : tilted(points[0], points[0], u, u, true);
    }
    let changed = false;
    for (let index = 1; index < points.length; index += 1) {
      const ua = axisAt(index - 1);
      const ub = axisAt(index);
      const piece = vertical(ua) && vertical(ub)
        ? cutMillPiece(stock, points[index - 1], points[index], tool)
        : tilted(points[index - 1], points[index], ua, ub, index === 1);
      if (piece) changed = true;
    }
    return changed;
  }

  // ----- lathe stock: outer and inner radius per Z column -----------------------------------

  // stock: { od, id, length, frontZ } (X programmed as diameter); the part occupies
  // frontZ - length <= z <= frontZ, id/2 <= r <= od/2.
  function createLatheStock(stock, resolution) {
    const outerRadius = Math.max(EPSILON, stock.od / 2);
    const innerRadius = Math.max(0, Math.min(outerRadius, (stock.id || 0) / 2));
    const backZ = stock.frontZ - stock.length;
    const cell = clampResolution(resolution, Math.max(stock.length, outerRadius));
    const nz = Math.max(2, Math.min(8192, Math.ceil(stock.length / cell) + 1));
    const outer = new Float32Array(nz);
    const inner = new Float32Array(nz);
    outer.fill(outerRadius);
    inner.fill(innerRadius);
    return { kind: "lathe", cell, nz, originZ: backZ, frontZ: stock.frontZ, outerRadius, innerRadius, outer, inner, dirty: emptyDirty() };
  }

  function tipDirection(tip) {
    return {
      1: { z: 1, r: 1 }, 2: { z: -1, r: 1 }, 3: { z: -1, r: -1 }, 4: { z: 1, r: -1 },
      5: { z: 1, r: 0 }, 6: { z: 0, r: 1 }, 7: { z: -1, r: 0 }, 8: { z: 0, r: -1 }
    }[tip];
  }

  // Which side of the nose the insert body (and the removed material) lies on: opposite the
  // imaginary tip. Unknown tips follow the tool type.
  function bodySide(tool) {
    const type = String(tool.type || "");
    if (/drill|reamer|tap/.test(type)) return { z: 1, r: -1, drill: true };
    if (/groove|cutoff/.test(type)) return { z: 0, r: type.includes("internal") ? -1 : 1 };
    const direction = tipDirection(tool.tip);
    if (direction) return { z: -direction.z, r: -direction.r };
    if (/internal|boring/.test(type)) return { z: 1, r: -1 };
    return { z: 1, r: 1 };
  }

  function markLathe(stock, iz) {
    if (iz < stock.dirty.min) stock.dirty.min = iz;
    if (iz > stock.dirty.max) stock.dirty.max = iz;
  }

  // Clears the nose circle at (zc, rc) and the insert body's quadrant beside it.
  function stampLathe(stock, zc, rc, rn, side, width) {
    const cell = stock.cell;
    const radius = Math.max(rn, 0);
    let changed = false;
    const setOuter = (iz, value) => {
      if (value < stock.outer[iz] - EPSILON) {
        stock.outer[iz] = Math.max(stock.inner[iz], value);
        markLathe(stock, iz);
        changed = true;
      }
    };
    const setInner = (iz, value) => {
      if (value > stock.inner[iz] + EPSILON) {
        stock.inner[iz] = Math.min(stock.outer[iz], value);
        markLathe(stock, iz);
        changed = true;
      }
    };
    // Columns the nose circle spans.
    const izNose0 = Math.max(0, Math.floor((zc - radius - stock.originZ) / cell));
    const izNose1 = Math.min(stock.nz - 1, Math.ceil((zc + radius - stock.originZ) / cell));
    for (let iz = izNose0; iz <= izNose1; iz += 1) {
      const dz = stock.originZ + iz * cell - zc;
      if (Math.abs(dz) > radius + EPSILON) continue;
      const chord = Math.sqrt(Math.max(0, radius * radius - dz * dz));
      if (side.r >= 0) setOuter(iz, rc - chord);
      else setInner(iz, rc + chord);
    }
    if (radius <= EPSILON) {
      // A point tool still removes the column it passes through.
      const iz = Math.round((zc - stock.originZ) / cell);
      if (iz >= 0 && iz < stock.nz) {
        if (side.r >= 0) setOuter(iz, rc);
        else setInner(iz, rc);
      }
    }
    // The insert body: everything beside the nose on its side, out to the stock edge.
    let iz0;
    let iz1;
    if (side.z > 0) {
      iz0 = Math.max(0, Math.ceil((zc - stock.originZ) / cell));
      iz1 = stock.nz - 1;
    } else if (side.z < 0) {
      iz0 = 0;
      iz1 = Math.min(stock.nz - 1, Math.floor((zc - stock.originZ) / cell));
    } else {
      const half = Math.max(width || 0, radius * 2, cell) / 2;
      iz0 = Math.max(0, Math.ceil((zc - half - stock.originZ) / cell));
      iz1 = Math.min(stock.nz - 1, Math.floor((zc + half - stock.originZ) / cell));
    }
    for (let iz = iz0; iz <= iz1; iz += 1) {
      if (side.r >= 0) setOuter(iz, rc);
      else setInner(iz, rc);
    }
    return changed;
  }

  // Lathe motion: points [{x (diameter) or r, z}], nose radius and imaginary tip direction
  // (tool.tip 1-8, 0 = unknown), tool.type, tool.width for grooving inserts.
  function cutLatheSegment(stock, points, tool) {
    if (!points || points.length === 0) return false;
    const side = bodySide(tool);
    const rn = side.drill ? Math.max(0, tool.radius || tool.noseRadius || 0) : Math.max(0, tool.noseRadius || 0);
    const direction = tipDirection(tool.tip);
    const center = (point) => {
      const r = Number.isFinite(point.r) ? point.r : point.x / 2;
      return direction && rn > 0 && !side.drill ? { z: point.z - direction.z * rn, r: r - direction.r * rn } : { z: point.z, r };
    };
    const step = Math.max(stock.cell * 0.5, Math.min(stock.cell, Math.max(rn, stock.cell) * 0.35));
    let changed = false;
    const stamp = (c) => { if (stampLathe(stock, c.z, c.r, rn, side, tool.width)) changed = true; };
    if (points.length === 1) {
      stamp(center(points[0]));
      return changed;
    }
    for (let index = 1; index < points.length; index += 1) {
      const a = center(points[index - 1]);
      const b = center(points[index]);
      const length = Math.hypot(b.z - a.z, b.r - a.r);
      const count = Math.max(1, Math.ceil(length / step));
      for (let sample = index === 1 ? 0 : 1; sample <= count; sample += 1) {
        const t = sample / count;
        stamp({ z: a.z + (b.z - a.z) * t, r: a.r + (b.r - a.r) * t });
      }
    }
    return changed;
  }

  function latheProfiles(stock) {
    return { outer: stock.outer, inner: stock.inner };
  }

  // ----- triangles for display and STL ----------------------------------------------------

  // Mill: the top surface grid, the underside of the topmost slab (the flat bottom, or the
  // ceiling of an undercut), walls where material meets air, and the undercut slabs as boxes.
  function millTriangles(stock) {
    const { nx, ny, cell, originX, originY, top, base, bottom } = stock;
    const triangles = [];
    const push = (a, b, c) => triangles.push(a[0], a[1], a[2], b[0], b[1], b[2], c[0], c[1], c[2]);
    const solid = (ix, iy) => ix >= 0 && iy >= 0 && ix < nx && iy < ny && !isEmpty(top[iy * nx + ix]);
    const at = (ix, iy) => [originX + ix * cell, originY + iy * cell, top[iy * nx + ix]];
    const under = (ix, iy) => [originX + ix * cell, originY + iy * cell, base[iy * nx + ix]];
    const undersideCells = stock.masked || stock.undercut;
    for (let iy = 0; iy < ny - 1; iy += 1) {
      for (let ix = 0; ix < nx - 1; ix += 1) {
        if (!solid(ix, iy) || !solid(ix + 1, iy) || !solid(ix + 1, iy + 1) || !solid(ix, iy + 1)) continue;
        const a = at(ix, iy);
        const b = at(ix + 1, iy);
        const c = at(ix + 1, iy + 1);
        const d = at(ix, iy + 1);
        push(a, b, c);
        push(a, c, d);
        if (undersideCells) {
          const e = under(ix, iy);
          const f = under(ix + 1, iy);
          const g = under(ix + 1, iy + 1);
          const h = under(ix, iy + 1);
          push(e, g, f);
          push(e, h, g);
        }
      }
    }
    // Vertical wall between two points along one grid edge, from their undersides up to their tops.
    const wall = (a, b, aBase, bBase) => {
      const a0 = [a[0], a[1], aBase];
      const b0 = [b[0], b[1], bBase];
      push(a, b0, b);
      push(a, a0, b0);
    };
    const baseAt = (ix, iy) => base[iy * nx + ix];
    if (!stock.masked) {
      for (let ix = 0; ix < nx - 1; ix += 1) {
        wall(at(ix + 1, 0), at(ix, 0), baseAt(ix + 1, 0), baseAt(ix, 0));
        wall(at(ix, ny - 1), at(ix + 1, ny - 1), baseAt(ix, ny - 1), baseAt(ix + 1, ny - 1));
      }
      for (let iy = 0; iy < ny - 1; iy += 1) {
        wall(at(0, iy), at(0, iy + 1), baseAt(0, iy), baseAt(0, iy + 1));
        wall(at(nx - 1, iy + 1), at(nx - 1, iy), baseAt(nx - 1, iy + 1), baseAt(nx - 1, iy));
      }
      if (!undersideCells) {
        const maxX = originX + (nx - 1) * cell;
        const maxY = originY + (ny - 1) * cell;
        push([originX, originY, bottom], [maxX, maxY, bottom], [maxX, originY, bottom]);
        push([originX, originY, bottom], [originX, maxY, bottom], [maxX, maxY, bottom]);
      }
    } else {
      // Round blank: a wall wherever a solid point borders an empty one, halfway between them.
      const half = cell / 2;
      for (let iy = 0; iy < ny; iy += 1) {
        for (let ix = 0; ix < nx; ix += 1) {
          if (!solid(ix, iy)) continue;
          const p = at(ix, iy);
          const b = baseAt(ix, iy);
          if (!solid(ix + 1, iy)) wall([p[0] + half, p[1] - half, p[2]], [p[0] + half, p[1] + half, p[2]], b, b);
          if (!solid(ix - 1, iy)) wall([p[0] - half, p[1] + half, p[2]], [p[0] - half, p[1] - half, p[2]], b, b);
          if (!solid(ix, iy + 1)) wall([p[0] + half, p[1] + half, p[2]], [p[0] - half, p[1] + half, p[2]], b, b);
          if (!solid(ix, iy - 1)) wall([p[0] - half, p[1] - half, p[2]], [p[0] + half, p[1] - half, p[2]], b, b);
        }
      }
    }
    // Undercut slabs below the topmost one: one box per grid cell and slab.
    const half = cell / 2;
    for (const [index, list] of stock.extras) {
      const ix = index % nx;
      const iy = Math.floor(index / nx);
      const x0 = originX + ix * cell - half;
      const x1 = x0 + cell;
      const y0 = originY + iy * cell - half;
      const y1 = y0 + cell;
      for (let k = 0; k < list.length; k += 2) {
        const lo = list[k];
        const hi = list[k + 1];
        if (!(hi > lo + EPSILON)) continue;
        push([x0, y0, hi], [x1, y0, hi], [x1, y1, hi]);
        push([x0, y0, hi], [x1, y1, hi], [x0, y1, hi]);
        push([x0, y0, lo], [x1, y1, lo], [x1, y0, lo]);
        push([x0, y0, lo], [x0, y1, lo], [x1, y1, lo]);
        wall([x0, y0, hi], [x1, y0, hi], lo, lo);
        wall([x1, y0, hi], [x1, y1, hi], lo, lo);
        wall([x1, y1, hi], [x0, y1, hi], lo, lo);
        wall([x0, y1, hi], [x0, y0, hi], lo, lo);
      }
    }
    return new Float32Array(triangles);
  }

  // Lathe: the outer and inner profiles revolved about Z (scene X/Y = radius plane).
  function latheTriangles(stock, sides) {
    const segments = Math.max(12, sides || 48);
    const { outer, inner } = latheProfiles(stock);
    const triangles = [];
    const ring = (r, z, k) => {
      const angle = k / segments * Math.PI * 2;
      return [r * Math.cos(angle), r * Math.sin(angle), z];
    };
    const push = (a, b, c) => triangles.push(a[0], a[1], a[2], b[0], b[1], b[2], c[0], c[1], c[2]);
    const z = (iz) => stock.originZ + iz * stock.cell;
    for (let iz = 0; iz < stock.nz - 1; iz += 1) {
      for (let k = 0; k < segments; k += 1) {
        const a = ring(outer[iz], z(iz), k);
        const b = ring(outer[iz], z(iz), k + 1);
        const c = ring(outer[iz + 1], z(iz + 1), k + 1);
        const d = ring(outer[iz + 1], z(iz + 1), k);
        push(a, c, b);
        push(a, d, c);
        if (inner[iz] > 0 || inner[iz + 1] > 0) {
          const e = ring(inner[iz], z(iz), k);
          const f = ring(inner[iz], z(iz), k + 1);
          const g = ring(inner[iz + 1], z(iz + 1), k + 1);
          const h = ring(inner[iz + 1], z(iz + 1), k);
          push(e, f, g);
          push(e, g, h);
        }
      }
    }
    // End faces (annuli).
    for (const iz of [0, stock.nz - 1]) {
      for (let k = 0; k < segments; k += 1) {
        const a = ring(outer[iz], z(iz), k);
        const b = ring(outer[iz], z(iz), k + 1);
        const c = ring(inner[iz], z(iz), k + 1);
        const d = ring(inner[iz], z(iz), k);
        if (iz === 0) { push(a, b, c); push(a, c, d); } else { push(a, c, b); push(a, d, c); }
      }
    }
    return new Float32Array(triangles);
  }

  function triangles(stock, sides) {
    return stock.kind === "lathe" ? latheTriangles(stock, sides) : millTriangles(stock);
  }

  // ----- STL --------------------------------------------------------------------------------

  function normalOf(a, b, c) {
    const ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
    const vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
    const nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
    const length = Math.hypot(nx, ny, nz) || 1;
    return [nx / length, ny / length, nz / length];
  }

  // Binary STL from flat triangle positions (9 numbers per triangle).
  function writeStl(positions, header) {
    const count = Math.floor(positions.length / 9);
    const buffer = new ArrayBuffer(84 + count * 50);
    const view = new DataView(buffer);
    const bytes = new Uint8Array(buffer);
    const text = String(header || "Meimad Planner machined stock").slice(0, 80);
    for (let index = 0; index < text.length; index += 1) bytes[index] = text.charCodeAt(index) & 0x7f;
    view.setUint32(80, count, true);
    let offset = 84;
    for (let index = 0; index < count; index += 1) {
      const base = index * 9;
      const a = [positions[base], positions[base + 1], positions[base + 2]];
      const b = [positions[base + 3], positions[base + 4], positions[base + 5]];
      const c = [positions[base + 6], positions[base + 7], positions[base + 8]];
      const n = normalOf(a, b, c);
      for (const value of [n[0], n[1], n[2], ...a, ...b, ...c]) {
        view.setFloat32(offset, value, true);
        offset += 4;
      }
      view.setUint16(offset, 0, true);
      offset += 2;
    }
    return buffer;
  }

  // Binary or ASCII STL -> flat triangle positions.
  function readStl(buffer) {
    const bytes = new Uint8Array(buffer);
    const head = String.fromCharCode(...bytes.subarray(0, Math.min(6, bytes.length))).toLowerCase();
    if (bytes.length >= 84) {
      const count = new DataView(buffer).getUint32(80, true);
      if (84 + count * 50 === bytes.length && !(head.startsWith("solid") && looksAscii(bytes))) {
        const view = new DataView(buffer);
        const positions = new Float32Array(count * 9);
        let offset = 84;
        for (let index = 0; index < count; index += 1) {
          offset += 12; // normal
          for (let value = 0; value < 9; value += 1) {
            positions[index * 9 + value] = view.getFloat32(offset, true);
            offset += 4;
          }
          offset += 2;
        }
        return positions;
      }
    }
    const text = decodeText(bytes);
    const values = [];
    const vertex = /vertex\s+([-+0-9.eE]+)\s+([-+0-9.eE]+)\s+([-+0-9.eE]+)/g;
    let match;
    while ((match = vertex.exec(text)) !== null) values.push(Number(match[1]), Number(match[2]), Number(match[3]));
    return new Float32Array(values.slice(0, Math.floor(values.length / 9) * 9));
  }

  // ASCII STL text; a bare V8 host (tests) has no TextDecoder.
  function decodeText(bytes) {
    if (typeof TextDecoder === "function") return new TextDecoder().decode(bytes);
    let text = "";
    for (let index = 0; index < bytes.length; index += 0x8000) {
      text += String.fromCharCode.apply(null, bytes.subarray(index, index + 0x8000));
    }
    return text;
  }

  function looksAscii(bytes) {
    const limit = Math.min(bytes.length, 400);
    for (let index = 0; index < limit; index += 1) {
      const value = bytes[index];
      if (value === 0 || (value > 126 && value !== 10 && value !== 13)) return false;
    }
    return true;
  }

  function bounds(positions) {
    const result = { minX: Infinity, maxX: -Infinity, minY: Infinity, maxY: -Infinity, minZ: Infinity, maxZ: -Infinity };
    for (let index = 0; index < positions.length; index += 3) {
      const x = positions[index], y = positions[index + 1], z = positions[index + 2];
      if (x < result.minX) result.minX = x;
      if (x > result.maxX) result.maxX = x;
      if (y < result.minY) result.minY = y;
      if (y > result.maxY) result.maxY = y;
      if (z < result.minZ) result.minZ = z;
      if (z > result.maxZ) result.maxZ = z;
    }
    return Number.isFinite(result.minX) ? result : undefined;
  }

  // A mill stock from an STL: the highest surface above each grid point (2.5D) over the mesh's extent.
  function millStockFromStl(positions, resolution, offset) {
    const extent = bounds(positions);
    if (!extent) return undefined;
    const shift = offset || { x: 0, y: 0, z: 0 };
    const box = {
      minX: extent.minX + shift.x, maxX: extent.maxX + shift.x,
      minY: extent.minY + shift.y, maxY: extent.maxY + shift.y,
      minZ: extent.minZ + shift.z, maxZ: extent.maxZ + shift.z
    };
    const stock = createMillStock(box, resolution);
    stock.top.fill(stock.bottom);
    for (let index = 0; index < positions.length; index += 9) {
      rasterizeTriangle(stock,
        [positions[index] + shift.x, positions[index + 1] + shift.y, positions[index + 2] + shift.z],
        [positions[index + 3] + shift.x, positions[index + 4] + shift.y, positions[index + 5] + shift.z],
        [positions[index + 6] + shift.x, positions[index + 7] + shift.y, positions[index + 8] + shift.z]);
    }
    stock.dirty = emptyDirty();
    stock.fromStl = true;
    return stock;
  }

  // Max-Z rasterization of one triangle onto the heightfield.
  function rasterizeTriangle(stock, a, b, c) {
    const cell = stock.cell;
    const minX = Math.min(a[0], b[0], c[0]), maxX = Math.max(a[0], b[0], c[0]);
    const minY = Math.min(a[1], b[1], c[1]), maxY = Math.max(a[1], b[1], c[1]);
    const ix0 = Math.max(0, Math.floor((minX - stock.originX) / cell));
    const ix1 = Math.min(stock.nx - 1, Math.ceil((maxX - stock.originX) / cell));
    const iy0 = Math.max(0, Math.floor((minY - stock.originY) / cell));
    const iy1 = Math.min(stock.ny - 1, Math.ceil((maxY - stock.originY) / cell));
    const det = (b[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (b[1] - a[1]);
    if (Math.abs(det) < 1e-12) return;
    for (let iy = iy0; iy <= iy1; iy += 1) {
      const y = stock.originY + iy * cell;
      for (let ix = ix0; ix <= ix1; ix += 1) {
        const x = stock.originX + ix * cell;
        const l0 = ((b[0] - x) * (c[1] - y) - (c[0] - x) * (b[1] - y)) / det;
        const l1 = ((c[0] - x) * (a[1] - y) - (a[0] - x) * (c[1] - y)) / det;
        const l2 = 1 - l0 - l1;
        const tolerance = -1e-6;
        if (l0 < tolerance || l1 < tolerance || l2 < tolerance) continue;
        const z = l0 * a[2] + l1 * b[2] + l2 * c[2];
        const index = iy * stock.nx + ix;
        if (z > stock.top[index]) stock.top[index] = z;
      }
    }
  }

  return {
    clampResolution,
    createMillStock,
    createLatheStock,
    cutMillSegment,
    cutLatheSegment,
    subtractColumn,
    toolParts,
    slerpAxis,
    edgeHeight,
    latheProfiles,
    triangles,
    millTriangles,
    latheTriangles,
    writeStl,
    readStl,
    bounds,
    millStockFromStl,
    takeDirty,
    isEmpty
  };
});
