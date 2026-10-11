/*
 * Meimad Planner Machine Designer: forward kinematics of a machine simulation definition (schema v1,
 * docs/machine-simulation-definition.md). Pure functions, no DOM or three.js, so the client tests run
 * them in V8 against the C# evaluator (MachineKinematics).
 *
 * Every component frame is parallel to the machine frame at all axes zero. A component's transform in
 * its parent's frame at reading q is
 *   linear:  T(position) · T(scale·q·d)
 *   rotary:  T(position) · T(p) · R(d, scale·q) · T(−p),  p = point − position,
 * with d the joint direction (normalized) and R a right-hand rotation; ancestors compose root first, so
 * q = 0 gives exactly the authored pose. Matrices are row-major arrays of 16 numbers.
 */
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory();
  else root.MeimadDesigner = factory();
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  const DEGREE = Math.PI / 180;

  function identity() {
    return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
  }

  function translation(x, y, z) {
    return [1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1];
  }

  function multiply(a, b) {
    const out = new Array(16).fill(0);
    for (let row = 0; row < 4; row += 1) {
      for (let column = 0; column < 4; column += 1) {
        let sum = 0;
        for (let k = 0; k < 4; k += 1) sum += a[row * 4 + k] * b[k * 4 + column];
        out[row * 4 + column] = sum;
      }
    }
    return out;
  }

  function vector(value, fallback) {
    return Array.isArray(value) && value.length === 3 && value.every(Number.isFinite) ? value : fallback;
  }

  function normalize(value) {
    const v = vector(value, [0, 0, 1]);
    const length = Math.hypot(v[0], v[1], v[2]);
    return length > 1e-12 ? [v[0] / length, v[1] / length, v[2] / length] : [0, 0, 1];
  }

  /** Right-hand rotation about the unit vector `axis` by `degrees`. */
  function rotation(axis, degrees) {
    const [x, y, z] = normalize(axis);
    const c = Math.cos(degrees * DEGREE);
    const s = Math.sin(degrees * DEGREE);
    const t = 1 - c;
    return [
      t * x * x + c, t * x * y - s * z, t * x * z + s * y, 0,
      t * x * y + s * z, t * y * y + c, t * y * z - s * x, 0,
      t * x * z - s * y, t * y * z + s * x, t * z * z + c, 0,
      0, 0, 0, 1
    ];
  }

  function reading(axes, joint) {
    const value = axes && joint ? Number(axes[joint.axis]) : 0;
    return Number.isFinite(value) ? value : 0;
  }

  /** The component's transform in its parent's frame at the axis readings. */
  function local(node, axes) {
    const position = vector(node.position, [0, 0, 0]);
    const base = translation(position[0], position[1], position[2]);
    const joint = node.joint;
    if (!joint || (joint.type !== "linear" && joint.type !== "rotary")) return base;
    const scale = Number.isFinite(joint.scale) && joint.scale !== 0 ? joint.scale : 1;
    const amount = reading(axes, joint) * scale;
    const d = normalize(joint.direction);
    if (joint.type === "linear") return multiply(base, translation(d[0] * amount, d[1] * amount, d[2] * amount));
    const point = vector(joint.point, position);
    const p = [point[0] - position[0], point[1] - position[1], point[2] - position[2]];
    return multiply(base, multiply(translation(p[0], p[1], p[2]), multiply(rotation(d, amount), translation(-p[0], -p[1], -p[2]))));
  }

  /**
   * World matrices of every component (an object keyed by id). A component whose parent is missing, or
   * that is part of a cycle, is placed at the machine origin so the rest still draws.
   */
  function worldMatrices(nodes, axes) {
    const byId = new Map((nodes || []).map((node) => [node.id, node]));
    const world = new Map();
    const resolving = new Set();
    const resolve = (node) => {
      if (world.has(node.id)) return world.get(node.id);
      if (resolving.has(node.id)) return identity();
      resolving.add(node.id);
      const parent = node.parent && byId.has(node.parent) ? resolve(byId.get(node.parent)) : identity();
      resolving.delete(node.id);
      const matrix = multiply(parent, local(node, axes));
      world.set(node.id, matrix);
      return matrix;
    };
    for (const node of nodes || []) resolve(node);
    const result = {};
    for (const [id, matrix] of world) result[id] = matrix;
    return result;
  }

  function transformPoint(m, p) {
    return [
      m[0] * p[0] + m[1] * p[1] + m[2] * p[2] + m[3],
      m[4] * p[0] + m[5] * p[1] + m[6] * p[2] + m[7],
      m[8] * p[0] + m[9] * p[1] + m[10] * p[2] + m[11]
    ];
  }

  /** A mount (tool or workpiece) in machine coordinates, or null when its component is missing. */
  function mountPoint(world, mount) {
    if (!mount || !mount.node || !world[mount.node]) return null;
    return transformPoint(world[mount.node], vector(mount.position, [0, 0, 0]));
  }

  /**
   * The rotation line of a rotary component (or the travel direction of a linear one) in machine
   * coordinates, drawn for the selected component: { origin, direction }.
   */
  function jointLine(nodes, world, id) {
    const node = (nodes || []).find((value) => value.id === id);
    if (!node || !node.joint) return null;
    const parent = node.parent && world[node.parent] ? world[node.parent] : identity();
    const position = vector(node.position, [0, 0, 0]);
    const origin = node.joint.type === "rotary" ? vector(node.joint.point, position) : position;
    const d = normalize(node.joint.direction);
    const start = transformPoint(parent, origin);
    const tip = transformPoint(parent, [origin[0] + d[0], origin[1] + d[1], origin[2] + d[2]]);
    return { origin: start, direction: [tip[0] - start[0], tip[1] - start[1], tip[2] - start[2]], type: node.joint.type };
  }

  return { identity, translation, multiply, rotation, local, worldMatrices, transformPoint, mountPoint, jointLine };
});
