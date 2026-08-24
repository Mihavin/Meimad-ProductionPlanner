using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Domain.ProductionRuns;

namespace Meimad.Planner.Server.Api.ProductionRuns;

internal sealed record CreateProductionRunRequest(
    int SharedSetupSeconds,
    string? SetupSnapshotJson,
    IReadOnlyList<CreateProductionRunProgramRequest>? Programs,
    AssignProductionRunRequest? Assignment)
{
    internal CreateProductionRunCommand ToCommand() => new(
        SharedSetupSeconds,
        SetupSnapshotJson ?? "{}",
        Programs?.Select(value => value.ToCommand()).ToArray() ?? [],
        Assignment?.ToCommand());
}

internal sealed record CreateProductionRunProgramRequest(
    string? ManufacturingProgramId,
    string? ProcessRevisionId,
    string? GCodeReleaseId,
    int SequencePosition,
    decimal CycleSeconds,
    IReadOnlyList<CreateProductionRunOutputRequest>? Outputs)
{
    internal CreateProductionRunProgramCommand ToCommand() => new(
        ManufacturingProgramId ?? string.Empty,
        ProcessRevisionId ?? string.Empty,
        GCodeReleaseId,
        SequencePosition,
        CycleSeconds,
        Outputs?.Select(value => value.ToCommand()).ToArray() ?? []);
}

internal sealed record CreateProductionRunOutputRequest(
    string? RevisionOutputId,
    string? BatchOperationId,
    long TargetQuantity)
{
    internal CreateProductionRunOutputCommand ToCommand() => new(
        RevisionOutputId ?? string.Empty,
        BatchOperationId ?? string.Empty,
        TargetQuantity);
}

internal sealed record AssignProductionRunRequest(
    string? MachineId,
    int BacklogPosition,
    string? PlanningMode,
    bool ConfirmCompatibilityOverride,
    string? OverrideReason)
{
    internal AssignProductionRunCommand ToCommand() => new(
        MachineId ?? string.Empty,
        BacklogPosition,
        PlanningMode ?? string.Empty,
        ConfirmCompatibilityOverride,
        OverrideReason);
}

internal sealed record CancelProductionRunRequest(string? Reason);
internal sealed record ProductionRunReasonRequest(string? Reason);
internal sealed record RecordProductionRunCycleRequest(string? Source, string? SourceEventId, DateTimeOffset? ObservedAt);

internal sealed record ProductionRunListResponse(IReadOnlyList<ProductionRunResponse> Items);

internal sealed record ProductionRunResponse(
    string ProductionRunId,
    string Status,
    int SharedSetupSeconds,
    string SetupSnapshotJson,
    DateTimeOffset? StructureLockedAt,
    string? LegacyBatchOperationId,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ProductionRunProgramResponse> Programs,
    ProductionRunAssignmentResponse? Assignment)
{
    internal static ProductionRunResponse FromDomain(ProductionRun value) => new(
        value.ProductionRunId, value.Status, value.SharedSetupSeconds,
        value.SetupSnapshotJson, value.StructureLockedAt, value.LegacyBatchOperationId,
        value.Version, value.CreatedAt, value.UpdatedAt,
        value.Programs.Select(ProductionRunProgramResponse.FromDomain).ToArray(),
        value.Assignment is null ? null : ProductionRunAssignmentResponse.FromDomain(value.Assignment));
}

internal sealed record ProductionRunProgramResponse(
    string ProductionRunProgramId,
    string ManufacturingProgramId,
    string? ProcessRevisionId,
    string? SelectedGCodeReleaseId,
    int SequencePosition,
    long TargetCycleCount,
    long CompletedCycleCount,
    string Status,
    double? CycleSeconds,
    int Version,
    IReadOnlyList<ProductionRunOutputResponse> Outputs)
{
    internal static ProductionRunProgramResponse FromDomain(ProductionRunProgram value) => new(
        value.ProductionRunProgramId, value.ManufacturingProgramId, value.ProcessRevisionId,
        value.SelectedGCodeReleaseId, value.SequencePosition, value.TargetCycleCount,
        value.CompletedCycleCount, value.Status, value.CycleSecondsSnapshot, value.Version,
        value.Outputs.Select(ProductionRunOutputResponse.FromDomain).ToArray());
}

internal sealed record ProductionRunOutputResponse(
    string ProductionRunOutputId,
    string BatchOperationId,
    string? RevisionOutputId,
    long QuantityPerCycle,
    long TargetQuantity,
    long ProducedQuantity,
    long RemainingQuantity,
    string Status,
    int Version)
{
    internal static ProductionRunOutputResponse FromDomain(ProductionRunOutput value) => new(
        value.ProductionRunOutputId, value.BatchOperationId, value.RevisionOutputId,
        value.QuantityPerCycle, value.TargetQuantity, value.ProducedQuantity,
        value.TargetQuantity - value.ProducedQuantity, value.Status, value.Version);
}

internal sealed record ProductionRunAssignmentResponse(
    string MachineAssignmentId, string MachineId, int BacklogPosition,
    string PlanningMode, int Version)
{
    internal static ProductionRunAssignmentResponse FromDomain(ProductionRunAssignment value) => new(
        value.MachineAssignmentId, value.MachineId, value.BacklogPosition,
        value.PlanningMode, value.Version);
}

internal sealed record UnallocatedBatchOperationListResponse(
    IReadOnlyList<UnallocatedBatchOperation> Items);
