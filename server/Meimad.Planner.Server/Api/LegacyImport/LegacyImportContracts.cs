using Meimad.Planner.Server.Domain.LegacyImport;

namespace Meimad.Planner.Server.Application.LegacyImport;

internal sealed record LegacyImportPreviewResponse(
    int SchemaVersion,
    string ImportToken,
    string WorkbookSha256,
    DateTimeOffset ExpiresAt,
    LegacyWorkbookResponse Workbook,
    LegacyImportSuggestionsResponse Suggestions,
    IReadOnlyList<LegacyMachineSectionResponse> MachineSections,
    IReadOnlyList<LegacyPlanningRowResponse> Rows,
    IReadOnlyList<LegacyOpenOrderRowResponse> OpenOrderRows,
    IReadOnlyList<LegacyImportIssueResponse> Issues);

internal sealed record LegacyWorkbookResponse(
    string FileName,
    IReadOnlyList<LegacyWorkbookSheetResponse> Sheets);

internal sealed record LegacyWorkbookSheetResponse(string Name, int RowCount, int ColumnCount);

internal sealed record LegacyImportSuggestionsResponse(
    string? PlanningSheet,
    string? OpenOrdersSheet,
    IReadOnlyList<LegacyColumnSuggestionResponse> PlanningColumns,
    IReadOnlyList<LegacyColumnSuggestionResponse> OpenOrderColumns);

internal sealed record LegacyColumnSuggestionResponse(
    string Field,
    string Column,
    string? Header,
    decimal Confidence);

internal sealed record LegacyMachineSectionResponse(
    string SectionKey,
    string SheetName,
    int HeaderRow,
    string SourceLabel,
    int FirstDataRow,
    int LastDataRow,
    IReadOnlyList<LegacyMachineCandidateResponse> Candidates);

internal sealed record LegacyMachineCandidateResponse(
    string MachineId,
    string Number,
    string Name,
    string ProcessType,
    string? AxisType,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> MachineTypeCapabilities,
    decimal Score,
    string Reason);

internal sealed record LegacyPlanningRowResponse(
    string RowKey,
    string SheetName,
    int RowNumber,
    string SectionKey,
    int SourceOrder,
    LegacyPlanningValuesResponse Values,
    IReadOnlyList<LegacyCellProvenanceResponse> Provenance,
    LegacyPlanningCandidatesResponse Candidates);

internal sealed record LegacyPlanningValuesResponse(
    string? Customer,
    string? PartNumber,
    string? CaseReference,
    string? Notes,
    int? Quantity,
    string? MaterialStatus,
    string? StartDate,
    string? EndDate,
    string? PlannerDeliveryDate,
    string? CustomerDeliveryDate);

internal sealed record LegacyOpenOrderRowResponse(
    string RowKey,
    string SheetName,
    int RowNumber,
    int SourceOrder,
    LegacyOpenOrderValuesResponse Values,
    IReadOnlyList<LegacyCellProvenanceResponse> Provenance,
    LegacyOpenOrderCandidatesResponse Candidates);

internal sealed record LegacyOpenOrderValuesResponse(
    string? PartNumber,
    string? OrderNumber,
    string? OrderLine,
    string? Customer,
    string? DeliveryDate,
    string? Revision,
    int? OutstandingQuantity,
    string? Notes,
    string? DrawingNumber,
    string? CaseReference,
    int? OrderedQuantity,
    string? ItemName,
    string? PicturePath);

internal sealed record LegacyCellProvenanceResponse(
    string Field,
    string Column,
    string Address,
    string Kind,
    string? Formula,
    string? Raw);

internal sealed record LegacyPlanningCandidatesResponse(
    IReadOnlyList<LegacyCaseCandidateResponse> Cases,
    IReadOnlyList<LegacyOrderCandidateResponse> Orders,
    IReadOnlyList<LegacyBatchCandidateResponse> Batches,
    IReadOnlyList<LegacyCaseOperationCandidateResponse> CaseOperations,
    IReadOnlyList<LegacyBatchOperationCandidateResponse> BatchOperations);

internal sealed record LegacyOpenOrderCandidatesResponse(
    IReadOnlyList<LegacyCaseCandidateResponse> Cases,
    IReadOnlyList<LegacyOrderCandidateResponse> Orders);

internal sealed record LegacyCaseCandidateResponse(
    string CaseId,
    string PartNumber,
    string Name,
    string? Revision,
    string? Customer,
    string Reason);

internal sealed record LegacyOrderCandidateResponse(
    string OrderId,
    string OrderNumber,
    int Quantity,
    string WorkFinishDate,
    string Reason);

internal sealed record LegacyBatchCandidateResponse(
    string BatchId,
    string BatchNumber,
    int PlannedQuantity,
    string Reason);

internal sealed record LegacyCaseOperationCandidateResponse(
    string CaseOperationId,
    string CaseId,
    int OperationNumber,
    string Name,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    int Version);

internal sealed record LegacyBatchOperationCandidateResponse(
    string BatchOperationId,
    string BatchId,
    string BatchNumber,
    string CaseId,
    string PartNumber,
    string CaseOperationId,
    int OperationNumber,
    string Name,
    string Status,
    string? RequiredMachineType,
    int Version,
    string? AssignmentId,
    string? MachineId,
    int? AssignmentVersion);

internal sealed record LegacyImportIssueResponse(
    string Severity,
    string Code,
    string Message,
    string? SheetName,
    int? RowNumber,
    string? Field,
    string? SectionKey)
{
    internal static LegacyImportIssueResponse FromDomain(LegacyImportIssue issue) => new(
        issue.Severity.ToToken(),
        issue.Code,
        issue.Message,
        issue.SheetName,
        issue.RowNumber,
        issue.Field,
        issue.SectionKey);
}

internal sealed record LegacyImportCommitRequest(
    int SchemaVersion,
    string? ImportToken,
    string? WorkbookSha256,
    string? PlanningSheet,
    string? OpenOrdersSheet,
    IReadOnlyList<LegacyColumnMappingRequest>? ColumnMappings,
    IReadOnlyList<LegacyMachineMappingRequest>? MachineMappings,
    IReadOnlyList<LegacyOpenOrderSelectionRequest>? OpenOrderSelections,
    IReadOnlyList<LegacyPlanningSelectionRequest>? PlanningSelections);

internal sealed record LegacyColumnMappingRequest(string? Scope, string? Field, string? Column);

internal sealed record LegacyMachineMappingRequest(string? SectionKey, string? MachineId);

internal sealed record LegacyOpenOrderSelectionRequest(
    string? RowKey,
    string? Action,
    string? ExistingCaseId,
    LegacyNewCaseRequest? NewCase,
    LegacyNewOrderRequest? Order);

internal sealed record LegacyNewCaseRequest(
    string? PartNumber,
    string? Name,
    string? Revision,
    string? Customer,
    string? CustomerReference,
    string? WorkingFolderPath,
    string? Notes);

internal sealed record LegacyNewOrderRequest(
    string? OrderNumber,
    int? Quantity,
    string? WorkFinishDate,
    string? Notes);

internal sealed record LegacyPlanningSelectionRequest(
    string? RowKey,
    string? Action,
    string? BatchOperationId,
    string? CaseId,
    string? CaseSourceRowKey,
    string? CaseOperationId,
    string? BatchNumber,
    IReadOnlyList<LegacyAllocationRequest>? Allocations,
    string? MachineId,
    LegacyCompatibilityOverrideRequest? CompatibilityOverride);

internal sealed record LegacyCompatibilityOverrideRequest(bool Confirmed, string? Reason);

internal sealed record LegacyAllocationRequest(
    string? Type,
    string? OrderId,
    string? OrderSourceRowKey,
    int? Quantity);

internal sealed record LegacyImportCommitResponse(
    int SchemaVersion,
    string WorkbookSha256,
    string CommitId,
    bool Replayed,
    LegacyImportEntityIdsResponse Created,
    LegacyImportEntityIdsResponse Unchanged,
    IReadOnlyList<LegacyImportedMachineBacklogResponse> MachineBacklogs);

internal sealed record LegacyImportEntityIdsResponse(
    IReadOnlyList<string> CaseIds,
    IReadOnlyList<string> OrderIds,
    IReadOnlyList<string> BatchIds,
    IReadOnlyList<string> AssignmentIds);

internal sealed record LegacyImportedMachineBacklogResponse(
    string MachineId,
    IReadOnlyList<string> AssignmentIdsInImportedSourceOrder);
