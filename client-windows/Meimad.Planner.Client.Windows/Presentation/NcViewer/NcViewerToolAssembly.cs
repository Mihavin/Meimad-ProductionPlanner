using System.Globalization;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.ToolPreparation;

namespace Meimad.Planner.Client.Windows.Presentation.NcViewer;

/// <summary>One assembled part of a measured tool as the Tool Room recorded it (millimetres).</summary>
internal sealed record NcViewerToolAssemblyComponent(
    int Sequence,
    string ComponentType,
    string Name,
    string? CatalogNumber,
    double? Length,
    double? Diameter);

/// <summary>One segment of the tool drawing, from the spindle gauge line down (millimetres).</summary>
internal sealed record NcViewerToolAssemblySegment(
    string Kind,
    double Top,
    double Height,
    double Diameter,
    string Label,
    double TipAngle,
    double CornerRadius,
    bool IsDefault,
    double TopDiameter = 0);

/// <summary>
/// A tool the Tool Room has measured, exactly as the Tool Room shows it (owner decision 2026-09-29):
/// its row of the Tool Room table and the drawing the Tool Room builds from the holder, the other
/// components and the cutter shape (<see cref="ToolShapeBuilder"/>). The NC viewer lists it in its
/// tool table and draws it at the tool tip instead of the schematic cutter. Tools the Tool Room has
/// not measured have no assembly and keep the viewer's own tool.
/// </summary>
internal sealed record NcViewerToolAssembly(
    int Number,
    string Identifier,
    string Description,
    int? OffsetNumber,
    double? MeasuredLength,
    double? MeasuredDiameter,
    string ShapeType,
    string? Hand,
    string? Notes,
    IReadOnlyList<NcViewerToolAssemblyComponent> Components,
    IReadOnlyList<NcViewerToolAssemblySegment> Segments,
    double TotalLength,
    double MaximumDiameter,
    double AboveGaugeLength = 0)
{
    /// <summary>A measured tool: the Tool Room saved a length or a diameter for it.</summary>
    internal static bool IsMeasured(PlannerToolPreparation preparation, PlannerToolPreparationTool tool) =>
        preparation.Version > 0 && (tool.MeasuredLength is not null || tool.MeasuredDiameter is not null);

    internal static NcViewerToolAssembly From(int number, PlannerToolPreparationTool tool, ToolSpindleShape? spindle = null)
    {
        var components = tool.Components.OrderBy(component => component.Sequence).ToArray();
        var geometry = ToolShapeBuilder.Build(
            tool.ShapeType,
            tool.Shape,
            components.Select(component => new ToolShapeComponent(
                component.ComponentType, component.Name, component.Length, component.Diameter)).ToArray(),
            tool.MeasuredLength,
            tool.MeasuredDiameter,
            spindle);
        return new NcViewerToolAssembly(
            number,
            tool.ToolIdentifier,
            tool.Description.Trim(),
            tool.OffsetNumber,
            tool.MeasuredLength,
            tool.MeasuredDiameter,
            tool.ShapeType.ToUpperInvariant(),
            tool.Hand,
            string.IsNullOrWhiteSpace(tool.Notes) ? null : tool.Notes.Trim(),
            components.Select(component => new NcViewerToolAssemblyComponent(
                component.Sequence, component.ComponentType.ToUpperInvariant(), component.Name,
                string.IsNullOrWhiteSpace(component.CatalogNumber) ? null : component.CatalogNumber.Trim(),
                component.Length, component.Diameter)).ToArray(),
            geometry.Segments.Select(segment => new NcViewerToolAssemblySegment(
                segment.Kind, Round(segment.Top), Round(segment.Height), Round(segment.Diameter), segment.Label,
                segment.TipAngle, Round(segment.CornerRadius), segment.IsDefault, Round(segment.TopDiameter))).ToArray(),
            Round(geometry.TotalLength),
            Round(geometry.MaximumDiameter),
            Round(geometry.AboveGaugeLength));
    }

    /// <summary>The components in one line, for the viewer's table ("Holder BT40 L70 D63 / Collet ER32").</summary>
    internal string ComponentsText => string.Join(" / ", Components.Select(component =>
    {
        var parts = new List<string> { component.Name };
        if (component.CatalogNumber is { } catalog && !string.Equals(catalog, component.Name, StringComparison.OrdinalIgnoreCase)) parts.Add(catalog);
        if (component.Length is { } length) parts.Add("L" + length.ToString("0.###", CultureInfo.InvariantCulture));
        if (component.Diameter is { } diameter) parts.Add("D" + diameter.ToString("0.###", CultureInfo.InvariantCulture));
        return string.Join(" ", parts);
    }));

    private static double Round(double value) => Math.Round(value, 4);
}
