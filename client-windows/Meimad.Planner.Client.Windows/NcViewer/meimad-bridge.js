"use strict";

// Meimad Planner replacement for the upstream Electron preload (desktop/preload.js). It gives the
// unmodified renderer.js and media/preview.js the same window.fanucDesktop and acquireVsCodeApi()
// contracts, carried over WebView2 web messages to the .NET NcViewerSession:
//   page -> host  { kind: "invoke", id, method, args }  answered by { kind: "result", id, ok, value | error }
//                 { kind: "send", channel, payload }     fire and forget
//   host -> page  { kind: "event", channel, payload }
// A render event names its packed model by URL (served from memory by the window) so large
// toolpaths never travel inside a web message.
(() => {
  const webview = window.chrome && window.chrome.webview;
  const PREVIEW_MESSAGE_TYPES = new Set([
    "ready",
    "g30Changed",
    "macrosChanged",
    "machineChanged",
    "workOffsetsChanged",
    "reloadParameters",
    "selectLine",
    "playbackLine"
  ]);
  const pending = new Map();
  const listeners = new Map();
  let nextId = 1;
  let queue = Promise.resolve();

  function post(message) {
    if (!webview) throw new Error("The NC viewer host is unavailable.");
    webview.postMessage(message);
  }

  function invoke(method, ...args) {
    return new Promise((resolve, reject) => {
      const id = nextId++;
      pending.set(id, { resolve, reject });
      try {
        post({ kind: "invoke", id, method, args });
      } catch (error) {
        pending.delete(id);
        reject(error);
      }
    });
  }

  function send(channel, payload) {
    try {
      post({ kind: "send", channel, payload: payload === undefined ? null : payload });
    } catch (error) {
      console.error(error);
    }
  }

  function listen(channel, callback) {
    if (typeof callback !== "function") return () => {};
    if (!listeners.has(channel)) listeners.set(channel, new Set());
    listeners.get(channel).add(callback);
    return () => listeners.get(channel)?.delete(callback);
  }

  function emit(channel, payload) {
    for (const callback of [...(listeners.get(channel) || [])]) {
      try {
        callback(payload);
      } catch (error) {
        console.error(error);
      }
    }
  }

  async function handle(message) {
    if (message.kind === "result") {
      const request = pending.get(message.id);
      if (!request) return;
      pending.delete(message.id);
      if (message.ok) request.resolve(message.value);
      else request.reject(new Error(message.error || "The NC viewer host reported an error."));
      return;
    }
    if (message.kind !== "event") return;
    if (message.channel === "preview:render" && message.payload && message.payload.modelUrl) {
      const { modelUrl, ...render } = message.payload;
      let packed;
      try {
        const response = await fetch(modelUrl, { cache: "no-store" });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        packed = await response.text();
      } catch (error) {
        emit("app:status", { message: `The toolpath could not be loaded: ${error.message}`, level: "error" });
        return;
      }
      emit("preview:render", { ...render, packed });
      return;
    }
    emit(message.channel, message.payload);
  }

  if (webview) {
    webview.addEventListener("message", (event) => {
      const message = event.data;
      if (!message || typeof message !== "object") return;
      // Events keep their order (a render is followed by its document state and decorations);
      // the model download of a render must finish before later events are applied.
      queue = queue.then(() => handle(message)).catch((error) => console.error(error));
    });
  }

  const onChannel = (channel) => (callback) => listen(channel, callback);

  window.fanucDesktop = Object.freeze({
    getInitialState: () => invoke("getInitialState"),
    openFile: () => invoke("openFile"),
    newFile: () => invoke("newFile"),
    saveFile: (text) => invoke("saveFile", text),
    saveFileAs: (text) => invoke("saveFileAs", text),
    updateDocument: (text, line) => send("document:update", { text, line }),
    markDirty: () => send("document:dirty"),
    confirmFlush: (id) => send("document:flushed", { id }),
    onFlushRequest: onChannel("document:flush-request"),
    updateSelection: (line) => send("document:selection", line),
    getSettings: () => invoke("getSettings"),
    openSettings: () => invoke("openSettings"),
    saveSettings: (partial) => invoke("saveSettings", partial),
    chooseProgramMemoryFolder: (machineId) => invoke("chooseProgramMemoryFolder", machineId),
    reloadXml: () => invoke("reloadXml"),
    setApiKey: () => Promise.reject(new Error("AI features are not part of Meimad Planner.")),
    clearApiKey: () => Promise.reject(new Error("AI features are not part of Meimad Planner.")),
    getToolTable: () => invoke("getToolTable"),
    saveToolTable: (table) => invoke("saveToolTable", table),
    showToolTable: () => invoke("showToolTable"),
    onRender: onChannel("preview:render"),
    onSelection: onChannel("editor:selection"),
    onPreviewSelection: onChannel("preview:selection"),
    onDocumentState: onChannel("document:state"),
    onDecorations: onChannel("editor:decorations"),
    onPlaybackLine: (callback) => listen("editor:decorations", (payload) => callback(payload?.playbackLine ?? null)),
    onStatus: onChannel("app:status"),
    onCommand: onChannel("app:command"),
    onMessage: (callback) => {
      const disposers = [
        listen("preview:render", (payload) => callback("render", payload)),
        listen("editor:selection", (payload) => callback("selection", payload)),
        listen("document:state", (payload) => callback("document", payload)),
        listen("editor:decorations", (payload) => callback("decorations", payload)),
        listen("app:status", (payload) => callback("status", payload)),
        listen("app:command", (payload) => callback("command", payload))
      ];
      return () => disposers.forEach((dispose) => dispose());
    },
    meimad: Object.freeze({
      state: () => invoke("meimadState"),
      applyFormat: (text, dialect) => invoke("meimadApplyFormat", text, dialect),
      useForRelease: (text) => invoke("meimadUseForRelease", text),
      validate: (text) => invoke("meimadValidate", text),
      editCopy: () => invoke("meimadEditCopy"),
      release: (text, options) => invoke("meimadRelease", text, options),
      chooseToolTable: () => invoke("meimadChooseToolTable"),
      onMode: onChannel("meimad:mode"),
      localization: () => invoke("meimadLocalization"),
      translate: (texts) => invoke("meimadTranslate", texts),
      onLocalization: onChannel("meimad:localization")
    })
  });

  let webviewState;
  window.acquireVsCodeApi = () => Object.freeze({
    postMessage(message) {
      if (message && PREVIEW_MESSAGE_TYPES.has(message.type)) send("preview:message", message);
    },
    getState() {
      return webviewState;
    },
    setState(value) {
      webviewState = value;
      return value;
    }
  });
})();
