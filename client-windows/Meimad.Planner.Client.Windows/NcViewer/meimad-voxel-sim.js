/*
 * Meimad Planner NC viewer: the stock simulator over the 3D voxel core (meimad-voxel-core.js) and its
 * meshes (meimad-voxel-mesh.js). It replays the program's motions in execution order and keeps:
 *   - the stock (material removal by the cutting profile of each tool, feeds only);
 *   - collision events: holder/shank in the stock, tool or holder in a fixture, holder in the finished
 *     part, a rapid through material, a gouge into the finished part and axis travel limits, each with
 *     the NC row, tool, contact position, tool axis and the severity its policy gives; one continuous
 *     contact over consecutive motions is one event;
 *   - a journal per motion (removed volume, sweeps, flags);
 *   - checkpoints, so seeking backwards restarts from the nearest one instead of the first motion;
 *   - the finished-part comparison (gouges, material left outside the part).
 * Pure: the material worker (meimad-material-worker.js) drives it and the V8 tests run it.
 *
 * Motion: { tool, cutting, rapid, points: [{x,y,z}], axis?: {x,y,z} | axes?: [{x,y,z}], line?,
 *           sourceUnit?, sourceLine?, toolRadius?, toolType?, toolCornerRadius?, toolLength?,
 *           axisLimit?: [{ axis, value, min, max }] }
 */
(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory(require("./meimad-voxel-core.js"), require("./meimad-voxel-mesh.js"));
  } else {
    root.MeimadVoxelSim = factory(root.MeimadVoxel, root.MeimadVoxelMesh);
  }
})(typeof self !== "undefined" ? self : this, function (V, MESH) {
  "use strict";

  const TYPES = Object.freeze({
    holderStock: "HolderCollision",
    toolFixture: "FixtureCollision",
    holderFixture: "FixtureCollision",
    holderPart: "HolderCollision",
    rapidStock: "RapidInMaterial",
    gouge: "Gouge",
    axisLimit: "AxisLimit"
  });

  // Default policy per check: "allowed" (not checked), "warning", "error" or "stop" (pause playback).
  const DEFAULT_POLICY = Object.freeze({
    holderStock: "error",
    toolFixture: "stop",
    holderFixture: "stop",
    holderPart: "warning",
    rapidStock: "error",
    gouge: "error",
    axisLimit: "error"
  });

  const OBJECTS = Object.freeze({
    holderStock: ["holder", "stock"],
    toolFixture: ["tool", "fixture"],
    holderFixture: ["holder", "fixture"],
    holderPart: ["holder", "part"],
    rapidStock: ["tool", "stock"],
    gouge: ["tool", "part"],
    axisLimit: ["machine", "travel"]
  });

  function normalizePolicy(policy) {
    const result = { ...DEFAULT_POLICY };
    for (const key of Object.keys(DEFAULT_POLICY)) {
      const value = policy && policy[key];
      if (value === "allowed" || value === "warning" || value === "error" || value === "stop") result[key] = value;
    }
    return result;
  }

  // options: { stock, resolution, tools: { [number]: definition }, assemblies: { [number]: assembly },
  //   motions, fixtures: [{ name, positions }], target: positions | null, policy, extent, limitBlocks,
  //   checkpointEvery, checkpointBytes, collisions: boolean }
  function create(options) {
    const motions = options.motions || [];
    const extent = options.extent || null;
    const grid = V.createStock(options.stock, options.resolution, extent, options.limitBlocks);
    const fixtures = (options.fixtures || []).filter((fixture) => fixture && fixture.positions && fixture.positions.length >= 9)
      .map((fixture) => ({ name: fixture.name || "Fixture", mask: V.createMask(grid, fixture.positions) }));
    const target = options.target && options.target.length >= 9 ? V.createMask(grid, options.target) : null;
    const policy = normalizePolicy(options.policy);
    const checkCollisions = options.collisions !== false;
    const profiles = new Map();
    const notes = new Map(); // tool number -> notes
    const checkpointEvery = Math.max(50, Number(options.checkpointEvery) || 2000);
    const checkpointBytes = Math.max(16 << 20, Number(options.checkpointBytes) || (320 << 20));
    const checkpoints = [{ index: -1, snap: V.snapshot(grid), events: 0 }];
    const journalRemoved = new Float32Array(motions.length);
    const journalSweeps = new Uint32Array(motions.length);
    const journalFlags = new Uint8Array(motions.length); // bit 0 cut, 1 collision, 2 gouge, 3 rapid in material, 4 axis limit
    const events = [];
    const active = new Map(); // key -> event (contact continuing over consecutive motions)
    let cursor = -1;          // motions fully applied
    let partial = null;       // { index, fraction } of a motion applied in part

    function profileFor(motion) {
      const number = Number(motion.tool);
      const assembly = options.assemblies && options.assemblies[number];
      const definition = (options.tools && options.tools[number]) || {};
      const radius = Number.isFinite(motion.toolRadius) && motion.toolRadius > 0 ? motion.toolRadius
        : Number(definition.diameter) > 0 ? Number(definition.diameter) / 2 : 0;
      const key = assembly ? `A${number}` : `${number}|${radius}|${motion.toolType || definition.type || ""}`;
      let profile = profiles.get(key);
      if (!profile) {
        profile = assembly && assembly.segments && assembly.segments.length
          ? V.profileFromAssembly(assembly)
          : V.profileFromDefinition({
            radius,
            type: motion.toolType || definition.type || "end-mill",
            cornerRadius: Number(motion.toolCornerRadius) || Number(definition.cornerRadius) || 0,
            pointAngle: definition.pointAngle,
            taperAngle: definition.taperAngle,
            length: Number(definition.length) > 0 ? Number(definition.length) : Number(motion.toolLength) > 0 ? Number(motion.toolLength) : undefined
          });
        profiles.set(key, profile);
        notes.set(number, profile.notes);
      }
      return profile;
    }

    function axesOf(motion) {
      if (Array.isArray(motion.axes) && motion.axes.length === (motion.points || []).length) return motion.axes;
      return motion.axis || null;
    }

    // The part of a motion up to `fraction` of its length (points and axes).
    function partOf(motion, fraction) {
      const points = motion.points || [];
      const axes = axesOf(motion);
      if (fraction >= 1 || points.length < 2) return { points, axes };
      const lengths = [];
      let total = 0;
      for (let n = 1; n < points.length; n += 1) {
        const length = Math.hypot(points[n].x - points[n - 1].x, points[n].y - points[n - 1].y, points[n].z - points[n - 1].z);
        lengths.push(length);
        total += length;
      }
      const goal = total * Math.max(0, fraction);
      const outPoints = [points[0]];
      const outAxes = Array.isArray(axes) ? [axes[0]] : axes;
      let travelled = 0;
      for (let n = 1; n < points.length; n += 1) {
        const length = lengths[n - 1];
        if (travelled + length <= goal) {
          outPoints.push(points[n]);
          if (Array.isArray(axes)) outAxes.push(axes[n]);
          travelled += length;
          continue;
        }
        const t = length > 0 ? (goal - travelled) / length : 0;
        const a = points[n - 1], b = points[n];
        outPoints.push({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t, z: a.z + (b.z - a.z) * t });
        if (Array.isArray(axes)) outAxes.push(V.slerp(V.normalize(axes[n - 1]), V.normalize(axes[n]), t));
        break;
      }
      return { points: outPoints, axes: outAxes };
    }

    function rowOf(motion) {
      return {
        line: Number.isFinite(motion.line) ? motion.line : null,
        sourceUnit: motion.sourceUnit || null,
        sourceLine: Number.isFinite(motion.sourceLine) ? motion.sourceLine : null
      };
    }

    // Records one contact of `check` during motion `index` (continuing an event of the previous motion).
    function contact(check, index, motion, detail) {
      const severity = policy[check];
      if (severity === "allowed") return;
      const objectName = detail.object || OBJECTS[check][1];
      const key = `${check}|${objectName}|${motion.tool}`;
      const open = active.get(key);
      if (open && open.lastIndex >= index - 1) {
        open.lastIndex = index;
        open.volume += detail.volume || 0;
        return;
      }
      const event = {
        id: events.length + 1,
        type: TYPES[check],
        check,
        objects: [OBJECTS[check][0], objectName],
        severity,
        tool: motion.tool,
        executionIndex: index,
        lastIndex: index,
        ...rowOf(motion),
        position: detail.position || null,
        axis: detail.axis || null,
        volume: detail.volume || 0,
        estimatedGeometry: Boolean(detail.estimated),
        message: detail.message || ""
      };
      events.push(event);
      active.set(key, event);
    }

    function closeStale(index) {
      for (const [key, event] of active) if (event.lastIndex < index - 1) active.delete(key);
    }

    const voxelVolume = () => grid.cell * grid.cell * grid.cell;

    // A motion as checks and operations on the stock, in program order. Static checks (fixtures and the
    // finished part, which never change) are done here; the stock operations (holder and rapid
    // counts, removal) are left to the executor: { kind: "count" | "cut", sw, pieces, result }.
    function plan(index, fraction) {
      const motion = motions[index];
      if (!motion || !motion.points || !motion.points.length || motion.nonCutting) return null;
      const isRapid = Boolean(motion.rapid);
      const cuts = Boolean(motion.cutting) && !isRapid;
      if (!cuts && !isRapid) return null;
      const profile = profileFor(motion);
      const part = partOf(motion, fraction);
      const sweeps = V.motionSweeps(part.points, part.axes, profile.reach, grid.cell * 0.5);
      const steps = [];
      const ops = [];
      for (const piece of sweeps) {
        const sw = V.sweep(piece.a, piece.b, piece.u);
        const step = { at: { x: piece.b.x, y: piece.b.y, z: piece.b.z }, u: piece.u, fixtureTool: [], fixtureHolder: [], holderPart: false };
        if (checkCollisions) {
          if (isRapid && policy.rapidStock !== "allowed") ops.push(step.rapid = { kind: "count", sw, pieces: profile.cutting, result: 0 });
          if (profile.collision.length && policy.holderStock !== "allowed") ops.push(step.holder = { kind: "count", sw, pieces: profile.collision, result: 0 });
          for (const fixture of fixtures) {
            if (policy.toolFixture !== "allowed" && V.countSweep(fixture.mask, sw, profile.cutting, 1)) step.fixtureTool.push(fixture.name);
            if (profile.collision.length && policy.holderFixture !== "allowed" && V.countSweep(fixture.mask, sw, profile.collision, 1)) step.fixtureHolder.push(fixture.name);
          }
          if (target && profile.collision.length && policy.holderPart !== "allowed") step.holderPart = V.countSweep(target, sw, profile.collision, 1) > 0;
        }
        if (cuts) ops.push(step.cut = { kind: "cut", sw, pieces: profile.cutting, removed: 0, gouged: 0 });
        steps.push(step);
      }
      return { index, motion, profile, part, steps, ops };
    }

    // The CPU executor: runs the stock operations of a plan in order.
    function executeCpu(planned) {
      for (const op of planned.ops) {
        if (op.kind === "count") op.result = V.countSweep(grid, op.sw, op.pieces, 1);
        else {
          op.removed = V.removeSweep(grid, op.sw, op.pieces, { target });
          op.gouged = target ? grid.lastGouged : 0;
        }
      }
    }

    // Turns the results of a planned motion into events and its journal row (the same order of checks
    // as the motion ran: rapid, holder in the stock, fixtures, holder in the part, removal).
    function commit(planned) {
      const { index, motion, profile, part, steps } = planned;
      closeStale(index);
      let removed = 0;
      let hits = 0;
      const seen = new Set();
      for (const step of steps) {
        const at = step.at;
        if (step.rapid && step.rapid.result && !seen.has("rapidStock")) {
          seen.add("rapidStock");
          contact("rapidStock", index, motion, { position: at, axis: step.u, volume: step.rapid.result * voxelVolume(), message: "Rapid move through material" });
          hits |= 8;
        }
        if (step.holder && step.holder.result && !seen.has("holderStock")) {
          seen.add("holderStock");
          contact("holderStock", index, motion, { position: at, axis: step.u, volume: step.holder.result * voxelVolume(), estimated: profile.estimated, message: "Holder or shank in the stock" });
          hits |= 2;
        }
        for (const name of step.fixtureTool) {
          if (seen.has(`toolFixture|${name}`)) continue;
          seen.add(`toolFixture|${name}`);
          contact("toolFixture", index, motion, { object: name, position: at, axis: step.u, message: `Tool in ${name}` });
          hits |= 2;
        }
        for (const name of step.fixtureHolder) {
          if (seen.has(`holderFixture|${name}`)) continue;
          seen.add(`holderFixture|${name}`);
          contact("holderFixture", index, motion, { object: name, position: at, axis: step.u, estimated: profile.estimated, message: `Holder in ${name}` });
          hits |= 2;
        }
        if (step.holderPart && !seen.has("holderPart")) {
          seen.add("holderPart");
          contact("holderPart", index, motion, { position: at, axis: step.u, estimated: profile.estimated, message: "Holder in the finished part" });
          hits |= 2;
        }
        if (step.cut) {
          removed += step.cut.removed;
          if (step.cut.gouged && !seen.has("gouge")) {
            seen.add("gouge");
            contact("gouge", index, motion, { position: at, axis: step.u, volume: step.cut.gouged * voxelVolume(), message: "Cut into the finished part" });
            hits |= 4;
          }
        }
      }
      if (checkCollisions && Array.isArray(motion.axisLimit) && motion.axisLimit.length) {
        const first = motion.axisLimit[0];
        contact("axisLimit", index, motion, {
          object: String(first.axis || "axis"),
          position: part.points[part.points.length - 1],
          message: motion.axisLimit.map((limit) => `${limit.axis} ${round(limit.value)} outside ${round(limit.min)}…${round(limit.max)}`).join(", ")
        });
        hits |= 16;
      }
      journalRemoved[index] = removed * voxelVolume();
      journalSweeps[index] = steps.length;
      journalFlags[index] = (removed ? 1 : 0) | hits;
    }

    // Applies one motion (in full or up to `fraction`) on the CPU.
    function apply(index, fraction) {
      const planned = plan(index, fraction);
      if (!planned) {
        closeStale(index);
        return;
      }
      executeCpu(planned);
      commit(planned);
    }

    function takeCheckpoint(index) {
      const last = checkpoints[checkpoints.length - 1];
      if (index - last.index < checkpointEvery) return;
      checkpoints.push({ index, snap: V.snapshot(grid), events: events.length });
      // Keep the total size under the budget by thinning the older checkpoints (the first stays).
      let bytes = checkpoints.reduce((sum, point) => sum + V.snapshotBytes(point.snap), 0);
      while (bytes > checkpointBytes && checkpoints.length > 2) {
        let drop = 1;
        let smallest = Infinity;
        for (let n = 1; n < checkpoints.length - 1; n += 1) {
          const gap = checkpoints[n + 1].index - checkpoints[n - 1].index;
          if (gap < smallest) { smallest = gap; drop = n; }
        }
        bytes -= V.snapshotBytes(checkpoints[drop].snap);
        checkpoints.splice(drop, 1);
      }
    }

    // Back to the stock after motion `index` (-1: the untouched stock) from the nearest checkpoint.
    function rewind(index) {
      let point = checkpoints[0];
      for (const candidate of checkpoints) if (candidate.index <= index && candidate.index > point.index) point = candidate;
      V.restore(grid, point.snap);
      while (checkpoints.length && checkpoints[checkpoints.length - 1].index > point.index) checkpoints.pop();
      events.length = point.events;
      active.clear();
      for (let n = point.index + 1; n < motions.length; n += 1) {
        journalRemoved[n] = 0;
        journalSweeps[n] = 0;
        journalFlags[n] = 0;
      }
      cursor = point.index;
      partial = null;
    }

    // Brings the stock to motion `index` cut up to `fraction` (1 = whole). Works in slices: returns
    // { done } false when `budgetMs` ran out first (call again).
    function cutTo(index, fraction, budgetMs) {
      const goal = Math.min(index, motions.length - 1);
      const goalFraction = Math.max(0, Math.min(1, fraction === undefined ? 1 : fraction));
      if (goal < cursor || (partial && goal === partial.index && goalFraction < partial.fraction - 1e-9) || (partial && goal < partial.index)) {
        rewind(goal - 1);
      }
      const started = Date.now();
      const budget = Number(budgetMs) > 0 ? Number(budgetMs) : Infinity;
      while (cursor < goal) {
        const next = cursor + 1;
        if (next === goal && goalFraction < 1) break;
        apply(next, 1);
        cursor = next;
        partial = null;
        takeCheckpoint(cursor);
        if (Date.now() - started > budget) return { done: false };
      }
      if (goal > cursor && goalFraction < 1) {
        apply(goal, goalFraction);
        partial = { index: goal, fraction: goalFraction };
      }
      return { done: true };
    }

    // The same as cutTo with the stock operations of many motions run together by `gpu.execute(ops)`
    // (meimad-voxel-gpu.js) and committed in program order afterwards. Resolves { done }.
    async function cutToGpu(index, fraction, budgetMs, gpu) {
      const goal = Math.min(index, motions.length - 1);
      const goalFraction = Math.max(0, Math.min(1, fraction === undefined ? 1 : fraction));
      if (goal < cursor || (partial && goal === partial.index && goalFraction < partial.fraction - 1e-9) || (partial && goal < partial.index)) {
        rewind(goal - 1);
        gpu.invalidate();
      }
      const started = Date.now();
      const budget = Number(budgetMs) > 0 ? Number(budgetMs) : Infinity;
      const lastWhole = goalFraction < 1 ? goal - 1 : goal;
      while (cursor < lastWhole) {
        const batch = [];
        let opCount = 0;
        const planStarted = Date.now();
        for (let next = cursor + 1; next <= lastWhole; next += 1) {
          const planned = plan(next, 1);
          batch.push(planned || { index: next, empty: true });
          if (planned) opCount += planned.ops.length;
          // A checkpoint falls between batches; a batch stays small enough to keep the page responsive.
          if (next - checkpoints[checkpoints.length - 1].index >= checkpointEvery) break;
          if (opCount >= gpu.maxOps || Date.now() - planStarted > 30) break;
        }
        const ops = [];
        for (const planned of batch) if (!planned.empty) for (const op of planned.ops) ops.push(op);
        if (ops.length) await gpu.execute(ops);
        for (const planned of batch) {
          if (planned.empty) closeStale(planned.index);
          else commit(planned);
          cursor = planned.index;
          partial = null;
        }
        takeCheckpoint(cursor);
        if (cursor < lastWhole && Date.now() - started > budget) return { done: false };
      }
      if (goalFraction < 1 && goal > cursor) {
        const planned = plan(goal, goalFraction);
        if (planned) {
          if (planned.ops.length) await gpu.execute(planned.ops);
          commit(planned);
        }
        partial = { index: goal, fraction: goalFraction };
      }
      return { done: true };
    }

    function reset() {
      rewind(-1);
    }

    // The finished-part comparison: gouges (part material removed) and material left outside the part.
    function deviation(limitMm) {
      if (!target) return null;
      const cell = grid.cell;
      const cap = Math.max(1, Math.round((Number(limitMm) > 0 ? Number(limitMm) : 10) / cell));
      let remaining = 0;
      const surface = [];
      for (let b = 0; b < grid.state.length; b += 1) {
        const state = grid.state[b];
        if (state !== V.B_MAT && state !== V.B_MIX) continue;
        const bx = b % grid.nbx, by = Math.floor(b / grid.nbx) % grid.nby, bz = Math.floor(b / (grid.nbx * grid.nby));
        const ts = target.state[b];
        if (state === V.B_MAT && ts === V.B_MAT) continue;
        for (let lz = 0; lz < V.BS; lz += 1) {
          for (let ly = 0; ly < V.BS; ly += 1) {
            for (let lx = 0; lx < V.BS; lx += 1) {
              const i = (bx << 4) + lx, j = (by << 4) + ly, k = (bz << 4) + lz;
              if (V.voxel(grid, i, j, k) !== V.MAT || V.voxel(target, i, j, k) === V.MAT) continue;
              remaining += 1;
              if (surface.length < 40000 && (V.voxel(grid, i + 1, j, k) !== V.MAT || V.voxel(grid, i - 1, j, k) !== V.MAT
                || V.voxel(grid, i, j + 1, k) !== V.MAT || V.voxel(grid, i, j - 1, k) !== V.MAT
                || V.voxel(grid, i, j, k + 1) !== V.MAT || V.voxel(grid, i, j, k - 1) !== V.MAT)) surface.push(i, j, k);
            }
          }
        }
      }
      // Thickness of what is left: from the exposed remaining voxels to the nearest part voxel.
      // Distance (in voxels) to the nearest voxel inside (or outside) the part, searched shell by shell;
      // a shell at Chebyshev radius r holds no point nearer than r, so the search ends once r passes the best.
      const nearest = (i, j, k, inside) => {
        let best = Infinity;
        for (let radius = 1; radius <= cap && radius < best; radius += 1) {
          for (let dk = -radius; dk <= radius; dk += 1) {
            for (let dj = -radius; dj <= radius; dj += 1) {
              for (let di = -radius; di <= radius; di += 1) {
                if (Math.max(Math.abs(di), Math.abs(dj), Math.abs(dk)) !== radius) continue;
                if ((V.voxel(target, i + di, j + dj, k + dk) === V.MAT) !== inside) continue;
                const distance = Math.hypot(di, dj, dk);
                if (distance < best) best = distance;
              }
            }
          }
        }
        return best === Infinity ? cap + 1 : best;
      };
      let maxRemaining = 0;
      const step = Math.max(1, Math.floor(surface.length / 3 / 4000));
      for (let n = 0; n < surface.length; n += 3 * step) maxRemaining = Math.max(maxRemaining, nearest(surface[n], surface[n + 1], surface[n + 2], true));
      // Depth of gouges: from cut part voxels to the nearest voxel outside the part.
      let maxGouge = 0;
      let gougeVoxels = 0;
      let sampled = 0;
      for (let b = 0; b < grid.state.length && sampled < 4000; b += 1) {
        const state = grid.state[b];
        if (state !== V.B_CUT && state !== V.B_MIX) continue;
        if (target.state[b] === V.B_AIR) continue;
        const bx = b % grid.nbx, by = Math.floor(b / grid.nbx) % grid.nby, bz = Math.floor(b / (grid.nbx * grid.nby));
        for (let index = 0; index < V.BS * V.BS * V.BS && sampled < 4000; index += 1) {
          const lx = index & 15, ly = (index >> 4) & 15, lz = index >> 8;
          const i = (bx << 4) + lx, j = (by << 4) + ly, k = (bz << 4) + lz;
          if (V.voxel(grid, i, j, k) !== V.CUT || V.voxel(target, i, j, k) !== V.MAT) continue;
          gougeVoxels += 1;
          if (gougeVoxels % 7 === 1) {
            sampled += 1;
            maxGouge = Math.max(maxGouge, nearest(i, j, k, false));
          }
        }
      }
      const firstGouge = events.find((event) => event.check === "gouge");
      return {
        maxGouge: grid.gouged ? Math.max(cell / 2, maxGouge * cell) : 0,
        maxRemainingStock: remaining ? maxRemaining * cell : 0,
        remainingCapped: maxRemaining > cap,
        gougeVoxelCount: grid.gouged,
        remainingVoxelCount: remaining,
        gougeVolume: grid.gouged * cell * cell * cell,
        remainingVolume: remaining * cell * cell * cell,
        firstGougeRow: firstGouge ? firstGouge.line : null,
        firstRemainingStockRow: null
      };
    }

    function stats() {
      const cell = grid.cell;
      const volume = cell * cell * cell;
      return {
        cell,
        motions: motions.length,
        cursor,
        removedVolume: grid.removed * volume,
        materialVolume: (grid.initialMaterial - grid.removed) * volume,
        collisions: events.filter((event) => event.check !== "gouge" && event.check !== "axisLimit").length,
        gouges: events.filter((event) => event.check === "gouge").length,
        axisLimits: events.filter((event) => event.check === "axisLimit").length,
        stops: events.filter((event) => event.severity === "stop").length,
        checkpoints: checkpoints.length
      };
    }

    function journal(from, to) {
      const rows = [];
      const end = Math.min(motions.length - 1, to === undefined ? cursor : to);
      for (let n = Math.max(0, from || 0); n <= end; n += 1) {
        const motion = motions[n];
        rows.push({
          index: n, ...rowOf(motion), tool: motion.tool,
          removedVolume: journalRemoved[n], sweeps: journalSweeps[n],
          cut: Boolean(journalFlags[n] & 1), collision: Boolean(journalFlags[n] & 2), gouge: Boolean(journalFlags[n] & 4),
          rapidInMaterial: Boolean(journalFlags[n] & 8), axisLimit: Boolean(journalFlags[n] & 16)
        });
      }
      return rows;
    }

    return {
      grid,
      target,
      fixtures,
      policy,
      events,
      cutTo,
      cutToGpu,
      reset,
      deviation,
      stats,
      journal,
      toolNotes: () => Object.fromEntries(notes),
      profile: (motion) => profileFor(motion),
      get cursor() { return cursor; },
      get partial() { return partial; },
      meshChunk: (key, view) => MESH.meshChunk(grid, key, view),
      exportTriangles: () => MESH.triangles(grid)
    };
  }

  const round = (value) => Math.round(Number(value) * 1000) / 1000;

  return { create, DEFAULT_POLICY, TYPES, normalizePolicy };
});
