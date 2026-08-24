using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.ProductionRuns;

namespace Meimad.Planner.Server.Application.ProductionRuns;

internal interface IProductionRunExecutionRepository
{
    Task<ProductionRun> StartAsync(string runId, int expectedVersion, EditAuthority authority, CancellationToken token);
    Task<ProductionRun> ActivateProgramAsync(string runId, string programId, int expectedVersion, EditAuthority authority, CancellationToken token);
    Task<ProductionRunCycleResult> RecordCycleAsync(string runId, string programId, int expectedVersion, RecordProductionRunCycleCommand command, EditAuthority authority, CancellationToken token);
    Task<ProductionRun> SuspendAsync(string runId, int expectedVersion, string reason, EditAuthority authority, CancellationToken token);
    Task<ProductionRun> ResumeAsync(string runId, int expectedVersion, EditAuthority authority, CancellationToken token);
    Task<ProductionRun> ResetAsync(string runId, int expectedVersion, string reason, EditAuthority authority, CancellationToken token);
}

internal sealed record RecordProductionRunCycleCommand(string Source, string SourceEventId, DateTimeOffset ObservedAt);
internal sealed record ProductionRunCycleResult(ProductionRun Run, bool WasDuplicate, int CompletedCycleCount);

internal sealed class ProductionRunExecutionService(
    IProductionRunExecutionRepository repository,
    ProductionRunReadinessService readiness)
{
    internal async Task<ProductionRun> StartAsync(string id, int version, EditAuthority authority, CancellationToken token)
    {
        var result = await readiness.ReadAsync(id, token);
        if (!result.IsReadyForProduction)
            throw new ProductionRunStateException("production_not_ready", "Production Run readiness has blocking components.");
        return await repository.StartAsync(id, version, authority, token);
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
    private static string Clean(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 2000
        ? value.Trim() : throw new ProductionRunValidationException("value", "required", "A non-empty value is required.");
}
