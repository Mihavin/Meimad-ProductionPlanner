using Meimad.Planner.NcEngine;
using Meimad.Planner.NcEngine.MachineDefinitions;

namespace Meimad.Planner.Server.Domain.Machines;

/// <summary>
/// The Server side of the Machine simulation definition (schema v114, docs/machine-simulation-definition.md):
/// the definition a Machine starts with, and the Machine columns it projects.
/// </summary>
internal static class MachineSimulationDefinitions
{
    internal const int MaximumDefinitionBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The definition of a Machine built from its NC viewer machine (the installed engine template) and its own magazine,
    /// timing and schema v113 model values. Unknown values stay null; nothing physical is invented.
    /// </summary>
    internal static MachineSimulationDefinition Initial(
        string machineId,
        string? name,
        string? ncViewerMachine,
        int? usableToolPositions,
        double? rapidRateMillimetersPerMinute,
        double? toolChangeTimeSeconds,
        double machineTimeFactor,
        IReadOnlyList<MachineNcViewerModel>? models,
        string? engineRoot = null)
    {
        var template = ncViewerMachine is null ? null : NcEngineMachineCatalog.LoadDefinition(ncViewerMachine, engineRoot);
        return MachineSimulationCompiler.FromTemplate(template, ncViewerMachine, new MachineLegacyValues(
            machineId,
            name,
            usableToolPositions,
            rapidRateMillimetersPerMinute,
            toolChangeTimeSeconds,
            machineTimeFactor,
            (models ?? []).Select(model => new MachineLegacyModel(
                model.Node,
                model.Path,
                [model.OffsetMm.X, model.OffsetMm.Y, model.OffsetMm.Z],
                [model.RotationDeg.X, model.RotationDeg.Y, model.RotationDeg.Z],
                model.Color,
                model.Opacity)).ToArray()));
    }

    /// <summary>
    /// The schema v113 model list a published definition projects: its meshes that are still file paths (imported
    /// meshes are served from the asset store and read from the definition).
    /// </summary>
    internal static IReadOnlyList<MachineNcViewerModel> ProjectedModels(MachineSimulationDefinition definition) =>
        definition.Nodes
            .SelectMany(node => node.Models
                .Where(model => !string.IsNullOrWhiteSpace(model.LegacyPath))
                .Select(model => new MachineNcViewerModel(
                    node.Id,
                    model.LegacyPath!,
                    Vector(model.Alignment.Position),
                    Vector(model.Alignment.RotationDeg),
                    model.Color ?? MachineNcViewerModels.DefaultColor,
                    Math.Clamp(model.Opacity, MachineNcViewerModels.MinimumOpacity, 1))))
            .Take(MachineNcViewerModels.MaximumCount)
            .ToArray();

    /// <summary>Applies <paramref name="map"/> to every legacy mesh path (network-folder storage form and back).</summary>
    internal static MachineSimulationDefinition MapLegacyPaths(MachineSimulationDefinition definition, Func<string, string> map) =>
        definition with
        {
            Nodes = definition.Nodes.Select(node => node.Models.Any(model => model.LegacyPath is not null)
                ? node with
                {
                    Models = node.Models.Select(model => model.LegacyPath is null ? model : model with { LegacyPath = map(model.LegacyPath) }).ToArray()
                }
                : node).ToArray()
        };

    internal static IEnumerable<string> LegacyPaths(MachineSimulationDefinition definition) =>
        definition.Nodes.SelectMany(node => node.Models).Select(model => model.LegacyPath).OfType<string>();

    internal static IEnumerable<string> AssetIds(MachineSimulationDefinition definition) =>
        definition.Nodes.SelectMany(node => node.Models).Select(model => model.AssetId).OfType<string>();

    private static MachineVector3 Vector(double[] value) =>
        MachineVectors.IsFinite3(value) ? new MachineVector3(value[0], value[1], value[2]) : MachineVector3.Zero;
}
