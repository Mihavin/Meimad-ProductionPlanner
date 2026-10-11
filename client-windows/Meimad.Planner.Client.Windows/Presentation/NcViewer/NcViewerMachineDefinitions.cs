using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.NcEngine;
using Meimad.Planner.NcEngine.MachineDefinitions;

namespace Meimad.Planner.Client.Windows.Presentation.NcViewer;

/// <summary>
/// A Machine's published simulation definition compiled for this client's NC engine (engine machine
/// <see cref="CompiledId"/>), with the meshes the viewer draws on its components.
/// </summary>
internal sealed record NcViewerCompiledMachine(
    string MachineId,
    string CompiledId,
    string? Template,
    string CompiledJson,
    MachineSimulationDefinition Definition,
    IReadOnlyList<NcViewerMachineModel> Models,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Loads the published Machine Designer definitions (schema v114) for the NC viewer: each is compiled against the
/// template installed with this client's NC engine, and its uploaded meshes are downloaded once into a local cache
/// (checked against their SHA-256) so the viewer serves them like any model file. The Server stays the authority;
/// the cache only avoids downloading a mesh again.
/// </summary>
internal static class NcViewerMachineDefinitions
{
    internal static string CacheFolder => Path.Combine(NcViewerSettingsStore.DataFolder, "MachineAssets");

    internal static async Task<IReadOnlyList<NcViewerCompiledMachine>> LoadAsync(
        IPlannerApiClient api, string? engineRoot = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PublishedMachineSimulation> published;
        try
        {
            published = await api.ListPublishedMachineSimulationsAsync(cancellationToken);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            return [];
        }
        var result = new List<NcViewerCompiledMachine>();
        foreach (var item in published)
        {
            var compiled = Compile(item.MachineId, item.Published, engineRoot);
            if (compiled is null) continue;
            var (models, warnings) = await ModelsAsync(api, item.MachineId, item.Published, cancellationToken);
            result.Add(compiled with { Models = models, Warnings = warnings });
        }
        return result;
    }

    /// <summary>The compiled machine, or null when the definition has no components or its template is not installed here.</summary>
    internal static NcViewerCompiledMachine? Compile(string machineId, MachineSimulationDefinition definition, string? engineRoot = null)
    {
        if (definition.Template is not { } template || definition.Nodes.Count == 0) return null;
        var templateJson = NcEngineMachineCatalog.LoadDefinition(template, engineRoot);
        var compiled = MachineSimulationCompiler.Compile(definition with { MachineId = machineId }, templateJson);
        return compiled is null
            ? null
            : new NcViewerCompiledMachine(machineId, MachineSimulationCompiler.CompiledId(machineId), template,
                compiled.ToJsonString(), definition, [], []);
    }

    /// <summary>
    /// The visible meshes of the definition as viewer models: file paths carried over from schema v113 as they are, uploaded
    /// meshes from the local cache. A mesh that cannot be downloaded is reported, and its component keeps its primitive.
    /// </summary>
    internal static async Task<(IReadOnlyList<NcViewerMachineModel> Models, IReadOnlyList<string> Warnings)> ModelsAsync(
        IPlannerApiClient api, string machineId, MachineSimulationDefinition definition, CancellationToken cancellationToken)
    {
        var models = new List<NcViewerMachineModel>();
        var warnings = new List<string>();
        IReadOnlyList<MachineSimulationAssetDto>? assets = null;
        foreach (var node in definition.Nodes)
        {
            foreach (var model in node.Models.Where(model => model.Visible))
            {
                string? path = model.LegacyPath;
                if (model.AssetId is { } assetId)
                {
                    try
                    {
                        assets ??= await api.ListMachineSimulationAssetsAsync(machineId, cancellationToken);
                        path = assets.FirstOrDefault(asset => asset.AssetId == assetId) is { } asset
                            ? await CachedAsync(api, machineId, asset, cancellationToken)
                            : null;
                        if (path is null) warnings.Add($"Mesh {model.Name ?? assetId} of {node.Label ?? node.Id} is not on the Server.");
                    }
                    catch (Exception exception) when (IsTransient(exception) || exception is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        warnings.Add($"Mesh {model.Name ?? assetId} of {node.Label ?? node.Id} could not be downloaded: {exception.Message}");
                        path = null;
                    }
                }
                if (string.IsNullOrWhiteSpace(path)) continue;
                var alignment = model.Alignment;
                models.Add(new NcViewerMachineModel(
                    node.Id,
                    path,
                    Xyz(alignment.Position),
                    Xyz(alignment.RotationDeg),
                    model.Color,
                    model.Opacity,
                    alignment.Scale,
                    model.Wireframe));
            }
        }
        return (models, warnings);
    }

    /// <summary>The asset's file in the local cache, downloaded and hash-checked when missing.</summary>
    internal static async Task<string> CachedAsync(
        IPlannerApiClient api, string machineId, MachineSimulationAssetDto asset, CancellationToken cancellationToken)
    {
        var extension = asset.MediaType == "model/gltf-binary" ? ".glb" : ".stl";
        Directory.CreateDirectory(CacheFolder);
        var path = Path.Combine(CacheFolder, asset.Sha256.ToLowerInvariant() + extension);
        if (File.Exists(path)) return path;
        var bytes = await api.DownloadMachineSimulationAssetAsync(machineId, asset.AssetId, cancellationToken);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded mesh does not match its SHA-256.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    private static PlannerXyz Xyz(double[] value) =>
        MachineVectors.IsFinite3(value) ? new PlannerXyz(value[0], value[1], value[2]) : PlannerXyz.Zero;

    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or PlannerApiException or NotSupportedException or PlannerProtocolException;
}
