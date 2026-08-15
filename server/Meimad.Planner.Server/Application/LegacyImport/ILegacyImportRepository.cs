using Meimad.Planner.Server.Application.EditMode;

namespace Meimad.Planner.Server.Application.LegacyImport;

internal interface ILegacyImportRepository
{
    Task<LegacyImportCandidatePool> ReadCandidatePoolAsync(CancellationToken cancellationToken);

    Task<LegacyImportCommitResponse> CommitAsync(
        LegacyImportCommitRequest request,
        LegacyImportPreviewResponse approvedPreview,
        string requestSha256,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);
}

internal sealed record LegacyImportCandidatePool(
    IReadOnlyList<LegacyImportCaseCandidate> Cases,
    IReadOnlyList<LegacyImportOrderCandidate> Orders,
    IReadOnlyList<LegacyImportBatchCandidate> Batches,
    IReadOnlyList<LegacyImportCaseOperationCandidate> CaseOperations,
    IReadOnlyList<LegacyImportBatchOperationCandidate> BatchOperations,
    IReadOnlyList<LegacyImportMachineCandidate> Machines);

internal sealed record LegacyImportCaseCandidate(
    string CaseId,
    string PartNumber,
    string Name,
    string? Revision,
    string? Customer);

internal sealed record LegacyImportOrderCandidate(
    string OrderId,
    string CaseId,
    string OrderNumber,
    int Quantity,
    DateOnly WorkFinishDate);

internal sealed record LegacyImportBatchCandidate(
    string BatchId,
    string CaseId,
    string BatchNumber,
    int PlannedQuantity);

internal sealed record LegacyImportCaseOperationCandidate(
    string CaseOperationId,
    string CaseId,
    int OperationNumber,
    string Name,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    int Version);

internal sealed record LegacyImportBatchOperationCandidate(
    string BatchOperationId,
    string BatchId,
    string SourceCaseOperationId,
    int OperationNumber,
    string Name,
    string Status,
    string? RequiredMachineType,
    int Version,
    string? AssignmentId,
    string? MachineId,
    int? AssignmentVersion);

internal sealed record LegacyImportMachineCandidate(
    string MachineId,
    string Number,
    string Name,
    string? AxisType,
    string ProcessType,
    bool IsActive);
