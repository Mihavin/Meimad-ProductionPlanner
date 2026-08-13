using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.JobPackages;

namespace Meimad.Planner.Server.Application.JobPackages;

internal interface IJobPackageRepository
{
    Task<JobPackageGenerationContext?> ReadGenerationContextAsync(
        string batchOperationId,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task PublishAsync(
        JobPackage package,
        JobPackageContextStamp expectedContext,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<JobPackageSetupWorker?> ReadSetupWorkerAsync(
        string resourceId,
        CancellationToken cancellationToken);
}

internal sealed record JobPackageGenerationContext(
    JobPackageSnapshot? Snapshot,
    string WorkingFolderPath,
    string? PreviewPath,
    JobPackageContextStamp Stamp);

internal sealed record JobPackageContextStamp(
    string CaseId,
    int CaseVersion,
    string BatchId,
    int BatchVersion,
    string BatchOperationId,
    int BatchOperationVersion,
    string? AssignmentId,
    int? AssignmentVersion,
    string? MachineId,
    int? MachineVersion);

internal sealed record GenerateJobPackageCommand(
    string BatchOperationId,
    string Revision,
    string? ToolCartId,
    bool IncludePreview,
    IReadOnlyList<JobPackageSourceFileCommand>? Files,
    IReadOnlyList<ToolTableEntry>? ToolTable,
    IReadOnlyList<OffsetEntry>? Offsets,
    string? Instructions,
    IReadOnlyList<ToolTableEntry>? ExpectedMachineTools = null,
    IReadOnlyList<LocalChecklistItem>? LocalChecklistItems = null);

internal sealed record JobPackageSourceFileCommand(
    string AssetType,
    string SourceRelativePath,
    string LogicalPath);

internal sealed record JobPackageSetupWorker(
    string ResourceId,
    string FirstName,
    string LastName,
    string? PhotoPath);
