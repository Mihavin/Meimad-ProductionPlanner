using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.ProductionRuns;

namespace Meimad.Planner.Server.Application.ProductionRuns;

internal interface IProductionRunExecutionRepository
{
    Task<ProductionRun> StartAsync(string runId, int expectedVersion, EditAuthority authority, CancellationToken token, string? readinessStamp = null);
    Task<ProductionRun> ActivateProgramAsync(string runId, string programId, int expectedVersion, EditAuthority authority, CancellationToken token);
    Task<ProductionRunCycleResult> RecordCycleAsync(string runId, string programId, int expectedVersion, RecordProductionRunCycleCommand command, EditAuthority authority, CancellationToken token);
    Task<ProductionRun> SuspendAsync(string runId, int expectedVersion, string reason, EditAuthority authority, CancellationToken token);
    Task<ProductionRun> ResumeAsync(string runId, int expectedVersion, EditAuthority authority, CancellationToken token);
    Task<ProductionRun> ResetAsync(string runId, int expectedVersion, string reason, EditAuthority authority, CancellationToken token);
    Task<UncountedCncCycles> ListUncountedCncCyclesAsync(string runId, CancellationToken token);
    Task<UncountedCncCyclesCountResult> CountUncountedCncCyclesAsync(string runId, int expectedVersion, IReadOnlyList<string> workflowEventIds, EditAuthority authority, CancellationToken token);
}

internal sealed record RecordProductionRunCycleCommand(string Source, string SourceEventId, DateTimeOffset ObservedAt);
internal sealed record ProductionRunCycleResult(ProductionRun Run, bool WasDuplicate, int CompletedCycleCount);

/// <summary>
/// A CNC <c>CYCLE_END</c> the Server retained as a workflow anomaly instead of counting (no matching
/// START). It stays immutable Machine evidence; a planner may count it explicitly (owner decision
/// 2026-10-14) but nothing counts it automatically.
/// </summary>
internal sealed record UncountedCncCycle(string WorkflowEventId, string ProductionRunProgramId, string Source,
    string SourceEventId, long? SourceSequence, DateTimeOffset ServerReceivedAt, DateTimeOffset? MachineTimestamp,
    string AnomalyType);
internal sealed record UncountedCncCycles(string ProductionRunId, string RunStatus, int RunVersion,
    IReadOnlyList<UncountedCncCycle> Items);
internal sealed record UncountedCncCyclesCountResult(ProductionRun Run, int CountedCycles);

internal sealed class ProductionRunExecutionService(
    IProductionRunExecutionRepository repository,
    ProductionRunReadinessService readiness)
{
    internal async Task<ProductionRun> StartAsync(string id, int version, EditAuthority authority, CancellationToken token, string? observedStamp = null)
    {
        var result = await readiness.ReadAsync(id, token);
        if (observedStamp is not null && observedStamp != result.ContextStamp)
            throw new ProductionRunStateException("production_readiness_changed", "Readiness changed since it was displayed. Refresh the Run and review its prerequisites.");
        if (!result.IsReadyForProduction)
            throw new ProductionRunStateException("production_not_ready", "Production Run readiness has blocking components.");
        return await repository.StartAsync(id, version, authority, token, result.ContextStamp);
    }
    internal Task<ProductionRun> ActivateProgramAsync(string id, string programId, int version, EditAuthority authority, CancellationToken token) =>
        repository.ActivateProgramAsync(Clean(id), Clean(programId), version, authority, token);
    internal Task<ProductionRunCycleResult> RecordCycleAsync(string id, string programId, int version, RecordProductionRunCycleCommand command, EditAuthority authority, CancellationToken token) =>
        repository.RecordCycleAsync(Clean(id), Clean(programId), version,
            command with { Source = Clean(command.Source), SourceEventId = Clean(command.SourceEventId) }, authority, token);
    internal Task<ProductionRun> SuspendAsync(string id, int version, string reason, EditAuthority authority, CancellationToken token) =>
        repository.SuspendAsync(Clean(id), version, Clean(reason), authority, token);
    internal Task<ProductionRun> ResumeAsync(string id, int version, EditAuthority authority, CancellationToken token) =>
        repository.ResumeAsync(Clean(id), version, authority, token);
    internal Task<ProductionRun> ResetAsync(string id, int version, string reason, EditAuthority authority, CancellationToken token) =>
        repository.ResetAsync(Clean(id), version, Clean(reason), authority, token);
    internal Task<UncountedCncCycles> ListUncountedCncCyclesAsync(string id, CancellationToken token) =>
        repository.ListUncountedCncCyclesAsync(Clean(id), token);
    internal Task<UncountedCncCyclesCountResult> CountUncountedCncCyclesAsync(string id, int version, IReadOnlyList<string>? workflowEventIds, EditAuthority authority, CancellationToken token)
    {
        var ids = (workflowEventIds ?? []).Select(x => x?.Trim() ?? "").ToArray();
        if (ids.Length is 0 or > 500 || ids.Any(x => x.Length is 0 or > 200) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ProductionRunValidationException("workflowEventIds", "invalid",
                "Select 1 to 500 different uncounted cycle ends.");
        return repository.CountUncountedCncCyclesAsync(Clean(id), version, ids, authority, token);
    }
    private static string Clean(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 2000
        ? value.Trim() : throw new ProductionRunValidationException("value", "required", "A non-empty value is required.");
}
