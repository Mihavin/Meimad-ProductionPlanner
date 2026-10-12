"use strict";

// Meimad Planner: one light rig for every Meimad mesh in the 3D view (the machined stock, machine
// models, fixtures and rest material). The key light shines from over the viewer's left shoulder and
// turns with the camera, so whichever way the view is turned the faces toward the viewer are lit,
// walls stand apart from floors and a cut reads by its shading; a weak fill from the lower right keeps
// faces turned away from being black. The toolpath lines are unlit and are not affected.
(() => {
  const rigs = new WeakMap(); // scene -> rig

  function follow(hooks, rig) {
    const camera = hooks.camera;
    if (!camera || !rig) return;
    const THREE = hooks.THREE;
    const { key, fill, right, up, back } = rig.userData;
    camera.updateMatrixWorld();
    right.setFromMatrixColumn(camera.matrixWorld, 0);
    up.setFromMatrixColumn(camera.matrixWorld, 1);
    back.setFromMatrixColumn(camera.matrixWorld, 2);
    // Directions only: both lights aim at the origin (their targets stay out of the scene).
    key.position.set(0, 0, 0).addScaledVector(back, 1).addScaledVector(up, 0.55).addScaledVector(right, -0.4);
    fill.position.set(0, 0, 0).addScaledVector(back, 0.3).addScaledVector(up, -0.5).addScaledVector(right, 0.85);
    if (!(key.position.lengthSq() > 0)) key.position.copy(new THREE.Vector3(1, -0.6, 1.4));
  }

  // The rig of the current 3D view, created on first use.
  function ensure(hooks) {
    if (!hooks || !hooks.scene || !hooks.THREE) return null;
    let rig = rigs.get(hooks.scene);
    if (rig) return rig;
    const THREE = hooks.THREE;
    rig = new THREE.Group();
    rig.name = "meimad-lights";
    const ambient = new THREE.HemisphereLight(0xffffff, 0x3a424c, 0.5);
    const key = new THREE.DirectionalLight(0xffffff, 2.5);
    const fill = new THREE.DirectionalLight(0xdfe8ff, 0.5);
    rig.add(ambient, key, fill);
    rig.userData = { key, fill, right: new THREE.Vector3(), up: new THREE.Vector3(), back: new THREE.Vector3() };
    hooks.scene.add(rig);
    rigs.set(hooks.scene, rig);
    follow(hooks, rig);
    if (typeof hooks.on === "function") {
      hooks.on("beforeRender", (scene) => {
        if (scene === hooks.scene) follow(hooks, rig);
      });
    }
    return rig;
  }

  window.MeimadLighting = Object.freeze({ ensure });
})();
