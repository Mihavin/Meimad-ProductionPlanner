using System.Net;

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
    DateTimeOffset UpdatedAt);

internal sealed record MachineResource(PlannerMachine Value, string EntityTag);

internal sealed record MachineCreate(
    string Number,
    string Name,
    string ProcessType,
    string? AxisType,
    IReadOnlyList<string> Capabilities,
    string WorkingCalendarId,
    bool IsActive,
    bool DisplayEnabled,
    string? PicturePath);

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
    DateTimeOffset UpdatedAt)
{
    public string DisplayName => ScheduleKind == "weekly"
        ? $"{Name} ({TimeZoneId}, {ShiftStartsAtLocal}-{ShiftEndsAtLocal})"
        : $"{Name} ({TimeZoneId}, explicit windows)";
}

internal sealed record WorkingCalendarCreate(
    string Name,
    string TimeZoneId,
    IReadOnlyList<string> Workdays,
    string ShiftStartsAtLocal,
    string ShiftEndsAtLocal);

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
    int Version = 1)
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
    string? SimultaneousGroupKey);

internal sealed record CaseOperationUpdate(
    int OperationNumber,
    string Name,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    string DependencyType,
    string? PredecessorCaseOperationId,
    string? SimultaneousGroupKey);

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
    int? BacklogPosition);

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
    internal PlannerApiException(HttpStatusCode statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    internal HttpStatusCode StatusCode { get; }

    internal string Code { get; }
}

internal sealed class PlannerProtocolException : Exception
{
    internal PlannerProtocolException(string message)
        : base(message)
    {
    }
}
