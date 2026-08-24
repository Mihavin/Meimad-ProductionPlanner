namespace Meimad.Planner.Server.Domain.ProductionRuns;

internal static class ProductionRunStatuses
{
    internal const string Draft = "DRAFT";
    internal const string Planned = "PLANNED";
    internal const string InProgress = "IN_PROGRESS";
    internal const string Suspended = "SUSPENDED";
    internal const string Completed = "COMPLETED";
    internal const string Cancelled = "CANCELLED";
    internal const string Aborted = "ABORTED";
}

internal static class ProductionRunProgramStatuses
{
    internal const string Planned = "PLANNED";
    internal const string Active = "ACTIVE";
    internal const string Suspended = "SUSPENDED";
    internal const string Completed = "COMPLETED";
    internal const string Cancelled = "CANCELLED";
    internal const string Aborted = "ABORTED";
}

internal sealed record ProductionRun(
    string ProductionRunId,
    string Status,
    int SharedSetupSeconds,
    string SetupSnapshotJson,
    DateTimeOffset? StructureLockedAt,
    string? LegacyBatchOperationId,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ProductionRunProgram> Programs,
    ProductionRunAssignment? Assignment = null);

internal sealed record ProductionRunAssignment(
    string MachineAssignmentId,
    string MachineId,
    int BacklogPosition,
    string PlanningMode,
    int Version);

internal sealed record ProductionRunProgram(
    string ProductionRunProgramId,
    string ProductionRunId,
    string ManufacturingProgramId,
    string? ProcessRevisionId,
    string? SelectedGCodeReleaseId,
    int SequencePosition,
    int TargetCycleCount,
    int CompletedCycleCount,
    string Status,
    double? CycleSecondsSnapshot,
    ProductionRunProductionPins ProductionPins,
    bool IsLegacyUnmanaged,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ProductionRunOutput> Outputs);

internal sealed record ProductionRunOutput(
    string ProductionRunOutputId,
    string ProductionRunProgramId,
    string BatchOperationId,
    string? RevisionOutputId,
    int QuantityPerCycle,
    int TargetQuantity,
    int ProducedQuantity,
    string Status,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record ProductionRunProductionPins(
    string? ProcessRevisionId,
    string? GCodeReleaseId,
    string? ToolTableReleaseId,
    string? GCodeFileHash,
    string? ToolTableFileHash);
