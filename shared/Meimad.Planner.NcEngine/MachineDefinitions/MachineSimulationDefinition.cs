using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meimad.Planner.NcEngine.MachineDefinitions;

/// <summary>
/// The physical Machine's simulation definition (schema v1, docs/machine-simulation-definition.md): a tree of static
/// and moving components authored at the all-axes-zero reference pose, with physical joints, mounts, meshes, tool
/// magazine and timing. The Server owns and versions it; the NC viewer, the Machine Designer and Server NC analysis
/// use the published copy compiled by <see cref="MachineSimulationCompiler"/>.
/// </summary>
public sealed record MachineSimulationDefinition
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string? MachineId { get; init; }
    public string? Name { get; init; }
    public string Units { get; init; } = "mm";
    public string AngleUnits { get; init; } = "deg";
    public string Handedness { get; init; } = "right";
    public string ReferencePose { get; init; } = "all-axes-zero";
    /// <summary>The installed NC engine machine whose controller, cycles and spindle interpret programs.</summary>
    public string? Template { get; init; }
    /// <summary>Physical axis name to the axis name the NC engine interprets (Mazak Variaxis: A to B).</summary>
    public IReadOnlyDictionary<string, string>? InterpreterAxes { get; init; }
    public IReadOnlyList<MachineNode> Nodes { get; init; } = [];
    public MachineMounts Mounts { get; init; } = new();
    public MachineMagazine Magazine { get; init; } = new();
    public MachineTiming Timing { get; init; } = new();
    public MachineCollisionSettings? Collision { get; init; }
    public MachineCalibration? Calibration { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static MachineSimulationDefinition Parse(string json) =>
        JsonSerializer.Deserialize<MachineSimulationDefinition>(json, Json)
            ?? throw new JsonException("The machine simulation definition is empty.");

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>A copy with joint directions normalized (done on every save; validation rejects zero vectors first).</summary>
    public MachineSimulationDefinition Normalized() => this with
    {
        Nodes = Nodes.Select(node => node.Joint is { } joint && MachineVectors.TryNormalize(joint.Direction, out var unit)
            ? node with { Joint = joint with { Direction = unit } }
            : node).ToArray()
    };

    /// <summary>The node that has <paramref name="id"/>, or null.</summary>
    public MachineNode? Node(string? id) => id is null ? null : Nodes.FirstOrDefault(node => node.Id == id);

    /// <summary>The physical axis name the readout shows for an engine (interpreter) axis name.</summary>
    public string PhysicalAxis(string interpreterAxis)
    {
        foreach (var (physical, interpreted) in InterpreterAxes ?? new Dictionary<string, string>())
            if (string.Equals(interpreted, interpreterAxis, StringComparison.Ordinal)) return physical;
        return interpreterAxis;
    }

    /// <summary>The engine (interpreter) axis name of a physical axis.</summary>
    public string InterpreterAxis(string physicalAxis) =>
        InterpreterAxes is not null && InterpreterAxes.TryGetValue(physicalAxis, out var interpreted) ? interpreted : physicalAxis;
}

public sealed record MachineNode
{
    public string Id { get; init; } = string.Empty;
    public string? Parent { get; init; }
    public string? Label { get; init; }
    /// <summary>Zero-pose translation in the parent's frame (mm). Node frames carry no rotation in schema v1.</summary>
    public double[] Position { get; init; } = [0, 0, 0];
    public MachineJoint? Joint { get; init; }
    public MachinePrimitive? Primitive { get; init; }
    public IReadOnlyList<MachineModel> Models { get; init; } = [];
}

public sealed record MachineJoint
{
    /// <summary>Physical axis name (X, Y, Z, A, B, C, ...).</summary>
    public string Axis { get; init; } = string.Empty;
    /// <summary><c>linear</c> or <c>rotary</c>.</summary>
    public string Type { get; init; } = "linear";
    /// <summary>Positive physical motion in the parent frame.</summary>
    public double[] Direction { get; init; } = [1, 0, 0];
    /// <summary>A point on the rotation line in the parent frame; rotary joints only.</summary>
    public double[]? Point { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    /// <summary>Rotary only: no limits, the reading wraps.</summary>
    public bool Continuous { get; init; }
    /// <summary>Why zero lies outside the travel (otherwise min &lt;= 0 &lt;= max is required).</summary>
    public string? ZeroOutsideReason { get; init; }
    /// <summary>mm/min for linear, deg/min for rotary.</summary>
    public double? RapidRate { get; init; }
    public double? MaxFeed { get; init; }
    /// <summary>Physical displacement per unit of the reading (0.5 for a lathe X read as a diameter).</summary>
    public double Scale { get; init; } = 1;
    public bool Diameter { get; init; }

    [JsonIgnore] public bool IsRotary => string.Equals(Type, "rotary", StringComparison.Ordinal);
    [JsonIgnore] public string Unit => IsRotary ? "deg" : "mm";
}

public sealed record MachinePrimitive
{
    public string Shape { get; init; } = "box";
    public double[]? Size { get; init; }
    public double? Radius { get; init; }
    public double? Height { get; init; }
    public double[] Center { get; init; } = [0, 0, 0];
    public string? Axis { get; init; }
}

public sealed record MachineModel
{
    /// <summary>A Server machine asset (32 hex characters).</summary>
    public string? AssetId { get; init; }
    /// <summary>A file path carried over from the schema v113 Machine 3D models; import it into the asset store.</summary>
    public string? LegacyPath { get; init; }
    public string? Name { get; init; }
    public MachineTransform Alignment { get; init; } = new();
    public string? Color { get; init; }
    public bool Visible { get; init; } = true;
    public double Opacity { get; init; } = 1;
    public bool Wireframe { get; init; }
    public bool CollisionParticipation { get; init; } = true;
}

public sealed record MachineTransform
{
    public double[] Position { get; init; } = [0, 0, 0];
    /// <summary>Degrees about X, then Y, then Z (fixed axes).</summary>
    public double[] RotationDeg { get; init; } = [0, 0, 0];
    public double Scale { get; init; } = 1;
}

public sealed record MachineMount
{
    public string? Node { get; init; }
    public double[] Position { get; init; } = [0, 0, 0];
    public double[] RotationDeg { get; init; } = [0, 0, 0];
}

public sealed record MachineMounts
{
    public MachineMount? Tool { get; init; }
    public MachineMount? Workpiece { get; init; }
}

public sealed record MachineMagazine
{
    public int? NominalCapacity { get; init; }
    /// <summary>The effective operational limit (the Machine's usable tool positions).</summary>
    public int? UsablePositions { get; init; }
    public double? ToolChangeSeconds { get; init; }
    public double[]? SafePosition { get; init; }
}

public sealed record MachineTiming
{
    public double? RapidRateMillimetersPerMinute { get; init; }
    public double MachineTimeFactor { get; init; } = 1;
}

public sealed record MachineCollisionSettings
{
    public IReadOnlyList<string[]> IgnoredPairs { get; init; } = [];
}

public sealed record MachineCalibration
{
    public bool ZeroVerified { get; init; }
    public bool RotaryLinesVerified { get; init; }
    public bool MountsVerified { get; init; }
    public string? Notes { get; init; }
}

/// <summary>Small vector helpers shared by validation, kinematics and compilation.</summary>
public static class MachineVectors
{
    public static bool IsFinite3(double[]? value) =>
        value is { Length: 3 } && value.All(double.IsFinite);

    public static bool TryNormalize(double[]? value, out double[] unit)
    {
        unit = [0, 0, 0];
        if (!IsFinite3(value)) return false;
        var length = Math.Sqrt(value![0] * value[0] + value[1] * value[1] + value[2] * value[2]);
        if (length < 1e-12) return false;
        unit = [value[0] / length, value[1] / length, value[2] / length];
        return true;
    }

    public static double[] Subtract(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    public static double[] Add(double[] a, double[] b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
}
