using Meimad.Planner.Server.Domain.Readiness;
using Meimad.Planner.Server.Domain.ToolPreparations;

namespace Meimad.Planner.Server.Application.ProductionPackages;

internal sealed record ProductionPackageSelection(string MachineAssignmentId, string ProductionRunId,
    string ProductionRunProgramId, string ProductionRunOutputId, string? ContextStamp = null);

internal sealed record ProductionPackageContext(string MachineAssignmentId, string ProductionRunId,
    string ProductionRunProgramId, string ProductionRunOutputId, string BatchOperationId, string MachineId,
    string? ProcessRevisionId, string ContextStamp, int TargetQuantity, int ProgramNumber = 1,
    string? OutputAllocationStamp = null)
{
    internal ProductionPackageSelection Selection => new(MachineAssignmentId, ProductionRunId,
        ProductionRunProgramId, ProductionRunOutputId, ContextStamp);
}

internal static class ProductionPackageArtifactTypes
{
    internal const string RunnableNc = "RUNNABLE_NC";
    /// <summary>A subprogram file of the NC release, copied unchanged beside the runnable program.</summary>
    internal const string NcSubprogram = "NC_SUBPROGRAM";
    internal const string ToolTable = "TOOL_TABLE";
    internal const string OffsetLoader = "OFFSET_LOADER";
    /// <summary>The Tool Room's measured tools, offsets, shapes and components as JSON.</summary>
    internal const string ToolOffsets = "TOOL_OFFSETS";
    /// <summary>The control's offset-input program when the package has no verification Offset Loader.</summary>
    internal const string ToolOffsetProgram = "TOOL_OFFSET_PROGRAM";
    internal const string ManualSetup = "MANUAL_SETUP";
    internal const string Manifest = "MANIFEST";
}

internal sealed record ProductionPackageVerificationConfiguration(
    int Version,
    int ChallengeProgramNumber,
    int VerifyProgramNumber,
    int ExpectedMacroVersion,
    int EventSequenceVariable);

/// <summary>
/// Part counting for a Machine whose enabled CNC connection reads DPRNT output: the cycle
/// markers expand to CST/CEN events printed through that source. The sequence variable and
/// macro version come from the Machine's verification configuration when one exists (enabled or
/// not), else from the dialect's documented defaults.
/// </summary>
internal sealed record ProductionPackagePartCounting(
    string DprntSource,
    int EventSequenceVariable,
    int MacroVersion,
    bool FromConfiguration);

internal sealed record ProductionPackageBuildContext(
    string BatchOperationId,
    string? ProductionRunId,
    int? RunNumber,
    string MachineAssignmentId,
    string MachineId,
    string MachineNumber,
    string MachineName,
    string ExecutionMode,
    string PartName,
    string OperationName,
    string? GCodeReleaseId,
    string? GCodeOriginalFileName,
    string? GCodeStoredRelativePath,
    string? GCodeHash,
    int? NcIdentityToken,
    string ToolTableReleaseId,
    string ToolTableOriginalFileName,
    string ToolTableStoredRelativePath,
    string ToolTableHash,
    ProductionPackageVerificationConfiguration? Verification,
    bool DirectTransferConfigured,
    bool DirectTransferOnline,
    bool ManualDummyToolOffsetsAllowed,
    string? CurrentPackageId,
    ProductionReadinessContext ReadinessContext,
    string NcDialect = "HAAS_NGC",
    string ProcessType = "mill",
    string ToolDiameterOffsetKind = ToolDiameterOffsetKinds.Radius,
    IReadOnlyList<ToolPreparationReleasedTool>? ReleasedTools = null,
    ToolPreparation? ToolPreparation = null,
    ProductionPackagePartCounting? PartCounting = null,
    IReadOnlyList<ProductionPackageSubprogramSource>? Subprograms = null,
    ProductionPackageContext? Context = null,
    long PublicationVersion = 0,
    string? CurrentOffsetLoaderStamp = null);

/// <summary>A subprogram file of the package's NC release (schema v88).</summary>
internal sealed record ProductionPackageSubprogramSource(
    string SubprogramId,
    string OriginalFileName,
    string StoredRelativePath,
    string FileHash);

internal sealed record ProductionPackageArtifact(
    string ArtifactId,
    string ArtifactType,
    string LogicalPath,
    string StoredRelativePath,
    long FileSize,
    string FileHash,
    string? SourceReleaseId);

internal sealed record ProductionPackageRecord(
    string ProductionPackageId,
    int PackageNumber,
    string BatchOperationId,
    string? ProductionRunId,
    string MachineAssignmentId,
    string MachineId,
    string? GCodeReleaseId,
    string ToolTableReleaseId,
    string? OffsetLoaderReleaseId,
    string ExecutionMode,
    string ToolOffsetMode,
    bool VerificationEnabled,
    int? VerificationConfigurationVersion,
    int? VerificationMacroVersion,
    string ManifestRelativePath,
    string ManifestHash,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string? SupersedesPackageId,
    bool DirectTransferConfigured,
    bool DirectTransferOnline,
    IReadOnlyList<ProductionPackageArtifact> Artifacts,
    string? ToolPreparationId = null,
    ProductionPackageContext? Context = null);

internal sealed record OffsetLoaderPublication(
    string ReleaseId,
    int ReleaseToken,
    string ArtifactHash);

internal interface IProductionPackageRepository
{
    Task<ProductionPackageRecord?> ReadRequestAsync(ProductionPackageRequest request, CancellationToken token);
    Task<bool> IsReferencedAsync(string packageId, CancellationToken token);
    Task<ProductionPackageRecord> PublishAsync(ProductionPackageRecord package, OffsetLoaderPublication? loader,
        ProductionPackageBuildContext observed, ProductionPackageRequest? request, CancellationToken token);
    Task<ProductionPackageRecord?> ReadHistoricalAsync(string packageId, CancellationToken token) => Task.FromResult<ProductionPackageRecord?>(null);
    Task<ProductionPackageBuildContext?> ReadBuildContextAsync(
        string batchOperationId,
        CancellationToken cancellationToken);

    Task<ProductionPackageBuildContext?> ReadBuildContextAsync(string batchOperationId,
        ProductionPackageSelection? selection, CancellationToken cancellationToken) => selection is null ? ReadBuildContextAsync(batchOperationId, cancellationToken) : throw new NotSupportedException();

    Task<int> AllocatePackageNumberAsync(CancellationToken cancellationToken);

    Task<ProductionPackageRecord?> ReadCurrentAsync(
        string batchOperationId,
        CancellationToken cancellationToken);

    Task<ProductionPackageRecord?> ReadCurrentAsync(string batchOperationId,
        ProductionPackageSelection? selection, CancellationToken cancellationToken) => selection is null ? ReadCurrentAsync(batchOperationId, cancellationToken) : throw new NotSupportedException();
}
