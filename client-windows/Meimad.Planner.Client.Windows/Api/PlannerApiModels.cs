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
    DateTimeOffset UpdatedAt,
    bool IsParent = false,
    bool IsChild = false);

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
    bool RespectMasterCalendar = true,
    string ExecutionMode = "MANUAL",
    IReadOnlyList<string>? SupportedPostprocessorIds = null,
    int? UsableToolPositions = null,
    double? RapidRateMillimetersPerMinute = null,
    double? ToolChangeTimeSeconds = null,
    double MachineTimeFactor = 1.0)
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
    bool RespectMasterCalendar = true,
    string ExecutionMode = "MANUAL",
    IReadOnlyList<string>? SupportedPostprocessorIds = null,
    int? UsableToolPositions = null,
    double? RapidRateMillimetersPerMinute = null,
    double? ToolChangeTimeSeconds = null,
    double MachineTimeFactor = 1.0);

internal sealed record HaasConnectionSettings(
    string MachineId, string Host, string MacAddress, int MdcPort, int MtConnectPort, int DprntPort,
    bool LocalNetShareEnabled, string? LocalNetSharePath, string? CredentialsReference,
    string PartCounterSource,
    int PollingIntervalMs, int ConnectionTimeoutMs, int StableProgramPolls,
    int HeaderLineLimit, int HeaderByteLimit, IReadOnlyList<string> HeaderPartPatterns,
    bool Enabled, int Version, DateTimeOffset? UpdatedAt, string TelemetryProvider = "MDC");

internal sealed record HaasConnectionUpdate(
    string Host, string MacAddress, int MdcPort, int MtConnectPort, int DprntPort,
    bool LocalNetShareEnabled, string? LocalNetSharePath, string? CredentialsReference,
    string PartCounterSource,
    int PollingIntervalMs, int ConnectionTimeoutMs, int StableProgramPolls,
    int HeaderLineLimit, int HeaderByteLimit, IReadOnlyList<string> HeaderPartPatterns,
    bool Enabled, int Version, string TelemetryProvider);

internal sealed record HaasConnectionTest(
    bool Succeeded, string Message, string? ProgramNumber, string? MachineStatus,
    int? Parts, PlannerNcHeaderMetadata? Header);

internal sealed record CncVerificationSettings(
    string MachineId, string DprintTransport, int DprintPort,
    int ChallengeProgramNumber, int VerifyProgramNumber, int? CustomGcodeAlias,
    int NonceVariable, int ResponseVariable, int VerificationStateVariable,
    int ReleaseTokenVariable, int? FinalizeProgramNumber, int? EventSequenceVariable,
    int ExpectedMacroVersion,
    int ResponseCodeDigits, int VerificationTimeoutSeconds, bool Enabled,
    int Version, DateTimeOffset UpdatedAt);

internal sealed record CncVerificationSettingsUpdate(
    string DprintTransport, int DprintPort, int ChallengeProgramNumber,
    int VerifyProgramNumber, int? CustomGcodeAlias, int NonceVariable,
    int ResponseVariable, int VerificationStateVariable, int ReleaseTokenVariable,
    int FinalizeProgramNumber, int EventSequenceVariable,
    int ExpectedMacroVersion, int ResponseCodeDigits,
    int VerificationTimeoutSeconds, bool Enabled, int Version);

internal sealed record OffsetLoaderRelease(
    string OffsetLoaderReleaseId, string ProductionRunId, string MachineId,
    string NcReleaseId, string ToolTableReleaseId, int VerificationReleaseToken,
    string? ArtifactHash, DateTimeOffset CreatedAt, string CreatedBy,
    string MetadataJson, bool IsCurrent);

internal sealed record CreateOffsetLoaderReleaseRequest(
    string MachineId, string NcReleaseId, string ToolTableReleaseId,
    string? ArtifactHash = null, string MetadataJson = "{}");

internal sealed record CncRecoveryRequest(string MachineId, string Reason);

internal sealed record CncRecoveryResult(
    string Action, string ProductionRunId, string MachineId,
    string? VerificationSessionId, string? OffsetLoaderReleaseId,
    string Reason, string PerformedBy, DateTimeOffset PerformedAt);

internal sealed record HaasMachineSnapshot(
    string MachineId, DateTimeOffset Timestamp, string ConnectivityState,
    string? MachineStatus, string? ProgramNumber, string? MachineHeaderPartName,
    string? MachineHeaderSourcePath, DateTimeOffset? HeaderReadAt,
    int? PartCounter,
    string? RawMdcStatus, string? LastError, DateTimeOffset? LastSeenAt, int Version);

internal sealed record HaasBenchSession(
    string BenchId, string BatchOperationId, string MachineId, string State,
    string MachineProgramNumber, string MachinePartName, DateTimeOffset SetupStartedAt,
    DateTimeOffset? SetupEndedAt, DateTimeOffset? ProductionStartedAt,
    bool PartCountingEnabled, int? PartCounterBaseline, int? PreviousPartCounter,
    int ProducedQuantity, DateTimeOffset? CompletedAt, int Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

internal sealed record HaasMachineMonitor(
    HaasConnectionSettings Settings, HaasMachineSnapshot? Snapshot,
    HaasBenchSession? ActiveBench, IReadOnlyList<HaasBenchStateInterval> Intervals,
    IReadOnlyList<HaasEvent> RecentEvents, double ActualSetupSeconds,
    double ActualProductionSeconds);

internal sealed record HaasBenchStateInterval(
    string IntervalId, string BenchId, string State, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, string Source);

internal sealed record HaasEvent(
    string EventId, string EventType, string MachineId, string? BenchId,
    DateTimeOffset Timestamp, string PayloadJson, string DedupeKey);

internal sealed record CncAdapterDefinition(
    string Id,
    string DisplayName,
    bool Implemented,
    CncAdapterCapabilities Capabilities)
{
    public string ChoiceLabel => DisplayName;
}

internal sealed record CncAdapterCapabilities(
    bool CanReadMachineState,
    bool CanReadActiveProgram,
    bool CanReadProgramHeader,
    bool CanReadVariables,
    bool CanWriteVariables,
    bool CanReadPartCounter,
    bool CanReadToolData,
    bool CanWriteToolData,
    bool CanReadAlarms,
    bool CanReadFeed,
    bool CanReadSpindle,
    bool CanUploadNcProgram,
    bool CanDownloadNcProgram);

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

internal sealed record PlannerPostprocessor(
    string PostprocessorId,
    string Name,
    string? Description,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string DisplayName => IsActive ? Name : $"{Name} (inactive)";
}

internal sealed record PostprocessorResource(
    PlannerPostprocessor Value,
    string EntityTag);

internal sealed record PostprocessorCreate(
    string Name,
    string? Description,
    bool IsActive);

internal sealed record PostprocessorUpdate(
    string Name,
    string? Description,
    bool IsActive);

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
    bool RespectMasterCalendar = true,
    double ToolLoadSecondsPerTool = 60,
    double? FixtureAssemblySeconds = null,
    double FirstPartRunningSpeedPercent = 66.6666666667)
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
    bool RespectMasterCalendar = true,
    double ToolLoadSecondsPerTool = 60, double? FixtureAssemblySeconds = null,
    double FirstPartRunningSpeedPercent = 66.6666666667);

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
    bool RespectMasterCalendar = true,
    double ToolLoadSecondsPerTool = 60, double? FixtureAssemblySeconds = null,
    double FirstPartRunningSpeedPercent = 66.6666666667);

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

internal sealed record PlannerToolTableRelease(
    string ToolTableReleaseId,
    int RevisionNumber,
    string OriginalFileName,
    long FileSize,
    string FileHash,
    DateTimeOffset ReleasedAt,
    string ReleasedBy,
    string ReleaseComment,
    int? RequiredToolCount = null,
    IReadOnlyList<PlannerReleasedTool>? Tools = null)
{
    public string RequiredToolCountText => RequiredToolCount.HasValue
        ? $"{RequiredToolCount.Value} required magazine tools"
        : "Required tool count unavailable — release a structured tool table";
}

internal sealed record PlannerReleasedTool(
    string ReleasedToolId,
    int RowNumber,
    string ToolIdentifier,
    string Description,
    bool IsRequired,
    bool RequiresMagazinePosition,
    bool IsActive,
    string? MagazinePosition)
{
    public string RequirementText => !IsActive
        ? "Inactive"
        : !IsRequired
            ? "Optional"
            : RequiresMagazinePosition ? "Required magazine tool" : "Required external tool";
}

internal sealed record PlannerProcessRevision(
    string ProcessRevisionId,
    int ProcessRevisionNumber,
    bool IsActive,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string ChangeDescription,
    int Version,
    PlannerToolTableRelease ToolTable)
{
    public string DisplayName => $"Process r{ProcessRevisionNumber} — {ChangeDescription}";
}

internal sealed record PlannerGCodeRelease(
    string GCodeReleaseId,
    string ProcessRevisionId,
    int ProcessRevisionNumber,
    string PostprocessorId,
    string PostprocessorName,
    int PostSpecificRevision,
    string OriginalFileName,
    long FileSize,
    string FileHash,
    DateTimeOffset ReleasedAt,
    string ReleasedBy,
    string ChangeScope,
    string ReleaseComment,
    string ToolTableReleaseId,
    bool IsCurrentForProcessAndPost,
    bool IsForActiveProcess,
    PlannerNcProgramAnalysis? NcAnalysis = null,
    IReadOnlyList<PlannerNcMachineCycleEstimate>? MachineCycleEstimates = null,
    PlannerNcHeaderMetadata? HeaderMetadata = null,
    PlannerNcVerificationHook? VerificationHook = null)
{
    public string DisplayName => $"Process r{ProcessRevisionNumber} / {PostprocessorName} r{PostSpecificRevision} — {OriginalFileName}";
    public string ShortHash => FileHash.Length > 12 ? FileHash[..12] : FileHash;
    public string NcEstimateSummary => NcAnalysis is null
        ? "Estimate unavailable"
        : $"{NcAnalysis.Confidence} · {MachineCycleEstimates?.Count(value => value.EstimatedCycleSeconds.HasValue) ?? 0} Machine estimate(s)";
    public string NcWarningSummary => NcAnalysis?.Warnings.Count > 0
        ? string.Join(Environment.NewLine, NcAnalysis.Warnings)
        : "No parser warnings.";
    public string HeaderPartName => HeaderMetadata?.PartName ?? "Header invalid";
    public string HeaderStatusMessage => HeaderMetadata?.Status == "VALID"
        ? $"Part identity from NC header: {HeaderMetadata.PartName}"
        : "Part name could not be extracted from NC header.";
    public string VerificationHookSummary => VerificationHook is null
        ? "Historical release — verification hook unavailable"
        : $"Verification hook v{VerificationHook.HookVersion} · {VerificationHook.InvocationKind} {VerificationHook.InvocationNumber} · NC ID {VerificationHook.NcIdentityToken}";
    public string NcCalculatedTimeSummary
    {
        get
        {
            var calculated = (MachineCycleEstimates ?? [])
                .Where(value => value.EstimatedCycleSeconds.HasValue)
                .OrderBy(value => value.MachineId, StringComparer.Ordinal)
                .ToArray();
            return calculated.Length == 0
                ? "Calculated time unavailable"
                : string.Join(
                    Environment.NewLine,
                    calculated.Select(value =>
                        $"{value.MachineId}: {CycleDuration(value.EstimatedCycleSeconds)} / part"));
        }
    }
    public string NcCalculatedTimeDetail
    {
        get
        {
            var lines = new List<string>();
            if (NcAnalysis is not null)
            {
                lines.Add($"Parser confidence: {NcAnalysis.Confidence}");
                lines.AddRange(NcAnalysis.Warnings);
            }
            foreach (var estimate in MachineCycleEstimates ?? [])
            {
                lines.AddRange(estimate.Warnings.Select(value => $"{estimate.MachineId}: {value}"));
            }

            return lines.Count == 0
                ? "No parser or Machine-estimate warnings."
                : string.Join(Environment.NewLine, lines.Distinct(StringComparer.Ordinal));
        }
    }

    private static string CycleDuration(double? seconds) => seconds.HasValue
        && double.IsFinite(seconds.Value)
        && seconds.Value >= 0
        && seconds.Value <= long.MaxValue
            ? Formatting.DurationText.Format((long)Math.Ceiling(seconds.Value))
            : "unavailable";
}

internal sealed record PlannerNcVerificationHook(
    int HookVersion, string InvocationKind, int InvocationNumber,
    int NcIdentityToken, int LineNumber);

internal sealed record PlannerNcHeaderMetadata(
    string Status, string? PartName, string? CaseNumber, string? Operation,
    string? Revision, string? ProgramNumber, string RawHeader, string ParserVersion);

internal sealed record PlannerNcProgramAnalysis(
    string ParserVersion,
    string Status,
    double FeedMotionSeconds,
    double RapidDistanceMillimeters,
    int ToolChangeCount,
    double DwellSeconds,
    string? DetectedUnits,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> UnsupportedConstructs,
    string Confidence,
    DateTimeOffset AnalyzedAt);

internal sealed record PlannerNcMachineCycleEstimate(
    string MachineId,
    string ParserVersion,
    double RawFeedSeconds,
    double RapidDistanceMillimeters,
    double? RapidSeconds,
    int ToolChangeCount,
    double? ToolChangeSeconds,
    double DwellSeconds,
    double? MachineRapidRateMillimetersPerMinute,
    double? MachineToolChangeTimeSeconds,
    double MachineTimeFactor,
    double? RawCycleSeconds,
    double? EstimatedCycleSeconds,
    IReadOnlyList<string> Warnings,
    string Confidence,
    DateTimeOffset CalculatedAt);

internal sealed record PlannerPostprocessorReleaseStatus(
    string PostprocessorId,
    string PostprocessorName,
    bool IsActive,
    string Status,
    PlannerGCodeRelease? CurrentRelease,
    PlannerGCodeRelease? LatestHistoricalRelease)
{
    public string StatusText => Status switch
    {
        "current" => "Current",
        "stale" => "Stale — regenerate for active process",
        _ => "Missing — release required"
    };
}

internal sealed record PlannerGCodeCatalog(
    string CaseOperationId,
    PlannerProcessRevision? ActiveProcessRevision,
    IReadOnlyList<PlannerProcessRevision> ProcessRevisions,
    IReadOnlyList<PlannerPostprocessorReleaseStatus> Postprocessors,
    IReadOnlyList<PlannerGCodeRelease> Releases);

internal sealed record GCodeReleaseCreate(
    string PostprocessorId,
    string ChangeScope,
    string ReleaseComment,
    string? ProcessChangeDescription,
    bool ConfirmNewProcessRevision,
    bool ReuseActiveToolTable,
    bool ConfirmToolTable,
    string GCodeFilePath,
    string? ToolTableFilePath);

internal sealed record CaseComponent(
    string CaseComponentId,
    string ParentCaseId,
    string ParentPartNumber,
    string ParentCaseName,
    string ChildCaseId,
    string ChildPartNumber,
    string ChildCaseName,
    double QuantityPerParent,
    int SortOrder,
    string? Notes,
    bool IsActive,
    int Version = 1)
{
    public string EntityTag => $"\"case-component:{CaseComponentId}:v{Version}\"";
}

internal sealed record CaseComponentCreate(
    string ChildCaseId,
    double QuantityPerParent,
    int SortOrder,
    string? Notes);

internal sealed record CaseComponentUpdate(
    double QuantityPerParent,
    int SortOrder,
    string? Notes,
    bool IsActive);

internal sealed record ComponentDemandPreview(
    string CaseId,
    string PartNumber,
    double OrderQuantity,
    IReadOnlyList<ComponentDemandRow> Items);

internal sealed record ComponentDemandRow(
    string ParentCaseId,
    string ChildCaseId,
    string ChildPartNumber,
    double QuantityPerParent,
    IReadOnlyList<double> MultiplierPath,
    double TotalRequiredQuantity,
    int Level,
    IReadOnlyList<string> Path)
{
    public string PathDisplay => string.Join(" → ", Path);
}

internal sealed record DerivedCaseOrder(
    string DerivedOrderKey,
    string ChildCaseId,
    string SourceOrderId,
    string SourceOrderNumber,
    string SourceParentCaseId,
    string SourceParentPartNumber,
    double QuantityPerParent,
    double DerivedQuantity,
    double AllocatedQuantity,
    double RemainingQuantity,
    string WorkFinishDate,
    string Status,
    int Level,
    IReadOnlyList<string> Path)
{
    public string PathDisplay => string.Join(" -> ", Path);
}

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
    int Quantity,
    string? DerivedOrderKey = null);

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
    int Quantity,
    string? DerivedOrderKey = null);

internal sealed record VerifiedMaterialReceipt(
    string ReceiptId,
    string CaseId,
    int Quantity,
    string Unit,
    DateTimeOffset ReceivedAt,
    DateTimeOffset VerifiedAt,
    string VerifiedBy,
    string? ExternalReference,
    string? Comment,
    int ReservedQuantity,
    int AvailableQuantity);

internal sealed record BatchMaterialReservation(
    string ReservationId,
    string ReceiptId,
    string ProductionBatchId,
    int Quantity,
    DateTimeOffset ReservedAt,
    string ReservedBy,
    string? Comment);

internal sealed record BatchMaterialReconciliation(
    string ProductionBatchId,
    string CaseId,
    string BatchNumber,
    int PlannedQuantity,
    int RequiredMaterialPieces,
    int ReservedQuantity,
    int VerifiedAvailableToBatch,
    int ShortageQuantity,
    string State,
    string Message,
    IReadOnlyList<VerifiedMaterialReceipt> Receipts,
    IReadOnlyList<BatchMaterialReservation> Reservations);

internal sealed record VerifiedMaterialReceiptCreate(
    string CaseId,
    int Quantity,
    DateTimeOffset ReceivedAt,
    string? ExternalReference,
    string? Comment);

internal sealed record MaterialReservationCreate(
    string ReceiptId,
    int Quantity,
    string? Comment = null);

internal sealed record MaterialReservationsReplace(
    IReadOnlyList<MaterialReservationCreate> Reservations);

internal sealed record PlanningBoardSnapshot(
    DateTimeOffset ReadAt,
    string ConflictCalculationStatus,
    string ConflictCalculationMessage,
    IReadOnlyList<PlanningConflict> Conflicts,
    IReadOnlyList<PlanningBoardOperation> Pool,
    IReadOnlyList<PlanningBoardMachine> Machines,
    IReadOnlyList<ProductionRunPlanningCard>? ProductionRuns = null);

internal sealed record ProductionRunPlanningCard(
    string ProductionRunId,string Status,string? MachineId,int? BacklogPosition,
    int SharedSetupSeconds,int ProgramCount,long RemainingDurationSeconds,
    string ReadinessState,bool IsReady,IReadOnlyList<ProductionRunPlanningProgram> Programs);
internal sealed record ProductionRunPlanningProgram(
    string ProductionRunProgramId,string ManufacturingProgramId,string? GCodeReleaseId,
    int SequencePosition,int TargetCycles,int CompletedCycles,long ForecastCompletionOffsetSeconds,
    IReadOnlyList<ProductionRunPlanningOutput> Outputs);
internal sealed record ProductionRunPlanningOutput(
    string ProductionRunOutputId,string BatchOperationId,string BatchNumber,string CaseId,
    string PartNumber,int OperationNumber,int QuantityPerCycle,int TargetQuantity,
    int ProducedQuantity,int RemainingQuantity);

internal sealed record ProductionRunCreate(
    int SharedSetupSeconds,string SetupSnapshotJson,IReadOnlyList<ProductionRunProgramCreate> Programs,
    ProductionRunAssignmentCreate? Assignment = null);
internal sealed record ProductionRunProgramCreate(
    string ManufacturingProgramId,string ProcessRevisionId,string? GCodeReleaseId,
    int SequencePosition,decimal CycleSeconds,IReadOnlyList<ProductionRunOutputCreate> Outputs);
internal sealed record ProductionRunOutputCreate(string RevisionOutputId,string BatchOperationId,long TargetQuantity);
internal sealed record ProductionRunAssignmentCreate(string MachineId,int BacklogPosition,string PlanningMode,bool ConfirmCompatibilityOverride=false,string? OverrideReason=null);
internal sealed record ProductionRunResource(string ProductionRunId,string Status,int SharedSetupSeconds,int Version,
    IReadOnlyList<ProductionRunProgramResource> Programs,ProductionRunAssignmentResource? Assignment);
internal sealed record ProductionRunProgramResource(string ProductionRunProgramId,string ManufacturingProgramId,string? ProcessRevisionId,
    string? SelectedGCodeReleaseId,int SequencePosition,long TargetCycleCount,long CompletedCycleCount,string Status,
    IReadOnlyList<ProductionRunOutputResource> Outputs);
internal sealed record ProductionRunOutputResource(string ProductionRunOutputId,string BatchOperationId,string? RevisionOutputId,
    long QuantityPerCycle,long TargetQuantity,long ProducedQuantity,long RemainingQuantity,string Status,int Version);
internal sealed record ProductionRunAssignmentResource(string MachineAssignmentId,string MachineId,int BacklogPosition,string PlanningMode,int Version);

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
    bool IsLatestStartOverdue = false,
    string ToolCapacityStatus = "not_managed",
    string ToolCapacityMessage = "Tool capacity is not managed for this Operation.",
    int? RequiredToolCount = null,
    int? AvailableToolPositions = null,
    bool IsToolCapacitySatisfied = true,
    string OverallReadinessState = "NOT_MANAGED",
    bool IsReadyForProduction = true,
    string ReadinessSummary = "Readiness is not managed for this legacy Operation.",
    IReadOnlyList<PlannerReadinessComponent>? ReadinessComponents = null,
    string? EffectiveGCodeReleaseId = null,
    bool RequiresExplicitGCodeSelection = false,
    IReadOnlyList<PlannerReadinessRelease>? CompatibleGCodeReleases = null,
    double? NcEstimatedCycleTimePerPartSeconds = null,
    double? PlanningCycleTimePerPartSeconds = null,
    string PlanningCycleTimeSource = "manual",
    string? NcEstimateConfidence = null,
    IReadOnlyList<string>? NcEstimateWarnings = null,
    string? NcEstimateGCodeReleaseId = null,
    double ToolLoadingTimeSeconds = 0,
    double? FixtureSetupTimeSeconds = null,
    double? FirstPieceProveOutTimeSeconds = null,
    double? TotalSetupTimeSeconds = null,
    int? RemainingProductionQuantity = null,
    double? RemainingProductionRuntimeSeconds = null,
    double? TotalPlannedMachineTimeSeconds = null,
    IReadOnlyList<string>? SetupEstimateWarnings = null,
    bool UsesSetupOccupancyEstimate = false);

internal sealed record PlannerReadinessComponent(
    string Key,
    string Label,
    string State,
    string Message,
    bool IsBlocking);

internal sealed record PlannerReadinessRelease(
    string GCodeReleaseId,
    string ProcessRevisionId,
    string PostprocessorId,
    string PostprocessorName,
    string OriginalFileName,
    int PostSpecificRevision)
{
    public string DisplayName =>
        $"{PostprocessorName} r{PostSpecificRevision} — {OriginalFileName}";
}

internal sealed record PlannerProductionReadiness(
    string OverallState,
    bool IsReadyForProduction,
    bool IsManaged,
    string Summary,
    IReadOnlyList<PlannerReadinessComponent> Components,
    string? EffectiveGCodeReleaseId,
    bool RequiresExplicitGCodeSelection,
    IReadOnlyList<PlannerReadinessRelease> CompatibleGCodeReleases);

internal sealed record ProductionReadinessInputUpdate(
    string? SelectedGCodeReleaseId,
    string MaterialStatus,
    string? MaterialComment,
    string ToolOffsetStatus,
    string? ToolOffsetComment);

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

internal sealed record ManualOperationReport(
    string BatchOperationId, string MachineId, string ReportType,
    DateTimeOffset RecordedAt, int? PartTimeSeconds);

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
    IReadOnlyList<TimelinePhase>? Phases = null,
    string OverallReadinessState = "NOT_MANAGED",
    bool IsReadyForProduction = true,
    string? ReadinessSummary = null,
    int CompletedQuantity = 0,
    int? TargetQuantity = null,
    double? MeasuredAverageCycleSeconds = null,
    int MeasuredCycleSampleCount = 0,
    string PlanningCycleTimeSource = "manual")
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

    public bool IsPlannedNotReady => IsForecast
        && OverallReadinessState == "NOT_READY"
        && !IsReadyForProduction;

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

internal sealed record UserTerminal(
    string DeviceId,
    string TabletId,
    string? HardwareId,
    string DeviceName,
    string? MachineId,
    bool IsEnabled,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastSeenAt = null,
    DateTimeOffset? LastServerContactAt = null,
    string? FirmwareVersion = null,
    decimal? BatteryVoltage = null,
    int? BatteryPercent = null,
    string? WifiIpAddress = null,
    int? WifiRssi = null,
    string? MachineNumber = null,
    string? MachineName = null,
    string? CurrentProductionRunId = null,
    string? CurrentWorkflowStatus = null,
    string? CurrentPackageRevision = null)
{
    public string BindingText => string.IsNullOrWhiteSpace(MachineId)
        ? "Spare / unassigned"
        : string.IsNullOrWhiteSpace(MachineName)
            ? MachineId
            : $"{MachineNumber} — {MachineName}";
    public string StateText => IsEnabled ? "Enabled" : "Disabled";
    public string LastSeenText => LastSeenAt?.ToLocalTime().ToString("g") ?? "Never";
    public string FirmwareText => FirmwareVersion ?? "Not reported";
    public string BatteryText => BatteryVoltage is null && BatteryPercent is null
        ? "Not reported"
        : $"{(BatteryVoltage is null ? string.Empty : $"{BatteryVoltage:0.00} V")}{(BatteryVoltage is not null && BatteryPercent is not null ? " / " : string.Empty)}{(BatteryPercent is null ? string.Empty : $"{BatteryPercent}%")}";
    public string WifiText => WifiIpAddress is null && WifiRssi is null
        ? "Not reported"
        : $"{WifiIpAddress ?? "IP unavailable"}{(WifiRssi is null ? string.Empty : $" / {WifiRssi} dBm")}";
    public string HealthText => !IsEnabled
        ? "Disabled"
        : LastServerContactAt is null
            ? "No server contact"
            : $"Server contact {LastServerContactAt.Value.ToLocalTime():g}";
    public string CurrentRunText => CurrentProductionRunId ?? "No current run";
    public string WorkflowText => CurrentWorkflowStatus ?? "Unavailable";
    public string PackageRevisionText => CurrentPackageRevision ?? "No current package";
}

internal sealed record CreateUserTerminalRequest(string DeviceName, string? MachineId, string? HardwareId);

internal sealed record UpdateUserTerminalRequest(string? MachineId, bool IsEnabled);

internal sealed record QcQueueItem(
    string ProductionRunId,
    string MachineId,
    string MachineNumber,
    string MachineName,
    string Part,
    string Operation,
    DateTimeOffset ReceivedAt,
    string? SetupistId,
    string? SetupistName)
{
    public string MachineText => $"{MachineNumber} — {MachineName}";
    public string ReceivedText => ReceivedAt.ToLocalTime().ToString("g");
    public string SetupistText => !string.IsNullOrWhiteSpace(SetupistName)
        ? SetupistName
        : SetupistId ?? "Not recorded";
}

internal sealed record QcDecisionRequest(string Decision, string? Reason);

internal sealed record QcDecisionResult(
    string EventId,
    string ProductionRunId,
    string Decision,
    string ResultingStatus,
    string UserId,
    string? Reason,
    DateTimeOffset Timestamp,
    DateTimeOffset? ProductionApprovedAt);

internal sealed record PreparationReadinessFact(
    string Key,
    string Label,
    string State,
    string Message,
    bool IsSatisfied)
{
    public string DisplayText => $"{Label}: {State.Replace('_', ' ')} — {Message}";
}

internal sealed record PreparationQueueItem(
    string Stage,
    string BatchOperationId,
    string? ProductionRunId,
    string MachineAssignmentId,
    string MachineId,
    string MachineNumber,
    string MachineName,
    string PartNumber,
    string PartName,
    string BatchNumber,
    int OperationNumber,
    string OperationName,
    string? ProcessRevisionId,
    string? GCodeReleaseId,
    string? ToolTableReleaseId,
    string WorkflowStatus,
    IReadOnlyList<PreparationReadinessFact> ReadinessFacts,
    string? CaseId = null,
    string? CaseOperationId = null)
{
    public string MachineText => $"{MachineNumber} — {MachineName}";
    public string PartText => $"{PartNumber} — {PartName}";
    public string OperationText => $"OP{OperationNumber:00} {OperationName}";
    public string ProductionRunText => ProductionRunId ?? "Not created";
    public string GCodeReleaseText => GCodeReleaseId ?? "Missing / not selected";
    public string ToolTableReleaseText => ToolTableReleaseId ?? "Missing";
    public string WorkflowText => WorkflowStatus.Replace('_', ' ');
}

internal sealed record ProductionPackageArtifactInfo(
    string ArtifactId,
    string ArtifactType,
    string LogicalPath,
    long FileSize,
    string Sha256,
    string? SourceReleaseId);

internal sealed record ProductionPackageInfo(
    string ProductionPackageId,
    string BatchOperationId,
    string? ProductionRunId,
    string MachineAssignmentId,
    string MachineId,
    string? GCodeReleaseId,
    string ToolTableReleaseId,
    string? OffsetLoaderReleaseId,
    string ExecutionMode,
    bool VerificationEnabled,
    int? VerificationConfigurationVersion,
    int? VerificationMacroVersion,
    string ManifestSha256,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string? SupersedesProductionPackageId,
    bool FileExportAvailable,
    bool DirectTransferConfigured,
    bool DirectTransferOnline,
    IReadOnlyList<ProductionPackageArtifactInfo> Artifacts);

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
