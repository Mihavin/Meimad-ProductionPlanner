using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.ProductionRuns;

namespace Meimad.Planner.Server.Application.ProductionRuns;

internal interface IProductionRunRepository
{
    Task<IReadOnlyList<ProductionRun>> ListAsync(CancellationToken token);
    Task<ProductionRun?> GetAsync(string runId, CancellationToken token);
    Task<IReadOnlyList<UnallocatedBatchOperation>> ListUnallocatedAsync(CancellationToken token);
    Task<ProductionRun> CreateAsync(CreateProductionRunCommand command, EditAuthority authority, CancellationToken token);
    Task<ProductionRun> UpdateCompositionAsync(string runId, int expectedVersion, CreateProductionRunCommand command, EditAuthority authority, CancellationToken token);
    Task<ProductionRun> AssignAsync(string runId, int expectedVersion, AssignProductionRunCommand command, EditAuthority authority, CancellationToken token);
    Task<ProductionRun> UnassignAsync(string runId, int expectedVersion, EditAuthority authority, CancellationToken token);
    Task<ProductionRun> CancelAsync(string runId, int expectedVersion, string reason, EditAuthority authority, CancellationToken token);
}

internal sealed record CreateProductionRunCommand(
    int SharedSetupSeconds,
    string SetupSnapshotJson,
    IReadOnlyList<CreateProductionRunProgramCommand> Programs,
    AssignProductionRunCommand? Assignment);

internal sealed record CreateProductionRunProgramCommand(
    string ManufacturingProgramId,
    string ProcessRevisionId,
    string? GCodeReleaseId,
    int SequencePosition,
    decimal CycleSeconds,
    IReadOnlyList<CreateProductionRunOutputCommand> Outputs);

internal sealed record CreateProductionRunOutputCommand(
    string RevisionOutputId,
    string BatchOperationId,
    long TargetQuantity);

internal sealed record AssignProductionRunCommand(
    string MachineId,
    int BacklogPosition,
    string PlanningMode,
    bool ConfirmCompatibilityOverride,
    string? OverrideReason);

internal sealed record UnallocatedBatchOperation(
    string BatchOperationId,
    string ProductionBatchId,
    string CaseId,
    string PartNumber,
    int OperationNumber,
    string OperationName,
    long RequiredQuantity,
    long ProducedQuantity,
    long ActiveAllocatedQuantity,
    long RemainingUnallocatedQuantity,
    long RemainingUnproducedQuantity);
