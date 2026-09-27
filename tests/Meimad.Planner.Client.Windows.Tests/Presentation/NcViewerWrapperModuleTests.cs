using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// The ES module that wraps the vendored 3D viewer (NcViewer/meimad-viewer3d.js) run in Node
/// against a fake upstream viewer: it must capture the scene objects the upstream creates from a
/// real module namespace (a derived object cannot shadow namespace exports, which broke the 3D
/// view once) and forward every wrapped call. Skipped when Node is not installed.
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
            File.WriteAllText(Path.Combine(desktop, "three-fake.mjs"), """
                export class Scene { constructor() { this.isScene = true; } }
                export class Group { constructor() { this.children = []; } add(...c) { this.children.push(...c); } }
                export class OrthographicCamera {}
                export class WebGLRenderer { constructor() { this.ok = true; } }
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
                  const state = { model: undefined, playback: undefined, frame: "part", view: "iso" };
                  return {
                    dispose() {}, fit() {}, pick() {}, pointerDown() {}, pointerMove() {}, pointerUp() {},
                    render() { onRender?.(); }, resize() {},
                    setFilters() {}, setFrame(f) { state.frame = f; }, setModel(m) { state.model = m; }, setOptions() {},
                    setPlayback(p) { state.playback = p; }, setSelection() {}, setToolPose() {}, setView(v) { state.view = v; }, wheel() {},
                    get frame() { return state.frame; }, get view() { return state.view; }, get kind() { return state.model?.kind || "mill"; },
                    _internals: { scene, contentRoot, machineRoot, camera, renderer }
                  };
                }
                """);
            File.WriteAllText(Path.Combine(desktop, "probe.mjs"), """
                import * as THREE from "./three-fake.mjs";
                const events = [];
                globalThis.window = { dispatchEvent(e) { events.push(e.type); } };
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
                console.log(JSON.stringify({
                  captured: hooks.captured,
                  sceneMatches: hooks.scene === internals.scene,
                  contentRootMatches: hooks.contentRoot === internals.contentRoot,
                  machineRootMatches: hooks.machineRoot === internals.machineRoot,
                  cameraMatches: hooks.camera === internals.camera,
                  rendererMatches: hooks.renderer === internals.renderer,
                  kind: viewer.kind, frame: viewer.frame, renders, seen, events, styles: Boolean(wrapper.SEGMENT_STYLES.feed)
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
