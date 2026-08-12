namespace Meimad.Planner.Server.Application.Timeline;

internal sealed record TimelineProjection(
    DateTimeOffset ReadAt,
    DateTimeOffset HorizonStart,
    DateTimeOffset HorizonEnd,
    IReadOnlyList<TimelineProjectionBatch> Batches,
    IReadOnlyList<TimelineProjectionMachine> Machines,
    IReadOnlyList<TimelineProjectionDependency> Dependencies,
    IReadOnlyList<TimelineProjectionConflict> Conflicts);

internal sealed record TimelineProjectionBatch(
    string BatchId,
    string BatchNumber,
    string PartNumber);

internal sealed record TimelineProjectionMachine(
    string MachineId,
    string Number,
    string Name,
    IReadOnlyList<TimelineProjectionInterval> Intervals);

internal sealed record TimelineProjectionInterval(
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

internal sealed record TimelineProjectionDependency(
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
    string? SimultaneousGroupKey);

internal sealed record TimelineProjectionConflict(
    string ConflictId,
    string Code,
    string Severity,
    string Message,
    IReadOnlyList<string> OperationIds,
    IReadOnlyList<string> MachineIds);
