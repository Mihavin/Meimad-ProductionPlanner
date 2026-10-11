using Meimad.Planner.NcEngine;
using Meimad.Planner.NcEngine.MachineDefinitions;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Machines;

namespace Meimad.Planner.Server.Application.Machines;

/// <summary>A Machine's simulation definition: the draft being edited and the published copy everything else uses.</summary>
internal sealed record MachineSimulationRecord(
    string MachineId,
    MachineSimulationDefinition Draft,
    int DraftVersion,
    MachineSimulationDefinition? Published,
    int PublishedVersion,
    DateTimeOffset? PublishedAt,
    string? PublishedBy,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

/// <summary>An uploaded mesh of a Machine (stored below the release root, identified by id, never by a client path).</summary>
internal sealed record MachineSimulationAsset(
    string AssetId,
    string MachineId,
    string FileName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    string StoredRelativePath,
    DateTimeOffset CreatedAt,
    string? CreatedBy);

internal enum MachineSimulationOutcome
{
    Saved,
    Published,
    MachineNotFound,
    Stale,
    Invalid
}

/// <summary>
/// The outcome of a definition write. <see cref="Record"/> is the saved record on success and the current one when the
/// write was refused (stale or invalid); <see cref="Issues"/> are the validation findings of the written definition.
/// </summary>
internal sealed record MachineSimulationWriteResult(
    MachineSimulationOutcome Outcome,
    MachineSimulationRecord? Record,
    IReadOnlyList<MachineSimulationIssue> Issues);

/// <summary>The Machine columns a published definition writes (docs/machine-simulation-definition.md §1).</summary>
internal sealed record MachineSimulationProjection(
    int? UsableToolPositions,
    double? RapidRateMillimetersPerMinute,
    double? ToolChangeTimeSeconds,
    double MachineTimeFactor,
    string? NcViewerMachine,
    IReadOnlyList<MachineNcViewerModel> NcViewerModels);

/// <summary>A definition's issues; <see cref="Refused"/> when the write must not happen.</summary>
internal sealed record MachineSimulationCheckResult(IReadOnlyList<MachineSimulationIssue> Issues, bool Refused);

/// <summary>Checks a definition against Server state inside the write transaction.</summary>
internal delegate MachineSimulationCheckResult MachineSimulationCheck(
    MachineSimulationRecord current, IReadOnlySet<string> assetIds);

internal interface IMachineSimulationRepository
{
    /// <summary>The Machine's definition record, or null when the Machine does not exist.</summary>
    Task<MachineSimulationRecord?> GetAsync(string machineId, CancellationToken cancellationToken);

    /// <summary>The published definitions of every Machine (machine id, published version, definition).</summary>
    Task<IReadOnlyList<(string MachineId, int PublishedVersion, MachineSimulationDefinition Published)>> ListPublishedAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the draft when it is still at <paramref name="expectedDraftVersion"/> and the check finds no error.</summary>
    Task<MachineSimulationWriteResult> SaveDraftAsync(
        string machineId,
        MachineSimulationDefinition draft,
        int expectedDraftVersion,
        MachineSimulationCheck check,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Publishes the draft at <paramref name="expectedDraftVersion"/> when the check finds no error: the published copy
    /// becomes the draft, and the Machine's projected columns, version and cycle estimates are updated in the same transaction.
    /// </summary>
    Task<MachineSimulationWriteResult> PublishAsync(
        string machineId,
        int expectedDraftVersion,
        MachineSimulationCheck check,
        Func<MachineSimulationDefinition, MachineSimulationProjection> projection,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MachineSimulationAsset>?> ListAssetsAsync(string machineId, CancellationToken cancellationToken);

    Task<MachineSimulationAsset?> GetAssetAsync(string machineId, string assetId, CancellationToken cancellationToken);

    /// <summary>Records an asset (or returns the Machine's existing asset with the same hash); null when the Machine is missing.</summary>
    Task<MachineSimulationAsset?> AddAssetAsync(MachineSimulationAsset asset, CancellationToken cancellationToken);
}

/// <summary>
/// Machine Designer definitions (schema v114): drafts are saved with optimistic concurrency and returned with their
/// validation issues; publishing requires a definition without errors. The published copy is the only write path of
/// the Machine's magazine, timing and NC viewer machine values.
/// </summary>
internal sealed class MachineSimulationService(
    IMachineSimulationRepository repository,
    TimeProvider timeProvider,
    NcEngineMachineCatalog? catalog = null)
{
    public Task<MachineSimulationRecord?> GetAsync(string machineId, CancellationToken cancellationToken) =>
        repository.GetAsync(machineId.Trim(), cancellationToken);

    public Task<IReadOnlyList<(string MachineId, int PublishedVersion, MachineSimulationDefinition Published)>> ListPublishedAsync(
        CancellationToken cancellationToken) => repository.ListPublishedAsync(cancellationToken);

    public async Task<IReadOnlyList<MachineSimulationIssue>?> ValidateAsync(
        string machineId, MachineSimulationDefinition definition, CancellationToken cancellationToken)
    {
        var current = await repository.GetAsync(machineId.Trim(), cancellationToken);
        if (current is null) return null;
        var assets = await repository.ListAssetsAsync(current.MachineId, cancellationToken) ?? [];
        return Check(Prepare(current.MachineId, definition), forPublish: true)(
            current, assets.Select(asset => asset.AssetId).ToHashSet(StringComparer.Ordinal)).Issues;
    }

    public Task<MachineSimulationWriteResult> SaveDraftAsync(
        string machineId,
        MachineSimulationDefinition draft,
        int expectedDraftVersion,
        EditAuthority authority,
        CancellationToken cancellationToken)
    {
        var prepared = Prepare(machineId.Trim(), draft);
        return repository.SaveDraftAsync(
            prepared.MachineId!, prepared, expectedDraftVersion, Check(prepared, forPublish: false),
            SignedInActor.Require(authority), timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<MachineSimulationWriteResult> PublishAsync(
        string machineId,
        int expectedDraftVersion,
        EditAuthority authority,
        CancellationToken cancellationToken) =>
        repository.PublishAsync(
            machineId.Trim(), expectedDraftVersion,
            (current, assets) => Check(current.Draft, forPublish: true)(current, assets),
            Project,
            SignedInActor.Require(authority), timeProvider.GetUtcNow(), cancellationToken);

    public Task<IReadOnlyList<MachineSimulationAsset>?> ListAssetsAsync(string machineId, CancellationToken cancellationToken) =>
        repository.ListAssetsAsync(machineId.Trim(), cancellationToken);

    public Task<MachineSimulationAsset?> GetAssetAsync(string machineId, string assetId, CancellationToken cancellationToken) =>
        repository.GetAssetAsync(machineId.Trim(), assetId.Trim().ToLowerInvariant(), cancellationToken);

    internal static MachineSimulationProjection Project(MachineSimulationDefinition definition) => new(
        definition.Magazine.UsablePositions,
        definition.Timing.RapidRateMillimetersPerMinute,
        definition.Magazine.ToolChangeSeconds,
        definition.Timing.MachineTimeFactor,
        definition.Template,
        MachineSimulationDefinitions.ProjectedModels(definition));

    private static MachineSimulationDefinition Prepare(string machineId, MachineSimulationDefinition definition) =>
        definition.Normalized() with { MachineId = machineId };

    /// <summary>
    /// The definition's issues with Server state: assets must belong to the Machine, a legacy mesh path may only be kept
    /// (never added: new meshes are uploaded), the template must be installed, and the projected Machine values must be
    /// valid Machine values. A save is refused only for a new legacy path; publishing is refused for any error.
    /// </summary>
    private MachineSimulationCheck Check(MachineSimulationDefinition definition, bool forPublish) => (current, assets) =>
    {
        var issues = MachineSimulationValidator.Validate(definition).ToList();
        var knownPaths = MachineSimulationDefinitions.LegacyPaths(current.Draft)
            .Concat(current.Published is null ? [] : MachineSimulationDefinitions.LegacyPaths(current.Published))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 0; n < definition.Nodes.Count; n++)
        {
            for (var m = 0; m < definition.Nodes[n].Models.Count; m++)
            {
                var model = definition.Nodes[n].Models[m];
                var path = $"nodes[{n}].models[{m}]";
                if (model.AssetId is { } assetId && !assets.Contains(assetId))
                    issues.Add(new(path + ".assetId", "asset_missing", "The mesh is not uploaded for this Machine; import it again."));
                if (model.LegacyPath is { } legacy && !knownPaths.Contains(legacy))
                    issues.Add(new(path + ".legacyPath", "legacy_path_not_allowed",
                        "Meshes are imported into the Server asset store; a file path cannot be added."));
            }
        }
        if (definition.Template is { } template && catalog is not null && !catalog.Contains(template))
            issues.Add(new("template", "template_not_installed", $"The NC viewer machine '{template}' is not installed on the Server."));
        if (forPublish)
        {
            try
            {
                var projection = Project(definition);
                MachineValidator.ValidateAndNormalize(new MachineValues(
                    "-", "-", "-", null, [], "-", true, true,
                    UsableToolPositions: projection.UsableToolPositions,
                    RapidRateMillimetersPerMinute: projection.RapidRateMillimetersPerMinute,
                    ToolChangeTimeSeconds: projection.ToolChangeTimeSeconds,
                    MachineTimeFactor: projection.MachineTimeFactor,
                    NcViewerMachine: projection.NcViewerMachine,
                    NcViewerModels: MachineNcViewerModels.ToInputs(projection.NcViewerModels)));
            }
            catch (MachineValidationException exception)
            {
                issues.AddRange(exception.Issues.Select(issue => new MachineSimulationIssue(ProjectedPath(issue.Field), issue.Code, issue.Message)));
            }
        }
        return new MachineSimulationCheckResult(issues, forPublish
            ? issues.Any(issue => issue.IsError)
            : issues.Any(issue => issue.Code == "legacy_path_not_allowed"));
    };

    private static string ProjectedPath(string field) => field switch
    {
        "usableToolPositions" => "magazine.usablePositions",
        "toolChangeTimeSeconds" => "magazine.toolChangeSeconds",
        "rapidRateMillimetersPerMinute" => "timing.rapidRateMillimetersPerMinute",
        "machineTimeFactor" => "timing.machineTimeFactor",
        "ncViewerMachine" => "template",
        _ => field
    };
}
