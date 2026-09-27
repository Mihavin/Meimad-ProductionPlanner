/*
 * Machine kinematic chains shared by the Node interpreters and the browser
 * preview. Matrices are row-major 4x4 arrays of 16 numbers; points are
 * { x, y, z } objects in the machine's native length unit.
 *
 * A chain is a tree of nodes. Each node's local transform is its fixed
 * origin followed by the motion of its axis (a translation along `direction`
 * for linear axes, a rotation about `direction` through `pivot` for rotary
 * axes). World transforms are parent-world * local. The node flagged
 * `toolMount` carries the spindle gauge point; the node flagged
 * `workpieceMount` carries the part (work offsets are applied on top of it).
 */
(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.CncKinematics = factory();
  }
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  const DEGREE = Math.PI / 180;

  function identity() {
    return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
  }

  function multiply(a, b) {
    const out = new Array(16);
    for (let row = 0; row < 4; row += 1) {
      for (let column = 0; column < 4; column += 1) {
        out[row * 4 + column] =
          a[row * 4] * b[column] +
          a[row * 4 + 1] * b[4 + column] +
          a[row * 4 + 2] * b[8 + column] +
          a[row * 4 + 3] * b[12 + column];
      }
    }
    return out;
  }

  function translation(x, y, z) {
    return [1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1];
  }

  function normalize(vector) {
    const length = Math.hypot(vector[0], vector[1], vector[2]);
    if (!(length > 0)) {
      throw new Error("Axis direction must be a non-zero vector");
    }
    return [vector[0] / length, vector[1] / length, vector[2] / length];
  }

  // Right-hand rotation of `degrees` about the unit `axis` (Rodrigues).
  function rotation(axis, degrees) {
    const [x, y, z] = normalize(axis);
    const angle = degrees * DEGREE;
    const c = Math.cos(angle);
    const s = Math.sin(angle);
    const t = 1 - c;
    return [
      t * x * x + c, t * x * y - s * z, t * x * z + s * y, 0,
      t * x * y + s * z, t * y * y + c, t * y * z - s * x, 0,
      t * x * z - s * y, t * y * z + s * x, t * z * z + c, 0,
      0, 0, 0, 1
    ];
  }

  function rotationAbout(axis, degrees, pivot) {
    if (!pivot || (!pivot[0] && !pivot[1] && !pivot[2])) {
      return rotation(axis, degrees);
    }
    return multiply(
      translation(pivot[0], pivot[1], pivot[2]),
      multiply(rotation(axis, degrees), translation(-pivot[0], -pivot[1], -pivot[2]))
    );
  }

  // Inverse of a rigid transform (rotation + translation only).
  function invertRigid(m) {
    const r = [m[0], m[4], m[8], m[1], m[5], m[9], m[2], m[6], m[10]];
    const tx = -(r[0] * m[3] + r[1] * m[7] + r[2] * m[11]);
    const ty = -(r[3] * m[3] + r[4] * m[7] + r[5] * m[11]);
    const tz = -(r[6] * m[3] + r[7] * m[7] + r[8] * m[11]);
    return [r[0], r[1], r[2], tx, r[3], r[4], r[5], ty, r[6], r[7], r[8], tz, 0, 0, 0, 1];
  }

  function transformPoint(m, point) {
    const x = point.x;
    const y = point.y;
    const z = point.z;
    return {
      x: m[0] * x + m[1] * y + m[2] * z + m[3],
      y: m[4] * x + m[5] * y + m[6] * z + m[7],
      z: m[8] * x + m[9] * y + m[10] * z + m[11]
    };
  }

  function transformVector(m, vector) {
    return {
      x: m[0] * vector.x + m[1] * vector.y + m[2] * vector.z,
      y: m[4] * vector.x + m[5] * vector.y + m[6] * vector.z,
      z: m[8] * vector.x + m[9] * vector.y + m[10] * vector.z
    };
  }

  function isIdentity(m, tolerance = 1e-12) {
    const reference = identity();
    return m.every((value, index) => Math.abs(value - reference[index]) <= tolerance);
  }

  function vectorOf(value, fallback) {
    if (Array.isArray(value) && value.length === 3 && value.every(Number.isFinite)) {
      return value.slice();
    }
    return fallback ? fallback.slice() : [0, 0, 0];
  }

  function buildChain(definition) {
    const nodes = Array.isArray(definition?.nodes) ? definition.nodes : [];
    if (!nodes.length) {
      throw new Error("Kinematic chain has no nodes");
    }
    const byId = new Map();
    for (const node of nodes) {
      if (!node || typeof node.id !== "string" || !node.id) {
        throw new Error("Every kinematic node needs an id");
      }
      if (byId.has(node.id)) {
        throw new Error(`Kinematic node "${node.id}" is defined twice`);
      }
      const type = node.type || (node.axis ? "linear" : "fixed");
      if (!["fixed", "linear", "rotary", "spindle"].includes(type)) {
        throw new Error(`Kinematic node "${node.id}" has unsupported type "${type}"`);
      }
      if ((type === "linear" || type === "rotary") && typeof node.axis !== "string") {
        throw new Error(`Kinematic node "${node.id}" moves but names no axis`);
      }
      byId.set(node.id, {
        ...node,
        type,
        origin: vectorOf(node.origin),
        direction: normalize(vectorOf(node.direction, [0, 0, 1])),
        pivot: vectorOf(node.pivot),
        sense: node.sense === -1 ? -1 : 1,
        scale: Number.isFinite(node.scale) && node.scale !== 0 ? node.scale : 1,
        children: []
      });
    }
    const roots = [];
    for (const node of byId.values()) {
      if (node.parent === undefined || node.parent === null) {
        roots.push(node);
        continue;
      }
      const parent = byId.get(node.parent);
      if (!parent) {
        throw new Error(`Kinematic node "${node.id}" names missing parent "${node.parent}"`);
      }
      parent.children.push(node);
    }
    if (roots.length !== 1) {
      throw new Error(`Kinematic chain must have exactly one root node (found ${roots.length})`);
    }
    const order = [];
    const visiting = new Set();
    const visit = (node) => {
      if (visiting.has(node.id)) {
        throw new Error(`Kinematic chain has a cycle at "${node.id}"`);
      }
      visiting.add(node.id);
      order.push(node);
      node.children.forEach(visit);
    };
    visit(roots[0]);
    if (order.length !== byId.size) {
      throw new Error("Kinematic chain contains unreachable nodes");
    }
    const toolMount = order.find((node) => node.toolMount);
    const workpieceMount = order.find((node) => node.workpieceMount);
    if (!toolMount || !workpieceMount) {
      throw new Error("Kinematic chain needs one toolMount node and one workpieceMount node");
    }
    const pathTo = (target) => {
      const path = [];
      let cursor = target;
      while (cursor) {
        path.unshift(cursor);
        cursor = cursor.parent === undefined || cursor.parent === null
          ? undefined
          : byId.get(cursor.parent);
      }
      return path;
    };
    return {
      root: roots[0],
      nodes: byId,
      order,
      toolMount,
      workpieceMount,
      toolPath: pathTo(toolMount),
      workpiecePath: pathTo(workpieceMount),
      axes: [...new Set(order.filter((node) => node.axis).map((node) => node.axis))]
    };
  }

  function axisValue(axes, name) {
    if (!axes) {
      return 0;
    }
    const value = axes[name] ?? axes[String(name).toLowerCase()];
    return Number.isFinite(value) ? value : 0;
  }

  function localTransform(node, axes) {
    let local = node.origin[0] || node.origin[1] || node.origin[2]
      ? translation(node.origin[0], node.origin[1], node.origin[2])
      : identity();
    if (node.type === "linear") {
      const value = axisValue(axes, node.axis) * node.sense * node.scale;
      local = multiply(local, translation(
        node.direction[0] * value,
        node.direction[1] * value,
        node.direction[2] * value
      ));
    } else if (node.type === "rotary") {
      const value = axisValue(axes, node.axis) * node.sense * node.scale;
      local = multiply(local, rotationAbout(node.direction, value, node.pivot));
    }
    return local;
  }

  function pathTransform(path, axes) {
    let world = identity();
    for (const node of path) {
      world = multiply(world, localTransform(node, axes));
    }
    return world;
  }

  function evaluateChain(chain, axes) {
    const world = new Map();
    for (const node of chain.order) {
      const parent = node.parent === undefined || node.parent === null
        ? identity()
        : world.get(node.parent);
      world.set(node.id, multiply(parent, localTransform(node, axes)));
    }
    return world;
  }

  function workpieceTransform(chain, axes) {
    return pathTransform(chain.workpiecePath, axes);
  }

  function toolMountTransform(chain, axes) {
    return pathTransform(chain.toolPath, axes);
  }

  // Tool axis (from tip toward spindle) expressed in the workpiece frame.
  function toolAxisInWorkpiece(chain, axes) {
    const tool = toolMountTransform(chain, axes);
    const part = workpieceTransform(chain, axes);
    const worldAxis = transformVector(tool, { x: 0, y: 0, z: 1 });
    return transformVector(invertRigid(part), worldAxis);
  }

  function dot(a, b) {
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  }

  function cross(a, b) {
    return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
  }

  // Right-hand rotation of vector v about unit axis d by `radians`.
  function rotateVector(v, d, radians) {
    const c = Math.cos(radians);
    const s = Math.sin(radians);
    const k = cross(d, v);
    const along = dot(d, v) * (1 - c);
    return [v[0] * c + k[0] * s + d[0] * along, v[1] * c + k[1] * s + d[1] * along, v[2] * c + k[2] * s + d[2] * along];
  }

  function normalizeDegrees(degrees) {
    let value = ((degrees + 180) % 360 + 360) % 360 - 180;
    if (Math.abs(value + 180) < 1e-12) value = -180;
    return value;
  }

  // Rotary positions that point the spindle along `normal` (a direction in
  // the workpiece frame) on a table-table machine: two rotary nodes on the
  // workpiece path, the outer one (master) carrying the inner one (slave).
  // Returns { master, slave, solutions: [{ [master]: deg, [slave]: deg,
  // singular }] } with angles in -180..180, or undefined for other layouts.
  function tableToolAxisSolutions(chain, normal) {
    const rotaries = chain.workpiecePath.filter((node) => node.type === "rotary");
    if (rotaries.length !== 2 || chain.toolPath.some((node) => node.type === "rotary")) return undefined;
    const [outer, inner] = rotaries;
    const n = normalize([normal.x, normal.y, normal.z]);
    // Linear and fixed nodes do not rotate, so the spindle direction is the
    // tool mount's +Z in the world frame.
    const toolWorld = transformVector(toolMountTransform(chain, {}), { x: 0, y: 0, z: 1 });
    const t = normalize([toolWorld.x, toolWorld.y, toolWorld.z]);
    const d1 = outer.direction;
    const d2 = inner.direction;
    // Need R1(p1) R2(p2) n = t, so d2 . R1(-p1) t = d2 . n. Expanding
    // R1(-p1) t with Rodrigues gives a cos(p1) + b sin(p1) + c.
    const d1t = dot(d1, t);
    const a = dot(d2, t) - d1t * dot(d2, d1);
    const b = -dot(d2, cross(d1, t));
    const c = d1t * dot(d2, d1);
    const amplitude = Math.hypot(a, b);
    const target = dot(d2, n) - c;
    if (amplitude < 1e-12 || Math.abs(target) > amplitude * (1 + 1e-9)) return { master: outer.axis, slave: inner.axis, solutions: [] };
    const phase = Math.atan2(b, a);
    const spread = Math.acos(Math.max(-1, Math.min(1, target / amplitude)));
    const outerAngles = spread < 1e-9 ? [phase] : [phase + spread, phase - spread];
    const solutions = outerAngles.map((p1) => {
      const r = rotateVector(t, d1, -p1);
      const q = n;
      const qPerp = q.map((value, index) => value - d2[index] * dot(d2, q));
      const rPerp = r.map((value, index) => value - d2[index] * dot(d2, r));
      const singular = Math.hypot(...qPerp) < 1e-9 || Math.hypot(...rPerp) < 1e-9;
      const p2 = singular ? 0 : Math.atan2(dot(d2, cross(qPerp, rPerp)), dot(qPerp, rPerp));
      return {
        [outer.axis]: normalizeDegrees(p1 / DEGREE / (outer.sense * outer.scale)),
        [inner.axis]: normalizeDegrees(p2 / DEGREE / (inner.sense * inner.scale)),
        singular
      };
    });
    return { master: outer.axis, slave: inner.axis, solutions };
  }

  return {
    DEGREE,
    buildChain,
    evaluateChain,
    identity,
    invertRigid,
    isIdentity,
    multiply,
    normalize,
    normalizeDegrees,
    rotation,
    rotationAbout,
    tableToolAxisSolutions,
    toolAxisInWorkpiece,
    toolMountTransform,
    transformPoint,
    transformVector,
    translation,
    workpieceTransform
  };
});
