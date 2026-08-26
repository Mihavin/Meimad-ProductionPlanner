using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;

namespace Meimad.Planner.Server.Application.Qc;

internal sealed record QcQueueItem(
    string ProductionRunId,
    string MachineId,
    string MachineNumber,
    string MachineName,
    string Part,
    string Operation,
    DateTimeOffset ReceivedAt,
    string? SetupistId,
    string? SetupistName);

internal sealed record QcDecisionCommand(
    string ProductionRunId,
    string Decision,
    string UserId,
    string? Reason);

internal sealed record QcDecisionResult(
    string EventId,
    string ProductionRunId,
    string Decision,
    string ResultingStatus,
    string UserId,
    string? Reason,
    DateTimeOffset Timestamp,
    DateTimeOffset? ProductionApprovedAt);

internal interface IQcWorkflowRepository
{
    Task<IReadOnlyList<QcQueueItem>> ListQueueAsync(CancellationToken cancellationToken);

    Task<QcDecisionResult> DecideAsync(
        QcDecisionCommand command,
        string metadataJson,
        EditAuthority authority,
        DateTimeOffset serverReceivedAt,
        CancellationToken cancellationToken);
}

internal sealed class QcWorkflowService(
    IQcWorkflowRepository repository,
    TimeProvider timeProvider)
{
    internal Task<IReadOnlyList<QcQueueItem>> ListQueueAsync(
        CancellationToken cancellationToken = default) =>
        repository.ListQueueAsync(cancellationToken);

    internal Task<QcDecisionResult> DecideAsync(
        QcDecisionCommand command,
        EditAuthority authority,
        CancellationToken cancellationToken = default)
    {
        var runId = Required(command.ProductionRunId, "Production Run", 200);
        var userId = Required(command.UserId, "User", 200);
        var decision = command.Decision?.Trim().ToUpperInvariant();
        if (decision is not ("PASS" or "FAIL"))
            throw new QcWorkflowValidationException(
                "qc_decision_invalid", "QC decision must be PASS or FAIL.");

        var reason = string.IsNullOrWhiteSpace(command.Reason)
            ? null
            : command.Reason.Trim();
        if (reason?.Length > 1000)
            throw new QcWorkflowValidationException(
                "qc_reason_too_long", "QC reason/comment must not exceed 1000 characters.");

        var metadata = JsonSerializer.Serialize(
            reason is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["reason"] = reason });
        return repository.DecideAsync(
            command with
            {
                ProductionRunId = runId,
                Decision = decision,
                UserId = userId,
                Reason = reason
            },
            metadata,
            authority,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private static string Required(string? value, string label, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximumLength)
            throw new QcWorkflowValidationException(
                "qc_decision_invalid", $"{label} is required and must not exceed {maximumLength} characters.");
        return normalized;
    }
}

internal sealed class QcWorkflowValidationException(string code, string message)
    : Exception(message)
{
    internal string Code { get; } = code;
}

internal sealed class QcWorkflowNotFoundException(string productionRunId)
    : Exception($"Production Run '{productionRunId}' was not found.");

internal sealed class QcWorkflowStateException(string message) : Exception(message);
