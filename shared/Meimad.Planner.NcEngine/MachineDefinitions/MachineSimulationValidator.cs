using System.Text.RegularExpressions;

namespace Meimad.Planner.NcEngine.MachineDefinitions;

/// <summary>One validation finding. <c>Severity</c> is <c>error</c> (blocks publishing) or <c>warning</c>.</summary>
public sealed record MachineSimulationIssue(string Path, string Code, string Message, string Severity = "error")
{
    public bool IsError => Severity == "error";
}

/// <summary>Validation rules of docs/machine-simulation-definition.md §2.</summary>
public static partial class MachineSimulationValidator
{
    public const int MaximumNodes = 200;
    public const int MaximumModelsPerNode = 32;
    public const double MaximumCoordinate = 100_000;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")] private static partial Regex IdPattern();
    [GeneratedRegex("^[A-Z][A-Z0-9]{0,3}$")] private static partial Regex AxisPattern();
    [GeneratedRegex("^[0-9a-f]{32}$")] private static partial Regex AssetPattern();
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")] private static partial Regex ColorPattern();

    public static IReadOnlyList<MachineSimulationIssue> Validate(MachineSimulationDefinition definition)
    {
        var issues = new List<MachineSimulationIssue>();
        void Error(string path, string code, string message) => issues.Add(new(path, code, message));
        void Warning(string path, string code, string message) => issues.Add(new(path, code, message, "warning"));
        bool Finite(double[]? value, string path, double limit = MaximumCoordinate)
        {
            if (!MachineVectors.IsFinite3(value)) { Error(path, "not_finite", "Enter three finite numbers."); return false; }
            if (value!.Any(v => Math.Abs(v) > limit)) { Error(path, "out_of_range", $"Values must be within ±{limit:0}."); return false; }
            return true;
        }

        if (definition.SchemaVersion != MachineSimulationDefinition.CurrentSchemaVersion)
            Error("schemaVersion", "schema_version", $"Only schema version {MachineSimulationDefinition.CurrentSchemaVersion} is supported.");
        if (definition.Units != "mm" || definition.AngleUnits != "deg" || definition.Handedness != "right" || definition.ReferencePose != "all-axes-zero")
            Error("units", "units", "Definitions use mm, degrees, a right-handed frame and the all-axes-zero reference pose.");
        if (definition.Nodes.Count > MaximumNodes)
            Error("nodes", "too_many_nodes", $"A machine can have at most {MaximumNodes} components.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var axes = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < definition.Nodes.Count; index++)
        {
            var node = definition.Nodes[index];
            var path = $"nodes[{index}]";
            if (string.IsNullOrEmpty(node.Id) || !IdPattern().IsMatch(node.Id))
                Error(path + ".id", "invalid_id", "Component IDs use lower-case letters, digits, '-' and '_' (up to 64).");
            else if (!ids.Add(node.Id))
                Error(path + ".id", "duplicate_id", $"The component ID '{node.Id}' is used twice.");
            Finite(node.Position, path + ".position");
            if (node.Joint is { } joint) ValidateJoint(joint, path + ".joint", axes, Error, Warning, Finite);
            if (node.Primitive is { } primitive) Finite(primitive.Center, path + ".primitive.center");
            if (node.Models.Count > MaximumModelsPerNode)
                Error(path + ".models", "too_many_models", $"A component can have at most {MaximumModelsPerNode} meshes.");
            for (var m = 0; m < node.Models.Count; m++)
            {
                var model = node.Models[m];
                var modelPath = $"{path}.models[{m}]";
                var hasAsset = !string.IsNullOrEmpty(model.AssetId);
                var hasLegacy = !string.IsNullOrEmpty(model.LegacyPath);
                if (hasAsset == hasLegacy)
                    Error(modelPath, "asset_reference", "A mesh references exactly one Server asset.");
                else if (hasAsset && !AssetPattern().IsMatch(model.AssetId!))
                    Error(modelPath + ".assetId", "asset_reference", "The asset ID is not a Server machine asset.");
                else if (hasLegacy)
                    Warning(modelPath + ".legacyPath", "legacy_path", $"'{model.Name ?? model.LegacyPath}' is an external file; import it into the Server asset store.");
                if (!double.IsFinite(model.Opacity) || model.Opacity < 0 || model.Opacity > 1)
                    Error(modelPath + ".opacity", "out_of_range", "Opacity is from 0 to 1.");
                if (model.Color is not null && !ColorPattern().IsMatch(model.Color))
                    Error(modelPath + ".color", "invalid_color", "Colors look like #9AA4AE.");
                Finite(model.Alignment.Position, modelPath + ".alignment.position");
                Finite(model.Alignment.RotationDeg, modelPath + ".alignment.rotationDeg", 360);
                if (!double.IsFinite(model.Alignment.Scale) || model.Alignment.Scale <= 0)
                    Error(modelPath + ".alignment.scale", "invalid_scale", "Mesh scale must be greater than zero.");
            }
        }

        // Parents and cycles.
        var roots = definition.Nodes.Where(node => string.IsNullOrEmpty(node.Parent)).ToArray();
        if (definition.Nodes.Count > 0 && roots.Length != 1)
            Error("nodes", "root_count", $"The machine needs exactly one root component (found {roots.Length}).");
        var byId = definition.Nodes.Where(node => !string.IsNullOrEmpty(node.Id)).GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.First());
        for (var index = 0; index < definition.Nodes.Count; index++)
        {
            var node = definition.Nodes[index];
            if (string.IsNullOrEmpty(node.Parent)) continue;
            if (!byId.ContainsKey(node.Parent))
            {
                Error($"nodes[{index}].parent", "missing_parent", $"'{node.Id}' names the missing parent '{node.Parent}'.");
                continue;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal) { node.Id };
            var current = node.Parent;
            while (current is not null && byId.TryGetValue(current, out var ancestor))
            {
                if (!seen.Add(current))
                {
                    Error($"nodes[{index}].parent", "cycle", $"'{node.Id}' is its own ancestor.");
                    break;
                }
                current = ancestor.Parent;
            }
        }

        // Mounts.
        void Mount(MachineMount? mount, string name, bool rotationIsError)
        {
            var path = $"mounts.{name}";
            if (mount?.Node is null)
            {
                if (definition.Nodes.Count > 0) Error(path, "mount_required", $"Choose the component that carries the {name} mount.");
                return;
            }
            if (!byId.ContainsKey(mount.Node)) Error(path + ".node", "mount_node", $"The {name} mount names the missing component '{mount.Node}'.");
            Finite(mount.Position, path + ".position");
            if (Finite(mount.RotationDeg, path + ".rotationDeg", 360) && mount.RotationDeg.Any(value => Math.Abs(value) > 1e-9))
            {
                if (rotationIsError) Error(path + ".rotationDeg", "mount_rotation", "The NC engine needs the tool along +Z of the tool mount; a rotated tool mount is not supported yet.");
                else Warning(path + ".rotationDeg", "mount_rotation", "The workpiece mount rotation is shown, not used by NC interpretation.");
            }
        }
        Mount(definition.Mounts.Tool, "tool", rotationIsError: true);
        Mount(definition.Mounts.Workpiece, "workpiece", rotationIsError: false);

        // Magazine and timing.
        var magazine = definition.Magazine;
        if (magazine.NominalCapacity is <= 0) Error("magazine.nominalCapacity", "magazine_capacity", "The magazine capacity must be at least 1.");
        if (magazine.UsablePositions is <= 0) Error("magazine.usablePositions", "magazine_capacity", "Usable positions must be at least 1.");
        if (magazine.NominalCapacity is { } nominal && magazine.UsablePositions is { } usable && usable > nominal)
            Error("magazine.usablePositions", "magazine_capacity", "Usable positions cannot exceed the magazine capacity.");
        if (magazine.ToolChangeSeconds is { } change && (!double.IsFinite(change) || change < 0))
            Error("magazine.toolChangeSeconds", "invalid_rate", "The tool-change time cannot be negative.");
        if (magazine.SafePosition is not null) Finite(magazine.SafePosition, "magazine.safePosition");
        if (definition.Timing.RapidRateMillimetersPerMinute is { } rapid && (!double.IsFinite(rapid) || rapid <= 0))
            Error("timing.rapidRateMillimetersPerMinute", "invalid_rate", "The rapid rate must be greater than zero.");
        if (!double.IsFinite(definition.Timing.MachineTimeFactor) || definition.Timing.MachineTimeFactor <= 0)
            Error("timing.machineTimeFactor", "invalid_rate", "The Machine time factor must be greater than zero.");

        if (definition.Nodes.Count > 0 && string.IsNullOrWhiteSpace(definition.Template))
            Error("template", "template_required", "Choose the NC engine machine (controller) that interprets programs for this Machine.");

        if (definition.InterpreterAxes is { } map)
        {
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (physical, interpreted) in map)
            {
                if (!axes.Contains(physical))
                    Error($"interpreterAxes.{physical}", "interpreter_axes", $"'{physical}' is not an axis of this machine.");
                if (!AxisPattern().IsMatch(interpreted ?? string.Empty) || !targets.Add(interpreted!))
                    Error($"interpreterAxes.{physical}", "interpreter_axes", "Each physical axis maps to a distinct interpreter axis name.");
            }
        }

        foreach (var pair in definition.Collision?.IgnoredPairs ?? [])
        {
            if (pair is not { Length: 2 } || pair.Any(id => !byId.ContainsKey(id ?? string.Empty)))
                Error("collision.ignoredPairs", "collision_pair", "Ignored collision pairs name two existing components.");
        }

        if (definition.Calibration is not { ZeroVerified: true })
            Warning("calibration", "unverified", "The reference zero and positions are not verified on the real machine.");
        return issues;
    }

    private static void ValidateJoint(MachineJoint joint, string path, HashSet<string> axes,
        Action<string, string, string> error, Action<string, string, string> warning, Func<double[]?, string, double, bool> finite)
    {
        if (!AxisPattern().IsMatch(joint.Axis ?? string.Empty))
            error(path + ".axis", "invalid_axis", "Axis names are an upper-case letter, optionally followed by letters or digits (X, Y, C2).");
        else if (!axes.Add(joint.Axis!))
            error(path + ".axis", "duplicate_axis", $"The axis '{joint.Axis}' is used twice.");
        if (joint.Type is not ("linear" or "rotary"))
        {
            error(path + ".type", "invalid_joint_type", "A joint is linear or rotary.");
            return;
        }
        if (finite(joint.Direction, path + ".direction", MaximumCoordinate) && !MachineVectors.TryNormalize(joint.Direction, out _))
            error(path + ".direction", "zero_direction", "The direction cannot be a zero vector.");
        if (joint.IsRotary)
        {
            if (joint.Point is null) error(path + ".point", "point_required", "A rotary axis needs a point on its rotation line.");
            else finite(joint.Point, path + ".point", MaximumCoordinate);
        }
        else if (joint.Point is not null)
        {
            error(path + ".point", "point_not_allowed", "A linear axis has a direction only; remove the point.");
        }
        if (!double.IsFinite(joint.Scale) || joint.Scale == 0)
            error(path + ".scale", "invalid_scale", "The reading scale must be a non-zero number.");

        var continuous = joint.IsRotary && joint.Continuous;
        if (joint.Continuous && !joint.IsRotary) error(path + ".continuous", "limits_required", "Only rotary axes can be continuous.");
        if (!continuous)
        {
            if (joint.Min is null || joint.Max is null)
                error(path, "limits_required", "Enter the minimum and maximum travel" + (joint.IsRotary ? " or mark the axis continuous." : "."));
            else if (!double.IsFinite(joint.Min.Value) || !double.IsFinite(joint.Max.Value))
                error(path, "not_finite", "Travel limits must be finite.");
            else if (joint.Min > joint.Max)
                error(path, "limits_order", "The minimum travel is above the maximum.");
            else if ((joint.Min > 0 || joint.Max < 0) && string.IsNullOrWhiteSpace(joint.ZeroOutsideReason))
                error(path, "zero_outside_limits", "Travel limits are readings from the reference zero and must include 0; explain an exception in 'zero outside reason'.");
        }
        if (joint.RapidRate is { } rapid && (!double.IsFinite(rapid) || rapid <= 0))
            error(path + ".rapidRate", "invalid_rate", "The rapid rate must be greater than zero.");
        else if (joint.RapidRate is null)
            warning(path + ".rapidRate", "rapid_rate_missing", "Enter the axis rapid rate from the machine's specification.");
        if (joint.MaxFeed is { } feed && (!double.IsFinite(feed) || feed <= 0))
            error(path + ".maxFeed", "invalid_rate", "The maximum feed must be greater than zero.");
    }
}
