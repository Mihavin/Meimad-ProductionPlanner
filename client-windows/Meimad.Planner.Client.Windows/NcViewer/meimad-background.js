"use strict";

// Meimad Planner: the 3D toolpath view's background (Preview settings → 3D view background): a vertical
// gradient or one color, saved with this PC's viewer settings. The vendored viewer clears its canvas
// to an opaque dark color; meimad-viewer3d.js clears it transparent instead (on-screen draws only, not
// the picking pass) while a background is set here, so the canvas's CSS background shows behind the
// toolpath. The settings form is the vendored one: meimad-bridge.js adds these fields when it saves and
// reports every settings answer through the "meimad:settings" event.
(() => {
  const DEFAULTS = Object.freeze({ backgroundMode: "gradient", backgroundTop: "#33475B", backgroundBottom: "#0B1016" });
  const COLOR = /^#[0-9a-f]{6}$/i;
  const byId = (id) => document.getElementById(id);
  const elements = {
    canvas: byId("toolpathCanvas"),
    mode: byId("settingsBackgroundMode"),
    top: byId("settingsBackgroundTop"),
    bottom: byId("settingsBackgroundBottom"),
    reset: byId("settingsBackgroundReset"),
    dialog: byId("settingsModal")
  };
  if (!elements.canvas || !elements.mode) return;

  let saved = { ...DEFAULTS };
  let hooks = window.meimadViewer3d || null;

  function normalize(value) {
    const source = value || {};
    return {
      backgroundMode: source.backgroundMode === "solid" ? "solid" : "gradient",
      backgroundTop: COLOR.test(source.backgroundTop || "") ? source.backgroundTop.toUpperCase() : DEFAULTS.backgroundTop,
      backgroundBottom: COLOR.test(source.backgroundBottom || "") ? source.backgroundBottom.toUpperCase() : DEFAULTS.backgroundBottom
    };
  }

  function formValues() {
    return normalize({ backgroundMode: elements.mode.value, backgroundTop: elements.top.value, backgroundBottom: elements.bottom.value });
  }

  function fill(value) {
    elements.mode.value = value.backgroundMode;
    elements.top.value = value.backgroundTop.toLowerCase();
    elements.bottom.value = value.backgroundBottom.toLowerCase();
    elements.bottom.disabled = value.backgroundMode !== "gradient";
  }

  function apply(value) {
    elements.canvas.style.background = value.backgroundMode === "gradient"
      ? `linear-gradient(to bottom, ${value.backgroundTop}, ${value.backgroundBottom})`
      : value.backgroundTop;
    if (hooks && hooks.renderer) {
      hooks.renderer.meimadTransparentBackground = true;
      try {
        hooks.upstream.render();
      } catch {
        // No model yet; the next draw uses the background.
      }
    }
  }

  window.addEventListener("meimad:settings", (event) => {
    saved = normalize(event.detail);
    fill(saved);
    apply(saved);
  });
  window.addEventListener("meimad:viewer3d", (event) => {
    hooks = event.detail;
    apply(saved);
  });

  // Changes show at once; Cancel (closing without saving) puts the saved background back.
  for (const input of [elements.mode, elements.top, elements.bottom]) {
    input.addEventListener("input", () => {
      const value = formValues();
      elements.bottom.disabled = value.backgroundMode !== "gradient";
      apply(value);
    });
  }
  elements.reset?.addEventListener("click", () => {
    fill(DEFAULTS);
    apply(DEFAULTS);
  });
  elements.dialog?.addEventListener("close", () => {
    fill(saved);
    apply(saved);
  });

  window.meimadBackground = Object.freeze({ formValues });
  fill(saved);
  apply(saved);
})();
