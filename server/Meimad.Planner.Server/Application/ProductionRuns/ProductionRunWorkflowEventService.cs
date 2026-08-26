using System.Text.Json;

namespace Meimad.Planner.Server.Application.ProductionRuns;

internal static class ProductionRunWorkflowEventTypes
{
    internal static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "OFFSET_LOADER_COMPLETED", "SETUP_VERIFICATION_REQUESTED",
        "SETUP_VERIFICATION_SUCCEEDED", "SETUP_VERIFICATION_FAILED",
        "SEND_TO_QC", "QC_PASS", "QC_FAIL", "CYCLE_START", "CYCLE_END",
        "CYCLE_INTERRUPTED", "PRODUCTION_SESSION_OPENED", "PRODUCTION_SESSION_CLOSED"
    };
}

internal sealed record ProductionRunWorkflowEvent(
    string EventId, string ProductionRunId, string MachineId, string EventType,
    string Source, string SourceEventId, long? SourceSequence,
    DateTimeOffset ServerReceivedAt, DateTimeOffset? MachineTimestamp,
    string? NcReleaseId, string? OffsetLoaderReleaseId, string? TabletDeviceId,
    string? UserId, string MetadataJson);

internal sealed record AppendProductionRunWorkflowEvent(
    string ProductionRunId, string MachineId, string EventType, string Source,
    string SourceEventId, long? SourceSequence = null,
    DateTimeOffset? MachineTimestamp = null, string? NcReleaseId = null,
    string? OffsetLoaderReleaseId = null, string? TabletDeviceId = null,
    string? UserId = null, string MetadataJson = "{}");

internal sealed record ProductionRunWorkflowAppendResult(
    ProductionRunWorkflowEvent Event, bool WasDuplicate,
    IReadOnlyList<ProductionRunWorkflowAnomaly> Anomalies);

internal sealed record ProductionRunWorkflowAnomaly(
    string AnomalyId, string ProductionRunId, string MachineId, string Source,
    string SourceEventId, string AnomalyType, long PreviousSequence,
    long ExpectedSequence, long ReceivedSequence, string WorkflowEventId,
    DateTimeOffset DetectedAt, string DetailsJson);

internal interface IProductionRunWorkflowEventRepository
{
    Task<ProductionRunWorkflowAppendResult> AppendAsync(
        AppendProductionRunWorkflowEvent command, DateTimeOffset serverReceivedAt,
        CancellationToken cancellationToken);
}

internal sealed class ProductionRunWorkflowEventService(
    IProductionRunWorkflowEventRepository repository, TimeProvider timeProvider)
{
    internal Task<ProductionRunWorkflowAppendResult> AppendAsync(
        AppendProductionRunWorkflowEvent command,
        CancellationToken cancellationToken = default)
    {
        Required(command.ProductionRunId, "productionRunId");
        Required(command.MachineId, "machineId");
        Required(command.Source, "source");
        Required(command.SourceEventId, "sourceEventId");
        if (!ProductionRunWorkflowEventTypes.All.Contains(command.EventType))
            throw new ProductionRunWorkflowEventValidationException(
                "eventType", "unsupported_workflow_event", "The workflow event type is not supported.");
        if (command.SourceSequence is < 0)
            throw new ProductionRunWorkflowEventValidationException(
                "sourceSequence", "invalid_sequence", "Source sequence must be zero or greater.");
        try
        {
            using var metadata = JsonDocument.Parse(command.MetadataJson);
            if (metadata.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ProductionRunWorkflowEventValidationException(
                "metadataJson", "invalid_json", "Workflow event metadata must be a JSON object.");
        }
        return repository.AppendAsync(command, timeProvider.GetUtcNow(), cancellationToken);
    }

    private static void Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ProductionRunWorkflowEventValidationException(
                field, "required", $"{field} is required.");
    }
}

internal sealed class ProductionRunWorkflowEventValidationException(
    string field, string code, string message) : Exception(message)
{
    internal string Field { get; } = field;
    internal string Code { get; } = code;
}

internal sealed class ProductionRunWorkflowTargetException(string message) : Exception(message);
