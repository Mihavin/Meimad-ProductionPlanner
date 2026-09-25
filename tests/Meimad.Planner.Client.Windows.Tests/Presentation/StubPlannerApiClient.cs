using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// Implements the interface members that have no default implementation with "not supported", so
/// a focused view-model test only re-implements the members it exercises.
/// </summary>
internal abstract class StubPlannerApiClient : IPlannerApiClient
{
    public virtual Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<EditModeStatus> GetEditModeAsync(string clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<EditModeStatus> RequestEditAsync(string clientId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<EditModeStatus> ReleaseEditAsync(string clientId, long generation, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<EditModeStatus> DecideTransferAsync(string clientId, long generation, string requestId, bool release, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<PlannerCase>> ListCasesAsync(CaseQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<CaseResource> GetCaseAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<CaseResource> UpdateCaseAsync(string caseId, CaseUpdate update, string entityTag, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<CaseOperation>> ListCaseOperationsAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<PlannerOrder>> ListOrdersAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<ProductionBatch>> ListBatchesAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<byte[]?> GetCasePreviewAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<PlanningBoardSnapshot> GetPlanningBoardAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<TimelineSnapshot> GetTimelineAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task AssignOrMoveOperationAsync(string batchOperationId, string machineId, int backlogPosition, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task UnassignOperationAsync(string batchOperationId, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual void Dispose()
    {
    }
}
