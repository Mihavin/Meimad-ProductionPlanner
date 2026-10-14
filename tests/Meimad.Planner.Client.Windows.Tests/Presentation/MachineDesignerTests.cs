using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.MachineDesigner;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;
using Meimad.Planner.NcEngine;
using Meimad.Planner.NcEngine.MachineDefinitions;
using Microsoft.ClearScript.V8;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// The Machine Designer (schema v114): its view model against a fake Server, the 3D page's kinematics
/// (NcViewer/meimad-designer-core.js) against the C# evaluator, the GLB reader and the NC viewer's use of
/// published definitions.
/// </summary>
public sealed class MachineDesignerTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "MeimadPlanner.MachineDesigner.Tests", Guid.NewGuid().ToString("N"));

    public MachineDesignerTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    private static PlannerMachine Machine(string id = "machine-10") => new(
        id, "10", "Haas VF-3SS", "mill", null, [], "calendar-1", true, true, null, null, 0, 3,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, UsableToolPositions: 24, MachineTimeFactor: 1.1,
        NcViewerMachine: "haas-vf-3ss");

    private static MachineSimulationDefinition Trunnion() => new()
    {
        MachineId = "machine-10",
        Name = "Trunnion",
        Template = "haas-umc-500",
        Nodes =
        [
            new MachineNode { Id = "base" },
            new MachineNode { Id = "z-head", Parent = "base", Position = [0, 0, 400], Joint = new MachineJoint { Axis = "Z", Direction = [0, 0, 1], Min = -500, Max = 0 } },
            new MachineNode { Id = "spindle", Parent = "z-head", Position = [0, 0, 50] },
            new MachineNode { Id = "x-table", Parent = "base", Position = [10, 20, 0], Joint = new MachineJoint { Axis = "X", Direction = [-1, 0, 0], Min = -600, Max = 0 } },
            new MachineNode { Id = "a-trunnion", Parent = "x-table", Joint = new MachineJoint { Axis = "A", Type = "rotary", Direction = [1, 0, 0], Point = [0, 0, 100], Min = -120, Max = 120 } },
            new MachineNode { Id = "c-table", Parent = "a-trunnion", Position = [0, 0, 100], Joint = new MachineJoint { Axis = "C", Type = "rotary", Direction = [0, 0, 1], Point = [5, 0, 100], Continuous = true },
                Primitive = new MachinePrimitive { Shape = "cylinder", Radius = 200, Height = 40, Axis = "z" } }
        ],
        Mounts = new MachineMounts { Tool = new MachineMount { Node = "spindle" }, Workpiece = new MachineMount { Node = "c-table" } },
        Magazine = new MachineMagazine { NominalCapacity = 40, UsablePositions = 30, ToolChangeSeconds = 4.2 },
        Timing = new MachineTiming { RapidRateMillimetersPerMinute = 30000, MachineTimeFactor = 1.05 },
        Calibration = new MachineCalibration { ZeroVerified = true, Notes = "Measured 2026-10-01." }
    };

    // ----- the 3D page's kinematics ---------------------------------------------------------------

    [Fact]
    public void Designer_page_kinematics_match_the_csharp_evaluator()
    {
        using var engine = new V8ScriptEngine();
        engine.Execute(File.ReadAllText(Path.Combine(RepositoryRoot(), "client-windows", "Meimad.Planner.Client.Windows", "NcViewer", "meimad-designer-core.js")));
        var definition = Trunnion();
        var viewModel = new MachineDesignerViewModel(new FakeDesignerApi(definition), "client-1", Machine(), canEdit: true, new FakeHost());
        viewModel.Populate(definition);
        var scene = JsonNode.Parse(viewModel.SceneJson())!;

        foreach (var axes in new[]
        {
            new Dictionary<string, double>(),
            new Dictionary<string, double> { ["X"] = -100, ["A"] = 30 },
            new Dictionary<string, double> { ["X"] = -250.5, ["Z"] = -40, ["A"] = -75, ["C"] = 212.5 }
        })
        {
            var expected = MachineKinematics.Evaluate(definition, axes);
            var actual = JsonNode.Parse((string)engine.Evaluate(
                $"JSON.stringify(MeimadDesigner.worldMatrices({scene["nodes"]!.ToJsonString()}, {JsonSerializer.Serialize(axes)}))"))!;
            foreach (var node in definition.Nodes)
            {
                var values = actual[node.Id]!.AsArray().Select(value => value!.GetValue<double>()).ToArray();
                for (var index = 0; index < 16; index++) Assert.Equal(expected[node.Id][index], values[index], 9);
            }
        }
        // The tool mount at zero sits on the spindle gauge line.
        var tool = JsonNode.Parse((string)engine.Evaluate(
            $"JSON.stringify(MeimadDesigner.mountPoint(MeimadDesigner.worldMatrices({scene["nodes"]!.ToJsonString()}, {{}}), {{ node: 'spindle', position: [0, 0, 0] }}))"))!;
        Assert.Equal([0, 0, 450], tool.AsArray().Select(value => value!.GetValue<double>()));
    }

    // ----- view model -----------------------------------------------------------------------------

    [Fact]
    public void A_loaded_definition_round_trips_through_the_editor_unchanged()
    {
        var definition = Trunnion();
        var viewModel = new MachineDesignerViewModel(new FakeDesignerApi(definition), "client-1", Machine(), canEdit: true, new FakeHost());

        viewModel.Populate(definition);
        var built = viewModel.BuildDefinition(out var problems);

        Assert.Empty(problems);
        Assert.False(viewModel.IsDirty);
        Assert.Equal(definition.Normalized().ToJson(), built.Normalized().ToJson());
        Assert.Equal(["base"], viewModel.RootNodes.Select(node => node.Id));
        Assert.Equal(["Z", "X", "A", "C"], viewModel.Jogs.Select(jog => jog.Axis));
        Assert.Equal(-360, viewModel.Jogs.Single(jog => jog.Axis == "C").Minimum);
        Assert.Equal(-600, viewModel.Jogs.Single(jog => jog.Axis == "X").Minimum);
    }

    [Fact]
    public void Edits_mark_the_draft_changed_and_fields_that_are_not_numbers_are_reported()
    {
        var viewModel = new MachineDesignerViewModel(new FakeDesignerApi(Trunnion()), "client-1", Machine(), canEdit: true, new FakeHost());
        viewModel.Populate(Trunnion());
        var scenes = 0;
        viewModel.SceneChanged += (_, _) => scenes++;

        var table = viewModel.Nodes.Single(node => node.Id == "x-table");
        table.Position.X = "12,5";
        viewModel.UsablePositions = "thirty";

        Assert.True(viewModel.IsDirty);
        Assert.True(scenes > 0);
        viewModel.BuildDefinition(out var problems);
        Assert.Contains(problems, problem => problem.Path == "nodes[3].position");
        Assert.Contains(problems, problem => problem.Path == "magazine.usablePositions");

        table.Position.X = "12.5";
        viewModel.UsablePositions = "28";
        var built = viewModel.BuildDefinition(out problems);
        Assert.Empty(problems);
        Assert.Equal(12.5, built.Node("x-table")!.Position[0]);
        Assert.Equal(28, built.Magazine.UsablePositions);

        // A jog changes the pose only, never the draft.
        viewModel.MarkSavedForTesting();
        viewModel.Jogs.Single(jog => jog.Axis == "A").Value = 45;
        Assert.False(viewModel.IsDirty);
        Assert.Equal(45, viewModel.Pose()["A"]);
        viewModel.ZeroAll();
        Assert.All(viewModel.Pose().Values, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Save_sends_the_draft_with_its_tag_and_publish_asks_first()
    {
        var api = new FakeDesignerApi(Trunnion());
        var host = new FakeHost();
        var viewModel = new MachineDesignerViewModel(api, "client-1", Machine(), canEdit: true, host);
        await viewModel.LoadAsync();
        Assert.Contains("v1", viewModel.VersionText, StringComparison.Ordinal);

        viewModel.RapidRate = "25400";
        Assert.True(await viewModel.SaveDraftAsync());
        Assert.Equal(25400, api.LastSaved!.Timing.RapidRateMillimetersPerMinute);
        Assert.Equal("\"machine-simulation:machine-10:v1\"", api.LastTag);
        Assert.False(viewModel.IsDirty);

        host.Answer = false;
        viewModel.PublishCommand.Execute(null);
        await WaitAsync(() => !viewModel.IsBusy);
        Assert.Equal(0, api.Publishes);
        host.Answer = true;
        viewModel.PublishCommand.Execute(null);
        await WaitAsync(() => api.Publishes == 1 && !viewModel.IsBusy);
        Assert.Equal("\"machine-simulation:machine-10:v2\"", api.LastTag);
        Assert.Contains("Published", viewModel.StatusText, StringComparison.Ordinal);

        // Someone else saved meanwhile: the draft is refused and the reason is shown.
        api.Stale = true;
        viewModel.RapidRate = "20000";
        Assert.False(await viewModel.SaveDraftAsync());
        Assert.Contains("Someone saved", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_setup_permission_the_definition_is_read_only_but_can_be_jogged()
    {
        var viewModel = new MachineDesignerViewModel(new FakeDesignerApi(Trunnion()), "client-1", Machine(), canEdit: false, new FakeHost());
        viewModel.Populate(Trunnion());

        Assert.True(viewModel.IsReadOnly);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.PublishCommand.CanExecute(null));
        Assert.False(viewModel.AddNodeCommand.CanExecute(null));
        Assert.True(viewModel.ZeroAllCommand.CanExecute(null));
        viewModel.Jogs.Single(jog => jog.Axis == "X").Value = -100;
        Assert.Equal(-100, viewModel.Pose()["X"]);
    }

    [Fact]
    public void Components_can_be_added_removed_and_started_from_an_installed_machine()
    {
        var host = new FakeHost();
        var viewModel = new MachineDesignerViewModel(new FakeDesignerApi(Trunnion()), "client-1", Machine(), canEdit: true, host);
        viewModel.Populate(Trunnion());

        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.Id == "c-table");
        var added = viewModel.AddNode();
        Assert.Equal("c-table", added.Parent);
        Assert.Contains(added, viewModel.Nodes.Single(node => node.Id == "c-table").Children);
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.Id == "a-trunnion");
        viewModel.RemoveSelectedNode();
        Assert.Equal("x-table", viewModel.Nodes.Single(node => node.Id == "c-table").Parent);
        Assert.DoesNotContain(viewModel.Jogs, jog => jog.Axis == "A");

        viewModel.SelectedTemplate = viewModel.Templates.Single(option => option.Id == "mazak-variaxis-i-500");
        viewModel.LoadComponentsFromTemplate();
        var built = viewModel.BuildDefinition(out _);
        Assert.Equal("mazak-variaxis-i-500", built.Template);
        Assert.Equal("B", built.InterpreterAxis("A"));
        Assert.Equal("A", built.Node("a-trunnion")!.Joint!.Axis);
        // Magazine and timing are kept; positions come from the installed machine, unverified.
        Assert.Equal(30, built.Magazine.UsablePositions);
        Assert.Equal(1.05, built.Timing.MachineTimeFactor);
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public void The_scene_lists_meshes_with_urls_and_names_the_ones_that_cannot_be_drawn()
    {
        var stl = Path.Combine(folder, "table.stl");
        File.WriteAllBytes(stl, new byte[84]);
        var definition = Trunnion();
        definition = definition with
        {
            Nodes = definition.Nodes.Select(node => node.Id == "x-table"
                ? node with { Models = [new MachineModel { LegacyPath = stl, Name = "Table", Opacity = 0.5 }, new MachineModel { AssetId = new string('a', 32), Name = "Lost" }] }
                : node).ToArray()
        };
        var host = new FakeHost();
        var viewModel = new MachineDesignerViewModel(new FakeDesignerApi(definition), "client-1", Machine(), canEdit: true, host);
        viewModel.Populate(definition);

        var models = JsonNode.Parse(viewModel.SceneJson())!["nodes"]!.AsArray()
            .Single(node => node!["id"]!.GetValue<string>() == "x-table")!["models"]!.AsArray();

        Assert.Equal("mesh:" + stl, models[0]!["meshUrl"]!.GetValue<string>());
        Assert.Equal(0.5, models[0]!["opacity"]!.GetValue<double>());
        Assert.Null(models[1]!["meshUrl"]);
        Assert.False(string.IsNullOrEmpty(models[1]!["problem"]!.GetValue<string>()));
        viewModel.ReportMeshProblem("x-table", "Table", "bad header");
        Assert.Contains(viewModel.Issues, issue => issue.Code == "mesh_not_drawn" && issue.IsError);
        var legacy = viewModel.Nodes.Single(node => node.Id == "x-table").Meshes[0];
        Assert.True(legacy.IsLegacy);
    }

    [Fact]
    public void A_palette_color_goes_to_the_selected_mesh_or_to_every_mesh_of_the_component()
    {
        var definition = Trunnion();
        definition = definition with
        {
            Nodes = definition.Nodes.Select(node => node.Id == "x-table"
                ? node with { Models = [new MachineModel { AssetId = new string('a', 32), Name = "Table" }, new MachineModel { AssetId = new string('b', 32), Name = "Vise" }] }
                : node).ToArray()
        };
        var viewModel = new MachineDesignerViewModel(new FakeDesignerApi(definition), "client-1", Machine(), canEdit: true, new FakeHost());
        viewModel.Populate(definition);
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.Id == "x-table");
        var red = viewModel.Palette.Single(color => color.Name == "Red");
        var blue = viewModel.Palette.Single(color => color.Name == "Blue");

        Assert.True(viewModel.SetMeshColorCommand.CanExecute(red));
        viewModel.SetMeshColor(red.Hex);
        Assert.All(viewModel.SelectedNode!.Meshes, mesh => Assert.Equal(red.Hex, mesh.Color));
        viewModel.SelectedMesh = viewModel.SelectedNode.Meshes[1];
        viewModel.SetMeshColor(blue.Hex);
        Assert.Equal([red.Hex, blue.Hex], viewModel.SelectedNode.Meshes.Select(mesh => mesh.Color));
        Assert.True(viewModel.IsDirty);
        Assert.Equal(blue.Hex, viewModel.BuildDefinition(out _).Node("x-table")!.Models[1].Color);
        Assert.DoesNotContain(MachineSimulationValidator.Validate(viewModel.BuildDefinition(out _)), issue => issue.Code == "invalid_color");

        // Read-only, or a component without meshes: nothing to color.
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.Id == "base");
        Assert.False(viewModel.SetMeshColorCommand.CanExecute(red));
        var readOnly = new MachineDesignerViewModel(new FakeDesignerApi(definition), "client-1", Machine(), canEdit: false, new FakeHost());
        readOnly.Populate(definition);
        readOnly.SelectedNode = readOnly.Nodes.Single(node => node.Id == "x-table");
        Assert.False(readOnly.SetMeshColorCommand.CanExecute(red));
    }

    // ----- GLB ------------------------------------------------------------------------------------

    [Fact]
    public void Glb_meshes_are_read_in_millimetres_with_their_node_transforms()
    {
        var glb = Glb(
            """{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0,"translation":[0.1,0,0],"scale":[2,2,2]}],"meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}],"accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},{"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"}],"bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":6}],"buffers":[{"byteLength":44}]}""",
            [0, 0, 0, 0.01f, 0, 0, 0, 0.02f, 0], [0, 1, 2]);

        var triangles = GlbMeshReader.ReadTriangles(glb);
        var stl = GlbMeshReader.ReadAsStl(glb);

        // (0.01 m · 2 + 0.1 m) = 120 mm; (0.02 m · 2) = 40 mm.
        Assert.Equal([100, 0, 0, 120, 0, 0, 100, 40, 0], triangles.Select(value => (double)Math.Round(value, 3)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(stl.AsSpan(80)));
        Assert.Equal(84 + 50, stl.Length);

        var compressed = Glb("""{"asset":{"version":"2.0"},"extensionsRequired":["KHR_draco_mesh_compression"]}""", [], []);
        Assert.Contains("KHR_draco_mesh_compression", Assert.Throws<InvalidDataException>(() => GlbMeshReader.ReadTriangles(compressed)).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => GlbMeshReader.ReadTriangles(Encoding.ASCII.GetBytes("solid not a glb")));
        var path = Path.Combine(folder, "part.glb");
        File.WriteAllBytes(path, glb);
        Assert.True(NcViewerMeshes.IsSupported(path));
        Assert.Equal(stl, NcViewerMeshes.ReadAsStl(path));
    }

    // ----- NC viewer --------------------------------------------------------------------------------

    [Fact]
    public void A_published_definition_is_compiled_and_selected_for_its_machine()
    {
        var compiled = NcViewerMachineDefinitions.Compile("machine-10", Trunnion());
        Assert.NotNull(compiled);
        Assert.Equal("meimad-machine-machine-10", compiled!.CompiledId);
        Assert.Equal("meimad-machine-machine-10", JsonNode.Parse(compiled.CompiledJson)!["id"]!.GetValue<string>());
        Assert.Null(NcViewerMachineDefinitions.Compile("machine-11", Trunnion() with { Template = null }));
        Assert.Null(NcViewerMachineDefinitions.Compile("machine-11", Trunnion() with { Nodes = [] }));

        var machines = new[] { Machine(), Machine("machine-11") };
        Assert.Equal("meimad-machine-machine-10", NcViewerMachines.Resolve(machines, "machine-10", null, [compiled]));
        Assert.Equal("haas-vf-3ss", NcViewerMachines.Resolve(machines, "machine-11", null, [compiled]));
        Assert.Equal("haas-vf-3ss", NcViewerMachines.Resolve(machines, "machine-10", null));

        using var runtime = new NcEngineRuntime();
        Assert.Empty(runtime.UseMachines([compiled.CompiledJson]));
        Assert.Contains(runtime.Machines, machine => machine.Id == compiled.CompiledId);
    }

    [Fact]
    public void The_viewer_lists_each_factory_machine_once_numbered_and_first()
    {
        // Owner, 2026-10-13: "Haas UMC-500" (installed) and "Haas UMC-500ss" (Machines 14/15, built on it) both showed, and
        // two Machines named NM-510 could not be told apart.
        var compiled = NcViewerMachineDefinitions.Compile("machine-14",
            NcViewerMachineDefinitions.Labelled(Trunnion() with { Name = null }, Machine("machine-14") with { Number = "14", Name = "Haas UMC-500ss" }))!;
        Assert.Equal("14 — Haas UMC-500ss", JsonNode.Parse(compiled.CompiledJson)!["name"]!.GetValue<string>());

        using var runtime = new NcEngineRuntime();
        Assert.Empty(runtime.UseMachines([compiled.CompiledJson]));
        runtime.ArrangeMachines([compiled.CompiledId], [compiled.Template!]);

        // The full list stays (selections are validated against it), the Machine first; the template is only hidden.
        Assert.Equal(compiled.CompiledId, runtime.Machines[0].Id);
        Assert.Contains(runtime.Machines, machine => machine.Id == "haas-umc-500");
        Assert.Contains("haas-umc-500", runtime.HiddenMachineIds);

        const string program = "%\nO1000\nG90 G54 G00 X0 Y0\nT1 M06\nG01 Z-2. F300.\nM30\n%\n";
        string[] Available(string selection)
        {
            var preview = runtime.ParsePreview(new NcEnginePreviewRequest(program, MachineSelection: selection));
            using var packed = JsonDocument.Parse(preview.Packed);
            return packed.RootElement.GetProperty("model").GetProperty("machineSelection").GetProperty("available")
                .EnumerateArray().Select(machine => machine.GetProperty("id").GetString()!).ToArray();
        }

        var listed = Available(compiled.CompiledId);
        Assert.Equal(compiled.CompiledId, listed[0]);
        Assert.DoesNotContain("haas-umc-500", listed);
        Assert.Contains("haas-vf-3ss", listed);                 // installed definitions no Machine is built on stay
        Assert.Contains("haas-umc-500", Available("haas-umc-500")); // a selected hidden template is still shown
    }

    [Fact]
    public void Mesh_scale_multiplies_the_model_but_not_its_offset_in_the_viewer()
    {
        using var engine = new V8ScriptEngine();
        engine.Execute(File.ReadAllText(Path.Combine(RepositoryRoot(), "client-windows", "Meimad.Planner.Client.Windows", "NcViewer", "meimad-scene-core.js")));
        var result = (string)engine.Evaluate(
            "JSON.stringify(Array.from(MeimadScene.placeOnNode(new Float32Array([1, 2, 3]), { offsetMm: { x: 10, y: 0, z: 0 }, scale: 25.4 }, 1)))");
        Assert.Equal("[35.400001525878906,50.79999923706055,76.19999694824219]", result);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
    }

    private static byte[] Glb(string json, float[] positions, ushort[] indices)
    {
        var binary = new List<byte>();
        foreach (var value in positions) binary.AddRange(BitConverter.GetBytes(value));
        foreach (var value in indices) binary.AddRange(BitConverter.GetBytes(value));
        while (binary.Count % 4 != 0) binary.Add(0);
        var jsonBytes = Encoding.UTF8.GetBytes(json).ToList();
        while (jsonBytes.Count % 4 != 0) jsonBytes.Add((byte)' ');
        var total = 12 + 8 + jsonBytes.Count + (binary.Count > 0 ? 8 + binary.Count : 0);
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(0x46546C67u));
        bytes.AddRange(BitConverter.GetBytes(2u));
        bytes.AddRange(BitConverter.GetBytes((uint)total));
        bytes.AddRange(BitConverter.GetBytes((uint)jsonBytes.Count));
        bytes.AddRange(BitConverter.GetBytes(0x4E4F534Au));
        bytes.AddRange(jsonBytes);
        if (binary.Count > 0)
        {
            bytes.AddRange(BitConverter.GetBytes((uint)binary.Count));
            bytes.AddRange(BitConverter.GetBytes(0x004E4942u));
            bytes.AddRange(binary);
        }
        return bytes.ToArray();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public async Task Copy_from_machine_takes_the_other_draft_with_its_meshes_copied_keeps_the_name_and_clears_calibration()
    {
        var mesh = new byte[84];
        mesh[0] = 7;
        var sourceAsset = new string('a', 32);
        var lostAsset = new string('c', 32);
        var source = Trunnion() with
        {
            MachineId = "machine-14",
            Name = "Haas UMC-500 #14",
            Nodes = Trunnion().Nodes.Select(node => node.Id == "c-table"
                ? node with { Models = [new MachineModel { AssetId = sourceAsset, Name = "table" }, new MachineModel { AssetId = lostAsset, Name = "gone" }] }
                : node).ToArray()
        };
        var target = new MachineSimulationDefinition { MachineId = "machine-10", Name = "VF-3SS", Nodes = [new MachineNode { Id = "base" }] };
        var api = new CopyApi(target, source, sourceAsset, mesh);
        var host = new FakeHost();
        var viewModel = new MachineDesignerViewModel(api, "client-1", Machine(), canEdit: true, host);
        await viewModel.LoadAsync();

        Assert.Equal(["14 — Haas UMC-500"], viewModel.SourceMachines.Select(machine => machine.Label));
        Assert.False(viewModel.CopyFromMachineCommand.CanExecute(null));
        viewModel.SelectedSourceMachine = viewModel.SourceMachines[0];
        host.Answer = false;
        await viewModel.CopyFromMachineAsync();
        Assert.Single(viewModel.Nodes);

        host.Answer = true;
        await viewModel.CopyFromMachineAsync();
        var built = viewModel.BuildDefinition(out _);
        Assert.Equal("VF-3SS", built.Name);
        Assert.Equal(source.Nodes.Select(node => node.Id), built.Nodes.Select(node => node.Id));
        Assert.Equal("c-table", built.Mounts.Workpiece?.Node);
        Assert.Equal(30, built.Magazine.UsablePositions);
        Assert.False(built.Calibration?.ZeroVerified ?? false);
        var copied = Assert.Single(built.Nodes.Single(node => node.Id == "c-table").Models);
        Assert.Equal(api.UploadedAssetId, copied.AssetId);
        Assert.Equal(("machine-10", "table.stl"), (api.UploadedTo, api.UploadedName));
        Assert.Equal(mesh, api.UploadedBytes);
        Assert.True(viewModel.IsDirty);
        Assert.Contains("1 meshes could not be copied", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Null(api.SavedDraft);
    }

    private sealed class CopyApi(MachineSimulationDefinition target, MachineSimulationDefinition source, string sourceAsset, byte[] mesh)
        : StubPlannerApiClient, IPlannerApiClient
    {
        internal string UploadedAssetId { get; } = new string('b', 32);
        internal string? UploadedTo { get; private set; }
        internal string? UploadedName { get; private set; }
        internal byte[]? UploadedBytes { get; private set; }
        internal MachineSimulationDefinition? SavedDraft { get; private set; }

        public Task<IReadOnlyList<PlannerMachine>> ListMachinesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerMachine>>([Machine(), Machine("machine-14") with { Number = "14", Name = "Haas UMC-500" }]);

        Task<MachineSimulationResource> IPlannerApiClient.GetMachineSimulationAsync(string machineId, CancellationToken cancellationToken)
        {
            var definition = machineId == "machine-14" ? source : target;
            return Task.FromResult(new MachineSimulationResource(
                new MachineSimulationRecordDto(machineId, definition, 1, null, 0, null, null,
                    DateTimeOffset.UnixEpoch, "planner", true, []),
                $"\"machine-simulation:{machineId}:v1\""));
        }

        Task<MachineSimulationResource> IPlannerApiClient.SaveMachineSimulationDraftAsync(
            string machineId, MachineSimulationDefinition value, string entityTag, string clientId, CancellationToken cancellationToken)
        {
            SavedDraft = value;
            throw new NotSupportedException();
        }

        Task<IReadOnlyList<MachineSimulationAssetDto>> IPlannerApiClient.ListMachineSimulationAssetsAsync(string machineId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MachineSimulationAssetDto>>(machineId == "machine-14"
                ? [new MachineSimulationAssetDto(sourceAsset, "table.stl", "model/stl", mesh.Length,
                    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(mesh)), DateTimeOffset.UnixEpoch, "planner")]
                : [new MachineSimulationAssetDto(UploadedAssetId, "table.stl", "model/stl", mesh.Length,
                    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(mesh)), DateTimeOffset.UnixEpoch, "planner")]);

        Task<byte[]> IPlannerApiClient.DownloadMachineSimulationAssetAsync(string machineId, string assetId, CancellationToken cancellationToken) =>
            Task.FromResult(mesh);

        async Task<MachineSimulationAssetDto> IPlannerApiClient.UploadMachineSimulationAssetAsync(
            string machineId, string fileName, Stream content, string clientId, CancellationToken cancellationToken)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            (UploadedTo, UploadedName, UploadedBytes) = (machineId, fileName, copy.ToArray());
            return new MachineSimulationAssetDto(UploadedAssetId, fileName, "model/stl", copy.Length,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(copy.ToArray())), DateTimeOffset.UnixEpoch, "planner");
        }
    }

    private sealed class FakeHost : IMachineDesignerHost
    {
        internal bool Answer { get; set; } = true;
        public IReadOnlyList<string> ChooseMeshFiles() => [];
        public bool Confirm(string message, string title) => Answer;
        public string MeshUrl(string path) => "mesh:" + path;
    }

    private sealed class FakeDesignerApi(MachineSimulationDefinition definition) : StubPlannerApiClient, IPlannerApiClient
    {
        private int draftVersion = 1;
        private int publishedVersion = 1;
        private MachineSimulationDefinition draft = definition;
        private MachineSimulationDefinition published = definition;

        internal MachineSimulationDefinition? LastSaved { get; private set; }
        internal string? LastTag { get; private set; }
        internal int Publishes { get; private set; }
        internal bool Stale { get; set; }

        private MachineSimulationResource Resource() => new(
            new MachineSimulationRecordDto("machine-10", draft, draftVersion, published, publishedVersion, DateTimeOffset.UnixEpoch, "planner",
                DateTimeOffset.UnixEpoch, "planner", draft.ToJson() != published.ToJson(), []),
            $"\"machine-simulation:machine-10:v{draftVersion}\"");

        Task<MachineSimulationResource> IPlannerApiClient.GetMachineSimulationAsync(string machineId, CancellationToken cancellationToken) =>
            Task.FromResult(Resource());

        Task<MachineSimulationResource> IPlannerApiClient.SaveMachineSimulationDraftAsync(
            string machineId, MachineSimulationDefinition value, string entityTag, string clientId, CancellationToken cancellationToken)
        {
            LastTag = entityTag;
            if (Stale)
                throw new PlannerApiException(System.Net.HttpStatusCode.PreconditionFailed, "resource_version_stale", "Changed.");
            LastSaved = value;
            draft = value;
            draftVersion++;
            return Task.FromResult(Resource());
        }

        Task<IReadOnlyList<MachineSimulationIssueDto>> IPlannerApiClient.ValidateMachineSimulationAsync(
            string machineId, MachineSimulationDefinition value, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MachineSimulationIssueDto>>([]);

        Task<MachineSimulationResource> IPlannerApiClient.PublishMachineSimulationAsync(
            string machineId, string entityTag, string clientId, CancellationToken cancellationToken)
        {
            LastTag = entityTag;
            Publishes++;
            published = draft;
            publishedVersion++;
            return Task.FromResult(Resource());
        }

        Task<IReadOnlyList<MachineSimulationAssetDto>> IPlannerApiClient.ListMachineSimulationAssetsAsync(string machineId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MachineSimulationAssetDto>>([]);
    }
}
