using System.Text.Json;
using System.Text.Json.Nodes;
using Meimad.Planner.NcEngine;
using Meimad.Planner.NcEngine.MachineDefinitions;

namespace Meimad.Planner.Server.Tests.NcEngine;

public sealed class MachineSimulationDefinitionTests : IDisposable
{
    private static readonly string[] InstalledMachines =
    [
        "chevalier-flc-200mc", "doosan-dvf-5000", "fanuc-0i-mc-vmc-3axis", "fanuc-0i-mc-vmc-4axis-a",
        "haas-st-25y", "haas-umc-500", "haas-vf-3ss", "mazak-variaxis-i-500", "okuma-genos-l200e-m"
    ];

    private readonly NcEngineRuntime runtime = new();

    public void Dispose() => runtime.Dispose();

    // A trunnion table: X moves the table along -X, A tilts about a line 100 mm above the table, C turns on A.
    private static MachineSimulationDefinition Trunnion() => new()
    {
        MachineId = "machine-test",
        Name = "Test trunnion",
        Template = "haas-umc-500",
        Nodes =
        [
            new MachineNode { Id = "base" },
            new MachineNode { Id = "z-head", Parent = "base", Position = [0, 0, 400], Joint = new MachineJoint { Axis = "Z", Direction = [0, 0, 1], Min = -500, Max = 0 } },
            new MachineNode { Id = "spindle", Parent = "z-head", Position = [0, 0, 50] },
            new MachineNode { Id = "x-table", Parent = "base", Position = [10, 20, 0], Joint = new MachineJoint { Axis = "X", Direction = [-1, 0, 0], Min = -600, Max = 0 } },
            new MachineNode { Id = "a-trunnion", Parent = "x-table", Position = [0, 0, 0], Joint = new MachineJoint { Axis = "A", Type = "rotary", Direction = [1, 0, 0], Point = [0, 0, 100], Min = -120, Max = 120 } },
            new MachineNode { Id = "c-table", Parent = "a-trunnion", Position = [0, 0, 100], Joint = new MachineJoint { Axis = "C", Type = "rotary", Direction = [0, 0, 1], Point = [5, 0, 100], Continuous = true } }
        ],
        Mounts = new MachineMounts
        {
            Tool = new MachineMount { Node = "spindle" },
            Workpiece = new MachineMount { Node = "c-table", Position = [0, 0, 0] }
        },
        Calibration = new MachineCalibration { ZeroVerified = true }
    };

    [Fact]
    public void Linear_joint_moves_along_its_direction_and_rotary_joint_turns_about_its_line()
    {
        var definition = Trunnion();
        var world = MachineKinematics.Evaluate(definition, new Dictionary<string, double> { ["X"] = -100, ["A"] = 30 });

        // X = -100 along direction (-1, 0, 0) moves the table +100 in machine X.
        Assert.Equal([110, 20, 0], Point(world["x-table"], [0, 0, 0]));
        // A = +30 about +X through (x, 0, 100) of the table: right-hand, so +Y tilts towards +Z.
        var tilted = Point(world["a-trunnion"], [0, 10, 100]);
        Assert.Equal(110, tilted[0], 9);
        Assert.Equal(20 + 10 * Math.Cos(Math.PI / 6), tilted[1], 9);
        Assert.Equal(100 + 10 * Math.Sin(Math.PI / 6), tilted[2], 9);
        // All axes zero gives the authored pose.
        var zero = MachineKinematics.Evaluate(definition);
        Assert.Equal([10, 20, 100], Point(zero["c-table"], [0, 0, 0]));
        Assert.Equal([0, 0, 450], MachineKinematics.MountPosition(definition, definition.Mounts.Tool)!.Select(v => Math.Round(v, 9)));
    }

    [Fact]
    public void Nested_rotaries_apply_the_parent_rotation_after_the_child()
    {
        var definition = Trunnion();
        var axes = new Dictionary<string, double> { ["A"] = 90, ["C"] = 90 };
        var world = MachineKinematics.Evaluate(definition, axes);

        // C turns the table +90 about its own (tilted) axis first: a point at +X goes to +Y of the table,
        // then A = 90 tilts table +Y up to machine +Z.
        var point = Point(world["c-table"], [5 + 10, 0, 0]);
        Assert.Equal(10 + 5, point[0], 9);
        Assert.Equal(20 + 0, point[1], 9);
        Assert.Equal(100 + 10, point[2], 9);
    }

    [Fact]
    public void Csharp_evaluator_matches_the_engine_kinematics_on_the_compiled_definition()
    {
        var definition = Trunnion();
        var template = NcEngineMachineCatalog.LoadDefinition("haas-umc-500", runtime.EngineRoot)!;
        var compiled = MachineSimulationCompiler.Compile(definition, template)!;
        var toolZero = MachineKinematics.MountPosition(definition, definition.Mounts.Tool)!;
        var kinematics = JsonSerializer.Serialize(Path.Combine(runtime.EngineRoot, "media", "kinematics.js"));

        foreach (var axes in new[]
        {
            new Dictionary<string, double>(),
            new Dictionary<string, double> { ["X"] = -100, ["Z"] = -35, ["A"] = 30 },
            new Dictionary<string, double> { ["X"] = -333.3, ["A"] = -75, ["C"] = 212.5 }
        })
        {
            var csharp = MachineKinematics.Evaluate(definition, axes);
            var script = $$"""
                (() => {
                  const k = __meimadModules.require({{kinematics}});
                  const chain = k.buildChain({{compiled["kinematics"]!.ToJsonString()}});
                  const world = k.evaluateChain(chain, {{JsonSerializer.Serialize(axes)}});
                  return JSON.stringify(Object.fromEntries([...world.entries()]));
                })()
                """;
            var engine = JsonNode.Parse((string)runtime.EvaluateForTesting(script)!)!.AsObject();
            foreach (var node in definition.Nodes)
            {
                var expected = csharp[node.Id];
                var actual = engine[node.Id]!.AsArray().Select(value => value!.GetValue<double>()).ToArray();
                // The compiled machine is shifted so the tool mount at zero is the machine origin.
                for (var row = 0; row < 3; row++)
                {
                    for (var column = 0; column < 3; column++)
                        Assert.Equal(expected[row * 4 + column], actual[row * 4 + column], 9);
                    Assert.Equal(expected[row * 4 + 3] - toolZero[row], actual[row * 4 + 3], 6);
                }
            }
        }
    }

    [Fact]
    public void Validation_rejects_invalid_joints_and_structures()
    {
        var definition = Trunnion();
        Assert.DoesNotContain(MachineSimulationValidator.Validate(definition), issue => issue.IsError);

        static IEnumerable<string> Codes(MachineSimulationDefinition value) =>
            MachineSimulationValidator.Validate(value).Where(issue => issue.IsError).Select(issue => issue.Code);
        MachineSimulationDefinition WithNode(int index, Func<MachineNode, MachineNode> change) =>
            definition with { Nodes = definition.Nodes.Select((node, i) => i == index ? change(node) : node).ToArray() };

        Assert.Contains("zero_direction", Codes(WithNode(3, node => node with { Joint = node.Joint! with { Direction = [0, 0, 0] } })));
        Assert.Contains("point_not_allowed", Codes(WithNode(3, node => node with { Joint = node.Joint! with { Point = [0, 0, 0] } })));
        Assert.Contains("point_required", Codes(WithNode(4, node => node with { Joint = node.Joint! with { Point = null } })));
        Assert.Contains("limits_order", Codes(WithNode(3, node => node with { Joint = node.Joint! with { Min = 10, Max = -10 } })));
        Assert.Contains("zero_outside_limits", Codes(WithNode(3, node => node with { Joint = node.Joint! with { Min = 10, Max = 100 } })));
        Assert.DoesNotContain("zero_outside_limits", Codes(WithNode(3, node => node with
        {
            Joint = node.Joint! with { Min = 10, Max = 100, ZeroOutsideReason = "Home is at the travel end." }
        })));
        Assert.Contains("duplicate_axis", Codes(WithNode(4, node => node with { Joint = node.Joint! with { Axis = "X" } })));
        Assert.Contains("not_finite", Codes(WithNode(3, node => node with { Position = [double.NaN, 0, 0] })));
        Assert.Contains("duplicate_id", Codes(WithNode(5, node => node with { Id = "base" })));
        Assert.Contains("missing_parent", Codes(WithNode(5, node => node with { Parent = "nowhere" })));
        Assert.Contains("root_count", Codes(WithNode(5, node => node with { Parent = null })));
        Assert.Contains("cycle", Codes(WithNode(0, node => node with { Parent = "c-table" })));
        Assert.Contains("mount_required", Codes(definition with { Mounts = definition.Mounts with { Tool = null } }));
        Assert.Contains("mount_node", Codes(definition with { Mounts = definition.Mounts with { Workpiece = new MachineMount { Node = "nowhere" } } }));
        Assert.Contains("mount_rotation", Codes(definition with { Mounts = definition.Mounts with { Tool = new MachineMount { Node = "spindle", RotationDeg = [0, 90, 0] } } }));
        Assert.Contains("magazine_capacity", Codes(definition with { Magazine = new MachineMagazine { NominalCapacity = 20, UsablePositions = 24 } }));
        Assert.Contains("asset_reference", Codes(WithNode(0, node => node with { Models = [new MachineModel { AssetId = "C:\\models\\base.stl" }] })));
        Assert.Contains("invalid_color", Codes(WithNode(0, node => node with { Models = [new MachineModel { AssetId = new string('a', 32), Color = "red" }] })));
        Assert.Contains("template_required", Codes(definition with { Template = null }));
        Assert.Contains("collision_pair", Codes(definition with { Collision = new MachineCollisionSettings { IgnoredPairs = [["base", "nowhere"]] } }));

        // A legacy path is a warning until the mesh is imported; an unverified zero is a warning.
        var legacy = MachineSimulationValidator.Validate(WithNode(0, node => node with { Models = [new MachineModel { LegacyPath = "\\\\server\\share\\base.stl" }] })
            with { Calibration = null });
        Assert.Contains(legacy, issue => issue is { Code: "legacy_path", IsError: false });
        Assert.Contains(legacy, issue => issue is { Code: "unverified", IsError: false });
        // A joint without a rapid rate is a warning, never an error.
        Assert.Contains(legacy, issue => issue is { Code: "rapid_rate_missing", IsError: false, Path: "nodes[1].joint.rapidRate" });
    }

    [Fact]
    public void Definition_round_trips_through_json_with_normalized_directions()
    {
        var definition = Trunnion();
        definition = definition with
        {
            Nodes = definition.Nodes.Select(node => node.Id == "z-head" ? node with { Joint = node.Joint! with { Direction = [0, 0, 5] } } : node).ToArray()
        };

        var parsed = MachineSimulationDefinition.Parse(definition.Normalized().ToJson());

        Assert.Equal([0, 0, 1], parsed.Node("z-head")!.Joint!.Direction);
        Assert.Equal([5, 0, 100], parsed.Node("c-table")!.Joint!.Point!);
        Assert.True(parsed.Node("c-table")!.Joint!.Continuous);
        Assert.Equal("spindle", parsed.Mounts.Tool!.Node);
        Assert.DoesNotContain("\"point\"", JsonNode.Parse(parsed.ToJson())!["nodes"]![3]!.ToJsonString());
    }

    [Theory]
    [MemberData(nameof(Installed))]
    public void Installed_machine_built_into_a_definition_compiles_back_to_the_same_kinematics(string id)
    {
        var template = NcEngineMachineCatalog.LoadDefinition(id, runtime.EngineRoot)!;
        var legacy = new MachineLegacyValues("machine-" + id, "Machine " + id, 20, 30000, 4.5, 1.1);

        var definition = MachineSimulationCompiler.FromTemplate(template, id, legacy);
        var issues = MachineSimulationValidator.Validate(definition);
        var compiled = MachineSimulationCompiler.Compile(definition, template)!;

        Assert.DoesNotContain(issues, issue => issue.IsError);
        Assert.Equal(20, definition.Magazine.UsablePositions);
        Assert.Equal(4.5, definition.Magazine.ToolChangeSeconds);
        Assert.Equal(30000, definition.Timing.RapidRateMillimetersPerMinute);
        Assert.Equal(1.1, definition.Timing.MachineTimeFactor);
        Assert.Equal(MachineSimulationCompiler.CompiledId("machine-" + id), compiled["id"]!.GetValue<string>());

        // The engine chains of the template and of the compiled definition agree at every pose.
        var kinematics = JsonSerializer.Serialize(Path.Combine(runtime.EngineRoot, "media", "kinematics.js"));
        foreach (var axes in new[]
        {
            "{}", "{\"X\":-120,\"Y\":-40,\"Z\":-200,\"A\":25,\"B\":-30,\"C\":135}", "{\"X\":80,\"Z\":-15,\"B\":60,\"C\":-45}"
        })
        {
            var script = $$"""
                (() => {
                  const k = __meimadModules.require({{kinematics}});
                  const pose = (m) => {
                    const chain = k.buildChain({ nodes: m.kinematics.nodes.map((n) => ({ ...n, pivot: n.pivot === "mrzp" ? [Number(m.mrzp?.x) || 0, Number(m.mrzp?.y) || 0, Number(m.mrzp?.z) || 0] : n.pivot })) });
                    const axes = {{axes}};
                    const tool = k.toolMountTransform(chain, axes);
                    const part = k.workpieceTransform(chain, axes);
                    return { axis: k.toolAxisInWorkpiece(chain, axes), tool: [tool[3], tool[7], tool[11]], part };
                  };
                  return JSON.stringify([pose({{template.ToJsonString()}}), pose({{compiled.ToJsonString()}})]);
                })()
                """;
            var poses = JsonNode.Parse((string)runtime.EvaluateForTesting(script)!)!.AsArray();
            Close(Numbers(poses[0]!["axis"]), Numbers(poses[1]!["axis"]));
            Close(Numbers(poses[0]!["part"]), Numbers(poses[1]!["part"]));
            Close(Numbers(poses[0]!["tool"]), Numbers(poses[1]!["tool"]));
        }
        if (template["mrzp"] is JsonObject mrzp)
        {
            Assert.Equal(mrzp["x"]!.GetValue<double>(), compiled["mrzp"]!["x"]!.GetValue<double>(), 9);
            Assert.Equal(mrzp["y"]!.GetValue<double>(), compiled["mrzp"]!["y"]!.GetValue<double>(), 9);
            Assert.Equal(mrzp["z"]!.GetValue<double>(), compiled["mrzp"]!["z"]!.GetValue<double>(), 9);
        }
        else
        {
            Assert.Null(compiled["mrzp"]);
        }
    }

    public static TheoryData<string> Installed() => new(InstalledMachines);

    [Fact]
    public void Mazak_physical_a_axis_is_interpreted_as_b()
    {
        var template = NcEngineMachineCatalog.LoadDefinition("mazak-variaxis-i-500", runtime.EngineRoot)!;
        var definition = MachineSimulationCompiler.FromTemplate(template, "mazak-variaxis-i-500",
            new MachineLegacyValues("machine-14", "Mazak", 30, null, null, 1));
        var compiled = MachineSimulationCompiler.Compile(definition, template)!;

        Assert.Equal("A", definition.Node("a-trunnion")!.Joint!.Axis);
        Assert.Equal("B", definition.InterpreterAxis("A"));
        Assert.Equal("A", definition.PhysicalAxis("B"));
        Assert.Equal("B", compiled["kinematics"]!["nodes"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == "a-trunnion")!["axis"]!.GetValue<string>());
        Assert.Equal("A", compiled["axes"]!["B"]!["programmedAs"]!.GetValue<string>());
        // Z travel 150..660 does not include zero; the carried-over reason keeps the definition publishable.
        Assert.NotNull(definition.Node("z-head")!.Joint!.ZeroOutsideReason);
    }

    [Fact]
    public void Compiled_definitions_register_in_the_engine_and_analyze_like_their_template()
    {
        var program = string.Join("\n", "%", "O1000", "G90 G54 G00 X0 Y0", "G43 H1 Z50. T1 M06", "S3000 M03",
            "G01 Z-2. F300.", "X40.", "Y40.", "G00 Z50.", "M30", "%");
        var definitions = InstalledMachines.Select(id => (id, template: NcEngineMachineCatalog.LoadDefinition(id, runtime.EngineRoot)!))
            .Select(entry => MachineSimulationCompiler.Compile(MachineSimulationCompiler.FromTemplate(entry.template, entry.id,
                new MachineLegacyValues("m-" + entry.id, entry.id, null, null, null, 1)), entry.template)!.ToJsonString())
            .ToArray();

        var errors = runtime.UseMachines(definitions);

        Assert.Empty(errors);
        Assert.Contains(runtime.Machines, machine => machine.Id == "meimad-machine-m-haas-vf-3ss");
        Assert.Equal(InstalledMachines.Length * 2, runtime.Machines.Count);
        var original = runtime.Analyze(new NcEngineAnalysisRequest(program, null, "haas-vf-3ss"));
        var compiled = runtime.Analyze(new NcEngineAnalysisRequest(program, null, "meimad-machine-m-haas-vf-3ss"));
        Assert.Empty(compiled.Errors);
        Assert.Equal("meimad-machine-m-haas-vf-3ss", compiled.MachineId);
        Assert.Equal(original.SegmentCount, compiled.SegmentCount);
        Assert.Equal(original.FeedSeconds, compiled.FeedSeconds, 6);
        Assert.Equal(original.EngineEstimatedCycleSeconds, compiled.EngineEstimatedCycleSeconds);
        Assert.Equal(original.ToolChangeCount, compiled.ToolChangeCount);

        // A definition the engine rejects leaves only the installed machines registered.
        var rejected = runtime.UseMachines(["{\"id\":\"meimad-machine-bad\",\"name\":\"Bad\",\"type\":\"mill\"}"]);
        Assert.NotEmpty(rejected);
        Assert.Equal(InstalledMachines, runtime.Machines.Select(machine => machine.Id).Order(StringComparer.Ordinal));
        Assert.Empty(runtime.UseMachines([]));
    }

    private static void Close(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++) Assert.Equal(expected[index], actual[index], 6);
    }

    private static double[] Point(double[] matrix, double[] point) =>
        MachineKinematics.TransformPoint(matrix, point).Select(value => Math.Round(value, 9) + 0).ToArray();

    private static double[] Numbers(JsonNode? node) => node switch
    {
        JsonArray array => array.Select(value => value!.GetValue<double>()).ToArray(),
        JsonObject point => [point["x"]!.GetValue<double>(), point["y"]!.GetValue<double>(), point["z"]!.GetValue<double>()],
        _ => []
    };
}
