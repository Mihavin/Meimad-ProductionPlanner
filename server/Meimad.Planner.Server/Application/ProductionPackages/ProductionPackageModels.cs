using Meimad.Planner.Server.Domain.Readiness;
using Meimad.Planner.Server.Domain.ToolPreparations;

namespace Meimad.Planner.Server.Application.ProductionPackages;

internal static class ProductionPackageArtifactTypes
{
    internal const string RunnableNc = "RUNNABLE_NC";
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
    ToolPreparation? ToolPreparation = null);

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
    string? ToolPreparationId = null);

internal sealed record OffsetLoaderPublication(
    string ReleaseId,
    int ReleaseToken,
    string ArtifactHash);

internal interface IProductionPackageRepository
{
    Task<ProductionPackageBuildContext?> ReadBuildContextAsync(
        string batchOperationId,
        CancellationToken cancellationToken);

    Task<int> AllocatePackageNumberAsync(CancellationToken cancellationToken);

    Task ActivateAsync(
        ProductionPackageRecord package,
        OffsetLoaderPublication? offsetLoader,
        CancellationToken cancellationToken);

    Task<ProductionPackageRecord?> ReadCurrentAsync(
        string batchOperationId,
        CancellationToken cancellationToken);
}
