using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// The ES module that wraps the vendored 3D viewer (NcViewer/meimad-viewer3d.js) run in Node
/// against a fake upstream viewer: it must capture the scene objects the upstream creates from a
/// real module namespace (a derived object cannot shadow namespace exports, which broke the 3D
/// view once) and forward every wrapped call. three.js assigns WebGLRenderer.render in its
/// constructor, so the wrapper's draw hook (the 3D view background, the travelling-table frame)
/// must wrap that instance property; a class method never ran. The Path selector patches the line
/// shader and keeps every instance in the upstream viewer. Skipped when Node is not installed.
/// </summary>
public sealed class NcViewerWrapperModuleTests
{
    [Fact]
    public void Wrapper_captures_the_upstream_scene_from_a_module_namespace_and_forwards_calls()
    {
        var node = FindNode();
        if (node is null) return; // Node is optional on build agents; the page is smoke-tested in the client.
        var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.NcViewerWrapper.Tests", Guid.NewGuid().ToString("N"));
        var desktop = Path.Combine(root, "desktop");
        var media = Path.Combine(root, "media");
        Directory.CreateDirectory(desktop);
        Directory.CreateDirectory(media);
        try
        {
            File.Copy(Path.Combine(RepositoryRoot(), "client-windows", "Meimad.Planner.Client.Windows", "NcViewer", "meimad-viewer3d.js"),
                Path.Combine(desktop, "meimad-viewer3d.js"));
            File.Copy(Path.Combine(RepositoryRoot(), "client-windows", "Meimad.Planner.Client.Windows", "NcViewer", "meimad-path-visibility-core.js"),
                Path.Combine(desktop, "meimad-path-visibility-core.cjs"));
            File.WriteAllText(Path.Combine(desktop, "three-fake.mjs"), """
                export class Scene { constructor() { this.isScene = true; } }
                export class Group { constructor() { this.children = []; } add(...c) { this.children.push(...c); } }
                export class OrthographicCamera {}
                export class WebGLRenderer {
                  constructor(parameters) {
                    this.ok = true; this.parameters = parameters; this.drawn = []; this.clearAlpha = 1; this.alphaDuringDraw = [];
                    // As three.js does: render is an instance property, not a prototype method.
                    this.render = (scene) => { this.drawn.push(scene); this.alphaDuringDraw.push(this.clearAlpha); };
                  }
                  getRenderTarget() { return null; }
                  getClearAlpha() { return this.clearAlpha; }
                  setClearAlpha(value) { this.clearAlpha = value; }
                }
                export class ShaderMaterial { constructor(parameters = {}) { Object.assign(this, parameters); } }
                export const NoBlending = 0;
                export class Vector3 { constructor(x = 0, y = 0, z = 0) { this.x = x; this.y = y; this.z = z; } }
                """);
            File.WriteAllText(Path.Combine(media, "viewer3d.js"), """
                export const SEGMENT_STYLES = { feed: { color: "#fff" } };
                export function createToolpathViewer({ THREE, canvas, onRender }) {
                  const scene = new THREE.Scene();
                  const camera = new THREE.OrthographicCamera();
                  const contentRoot = new THREE.Group();
                  const machineRoot = new THREE.Group();
                  const gridRoot = new THREE.Group();
                  const pickScene = new THREE.Scene();
                  const pickRoot = new THREE.Group();
                  const renderer = new THREE.WebGLRenderer({ canvas });
                  const line = new THREE.ShaderMaterial({
                    uniforms: { opacity: { value: 1 } },
                    vertexShader: "attribute float instancePick; void main() { vColor = instanceColor; if (instanceStyle.z < 0.5) { } }"
                  });
                  const state = { model: undefined, playback: undefined, frame: "part", view: "iso", target: new THREE.Vector3(), up: new THREE.Vector3(0, 0, 1), front: new THREE.Vector3(0, -1, 0) };
                  return {
                    dispose() {}, fit() {}, pick() {}, pointerDown() {}, pointerMove() {}, pointerUp() {},
                    render() { onRender?.(); }, resize() {},
                    setFilters() {}, setFrame(f) { state.frame = f; }, setModel(m) { state.model = m; }, setOptions() {},
                    setPlayback(p) { state.playback = p; }, setSelection() {}, setToolPose() {}, setView(v) { state.view = v; }, wheel() {},
                    get frame() { return state.frame; }, get view() { return state.view; }, get kind() { return state.model?.kind || "mill"; },
                    _internals: { scene, contentRoot, machineRoot, camera, renderer, line, state }
                  };
                }
                """);
            File.WriteAllText(Path.Combine(desktop, "probe.mjs"), """
                import * as THREE from "./three-fake.mjs";
                import { createRequire } from "node:module";
                const events = [];
                globalThis.window = { dispatchEvent(e) { events.push(e.type); } };
                globalThis.window.MeimadPathVisibility = createRequire(import.meta.url)("./meimad-path-visibility-core.cjs");
                globalThis.CustomEvent = class { constructor(type, init) { this.type = type; this.detail = init?.detail; } };
                const wrapper = await import("./meimad-viewer3d.js");
                let renders = 0;
                const viewer = wrapper.createToolpathViewer({ THREE, canvas: {}, onRender: () => { renders += 1; } });
                const hooks = globalThis.window.meimadViewer3d;
                const seen = [];
                hooks.on("playback", (p) => seen.push(["playback", p?.completedIndex]));
                hooks.on("model", (m) => seen.push(["model", m.kind]));
                hooks.on("toolPose", (pose) => seen.push(["toolPose", pose?.radius]));
                hooks.on("filters", (f) => seen.push(["filters", f?.tool]));
                viewer.setModel({ kind: "lathe" });
                viewer.setPlayback({ completedIndex: 4 });
                viewer.setToolPose({ radius: 3 });
                viewer.setFilters({ tool: "2" });
                viewer.setFrame("machine");
                viewer.render();
                const internals = hooks.upstream._internals;
                const beforeRender = [];
                hooks.on("beforeRender", (scene) => beforeRender.push(scene === hooks.scene));
                hooks.renderer.meimadTransparentBackground = true;
                hooks.renderer.render(hooks.scene, hooks.camera);
                const upstreamPlayback = internals.state.playback;
                hooks.setPathMode("hidden");
                console.log(JSON.stringify({
                  captured: hooks.captured,
                  sceneMatches: hooks.scene === internals.scene,
                  contentRootMatches: hooks.contentRoot === internals.contentRoot,
                  machineRootMatches: hooks.machineRoot === internals.machineRoot,
                  cameraMatches: hooks.camera === internals.camera,
                  rendererMatches: hooks.renderer === internals.renderer,
                  kind: viewer.kind, frame: viewer.frame, renders, seen, events, styles: Boolean(wrapper.SEGMENT_STYLES.feed),
                  beforeRender, drawn: internals.renderer.drawn.length, alphaDuringDraw: internals.renderer.alphaDuringDraw,
                  alphaAfter: internals.renderer.clearAlpha, alphaCanvas: internals.renderer.parameters.alpha === true,
                  shaderPatched: internals.line.vertexShader.includes("meimadHidden"), keptUniform: internals.line.uniforms.opacity.value === 1,
                  upstreamComplete: upstreamPlayback.complete === true && upstreamPlayback.completedIndex === 4,
                  hiddenMode: internals.line.uniforms.meimadPathMode.value, pathMode: hooks.pathMode,
                  cameraTarget: hooks.cameraTarget === internals.state.target
                }));
                """);
            var process = Process.Start(new ProcessStartInfo(node, "probe.mjs")
            {
                WorkingDirectory = desktop,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            })!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            Assert.True(process.ExitCode == 0, error);
            using var result = JsonDocument.Parse(output.Trim().Split('\n')[^1]);
            var root_ = result.RootElement;
            Assert.True(root_.GetProperty("captured").GetBoolean(), output);
            foreach (var key in new[] { "sceneMatches", "contentRootMatches", "machineRootMatches", "cameraMatches", "rendererMatches", "styles" })
            {
                Assert.True(root_.GetProperty(key).GetBoolean(), key);
            }
            Assert.Equal("lathe", root_.GetProperty("kind").GetString());
            Assert.Equal("machine", root_.GetProperty("frame").GetString());
            Assert.Equal(1, root_.GetProperty("renders").GetInt32());
            Assert.Equal("[[\"model\",\"lathe\"],[\"playback\",4],[\"toolPose\",3],[\"filters\",\"2\"]]", root_.GetProperty("seen").GetRawText());
            Assert.Equal("[\"meimad:viewer3d\"]", root_.GetProperty("events").GetRawText());
            Assert.Equal("[true]", root_.GetProperty("beforeRender").GetRawText());
            Assert.Equal(1, root_.GetProperty("drawn").GetInt32());
            Assert.Equal("[0]", root_.GetProperty("alphaDuringDraw").GetRawText()); // the background shows through
            Assert.Equal(1, root_.GetProperty("alphaAfter").GetDouble());
            Assert.True(root_.GetProperty("alphaCanvas").GetBoolean());
            Assert.True(root_.GetProperty("shaderPatched").GetBoolean());
            Assert.True(root_.GetProperty("keptUniform").GetBoolean());
            Assert.True(root_.GetProperty("upstreamComplete").GetBoolean()); // the shader, not the instance count, hides moves
            Assert.Equal(4, root_.GetProperty("hiddenMode").GetDouble());
            Assert.Equal("hidden", root_.GetProperty("pathMode").GetString());
            Assert.True(root_.GetProperty("cameraTarget").GetBoolean()); // Follow tool moves the upstream camera target
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string? FindNode()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            foreach (var name in new[] { "node.exe", "node" })
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
