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

internal sealed record CaseQuery(string? Search, string? Customer, bool? IsActive);

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
    string? MachineTypeId = null)
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
    string? MachineTypeId = null);

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
    DateTimeOffset UpdatedAt)
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
    bool IsActive);

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
    bool IsActive);

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
    bool DayShiftOnly = false)
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
    bool DayShiftOnly = false);

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
    bool DayShiftOnly = false);

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
    int Version = 1)
{
    public string StatusDisplay => Status switch
    {
        "waiting" => "Waiting",
        "in_production" => "In Production",
        "complete" => "Complete",
        _ => Status.Replace('_', ' ')
    };
}

internal sealed record ProductionBatchCreate(
    string CaseId,
    string BatchNumber,
    string Status,
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
    string? CaseName = null);

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

internal sealed record TimelineSnapshot(
    DateTimeOffset ReadAt,
    DateTimeOffset HorizonStart,
    DateTimeOffset HorizonEnd,
    IReadOnlyList<TimelineBatch> Batches,
    IReadOnlyList<TimelineMachine> Machines,
    IReadOnlyList<TimelineDependency> Dependencies,
    IReadOnlyList<TimelineConflict> Conflicts);

internal sealed record TimelineBatch(string BatchId, string BatchNumber, string PartNumber)
{
    public string DisplayName => $"{PartNumber} / {BatchNumber}";
}

internal sealed record TimelineMachine(
    string MachineId,
    string Number,
    string Name,
    IReadOnlyList<TimelineInterval> Intervals);

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
    string? Detail);

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
