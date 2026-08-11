using Meimad.Planner.Server.Domain.Cases;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.CaseOperations;

namespace Meimad.Planner.Server.Application.Cases;

internal interface ICaseRepository
{
    Task<PlannerCase> CreateAsync(
        PlannerCase plannerCase,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<PlannerCase?> GetByIdAsync(
        string caseId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PlannerCase>> ListAsync(
        string? search,
        string? customer,
        bool? isActive,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CaseOperationDetails>> ListOperationsAsync(
        string caseId,
        CancellationToken cancellationToken);

    Task<CaseOperationDetails?> CreateOperationAsync(
        NewCaseOperation operation,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<PlannerCase?> UpdateAsync(
        PlannerCase plannerCase,
        int expectedVersion,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);
}

internal sealed record NewCaseOperation(
    string CaseOperationId,
    string CaseId,
    int OperationNumber,
    string Name,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    CaseOperationDependencyType DependencyType,
    string? PredecessorCaseOperationId,
    string? SimultaneousGroupKey,
    DateTimeOffset CreatedAt);

internal sealed record CaseOperationDetails(
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
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
