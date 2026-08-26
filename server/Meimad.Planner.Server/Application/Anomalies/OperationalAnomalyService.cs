using System.Text.Json;

namespace Meimad.Planner.Server.Application.Anomalies;

internal static class OperationalAnomalyTypes
{
    internal static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "wrong_nc_program", "active_nc_identity_unavailable", "stale_offset_loader",
        "offset_loader_not_executed", "offset_loader_interrupted", "verification_failed",
        "verification_expired", "verification_macro_version_mismatch",
        "cycle_started_before_qc_pass", "cycle_end_without_start", "cycle_interrupted",
        "cnc_event_sequence_gap", "duplicate_cnc_event", "unknown_production_run",
        "ambiguous_production_run", "tablet_offline", "tablet_credential_revoked"
    };
}

internal sealed record OperationalAnomaly(
    string AnomalyId,
    string AnomalyType,
    string? MachineId,
    string? ProductionRunId,
    string? TabletDeviceId,
    string Source,
    string? SourceEventId,
    string? WorkflowEventId,
    DateTimeOffset DetectedAt,
    string DetailsJson,
    string Message);

internal sealed record AppendOperationalAnomaly(
    string AnomalyType,
    string Source,
    string DedupeKey,
    DateTimeOffset DetectedAt,
    string? MachineId = null,
    string? ProductionRunId = null,
    string? TabletDeviceId = null,
    string? SourceEventId = null,
    string? WorkflowEventId = null,
    string DetailsJson = "{}");

internal interface IOperationalAnomalyRepository
{
    Task AppendAsync(AppendOperationalAnomaly value, CancellationToken cancellationToken);

    Task<IReadOnlyList<OperationalAnomaly>> ListAsync(
        string? machineId,
        string? productionRunId,
        string? anomalyType,
        int limit,
        CancellationToken cancellationToken);
}

internal sealed class OperationalAnomalyService(IOperationalAnomalyRepository repository)
{
    internal Task<IReadOnlyList<OperationalAnomaly>> ListAsync(
        string? machineId,
        string? productionRunId,
        string? anomalyType,
        int limit,
        CancellationToken cancellationToken = default)
    {
        machineId = Clean(machineId);
        productionRunId = Clean(productionRunId);
        anomalyType = Clean(anomalyType)?.ToLowerInvariant();
        if (anomalyType is not null && !OperationalAnomalyTypes.All.Contains(anomalyType))
            throw new OperationalAnomalyValidationException(
                "anomalyType", "unsupported_anomaly_type", "The anomaly type is not supported.");
        if (limit is < 1 or > 1000)
            throw new OperationalAnomalyValidationException(
                "limit", "invalid_anomaly_limit", "Anomaly limit must be between 1 and 1000.");
        return repository.ListAsync(
            machineId, productionRunId, anomalyType, limit, cancellationToken);
    }

    internal Task AppendAsync(
        AppendOperationalAnomaly value,
        CancellationToken cancellationToken = default)
    {
        if (!OperationalAnomalyTypes.All.Contains(value.AnomalyType))
            throw new OperationalAnomalyValidationException(
                "anomalyType", "unsupported_anomaly_type", "The anomaly type is not supported.");
        if (string.IsNullOrWhiteSpace(value.Source) || string.IsNullOrWhiteSpace(value.DedupeKey))
            throw new OperationalAnomalyValidationException(
                "source", "invalid_anomaly_source", "Anomaly source and dedupe key are required.");
        try
        {
            using var details = JsonDocument.Parse(value.DetailsJson);
            if (details.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        }
        catch (JsonException)
        {
            throw new OperationalAnomalyValidationException(
                "detailsJson", "invalid_anomaly_details", "Anomaly details must be a JSON object.");
        }
        return repository.AppendAsync(value, cancellationToken);
    }

    internal static string Message(string anomalyType) => anomalyType switch
    {
        "wrong_nc_program" => "The active NC program does not match the Production Run.",
        "active_nc_identity_unavailable" => "The active NC program identity is unavailable.",
        "stale_offset_loader" => "The reported Offset Loader release is not current.",
        "offset_loader_not_executed" => "The current Offset Loader has not completed execution.",
        "offset_loader_interrupted" => "Offset Loader execution was interrupted before completion.",
        "verification_failed" => "CNC setup verification failed.",
        "verification_expired" => "The setup-verification session expired.",
        "verification_macro_version_mismatch" => "CNC VERIFICATION MACRO UPDATE REQUIRED. The reported protected-macro version does not match the Server configuration.",
        "cycle_started_before_qc_pass" => "A production cycle started before QC approval.",
        "cycle_end_without_start" => "A cycle END has no valid immediately preceding START.",
        "cycle_interrupted" => "A cycle was interrupted and did not count output.",
        "cnc_event_sequence_gap" => "CNC event sequence evidence is missing or out of order.",
        "duplicate_cnc_event" => "A duplicate CNC event was received and ignored idempotently.",
        "unknown_production_run" => "The reported Production Run is unknown or not current for the Machine.",
        "ambiguous_production_run" => "More than one Production Run could match the Machine evidence.",
        "tablet_offline" => "The assigned tablet is offline.",
        "tablet_credential_revoked" => "The tablet credential has been revoked.",
        _ => "Operational anomaly."
    };

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class OperationalAnomalyValidationException(
    string field,
    string code,
    string message) : Exception(message)
{
    internal string Field { get; } = field;
    internal string Code { get; } = code;
}
