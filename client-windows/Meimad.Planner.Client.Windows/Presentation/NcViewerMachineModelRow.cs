using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.NcEngine;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// One editable row of a Machine's NC viewer 3D models (Setup → Machines). Numbers are typed as
/// text with an invariant decimal point, as the other Machine fields are.
/// </summary>
internal sealed class NcViewerMachineModelRow
{
    public string Node { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string OffsetX { get; set; } = "0";
    public string OffsetY { get; set; } = "0";
    public string OffsetZ { get; set; } = "0";
    public string RotationX { get; set; } = "0";
    public string RotationY { get; set; } = "0";
    public string RotationZ { get; set; } = "0";
    public string Color { get; set; } = "#9AA4AE";
    public string Opacity { get; set; } = "1";

    public string FileName => System.IO.Path.GetFileName(Path);

    internal static NcViewerMachineModelRow From(NcViewerMachineModel model) => new()
    {
        Node = model.Node,
        Path = model.Path,
        OffsetX = Format(model.OffsetMm?.X),
        OffsetY = Format(model.OffsetMm?.Y),
        OffsetZ = Format(model.OffsetMm?.Z),
        RotationX = Format(model.RotationDeg?.X),
        RotationY = Format(model.RotationDeg?.Y),
        RotationZ = Format(model.RotationDeg?.Z),
        Color = string.IsNullOrWhiteSpace(model.Color) ? "#9AA4AE" : model.Color!,
        Opacity = Format(model.Opacity ?? 1)
    };

    /// <summary>The Server value, or a message naming the first invalid field.</summary>
    internal bool TryCreate(out NcViewerMachineModel? model, out string error)
    {
        model = null;
        var label = string.IsNullOrWhiteSpace(Path) ? "A 3D model" : FileName;
        if (string.IsNullOrWhiteSpace(Node))
        {
            error = $"{label}: choose the kinematic node it is attached to.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(Path) || !System.IO.Path.IsPathFullyQualified(Path)
            || !string.Equals(System.IO.Path.GetExtension(Path), ".stl", StringComparison.OrdinalIgnoreCase))
        {
            error = $"{label}: the file must be a full path to an .stl file.";
            return false;
        }
        double[] values = new double[6];
        string[] texts = [OffsetX, OffsetY, OffsetZ, RotationX, RotationY, RotationZ];
        string[] names = ["offset X", "offset Y", "offset Z", "rotation X", "rotation Y", "rotation Z"];
        for (var index = 0; index < texts.Length; index++)
        {
            if (!TryParse(texts[index], out values[index])
                || (index < 3 ? Math.Abs(values[index]) > 100000 : Math.Abs(values[index]) > 360))
            {
                error = $"{label}: {names[index]} must be a number{(index < 3 ? " up to ±100000 mm" : " from -360 to 360 degrees")}.";
                return false;
            }
        }
        if (!TryParse(Opacity, out var opacity) || opacity < 0.05 || opacity > 1)
        {
            error = $"{label}: opacity must be from 0.05 to 1.";
            return false;
        }
        var color = string.IsNullOrWhiteSpace(Color) ? "#9AA4AE" : Color.Trim();
        if (!Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
        {
            error = $"{label}: the color must look like #9AA4AE.";
            return false;
        }
        model = new NcViewerMachineModel(
            Node.Trim(),
            Path.Trim(),
            new PlannerXyz(values[0], values[1], values[2]),
            new PlannerXyz(values[3], values[4], values[5]),
            color.ToUpperInvariant(),
            opacity);
        error = string.Empty;
        return true;
    }

    private static bool TryParse(string? text, out double value) =>
        double.TryParse(string.IsNullOrWhiteSpace(text) ? "0" : text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    private static string Format(double? value) => (value ?? 0).ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>
/// The kinematic node ids of an installed NC viewer machine definition (for the 3D-model node
/// list): <c>nc-engine\meimad\machines</c> replaces a vendored definition of the same id.
/// </summary>
internal static class NcViewerMachineNodes
{
    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    internal static IReadOnlyList<string> For(string? viewerMachineId, string? engineRoot = null)
    {
        if (string.IsNullOrWhiteSpace(viewerMachineId)) return [];
        var root = System.IO.Path.GetFullPath(engineRoot ?? NcEngineRuntime.DefaultEngineRoot);
        IReadOnlyList<string> found = [];
        foreach (var folder in new[] { System.IO.Path.Combine(root, "machines"), System.IO.Path.Combine(root, "meimad", "machines") })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(file), Options);
                    var element = document.RootElement;
                    if (element.ValueKind != JsonValueKind.Object
                        || !element.TryGetProperty("id", out var id) || id.GetString() != viewerMachineId) continue;
                    if (!element.TryGetProperty("kinematics", out var kinematics) || kinematics.ValueKind != JsonValueKind.Object
                        || !kinematics.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) continue;
                    found = nodes.EnumerateArray()
                        .Select(node => node.ValueKind == JsonValueKind.Object && node.TryGetProperty("id", out var nodeId) ? nodeId.GetString() : null)
                        .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
                        .Select(nodeId => nodeId!)
                        .ToArray();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    // An unreadable definition offers no nodes; the node can still be typed.
                }
            }
        }
        return found;
    }
}
