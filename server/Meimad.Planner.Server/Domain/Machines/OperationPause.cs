namespace Meimad.Planner.Server.Domain.Machines;

internal static class OperationPauseReasonTypes
{
    internal const string AdditionalQa = "additional_qa";
    internal const string ToolingProblem = "tooling_problem";
    internal const string CustomerRequest = "customer_request";
    internal const string Other = "other";
}

internal sealed record OperationPauseReason(
    string ReasonType,
    string? ProblemDescription,
    string? ToolingItemDescription,
    string? CustomerContactName,
    string? RequestDescription,
    string? Comment);

