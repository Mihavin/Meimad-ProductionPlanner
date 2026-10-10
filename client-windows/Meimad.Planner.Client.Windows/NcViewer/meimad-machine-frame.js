"use strict";

// Meimad Planner: real machine kinematics in the mill Machine view. The vendored viewer poses the
// machine nodes with all joints but places the part (toolpath, stock, fixtures) with the rotary axes
// only, which is right while X, Y and Z move the spindle. On a machine whose table travels in X/Y
// (a C-frame VMC such as the Haas VF series) the part must ride on the table, so just before each
// draw this script sets the part frame to the workpiece mount at all joints. The joints are the same
// machine coordinates the viewer computes from the tool pose (meimad-axes-core.js), so the tool tip
// stays on the toolpath while the table, not the column, moves.
(() => {
  const core = window.MeimadAxes;
  const kinematics = window.CncKinematics;
  if (!core || !kinematics || !core.partMatrix) return;

  let hooks = null;
  let model = null;
  let chain = null;
  let calculator = null;
  let moving = false;
  let pose = null;

  function machineFrame() {
    return Boolean(hooks && hooks.upstream && hooks.upstream.frame === "machine" && model && model.kind === "mill");
  }

  function apply(target, rows) {
    if (!target) return;
    target.matrix.set(
      rows[0], rows[1], rows[2], rows[3],
      rows[4], rows[5], rows[6], rows[7],
      rows[8], rows[9], rows[10], rows[11],
      rows[12], rows[13], rows[14], rows[15]);
    target.matrixWorldNeedsUpdate = true;
  }

  function beforeRender() {
    if (!moving || !machineFrame()) return;
    const joints = pose ? core.poseJoints(model, kinematics, pose, calculator) : null;
    const rows = core.partMatrix(model, kinematics, joints, chain);
    if (!rows) return;
    apply(hooks.contentRoot, rows);
    apply(hooks.gridRoot, rows);
    apply(hooks.pickRoot, rows);
    // The travel envelope drawn by the viewer is the spindle's; with a travelling table it would
    // mislead, so it is hidden.
    for (const child of (hooks.machineRoot && hooks.machineRoot.children) || []) {
      if (child.userData && child.userData.envelope) child.visible = false;
    }
  }

  function attach(value) {
    if (!value || hooks === value || !value.captured) return;
    hooks = value;
    hooks.on("modelApplied", (next) => {
      model = next || null;
      chain = model && model.kind === "mill" ? core.chainFor(model, kinematics) : null;
      calculator = model ? core.createCalculator(model, kinematics) : null;
      moving = core.workpieceMovesLinearly(chain);
      pose = null;
    });
    hooks.on("toolPose", (next) => { pose = next || null; });
    hooks.on("beforeRender", beforeRender);
  }

  if (window.meimadViewer3d) attach(window.meimadViewer3d);
  window.addEventListener("meimad:viewer3d", (event) => attach(event.detail));
})();
