using System.Globalization;
using System.Text.Json.Nodes;

namespace Meimad.Planner.NcEngine.MachineDefinitions;

/// <summary>The Machine's former columns, read when a definition is first built from a template.</summary>
public sealed record MachineLegacyValues(
    string MachineId,
    string? Name,
    int? UsableToolPositions,
    double? RapidRateMillimetersPerMinute,
    double? ToolChangeTimeSeconds,
    double MachineTimeFactor,
    IReadOnlyList<MachineLegacyModel>? Models = null);

/// <summary>A schema v113 Machine 3D model (STL path on a kinematic node).</summary>
public sealed record MachineLegacyModel(string Node, string Path, double[] OffsetMm, double[] RotationDeg, string? Color, double Opacity);

/// <summary>
/// Builds definitions from installed NC engine machines (templates) and compiles definitions back into the engine's
/// machine format (docs/machine-simulation-definition.md §3, §5).
/// </summary>
public static class MachineSimulationCompiler
{
    public const string CompiledIdPrefix = "meimad-machine-";
    public const string ToolMountNodeId = "meimad-tool-mount";
    public const string WorkpieceMountNodeId = "meimad-workpiece-mount";

    /// <summary>The engine machine id a Machine's compiled definition is registered under.</summary>
    public static string CompiledId(string machineId)
    {
        var chars = machineId.ToLowerInvariant().Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ? c : '-').ToArray();
        var id = (CompiledIdPrefix + new string(chars)).Trim('-');
        return id.Length > 100 ? id[..100] : id;
    }

    // ----- template -> definition -----------------------------------------------------------------------

    /// <summary>
    /// A definition for an existing Machine: kinematics, travel, primitives and mounts from the installed template
    /// (positions as the template states them, unverified unless the template says so), magazine and timing from
    /// the Machine's own values. A null template gives a definition without components (auto-detection).
    /// </summary>
    public static MachineSimulationDefinition FromTemplate(JsonObject? template, string? templateId, MachineLegacyValues legacy)
    {
        var magazine = new MachineMagazine
        {
            UsablePositions = legacy.UsableToolPositions,
            ToolChangeSeconds = legacy.ToolChangeTimeSeconds
        };
        var timing = new MachineTiming
        {
            RapidRateMillimetersPerMinute = legacy.RapidRateMillimetersPerMinute,
            MachineTimeFactor = legacy.MachineTimeFactor > 0 ? legacy.MachineTimeFactor : 1
        };
        if (template is null)
        {
            return WithLegacyModels(new MachineSimulationDefinition
            {
                MachineId = legacy.MachineId,
                Name = legacy.Name,
                // An id whose definition is not installed is kept, so nothing selected is lost.
                Template = templateId,
                Magazine = magazine,
                Timing = timing,
                Calibration = new MachineCalibration
                {
                    Notes = templateId is null
                        ? "No NC viewer machine was selected; programs are interpreted by auto-detection."
                        : $"The NC viewer machine '{templateId}' is not installed; its components could not be carried over."
                }
            }, legacy.Models);
        }

        var mrzp = Vector(template["mrzp"]) ?? [0, 0, 0];
        var hasMrzp = template["mrzp"] is JsonObject;
        var axes = template["axes"] as JsonObject ?? new JsonObject();
        var nodes = new List<MachineNode>();
        var interpreterAxes = new Dictionary<string, string>(StringComparer.Ordinal);
        string? toolNode = null;
        string? workpieceNode = null;
        foreach (var item in (template["kinematics"]?["nodes"] as JsonArray) ?? [])
        {
            if (item is not JsonObject node) continue;
            var id = Text(node["id"]) ?? string.Empty;
            var position = Vector(node["origin"]) ?? [0, 0, 0];
            var type = Text(node["type"]) ?? "fixed";
            MachineJoint? joint = null;
            if (type is "linear" or "rotary")
            {
                var interpreter = Text(node["axis"]) ?? string.Empty;
                var axis = axes[interpreter] as JsonObject;
                var physical = Text(axis?["programmedAs"]) ?? interpreter;
                if (physical != interpreter) interpreterAxes[physical] = interpreter;
                var sense = Number(node["sense"]) is -1 ? -1 : 1;
                var direction = (Vector(node["direction"]) ?? [1, 0, 0]).Select(value => value * sense).ToArray();
                var min = Number(axis?["min"]);
                var max = Number(axis?["max"]);
                var continuous = type == "rotary" && min is null && max is null;
                var pivot = Reference(node["pivot"], mrzp) ?? [0, 0, 0];
                // As the engine resolves it: Setting 254 moves a tilt axis on the MRZP away from the rotary centre along X.
                if (Text(node["pivot"]) == "mrzp" && type == "rotary" && Number(node["direction"]?[2]) == 0)
                    pivot = [pivot[0] + (Number(template["mrzp"]?["rotaryCenterDistance"]) ?? 0), pivot[1], pivot[2]];
                joint = new MachineJoint
                {
                    Axis = physical,
                    Type = type,
                    Direction = direction,
                    Point = type == "rotary" ? MachineVectors.Add(position, pivot) : null,
                    Min = continuous ? null : min,
                    Max = continuous ? null : max,
                    Continuous = continuous,
                    ZeroOutsideReason = min > 0 || max < 0
                        ? "Carried over from the installed NC viewer machine, whose reference zero is not the all-axes-zero pose; verify on the machine."
                        : null,
                    RapidRate = Number(axis?["rapidRate"]),
                    MaxFeed = Number(axis?["maxFeed"]),
                    Scale = Number(node["scale"]) is { } scale && scale != 0 ? scale : 1,
                    Diameter = axis?["diameter"]?.GetValueKind() == System.Text.Json.JsonValueKind.True
                };
            }
            if (node["toolMount"]?.GetValueKind() == System.Text.Json.JsonValueKind.True) toolNode = id;
            if (node["workpieceMount"]?.GetValueKind() == System.Text.Json.JsonValueKind.True) workpieceNode = id;
            nodes.Add(new MachineNode
            {
                Id = id,
                Parent = Text(node["parent"]),
                Label = Text(node["label"]) ?? id,
                Position = position,
                Joint = joint,
                Primitive = Primitive(node["geometry"] as JsonObject, mrzp)
            });
        }

        var changer = template["toolChanger"] as JsonObject;
        var nominal = (int?)(Number(changer?["capacity"]) ?? Number(changer?["stations"]));
        if (nominal is { } capacity && magazine.UsablePositions is { } usable && usable > capacity) nominal = null;
        magazine = magazine with { NominalCapacity = nominal };
        var verified = template["mrzp"]?["verified"]?.GetValueKind() == System.Text.Json.JsonValueKind.True;
        return WithLegacyModels(new MachineSimulationDefinition
        {
            MachineId = legacy.MachineId,
            Name = legacy.Name,
            Template = templateId,
            InterpreterAxes = interpreterAxes.Count > 0 ? interpreterAxes : null,
            Nodes = nodes,
            Mounts = new MachineMounts
            {
                Tool = toolNode is null ? null : new MachineMount { Node = toolNode },
                // The template's work-offset reference (MRZP) is where fixtures and undefined work offsets sit.
                Workpiece = workpieceNode is null ? null : new MachineMount { Node = workpieceNode, Position = hasMrzp ? mrzp : [0, 0, 0] }
            },
            Magazine = magazine,
            Timing = timing,
            Calibration = new MachineCalibration
            {
                RotaryLinesVerified = verified,
                MountsVerified = verified,
                Notes = $"Built from the installed NC viewer machine '{templateId}'. Positions are as that definition states them; verify on the real machine."
            }
        }, legacy.Models);
    }

    /// <summary>
    /// Schema v113 Machine 3D models become meshes (legacy paths, to be imported into the asset store) on their nodes.
    /// A model whose node does not exist keeps its node as a fixed component under the root (the first one becomes the
    /// root of a definition without components), so no configured mesh is lost.
    /// </summary>
    private static MachineSimulationDefinition WithLegacyModels(MachineSimulationDefinition definition, IReadOnlyList<MachineLegacyModel>? models)
    {
        var nodes = definition.Nodes.ToList();
        foreach (var model in models ?? [])
        {
            var index = nodes.FindIndex(node => node.Id == model.Node);
            if (index < 0)
            {
                nodes.Add(new MachineNode { Id = model.Node, Label = model.Node, Parent = nodes.FirstOrDefault(node => node.Parent is null)?.Id });
                index = nodes.Count - 1;
            }
            nodes[index] = nodes[index] with
            {
                Models = nodes[index].Models.Append(new MachineModel
                {
                    LegacyPath = model.Path,
                    Name = Path.GetFileName(model.Path),
                    Alignment = new MachineTransform { Position = model.OffsetMm, RotationDeg = model.RotationDeg },
                    Color = model.Color,
                    Opacity = model.Opacity
                }).ToArray()
            };
        }
        return definition with { Nodes = nodes };
    }

    // ----- definition -> engine machine -----------------------------------------------------------------

    /// <summary>
    /// The engine machine definition of a Machine (id <see cref="CompiledId"/>), or null when the definition has no
    /// components or no template. The tool mount at all-axes-zero becomes the machine-coordinate origin (the NC
    /// engine reads machine coordinates as the tool gauge relative to its zero position); the workpiece mount moves
    /// the part only through its ancestors, and its position becomes the engine's MRZP.
    /// </summary>
    public static JsonObject? Compile(MachineSimulationDefinition definition, JsonObject? template)
    {
        if (template is null || definition.Nodes.Count == 0 || definition.MachineId is null) return null;
        var compiled = (JsonObject)template.DeepClone();
        var toolZero = MachineKinematics.MountPosition(definition, definition.Mounts.Tool) ?? [0, 0, 0];
        var workpieceZero = MachineKinematics.MountPosition(definition, definition.Mounts.Workpiece) ?? [0, 0, 0];
        compiled["id"] = CompiledId(definition.MachineId);
        compiled["name"] = definition.Name ?? Text(template["name"]);
        compiled["detection"] = new JsonObject { ["keywords"] = new JsonArray(), ["codes"] = new JsonArray() };
        compiled.Remove("builtIn");
        compiled.Remove("sourcePath");

        var templateAxes = template["axes"] as JsonObject ?? new JsonObject();
        var axes = new JsonObject();
        foreach (var node in definition.Nodes)
        {
            if (node.Joint is not { } joint) continue;
            var interpreter = definition.InterpreterAxis(joint.Axis);
            var axis = templateAxes[interpreter] is JsonObject known ? (JsonObject)known.DeepClone() : new JsonObject();
            axis["type"] = joint.IsRotary ? "rotary" : "linear";
            axis["min"] = joint.Continuous ? null : joint.Min;
            axis["max"] = joint.Continuous ? null : joint.Max;
            if (joint.IsRotary) axis["wrap"] = joint.Continuous;
            if (joint.RapidRate is { } rapid) axis["rapidRate"] = rapid;
            if (joint.MaxFeed is { } feed) axis["maxFeed"] = feed;
            if (joint.Diameter) axis["diameter"] = true;
            if (interpreter != joint.Axis)
            {
                axis["programmedAs"] = joint.Axis;
                axis["label"] = joint.Axis;
            }
            axes[interpreter] = axis;
        }
        compiled["axes"] = axes;

        var nodes = new JsonArray();
        foreach (var node in definition.Nodes)
        {
            var origin = string.IsNullOrEmpty(node.Parent) ? MachineVectors.Subtract(node.Position, toolZero) : node.Position;
            var entry = new JsonObject
            {
                ["id"] = node.Id,
                ["parent"] = node.Parent,
                ["label"] = node.Label ?? node.Id,
                ["origin"] = Array(origin)
            };
            if (node.Joint is { } joint && MachineVectors.TryNormalize(joint.Direction, out var direction))
            {
                entry["type"] = joint.IsRotary ? "rotary" : "linear";
                entry["axis"] = definition.InterpreterAxis(joint.Axis);
                entry["direction"] = Array(direction);
                if (joint.Scale != 1) entry["scale"] = joint.Scale;
                if (joint.IsRotary && joint.Point is { } point) entry["pivot"] = Array(MachineVectors.Subtract(point, node.Position));
            }
            else
            {
                entry["type"] = "fixed";
            }
            entry["geometry"] = node.Primitive is { } primitive ? Geometry(primitive) : null;
            entry["model"] = null;
            nodes.Add(entry);
        }
        nodes.Add(new JsonObject
        {
            ["id"] = ToolMountNodeId,
            ["parent"] = definition.Mounts.Tool?.Node,
            ["label"] = "Tool mount (gauge line)",
            ["type"] = "fixed",
            ["toolMount"] = true,
            ["origin"] = Array(definition.Mounts.Tool?.Position ?? [0, 0, 0]),
            ["geometry"] = null,
            ["model"] = null
        });
        nodes.Add(new JsonObject
        {
            ["id"] = WorkpieceMountNodeId,
            ["parent"] = definition.Mounts.Workpiece?.Node,
            ["label"] = "Workpiece mount",
            ["type"] = "fixed",
            ["workpieceMount"] = true,
            ["origin"] = Array([0, 0, 0]),
            ["geometry"] = null,
            ["model"] = null
        });
        var kinematics = template["kinematics"] is JsonObject templateKinematics ? (JsonObject)templateKinematics.DeepClone() : new JsonObject();
        kinematics["description"] = $"Meimad Machine Designer definition of {definition.Name ?? definition.MachineId}.";
        kinematics["nodes"] = nodes;
        compiled["kinematics"] = kinematics;

        var mrzp = template["mrzp"] is JsonObject templateMrzp ? (JsonObject)templateMrzp.DeepClone() : new JsonObject();
        var reference = MachineVectors.Subtract(workpieceZero, toolZero);
        mrzp["x"] = reference[0];
        mrzp["y"] = reference[1];
        mrzp["z"] = reference[2];
        mrzp["verified"] = definition.Calibration?.MountsVerified ?? false;
        mrzp["label"] ??= "Workpiece mount";
        mrzp["source"] = "Machine Designer workpiece mount at all axes zero.";
        // Lathe templates have no MRZP; their interpreters keep working without one.
        if (template["mrzp"] is JsonObject) compiled["mrzp"] = mrzp;

        if (compiled["toolChanger"] is JsonObject changer)
        {
            if (definition.Magazine.NominalCapacity is { } capacity) changer[changer.ContainsKey("stations") ? "stations" : "capacity"] = capacity;
            if (definition.Magazine.ToolChangeSeconds is { } seconds) changer["changeSeconds"] = seconds;
        }
        return compiled;
    }

    // ----- helpers ------------------------------------------------------------------------------------------

    private static MachinePrimitive? Primitive(JsonObject? geometry, double[] mrzp)
    {
        if (geometry is null || Text(geometry["shape"]) is not { } shape) return null;
        var center = Reference(geometry["center"], mrzp) ?? [0, 0, 0];
        var offset = Vector(geometry["offset"]) ?? [0, 0, 0];
        return new MachinePrimitive
        {
            Shape = shape,
            Size = Vector(geometry["size"]),
            Radius = Number(geometry["radius"]),
            Height = Number(geometry["height"]),
            Center = MachineVectors.Add(center, offset),
            Axis = Text(geometry["axis"])
        };
    }

    private static JsonObject Geometry(MachinePrimitive primitive)
    {
        var geometry = new JsonObject { ["shape"] = primitive.Shape, ["center"] = Array(primitive.Center) };
        if (primitive.Size is { } size) geometry["size"] = Array(size);
        if (primitive.Radius is { } radius) geometry["radius"] = radius;
        if (primitive.Height is { } height) geometry["height"] = height;
        if (primitive.Axis is { } axis) geometry["axis"] = axis;
        return geometry;
    }

    private static double[]? Reference(JsonNode? value, double[] mrzp) =>
        value?.GetValueKind() == System.Text.Json.JsonValueKind.String && Text(value) == "mrzp" ? mrzp : Vector(value);

    private static double[]? Vector(JsonNode? value)
    {
        if (value is JsonArray array && array.Count == 3 && array.All(item => Number(item) is not null))
            return array.Select(item => Number(item)!.Value).ToArray();
        if (value is JsonObject point && Number(point["x"]) is { } x && Number(point["y"]) is { } y && Number(point["z"]) is { } z)
            return [x, y, z];
        return null;
    }

    private static double? Number(JsonNode? value)
    {
        if (value is null || value.GetValueKind() != System.Text.Json.JsonValueKind.Number) return null;
        var number = value.GetValue<double>();
        return double.IsFinite(number) ? number : null;
    }

    private static string? Text(JsonNode? value) =>
        value?.GetValueKind() == System.Text.Json.JsonValueKind.String ? value.GetValue<string>() : null;

    private static JsonArray Array(double[] values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(Math.Round(value, 9, MidpointRounding.ToEven))).ToArray());

    internal static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
