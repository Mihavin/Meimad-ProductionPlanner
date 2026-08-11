namespace Meimad.Planner.Server.Domain.CaseOperations;

internal sealed record CaseOperationDependency(
    string DependencyId,
    CaseOperationDependencyType Type,
    string FromCaseOperationId,
    string ToCaseOperationId,
    string? SimultaneousGroupKey = null);
