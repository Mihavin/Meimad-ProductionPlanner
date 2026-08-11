namespace Meimad.Planner.Server.Application.Timeline;

internal interface ITimelineSourceRepository
{
    Task<TimelineSourceSnapshot> ReadAsync(
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        CancellationToken cancellationToken);
}

internal sealed record TimelineSourceSnapshot(
    DateTimeOffset ReadAt,
    IReadOnlyList<TimelineSourceMachine> Machines,
    IReadOnlyList<TimelineSourceOperation> Operations,
    IReadOnlyList<TimelineSourceDowntime> Downtimes,
    string? SetupCalendarJson);

internal sealed record TimelineSourceMachine(
    string MachineId,
    string Number,
    string Name,
    string TimeZoneId,
    string CalendarJson);

internal sealed record TimelineSourceOperation(
    string OperationId,
    string BatchId,
    string BatchNumber,
    string CaseId,
    string PartNumber,
    int OperationNumber,
    string OperationName,
    string Status,
    int PlannedQuantity,
    int? SetupSeconds,
    int? CycleSeconds,
    string SourceCaseOperationId,
    string DependencyType,
    string? PredecessorSourceCaseOperationId,
    string? SimultaneousGroupKey,
    string? MachineId,
    int? BacklogPosition);

internal sealed record TimelineSourceDowntime(
    string DowntimeId,
    string MachineId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Reason);
