using System.Net;
using System.Text.Json.Serialization;

namespace Meimad.Planner.Client.Windows.Api;

internal enum ClientEditState
{
    Viewer,
    Editor,
    RequestingEdit
}

internal sealed record ServerHealth(
    string Status,
    string Service,
    string Version,
    DateTimeOffset ServerTimeUtc);

internal sealed record EditModeHolder(
    string ClientId,
    string UserId,
    long Generation,
    DateTimeOffset AcquiredAt);

internal sealed record EditTransferRequest(
    string RequestId,
    string RequesterClientId,
    string RequesterUserId,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset DecisionDeadline);

internal sealed record EditModeStatus(
    ClientEditState State,
    long Generation,
    EditModeHolder? Holder,
    EditTransferRequest? PendingRequest,
    DateTimeOffset ServerTime,
    int TransferTimeoutSeconds);

internal sealed record PlannerCase(
    string CaseId,
    string PartNumber,
    string Name,
    string? Revision,
    string? Customer,
    string? CustomerReference,
    string? PreviewPath,
    string WorkingFolderPath,
    string? MaterialType,
    string? MaterialSpecification,
    string? RawMaterialForm,
    string? RawMaterialDimensions,
    int? CurrentSetupTimeSeconds,
    int? CurrentCycleTimePerPartSeconds,
    string? Notes,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record CaseResource(PlannerCase Value, string EntityTag);

internal sealed record CaseQuery(string? Search, string? Customer, bool? IsActive, string? Sort = null);

internal sealed record CaseUpdate(
    string PartNumber,
    string Name,
    string? Revision,
    string? Customer,
    string? CustomerReference,
    string? PreviewPath,
    string WorkingFolderPath,
    string? MaterialType,
    string? MaterialSpecification,
    string? RawMaterialForm,
    string? RawMaterialDimensions,
    string? Notes);

internal sealed record PlannerMachine(
    string MachineId,
    string Number,
    string Name,
    string ProcessType,
    string? AxisType,
    IReadOnlyList<string> Capabilities,
    string WorkingCalendarId,
    bool IsActive,
    bool DisplayEnabled,
    string? PicturePath,
    string? DeviceId,
    int BacklogCount,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? MachineTypeId = null,
    bool RespectMasterCalendar = true)
{
    public string DisplayName => $"{Number} — {Name}";
}

internal sealed record MachineResource(PlannerMachine Value, string EntityTag);

internal sealed record MachineDowntime(
    string DowntimeId,
    string MachineId,
    string DowntimeType,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    string Reason,
    string? PlannedBy,
    string? RepairNote,
    string? ReportedBy,
    string Status,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string DisplayName => DowntimeType == "breakdown"
        ? $"{StartsAt.ToLocalTime():yyyy-MM-dd HH:mm} - Breakdown ({Status})"
        : $"{StartsAt.ToLocalTime():yyyy-MM-dd HH:mm} - Maintenance";
}

internal sealed record MachineDowntimeResource(MachineDowntime Value, string EntityTag);
internal sealed record MachineDowntimeCreate(
    string DowntimeType, string MachineId, DateTimeOffset StartsAt, DateTimeOffset? EndsAt,
    string Reason, string? PlannedBy, string? ReportedBy);
internal sealed record PlannedMaintenanceUpdate(
    string MachineId, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    string Reason, string PlannedBy);
internal sealed record BreakdownRestore(DateTimeOffset RestoredAt, string? RepairNote);

internal sealed record LegacyWorkingPlanPreview(
    int SchemaVersion,
    string ImportToken,
    string WorkbookSha256,
    DateTimeOffset ExpiresAt,
    LegacyImportWorkbook Workbook,
    LegacyImportSuggestions Suggestions,
    IReadOnlyList<LegacyImportMachineSection> MachineSections,
    IReadOnlyList<LegacyImportPlanningRow> Rows,
    IReadOnlyList<LegacyImportOpenOrderRow> OpenOrderRows,
    IReadOnlyList<LegacyImportIssue> Issues);

internal sealed record LegacyImportWorkbook(
    string FileName,
    IReadOnlyList<LegacyImportSheet> Sheets);

internal sealed record LegacyImportSheet(
    string Name,
    int RowCount,
    int ColumnCount,
    IReadOnlyList<LegacyImportSourceColumn>? Columns = null)
{
    public string DisplayName => $"{Name} ({RowCount} rows, {ColumnCount} columns)";
}

internal sealed record LegacyImportSourceColumn(string Column, string? Header, string? Sample);

internal sealed record LegacyImportSuggestions(
    string? PlanningSheet,
    string? OpenOrdersSheet,
    IReadOnlyList<LegacyImportColumnSuggestion> PlanningColumns,
    IReadOnlyList<LegacyImportColumnSuggestion> OpenOrderColumns);

internal sealed record LegacyImportColumnSuggestion(
    string Field,
    string? Column,
    string? Header,
    decimal Confidence,
    bool? Required = null);

internal sealed record LegacyImportMachineSection(
    string SectionKey,
    string SheetName,
    int HeaderRow,
    string SourceLabel,
    int FirstDataRow,
    int LastDataRow,
    IReadOnlyList<LegacyImportMachineCandidate> Candidates);

internal sealed record LegacyImportMachineCandidate(
    string MachineId,
    string Number,
    string Name,
    string ProcessType,
    string? AxisType,
    IReadOnlyList<string>? Capabilities,
    IReadOnlyList<string>? MachineTypeCapabilities,
    decimal Score,
    string Reason)
{
    public string DisplayName => string.IsNullOrWhiteSpace(ProcessType)
        ? $"{Number} - {Name}"
        : $"{Number} - {Name} ({ProcessType}{(string.IsNullOrWhiteSpace(AxisType) ? string.Empty : $", {AxisType}")})";
}

internal sealed record LegacyImportPlanningRow(
    string RowKey,
    string SheetName,
    int RowNumber,
    string SectionKey,
    int SourceOrder,
    LegacyImportPlanningValues Values,
    IReadOnlyList<LegacyImportProvenance> Provenance,
    LegacyImportPlanningCandidates Candidates,
    IReadOnlyList<LegacyImportRelatedOrder>? RelatedOrders = null);

internal sealed record LegacyImportRelatedOrder(
    string RowKey,
    string OrderNumber,
    int Quantity,
    string? ExistingOrderId);

internal sealed record LegacyImportOpenOrderRow(
    string RowKey,
    string SheetName,
    int RowNumber,
    int SourceOrder,
    LegacyImportOpenOrderValues Values,
    IReadOnlyList<LegacyImportProvenance> Provenance,
    LegacyImportOpenOrderCandidates Candidates);

internal sealed record LegacyImportPlanningValues(
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

internal sealed record LegacyImportOpenOrderValues(
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
    string? PicturePath,
    string? ProductionInstruction = null,
    string? BatchNumber = null);

internal sealed record LegacyImportProvenance(
    string Field,
    string? Column,
    string? Address,
    string? Kind,
    string? Formula,
    string? Raw);

internal sealed record LegacyImportPlanningCandidates(
    IReadOnlyList<LegacyImportCaseCandidate> Cases,
    IReadOnlyList<LegacyImportOrderCandidate> Orders,
    IReadOnlyList<LegacyImportBatchCandidate> Batches,
    IReadOnlyList<LegacyImportCaseOperationCandidate>? CaseOperations = null,
    IReadOnlyList<LegacyImportBatchOperationCandidate>? BatchOperations = null);

internal sealed record LegacyImportOpenOrderCandidates(
    IReadOnlyList<LegacyImportCaseCandidate> Cases,
    IReadOnlyList<LegacyImportOrderCandidate> Orders);

internal sealed record LegacyImportCaseCandidate(
    string CaseId,
    string PartNumber,
    string Name,
    string? Revision,
    string? Customer,
    string Reason)
{
    public string DisplayName => $"{PartNumber} — {Name}";
}

internal sealed record LegacyImportOrderCandidate(
    string OrderId,
    string OrderNumber,
    long Quantity,
    string? WorkFinishDate,
    string Reason)
{
    public string DisplayName => $"{OrderNumber} ({Quantity})";
}

internal sealed record LegacyImportBatchCandidate(
    string BatchId,
    string BatchNumber,
    long PlannedQuantity,
    string Reason)
{
    public string DisplayName => $"{BatchNumber} ({PlannedQuantity})";
}

internal sealed record LegacyImportCaseOperationCandidate(
    string CaseOperationId,
    string CaseId,
    int OperationNumber,
    string Name,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    int Version)
{
    public string DisplayName => string.IsNullOrWhiteSpace(RequiredMachineType)
        ? $"OP{OperationNumber} - {Name}"
        : $"OP{OperationNumber} - {Name} (requires {RequiredMachineType})";
}

internal sealed record LegacyImportBatchOperationCandidate(
    string BatchOperationId,
    string BatchId,
    string? BatchNumber,
    string? CaseId,
    string? PartNumber,
    string CaseOperationId,
    int OperationNumber,
    string Name,
    string Status,
    string? RequiredMachineType,
    int Version,
    string? AssignmentId,
    string? MachineId,
    int? AssignmentVersion)
{
    public bool IsAlreadyAssigned => !string.IsNullOrWhiteSpace(AssignmentId);
    public string BatchContext => $"Batch {BatchNumber ?? BatchId}";
    public string PartContext => string.IsNullOrWhiteSpace(PartNumber) ? string.Empty : $" / {PartNumber}";
    public string DisplayName => IsAlreadyAssigned
        ? $"{BatchContext}{PartContext} / OP{OperationNumber} - {Name} ({Status}; already assigned{(string.IsNullOrWhiteSpace(RequiredMachineType) ? string.Empty : $"; requires {RequiredMachineType}")})"
        : string.IsNullOrWhiteSpace(RequiredMachineType)
            ? $"{BatchContext}{PartContext} / OP{OperationNumber} - {Name} ({Status})"
            : $"{BatchContext}{PartContext} / OP{OperationNumber} - {Name} ({Status}; requires {RequiredMachineType})";
}

internal sealed record LegacyImportIssue(
    string Severity,
    string Code,
    string Message,
    string? SheetName,
    int? RowNumber,
    string? Field,
    string? SectionKey,
    string? Scope = null);

internal sealed record LegacyWorkingPlanCommit(
    int SchemaVersion,
    string ImportToken,
    string WorkbookSha256,
    string? PlanningSheet,
    string? OpenOrdersSheet,
    IReadOnlyList<LegacyImportColumnMapping> ColumnMappings,
    IReadOnlyList<LegacyImportMachineMapping> MachineMappings,
    IReadOnlyList<LegacyImportOpenOrderSelection> OpenOrderSelections,
    IReadOnlyList<LegacyImportPlanningSelection> PlanningSelections);

internal sealed record LegacyImportColumnMapping(string Scope, string Field, string? Column);
internal sealed record LegacyImportMachineMapping(string SectionKey, string? MachineId);

internal sealed record LegacyImportOpenOrderSelection(
    string RowKey,
    string Action,
    string? ExistingCaseId,
    LegacyImportNewCase? NewCase,
    LegacyImportOrderInput? Order,
    string? CaseSourceRowKey = null);

internal sealed record LegacyImportNewCase(
    string? PartNumber,
    string? Name,
    string? Revision,
    string? Customer,
    string? CustomerReference,
    string? WorkingFolderPath,
    string? Notes);

internal sealed record LegacyImportOrderInput(
    string? OrderNumber,
    int? Quantity,
    string? WorkFinishDate,
    string? Notes);

internal sealed record LegacyImportPlanningSelection(
    string RowKey,
    string Action,
    string? BatchOperationId,
    string? CaseId,
    string? CaseSourceRowKey,
    string? CaseOperationId,
    string? BatchNumber,
    IReadOnlyList<LegacyImportAllocation>? Allocations,
    string? MachineId,
    LegacyImportCompatibilityOverride? CompatibilityOverride = null,
    IReadOnlyList<LegacyImportExpectedCaseRoute>? ExpectedCaseRoute = null);

internal sealed record LegacyImportExpectedCaseRoute(string CaseOperationId, int Version);

internal sealed record LegacyImportAllocation(
    string Type,
    string? OrderId,
    string? OrderSourceRowKey,
    int? Quantity);
internal sealed record LegacyImportCompatibilityOverride(bool Confirmed, string? Reason);

internal sealed record LegacyWorkingPlanCommitReceipt(
    int SchemaVersion,
    string WorkbookSha256,
    string CommitId,
    bool Replayed,
    LegacyImportAffectedIds Created,
    LegacyImportAffectedIds Unchanged,
    IReadOnlyList<LegacyImportMachineBacklog> MachineBacklogs,
    IReadOnlyList<string>? PoolBatchOperationIds = null);

internal sealed record LegacyImportAffectedIds(
    IReadOnlyList<string> CaseIds,
    IReadOnlyList<string> OrderIds,
    IReadOnlyList<string> BatchIds,
    IReadOnlyList<string> AssignmentIds,
    IReadOnlyList<string>? BatchOperationIds = null);

internal sealed record LegacyImportMachineBacklog(
    string MachineId,
    IReadOnlyList<string> AssignmentIdsInImportedSourceOrder);

internal sealed record MachineCreate(
    string Number,
    string Name,
    string ProcessType,
    string? AxisType,
    IReadOnlyList<string> Capabilities,
    string WorkingCalendarId,
    bool IsActive,
    bool DisplayEnabled,
    string? PicturePath,
    string? MachineTypeId = null,
    bool RespectMasterCalendar = true);

internal sealed record WorkingCalendar(
    string WorkingCalendarId,
    string Name,
    string TimeZoneId,
    IReadOnlyList<string> Workdays,
    string? ShiftStartsAtLocal,
    string? ShiftEndsAtLocal,
    string ScheduleKind,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<WorkingCalendarWindow>? Windows = null,
    IReadOnlyList<WorkingCalendarWindow>? BreakWindows = null,
    IReadOnlyList<WorkingCalendarException>? Exceptions = null,
    IReadOnlyList<string>? Usages = null,
    bool UseIsraeliHolidays = false)
{
    public string DisplayName => ScheduleKind == "weekly"
        ? $"{Name} ({TimeZoneId}, {WindowSummary})"
        : $"{Name} ({TimeZoneId}, explicit windows)";

    private string WindowSummary => Windows is { Count: > 0 }
        ? string.Join(", ", Windows.Select(window => $"{window.StartsAtLocal}-{window.EndsAtLocal}"))
        : $"{ShiftStartsAtLocal}-{ShiftEndsAtLocal}";
}

internal sealed record WorkingCalendarResource(
    WorkingCalendar Value,
    string EntityTag);

internal sealed record WorkingCalendarCreate(
    string Name,
    string TimeZoneId,
    IReadOnlyList<string> Workdays,
    string? ShiftStartsAtLocal,
    string? ShiftEndsAtLocal,
    IReadOnlyList<WorkingCalendarWindow>? Windows = null,
    IReadOnlyList<WorkingCalendarWindow>? BreakWindows = null,
    IReadOnlyList<WorkingCalendarException>? Exceptions = null,
    IReadOnlyList<string>? Usages = null,
    bool UseIsraeliHolidays = false);

internal sealed record WorkingCalendarUpdate(
    string Name,
    string TimeZoneId,
    IReadOnlyList<string> Workdays,
    string? ShiftStartsAtLocal,
    string? ShiftEndsAtLocal,
    IReadOnlyList<WorkingCalendarWindow>? Windows = null,
    IReadOnlyList<WorkingCalendarWindow>? BreakWindows = null,
    IReadOnlyList<WorkingCalendarException>? Exceptions = null,
    IReadOnlyList<string>? Usages = null,
    bool UseIsraeliHolidays = false);

internal sealed record WorkingCalendarWindow(string StartsAtLocal, string EndsAtLocal);

internal sealed record WorkingCalendarException(
    string Date,
    IReadOnlyList<WorkingCalendarWindow> Windows,
    IReadOnlyList<WorkingCalendarWindow> BreakWindows,
    string? Name);

internal sealed record SetupCalendarSelection(
    string? WorkingCalendarId,
    WorkingCalendar? Calendar);

internal sealed record SetupCalendarUpdate(string WorkingCalendarId);

internal sealed record PlannerMachineType(
    string MachineTypeId,
    string Name,
    IReadOnlyList<string> Capabilities,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<WorkingCalendarWindow>? Windows = null)
{
    public string DisplayName => Capabilities.Count == 0
        ? Name
        : $"{Name} ({string.Join(", ", Capabilities)})";
}

internal sealed record MachineTypeResource(
    PlannerMachineType Value,
    string EntityTag);

internal sealed record MachineTypeCreate(
    string Name,
    IReadOnlyList<string> Capabilities);

internal sealed record MachineTypeUpdate(
    string Name,
    IReadOnlyList<string> Capabilities);

internal sealed record PlannerResource(
    string ResourceId,
    string EmployeeNumber,
    string Name,
    string FirstName,
    string LastName,
    string Role,
    IReadOnlyList<string> Skills,
    string AssignedCalendarId,
    string? PhotoPath,
    string? Notes,
    string? Email,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool RespectMasterCalendar = true)
{
    public string DisplayName => $"{EmployeeNumber} - {Name}";
}

internal sealed record ResourceResource(PlannerResource Value, string EntityTag);

internal sealed record ResourceCreate(
    string EmployeeNumber,
    string FirstName,
    string LastName,
    string Role,
    IReadOnlyList<string> Skills,
    string AssignedCalendarId,
    string? PhotoPath,
    string? Notes,
    string? Email,
    bool IsActive,
    bool RespectMasterCalendar = true);

internal sealed record ResourceUpdate(
    string EmployeeNumber,
    string FirstName,
    string LastName,
    string Role,
    IReadOnlyList<string> Skills,
    string AssignedCalendarId,
    string? PhotoPath,
    string? Notes,
    string? Email,
    bool IsActive,
    bool RespectMasterCalendar = true);

internal sealed record EmployeeCalendarException(
    string ExceptionId,
    string ResourceId,
    string Date,
    string ExceptionType,
    bool IsFullDay,
    string? StartsAtLocal,
    string? EndsAtLocal,
    string? Note,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string DisplayName => IsFullDay
        ? $"{Date} - {ExceptionType} (full day)"
        : $"{Date} - {ExceptionType} ({StartsAtLocal}-{EndsAtLocal})";
}

internal sealed record EmployeeCalendarExceptionCreate(
    string Date, string ExceptionType, bool IsFullDay,
    string? StartsAtLocal, string? EndsAtLocal, string? Note);

internal sealed record EmployeeCalendarExceptionUpdate(
    string Date, string ExceptionType, bool IsFullDay,
    string? StartsAtLocal, string? EndsAtLocal, string? Note);

internal sealed record EmployeeCalendarExceptionResource(
    EmployeeCalendarException Value, string EntityTag);

internal sealed record EmployeeAvailabilityWindow(DateTimeOffset StartsAt, DateTimeOffset EndsAt);
internal sealed record EmployeeAvailability(
    string ResourceId, bool IsActive, string? AssignedCalendarId, string? TimeZoneId,
    IReadOnlyList<EmployeeAvailabilityWindow> Windows,
    IReadOnlyList<EmployeeCalendarException> Exceptions);

internal sealed record IsraeliHoliday(
    string IsraeliHolidayId,
    string Date,
    string Name,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Status = "non_working",
    string? StartsAtLocal = null,
    string? EndsAtLocal = null,
    string Source = "manual",
    bool IsManualOverride = true)
{
    public string DisplayName => $"{Date} - {Name} ({Status.Replace('_', ' ')})";
}

internal sealed record IsraeliHolidayResource(IsraeliHoliday Value, string EntityTag);

internal sealed record IsraeliHolidayCreate(
    string Date, string Name, string Status = "non_working",
    string? StartsAtLocal = null, string? EndsAtLocal = null);

internal sealed record IsraeliHolidayUpdate(
    string Date, string Name, string Status = "non_working",
    string? StartsAtLocal = null, string? EndsAtLocal = null);

internal sealed record IsraeliHolidaySyncRequest(int FromYear, int ToYear);
internal sealed record IsraeliHolidaySyncResult(
    bool Succeeded, string Provider, int FromYear, int ToYear, int Created, int Updated,
    int PreservedManual, DateTimeOffset LastAttemptAt, DateTimeOffset? LastSuccessAt, string? Error);

internal sealed record ReportEmailSettings(
    string? SenderAddress,
    IReadOnlyList<string> Recipients,
    string? SmtpHost,
    int? SmtpPort,
    bool UseSsl,
    bool DailyReportEnabled,
    string? DailyReportTimeLocal,
    string? TimeZoneId,
    int Version,
    DateTimeOffset UpdatedAt,
    bool WeeklyMaterialReportEnabled = false,
    string WeeklyMaterialReportSendDay = "thursday",
    string WeeklyMaterialReportTimeLocal = "08:00",
    bool WeeklyEmployeeEfficiencyEnabled = false,
    string WeeklyEmployeeEfficiencySendDay = "sunday",
    string WeeklyEmployeeEfficiencyTimeLocal = "08:00");

internal sealed record ReportEmailSettingsResource(ReportEmailSettings Value, string EntityTag);

internal sealed record ReportEmailSettingsUpdate(
    string? SenderAddress,
    IReadOnlyList<string> Recipients,
    string? SmtpHost,
    int? SmtpPort,
    bool UseSsl,
    bool DailyReportEnabled,
    string? DailyReportTimeLocal,
    string? TimeZoneId,
    bool WeeklyMaterialReportEnabled = false,
    string WeeklyMaterialReportSendDay = "thursday",
    string WeeklyMaterialReportTimeLocal = "08:00",
    bool WeeklyEmployeeEfficiencyEnabled = false,
    string WeeklyEmployeeEfficiencySendDay = "sunday",
    string WeeklyEmployeeEfficiencyTimeLocal = "08:00");

internal sealed record WeeklyMaterialReportItem(
    string CasePartNumber,
    long RequiredMaterialPieceQuantity);
internal sealed record WeeklyMaterialReport(IReadOnlyList<WeeklyMaterialReportItem> Items);
internal sealed record WeeklyEmployeeEfficiencyItem(
    string EmployeeResourceId, string EmployeeNumber, string FirstName, string LastName, string Role,
    long PlannedSeconds, long ActualSeconds, long DifferenceSeconds, decimal? PercentageDifference,
    long AvailableCapacitySeconds, decimal? PlannedCapacityPercent, decimal? ActualCapacityPercent);
internal sealed record WeeklyEmployeeEfficiencyReport(
    string WeekStart, string WeekEnd, IReadOnlyList<WeeklyEmployeeEfficiencyItem> Employees);

internal sealed record CaseOperation(
    string CaseOperationId,
    string CaseId,
    int OperationNumber,
    int RoutePosition,
    string Name,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    string DependencyType,
    string? PredecessorCaseOperationId,
    string? SimultaneousGroupKey,
    int Version = 1,
    int QaTimeAfterSetupSeconds = 0,
    int LoadUnloadTimeSeconds = 0,
    bool LoadUnloadRequiresWorker = false,
    bool AutomaticLoading = false,
    int? LoadUnloadEveryNParts = null,
    bool DayShiftOnly = false,
    bool HasExternalDelay = false,
    string? ExternalDelayDescription = null,
    double ExternalDelayDuration = 0,
    string ExternalDelayDurationUnit = "hours",
    string? ExternalDelayCalendarId = null,
    bool RespectMasterCalendar = true)
{
    public string DisplayName => $"OP{OperationNumber} - {Name}";

    public int RouteDisplayPosition => RoutePosition + 1;

    public string SetupTimeDisplay => Formatting.DurationText.FormatOptional(SetupTimeSeconds);

    public string CycleTimePerPartDisplay => Formatting.DurationText.FormatOptional(CycleTimePerPartSeconds);
}

internal sealed record CaseOperationCreate(
    int OperationNumber,
    string Name,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    string DependencyType,
    string? PredecessorCaseOperationId,
    string? SimultaneousGroupKey,
    int QaTimeAfterSetupSeconds = 0,
    int LoadUnloadTimeSeconds = 0,
    bool LoadUnloadRequiresWorker = false,
    bool AutomaticLoading = false,
    int? LoadUnloadEveryNParts = null,
    bool DayShiftOnly = false,
    bool HasExternalDelay = false,
    string? ExternalDelayDescription = null,
    double ExternalDelayDuration = 0,
    string ExternalDelayDurationUnit = "hours",
    string? ExternalDelayCalendarId = null,
    bool RespectMasterCalendar = true);

internal sealed record CaseOperationUpdate(
    int OperationNumber,
    string Name,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    string DependencyType,
    string? PredecessorCaseOperationId,
    string? SimultaneousGroupKey,
    int QaTimeAfterSetupSeconds = 0,
    int LoadUnloadTimeSeconds = 0,
    bool LoadUnloadRequiresWorker = false,
    bool AutomaticLoading = false,
    int? LoadUnloadEveryNParts = null,
    bool DayShiftOnly = false,
    bool HasExternalDelay = false,
    string? ExternalDelayDescription = null,
    double ExternalDelayDuration = 0,
    string ExternalDelayDurationUnit = "hours",
    string? ExternalDelayCalendarId = null,
    bool RespectMasterCalendar = true);

internal sealed record PlannerOrder(
    string OrderId,
    string CaseId,
    string OrderNumber,
    int Quantity,
    string WorkFinishDate,
    string Status,
    string? Notes,
    int Version = 1);

internal sealed record OrderCreate(
    string CaseId,
    string OrderNumber,
    int Quantity,
    string WorkFinishDate,
    string Status,
    string? Notes);

internal sealed record OrderUpdate(
    string OrderNumber,
    int Quantity,
    string WorkFinishDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status,
    string? Notes);

internal sealed record ProductionBatch(
    string BatchId,
    string CaseId,
    string BatchNumber,
    string Status,
    int PlannedQuantity,
    int? RouteRevision,
    int BatchOperationCount,
    int Version = 1,
    IReadOnlyList<BatchAllocation>? Allocations = null)
{
    public string StatusDisplay => Status switch
    {
        "waiting" => "Waiting",
        "in_production" => "In Production",
        "complete" => "Complete",
        _ => Status.Replace('_', ' ')
    };
}

internal sealed record BatchAllocation(
    string AllocationId,
    string AllocationType,
    string? OrderId,
    int Quantity);

internal sealed record ProductionBatchCreate(
    string CaseId,
    string BatchNumber,
    string Status,
    int PlannedQuantity,
    IReadOnlyList<BatchAllocationCreate> Allocations);

internal sealed record ProductionBatchUpdate(
    string BatchNumber,
    int PlannedQuantity,
    IReadOnlyList<BatchAllocationCreate> Allocations);

internal sealed record BatchAllocationCreate(
    string AllocationType,
    string? OrderId,
    int Quantity);

internal sealed record PlanningBoardSnapshot(
    DateTimeOffset ReadAt,
    string ConflictCalculationStatus,
    string ConflictCalculationMessage,
    IReadOnlyList<PlanningConflict> Conflicts,
    IReadOnlyList<PlanningBoardOperation> Pool,
    IReadOnlyList<PlanningBoardMachine> Machines);

internal sealed record PlanningConflict(
    string ConflictId,
    string Code,
    string Severity,
    string Title,
    string Message);

internal sealed record PlanningBoardOperation(
    string BatchOperationId,
    string BatchId,
    string BatchNumber,
    string CaseId,
    string PartNumber,
    int OperationNumber,
    string OperationName,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    string Status,
    string? MachineId,
    int? BacklogPosition,
    int PlannedQuantity = 0,
    IReadOnlyList<string>? OrderReferences = null,
    long? EstimatedTimeSeconds = null,
    int QaTimeAfterSetupSeconds = 0,
    int LoadUnloadTimeSeconds = 0,
    bool LoadUnloadRequiresWorker = false,
    bool AutomaticLoading = false,
    int? LoadUnloadEveryNParts = null,
    bool DayShiftOnly = false,
    string? ActivePauseReason = null,
    string? PausedBy = null,
    DateTimeOffset? PauseStartedAt = null,
    string? CaseName = null,
    string? MachineAssignmentId = null,
    int? AssignmentVersion = null,
    string PlanningMode = "manual",
    string? WorkFinishDate = null,
    DateTimeOffset? LatestStart = null,
    string? LatestStartWarning = null,
    bool IsLatestStartOverdue = false);

internal sealed record PlanningBoardMachine(
    string MachineId,
    string Number,
    string Name,
    string ProcessType,
    string? AxisType,
    IReadOnlyList<string> Capabilities,
    bool IsActive,
    IReadOnlyList<PlanningBoardOperation> Backlog);

internal sealed record BatchOperationExecution(
    string BatchOperationId,
    string MachineId,
    string Status,
    int Version);

internal sealed record OperationPauseRequest(
    string ReasonType,
    string? ProblemDescription = null,
    string? ToolingItemDescription = null,
    string? CustomerContactName = null,
    string? RequestDescription = null,
    string? Comment = null);

internal sealed record MachineAssignmentCompatibilityOverride(
    bool Confirmed,
    string Reason);

internal sealed record MachineAssignment(
    string MachineAssignmentId,
    string BatchOperationId,
    string MachineId,
    int BacklogPosition,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string PlanningMode = "manual");

internal sealed record TimelineSnapshot(
    DateTimeOffset ReadAt,
    DateTimeOffset HorizonStart,
    DateTimeOffset HorizonEnd,
    IReadOnlyList<TimelineBatch> Batches,
    IReadOnlyList<TimelineMachine> Machines,
    IReadOnlyList<TimelineDependency> Dependencies,
    IReadOnlyList<TimelineConflict> Conflicts,
    string? DisplayTimeZoneId = null,
    string? DayStartsAtLocal = null,
    string? DayEndsAtLocal = null);

internal sealed record TimelineBatch(
    string BatchId,
    string BatchNumber,
    string PartNumber,
    DateOnly? WorkFinishDate = null)
{
    public string DisplayName => WorkFinishDate.HasValue
        ? $"{PartNumber} / {BatchNumber} • due {WorkFinishDate:yyyy-MM-dd}"
        : $"{PartNumber} / {BatchNumber}";
}

internal sealed record TimelineMachine(
    string MachineId,
    string Number,
    string Name,
    IReadOnlyList<TimelineInterval> Intervals,
    IReadOnlyList<TimelineNonWorkingWindow>? NonWorkingWindows = null);

internal sealed record TimelineNonWorkingWindow(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Detail = null);

internal sealed record TimelineInterval(
    string Type,
    string MachineId,
    string? OperationId,
    string? BatchId,
    string? BatchNumber,
    string? PartNumber,
    int? OperationNumber,
    string? OperationName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Detail,
    string? TimingKind = null,
    string? OperationStatus = null,
    DateTimeOffset? ForecastStart = null,
    DateTimeOffset? ForecastEnd = null,
    DateTimeOffset? ActualStart = null,
    DateTimeOffset? ActualEnd = null,
    string PlanningMode = "manual",
    DateOnly? WorkFinishDate = null,
    string? MachineAssignmentId = null,
    IReadOnlyList<TimelinePhase>? Phases = null)
{
    /// <summary>
    /// The server calculation is authoritative. These optional fields let newer
    /// servers distinguish a floating forecast from recorded shop-floor time,
    /// while remaining compatible with older Timeline responses.
    /// </summary>
    public bool IsForecast => string.Equals(TimingKind, "forecast", StringComparison.OrdinalIgnoreCase);

    public bool IsActual => string.Equals(TimingKind, "actual", StringComparison.OrdinalIgnoreCase);

    public bool IsHold => string.Equals(TimingKind, "hold", StringComparison.OrdinalIgnoreCase)
        || string.Equals(OperationStatus, "suspended", StringComparison.OrdinalIgnoreCase);

    public bool IsBlocked => !IsHold
        && (string.Equals(TimingKind, "blocked", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(Type, "waiting", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(MachineAssignmentId)));

    public string TimingLabel => IsBlocked
        ? "Blocked — waiting"
        : IsHold
        ? "Hold — paused"
        : IsForecast
            ? "Forecast — not started"
            : IsActual
                ? "Actual"
                : string.IsNullOrWhiteSpace(OperationStatus)
                    ? "Calculated"
                    : OperationStatus.Replace('_', ' ');

    public string PlanningModeLabel => PlanningMode?.Trim().ToLowerInvariant() switch
    {
        null or "" or "manual" => "Manual",
        "backward" => "Backward",
        "forward" => "Forward",
        var unknown => $"Unknown ({unknown})"
    };
}

internal sealed record TimelinePhase(
    string Type,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Detail = null);

internal sealed record TimelineDependency(
    string DependencyId,
    string BatchId,
    string BatchNumber,
    string PartNumber,
    string Type,
    string FromOperationId,
    int FromOperationNumber,
    string FromOperationName,
    string ToOperationId,
    int ToOperationNumber,
    string ToOperationName,
    string? SimultaneousGroupKey)
{
    public string Summary => $"OP{FromOperationNumber} {FromOperationName}  →  OP{ToOperationNumber} {ToOperationName}";

    public string RuleLabel => SimultaneousGroupKey is null
        ? Type
        : $"{Type} • group {SimultaneousGroupKey}";
}

internal sealed record TimelineConflict(
    string ConflictId,
    string Code,
    string Severity,
    string Message,
    IReadOnlyList<string> OperationIds,
    IReadOnlyList<string> MachineIds)
{
    public string SeverityLabel => Severity.ToUpperInvariant();

    public bool IsMissedForecastStart => string.Equals(Code, "missed_forecast_start", StringComparison.OrdinalIgnoreCase);

    public string DisplayMessage => IsMissedForecastStart
        && !Message.StartsWith("Planned start was missed", StringComparison.OrdinalIgnoreCase)
            ? $"Planned start was missed. {Message}"
            : Message;
}

internal sealed class PlannerApiException : Exception
{
    internal PlannerApiException(
        HttpStatusCode statusCode, string code, string message,
        string? requiredMachineType = null, string? selectedMachineType = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        RequiredMachineType = requiredMachineType;
        SelectedMachineType = selectedMachineType;
    }

    internal HttpStatusCode StatusCode { get; }

    internal string Code { get; }
    internal string? RequiredMachineType { get; }
    internal string? SelectedMachineType { get; }
}

internal sealed class PlannerProtocolException : Exception
{
    internal PlannerProtocolException(string message)
        : base(message)
    {
    }
}
