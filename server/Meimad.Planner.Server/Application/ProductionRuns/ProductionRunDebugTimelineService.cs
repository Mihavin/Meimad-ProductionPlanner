using System.Text.Json;

namespace Meimad.Planner.Server.Application.ProductionRuns;

internal sealed record ProductionRunDebugTimeline(
    string MachineId,
    string MachineNumber,
    string MachineName,
    string ProductionRunId,
    string ProductionRunStatus,
    IReadOnlyList<ProductionRunDebugTimelineItem> Items);

internal sealed record ProductionRunDebugTimelineItem(
    string ItemId,
    string Kind,
    string EventType,
    DateTimeOffset ServerReceivedAt,
    DateTimeOffset? MachineTimestamp,
    long? SourceSequence,
    string? AttemptState,
    bool IsAnomaly,
    string Message);

internal sealed record ProductionRunDebugTimelineSource(
    string MachineId,
    string MachineNumber,
    string MachineName,
    string ProductionRunId,
    string ProductionRunStatus,
    IReadOnlyList<ProductionRunDebugWorkflowEvidence> WorkflowEvents,
    IReadOnlyList<ProductionRunDebugAnomalyEvidence> Anomalies);

internal sealed record ProductionRunDebugWorkflowEvidence(
    string EventId,
    string EventType,
    string Source,
    string? SourceEventId,
    long? SourceSequence,
    DateTimeOffset ServerReceivedAt,
    DateTimeOffset? MachineTimestamp,
    string? OffsetLoaderReleaseId,
    string? UserId,
    string MetadataJson,
    string? AttemptState,
    bool IsValidatedCompletion);

internal sealed record ProductionRunDebugAnomalyEvidence(
    string AnomalyId,
    string AnomalyType,
    string SourceEventId,
    long? PreviousSequence,
    long? ExpectedSequence,
    long ReceivedSequence,
    DateTimeOffset DetectedAt);

internal interface IProductionRunDebugTimelineRepository
{
    Task<ProductionRunDebugTimelineSource?> ReadAsync(
        string machineId,
        string productionRunId,
        int limit,
        CancellationToken cancellationToken);
}

internal sealed class ProductionRunDebugTimelineService(
    IProductionRunDebugTimelineRepository repository)
{
    internal async Task<ProductionRunDebugTimeline> ReadAsync(
        string machineId,
        string productionRunId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(machineId) || string.IsNullOrWhiteSpace(productionRunId))
            throw new ProductionRunDebugTimelineValidationException(
                "Machine ID and Production Run ID are required.");
        if (limit is < 1 or > 500)
            throw new ProductionRunDebugTimelineValidationException(
                "Timeline limit must be between 1 and 500.");

        var source = await repository.ReadAsync(
            machineId.Trim(), productionRunId.Trim(), limit, cancellationToken)
            ?? throw new ProductionRunDebugTimelineNotFoundException(
                machineId.Trim(), productionRunId.Trim());

        var items = source.WorkflowEvents
            .Select(ToItem)
            .Concat(source.Anomalies.Select(ToItem))
            .OrderByDescending(item => item.ServerReceivedAt)
            .ThenByDescending(item => item.ItemId, StringComparer.Ordinal)
            .Take(limit)
            .OrderBy(item => item.ServerReceivedAt)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal)
            .ToArray();

        return new(
            source.MachineId,
            source.MachineNumber,
            source.MachineName,
            source.ProductionRunId,
            source.ProductionRunStatus,
            items);
    }

    private static ProductionRunDebugTimelineItem ToItem(
        ProductionRunDebugWorkflowEvidence value) => new(
            $"workflow:{value.EventId}",
            "WORKFLOW_EVENT",
            value.EventType,
            value.ServerReceivedAt,
            value.MachineTimestamp,
            value.SourceSequence,
            value.AttemptState,
            false,
            Message(value));

    private static ProductionRunDebugTimelineItem ToItem(
        ProductionRunDebugAnomalyEvidence value) => new(
            $"anomaly:{value.AnomalyId}",
            "DATA_QUALITY_ANOMALY",
            value.AnomalyType,
            value.DetectedAt,
            null,
            value.ReceivedSequence,
            null,
            true,
            value.AnomalyType switch
            {
                "EVENT_SEQUENCE_GAP" =>
                    $"CNC event sequence gap: expected {value.ExpectedSequence}, received {value.ReceivedSequence}.",
                "EVENT_SEQUENCE_OUT_OF_ORDER" =>
                    $"CNC event arrived out of order: previous {value.PreviousSequence}, received {value.ReceivedSequence}.",
                "CYCLE_END_WITHOUT_START" =>
                    $"Cycle END {value.SourceEventId} has no matching START; output was not counted.",
                "CYCLE_END_SEQUENCE_MISMATCH" =>
                    $"Cycle END sequence {value.ReceivedSequence} does not immediately follow its START; output was not counted.",
                _ => $"Data-quality anomaly: {Humanize(value.AnomalyType)}."
            });

    private static string Message(ProductionRunDebugWorkflowEvidence value)
    {
        var sequence = value.SourceSequence.HasValue
            ? $" #{value.SourceSequence.Value}"
            : string.Empty;
        return value.EventType switch
        {
            "OFFSET_LOADER_COMPLETED" => value.OffsetLoaderReleaseId is null
                ? "Offset Loader executed; setup started."
                : $"Offset Loader release {value.OffsetLoaderReleaseId} executed; setup started.",
            "SETUP_VERIFICATION_REQUESTED" => "Setup verification challenge created.",
            "SETUP_VERIFICATION_SUCCEEDED" => "Setup verification accepted; setup run started.",
            "SETUP_VERIFICATION_FAILED" => WithReason("Setup verification failed.", value.MetadataJson),
            "SEND_TO_QC" => "Sent to QC.",
            "QC_PASS" => WithReason("QC passed; ready for production.", value.MetadataJson),
            "QC_FAIL" => WithReason("QC failed; returned to setup run.", value.MetadataJson),
            "CYCLE_START" => $"Cycle started{sequence}.",
            "CYCLE_END" when value.IsValidatedCompletion => $"Cycle completed{sequence}.",
            "CYCLE_END" => $"Cycle END received{sequence}; completion was not validated.",
            "CYCLE_INTERRUPTED" => InterruptedMessage(value.MetadataJson),
            "PRODUCTION_SESSION_OPENED" => "Production session opened.",
            "PRODUCTION_SESSION_CLOSED" => ClosureMessage(value.MetadataJson),
            _ => Humanize(value.EventType) + "."
        };
    }

    private static string InterruptedMessage(string metadataJson)
    {
        var start = Text(metadataJson, "interruptedSourceEventId");
        var by = Text(metadataJson, "interruptedBySourceEventId");
        return start is not null && by is not null
            ? $"Cycle {start} interrupted by new START {by}; output was not counted."
            : "Cycle interrupted; output was not counted.";
    }

    private static string ClosureMessage(string metadataJson)
    {
        var inferred = Boolean(metadataJson, "endTimeInferred");
        return inferred == true
            ? "Production session closed at the next setup boundary; end time is inferred."
            : "Production session closed at the next setup boundary.";
    }

    private static string WithReason(string message, string metadataJson)
    {
        var reason = Text(metadataJson, "reason");
        return reason is null ? message : $"{message} Reason: {reason}";
    }

    private static string? Text(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool? Boolean(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Humanize(string value) =>
        string.Join(' ', value.Split('_', StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
}

internal sealed class ProductionRunDebugTimelineValidationException(string message)
    : Exception(message);

internal sealed class ProductionRunDebugTimelineNotFoundException(
    string machineId,
    string productionRunId)
    : Exception($"Production Run '{productionRunId}' has no operational relationship with Machine '{machineId}'.");
