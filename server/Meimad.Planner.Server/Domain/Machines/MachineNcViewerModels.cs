using System.Text.Json;
using System.Text.RegularExpressions;

namespace Meimad.Planner.Server.Domain.Machines;

/// <summary>A millimetre offset or a rotation in degrees about the X, Y and Z axes.</summary>
internal sealed record MachineVector3(double X, double Y, double Z)
{
    internal static readonly MachineVector3 Zero = new(0, 0, 0);
}

/// <summary>
/// An STL mesh the NC viewer draws for a Machine, attached to the kinematic node <see cref="Node"/>
/// (an id of the Machine's NC viewer machine definition, such as <c>base</c>, <c>table</c> or
/// <c>spindle-head</c>). The mesh is placed in that node's frame by rotating it about X, then Y, then
/// Z (degrees, extrinsic, R = Rz·Ry·Rx) and then translating it by <see cref="OffsetMm"/>.
/// </summary>
internal sealed record MachineNcViewerModel(
    string Node,
    string Path,
    MachineVector3 OffsetMm,
    MachineVector3 RotationDeg,
    string Color,
    double Opacity);

/// <summary>A requested NC viewer model before validation; omitted values take their defaults.</summary>
internal sealed record MachineNcViewerModelInput(
    string? Node,
    string? Path,
    MachineVector3? OffsetMm,
    MachineVector3? RotationDeg,
    string? Color,
    double? Opacity);

internal static partial class MachineNcViewerModels
{
    internal const int MaximumCount = 32;
    internal const int NodeMaximumLength = 64;
    internal const int PathMaximumLength = 4096;
    internal const double OffsetLimitMm = 100000;
    internal const double RotationLimitDeg = 360;
    internal const double MinimumOpacity = 0.05;
    internal const string DefaultColor = "#9AA4AE";

    // Stored with the same camel-case names as the API; System.Text.Json writes numbers
    // culture-invariantly (shortest round-trip form, '.' decimal separator).
    private static readonly JsonSerializerOptions StorageOptions = new(JsonSerializerDefaults.Web);

    internal static IReadOnlyList<MachineNcViewerModelInput?> ToInputs(IReadOnlyList<MachineNcViewerModel>? models) =>
        (models ?? []).Select(model => (MachineNcViewerModelInput?)new MachineNcViewerModelInput(
            model.Node, model.Path, model.OffsetMm, model.RotationDeg, model.Color, model.Opacity)).ToArray();

    internal static string Serialize(IReadOnlyList<MachineNcViewerModel>? models) =>
        JsonSerializer.Serialize(models ?? [], StorageOptions);

    internal static IReadOnlyList<MachineNcViewerModel> Deserialize(string json) =>
        JsonSerializer.Deserialize<MachineNcViewerModel[]>(json, StorageOptions)
        ?? throw new InvalidDataException("Stored Machine NC viewer models must be a JSON array.");

    /// <summary>Applies <paramref name="map"/> to every model path (network-folder storage form and back).</summary>
    internal static IReadOnlyList<MachineNcViewerModel> MapPaths(
        IReadOnlyList<MachineNcViewerModel>? models, Func<string, string?> map) =>
        (models ?? []).Select(model => model with { Path = map(model.Path) ?? model.Path }).ToArray();

    internal static IReadOnlyList<MachineNcViewerModel> Validate(
        IReadOnlyList<MachineNcViewerModelInput?>? values,
        ICollection<MachineValidationIssue> issues)
    {
        if (values is null) return [];
        if (values.Count > MaximumCount)
        {
            issues.Add(new MachineValidationIssue(
                "ncViewerModels", "too_many", $"ncViewerModels may contain at most {MaximumCount} models."));
            return [];
        }

        var models = new List<MachineNcViewerModel>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var field = $"ncViewerModels[{index}]";
            var value = values[index];
            if (value is null)
            {
                issues.Add(new MachineValidationIssue(field, "required", "NC viewer model entries cannot be null."));
                continue;
            }

            var before = issues.Count;
            var node = value.Node?.Trim();
            if (string.IsNullOrEmpty(node))
                issues.Add(new MachineValidationIssue($"{field}.node", "required", "Each NC viewer model needs the machine node it is attached to."));
            else if (node.Length > NodeMaximumLength)
                issues.Add(new MachineValidationIssue($"{field}.node", "too_long", $"An NC viewer model node may contain at most {NodeMaximumLength} characters."));

            var path = ValidatePath(value.Path, $"{field}.path", issues);
            var offset = ValidateVector(value.OffsetMm, $"{field}.offsetMm", OffsetLimitMm, "mm", issues);
            var rotation = ValidateVector(value.RotationDeg, $"{field}.rotationDeg", RotationLimitDeg, "degrees", issues);

            var color = string.IsNullOrWhiteSpace(value.Color) ? DefaultColor : value.Color.Trim();
            if (!ColorPattern().IsMatch(color))
                issues.Add(new MachineValidationIssue($"{field}.color", "invalid_color", "An NC viewer model color must be #RRGGBB."));

            var opacity = value.Opacity ?? 1.0;
            if (!double.IsFinite(opacity))
                issues.Add(new MachineValidationIssue($"{field}.opacity", "not_finite", "opacity must be a finite number."));
            else if (opacity is < MinimumOpacity or > 1.0)
                issues.Add(new MachineValidationIssue($"{field}.opacity", "out_of_range", "opacity must be between 0.05 and 1."));

            if (issues.Count == before)
                models.Add(new MachineNcViewerModel(node!, path!, offset, rotation, color.ToUpperInvariant(), opacity));
        }

        return models;
    }

    private static string? ValidatePath(string? value, string field, ICollection<MachineValidationIssue> issues)
    {
        var path = value?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            issues.Add(new MachineValidationIssue(field, "required", "Each NC viewer model needs the path of its STL file."));
            return null;
        }
        if (path.Length > PathMaximumLength)
        {
            issues.Add(new MachineValidationIssue(field, "too_long", $"An NC viewer model path may contain at most {PathMaximumLength} characters."));
            return null;
        }
        if (path.IndexOf('\0') >= 0)
        {
            issues.Add(new MachineValidationIssue(field, "invalid_path", "The NC viewer model path contains an invalid path character."));
            return null;
        }
        if (!(System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || DrivePath().IsMatch(path)))
        {
            issues.Add(new MachineValidationIssue(field, "absolute_path_required", "An NC viewer model path must be a full path (drive or network share)."));
            return null;
        }
        if (!string.Equals(System.IO.Path.GetExtension(path), ".stl", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new MachineValidationIssue(field, "stl_required", "An NC viewer model must be an .stl file."));
            return null;
        }
        return path;
    }

    private static MachineVector3 ValidateVector(
        MachineVector3? value, string field, double limit, string unit, ICollection<MachineValidationIssue> issues)
    {
        if (value is null) return MachineVector3.Zero;
        foreach (var (axis, component) in new[] { ("x", value.X), ("y", value.Y), ("z", value.Z) })
        {
            if (!double.IsFinite(component))
                issues.Add(new MachineValidationIssue($"{field}.{axis}", "not_finite", $"{field}.{axis} must be a finite number."));
            else if (Math.Abs(component) > limit)
                issues.Add(new MachineValidationIssue($"{field}.{axis}", "out_of_range",
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{field}.{axis} must be between -{limit} and {limit} {unit}.")));
        }
        return value;
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePath();
}
