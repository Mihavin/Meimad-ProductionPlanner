using System.Globalization;
using Meimad.Planner.Server.Backup;
using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Application.LocalAi;

namespace Meimad.Planner.Server.Application.Maintenance;

internal sealed record OperationalHealthItem(string Category, string EntityId, string Name, string Status,
    string Freshness, DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSucceededAt,
    DateTimeOffset? ObservedAt, DateTimeOffset? ReceivedAt, string Detail, string? SourceVersion = null,
    string? ContentRevision = null, string? ObservedState = null);
internal sealed record OperationalHealthPage(DateTimeOffset ReadAt, string Revision,
    IReadOnlyList<OperationalHealthItem> Items, string? NextCursor);

internal sealed class OperationalHealthService(SqliteDatabase database, RecoverySetService recovery,
    ArtifactReconciliationService artifacts, TimeProvider clock, LocalAiRuntimeDiagnostics aiRuntime)
{
    // Diagnostic freshness thresholds, not production gates or device polling schedules.
    internal static string Freshness(DateTimeOffset now, DateTimeOffset? observed, TimeSpan maximumAge) =>
        observed is null ? "UNKNOWN" : observed > now ? "CLOCK_SKEW" : now - observed > maximumAge ? "STALE" : "FRESH";

    internal async Task<OperationalHealthPage> ReadAsync(string? after, int limit, CancellationToken token)
    {
        if (limit is < 1 or > 200 || after?.Length > 512) throw new ArgumentException("limit must be 1-200 and cursor at most 512 characters.");
        var now = clock.GetUtcNow();
        var items = new List<OperationalHealthItem>();
        string revision;
        await using (var connection = await database.OpenConnectionAsync(token))
        await using (var transaction = connection.BeginTransaction(deferred: true))
        {
            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SELECT epoch || ':' || revision FROM projection_revision WHERE id=1";
            revision = (string)(await query.ExecuteScalarAsync(token))!;
            query.CommandText = """
                SELECT 'CNC:' || machine.id,machine.id,machine.name,connection.enabled,connection.connection_status,
                    connection.last_connection_attempt_at,connection.last_successful_poll_at,state.observed_at,state.last_seen_at,
                    connection.polling_interval_ms,connection.version,state.machine_state
                FROM machines machine LEFT JOIN machine_connections connection ON connection.machine_id=machine.id
                LEFT JOIN machine_current_state state ON state.machine_id=machine.id
                WHERE ('CNC:' || machine.id)>$after ORDER BY machine.id LIMIT $limit;
                """;
            query.Parameters.AddWithValue("$after", after ?? "");
            query.Parameters.AddWithValue("$limit", limit + 1);
            await using (var reader = await query.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token))
                {
                    var enabled = !reader.IsDBNull(3) && reader.GetBoolean(3);
                    var observation = Date(reader, 7);
                    items.Add(new("CNC", reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? "NOT_CONFIGURED" : !enabled ? "DISABLED" : reader.GetString(4),
                        Freshness(now, observation, TimeSpan.FromMilliseconds(reader.IsDBNull(9) ? 60000 : Math.Max(60000, reader.GetInt32(9) * 3))),
                        Date(reader, 5), Date(reader, 6), observation, null,
                        "Connection status and observation freshness are separate; neither proves NC or Offset Loader verification.", Text(reader, 10), ObservedState: Text(reader, 11)));
                }
            query.CommandText = """
                SELECT id,device_name,is_enabled,last_server_contact_at,last_seen_at,version
                FROM device_registry WHERE device_type='eink' AND ('TABLET:' || id)>$after ORDER BY id LIMIT $limit;
                """;
            await using (var reader = await query.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token))
                {
                    var contact = Date(reader, 3) ?? Date(reader, 4);
                    items.Add(new("TABLET", reader.GetString(0), reader.GetString(1), reader.GetBoolean(2) ? "REGISTERED" : "DISABLED",
                        Freshness(now, contact, TimeSpan.FromHours(24)), null, contact, null, contact,
                        "Last contact only; sleep can be normal. The Server has no confirmation of the revision activated on the tablet.", Text(reader, 5)));
                }
            query.Parameters.Clear();
            query.CommandText = "SELECT sync_status,last_started_at,last_successful_at,version FROM kitaron_sync_state WHERE id=1";
            await using (var reader = await query.ExecuteReaderAsync(token))
            {
                if (await reader.ReadAsync(token))
                    items.Add(new("ERP_IMPORT", "kitaron", "Kitaron import", reader.GetString(0).ToUpperInvariant(),
                        Freshness(now, Date(reader, 2), TimeSpan.FromHours(4)), Date(reader, 1), Date(reader, 2), null, null,
                        "Last successful import is retained separately from the latest attempt. Unknown historical success is not inferred.", Text(reader, 3)));
                else items.Add(new("ERP_IMPORT", "kitaron", "Kitaron import", "NEVER_RUN", "UNKNOWN", null, null, null, null, "No import evidence recorded."));
            }
            query.CommandText = """
                SELECT id,lifecycle_state,started_at,finished_at,intent_version,
                    (SELECT MAX(finished_at) FROM kitaron_push_runs WHERE lifecycle_state='Succeeded')
                FROM kitaron_push_runs ORDER BY (lifecycle_state IN ('OutcomeUnknown','Writing','Prepared','Preparing')) DESC,started_at DESC,id DESC LIMIT 1;
                """;
            await using (var reader = await query.ExecuteReaderAsync(token))
            {
                if (await reader.ReadAsync(token))
                    items.Add(new("ERP_PUSH", reader.GetString(0), "Kitaron push", reader.GetString(1), "NOT_APPLICABLE",
                        Date(reader, 2), Date(reader, 5), null, Date(reader, 3),
                        "Unresolved work takes priority. Reconciled means reviewed, not proof of ERP commit.", Text(reader, 4)));
                else items.Add(new("ERP_PUSH", "kitaron", "Kitaron push", "NEVER_RUN", "NOT_APPLICABLE", null, null, null, null, "No push evidence recorded."));
            }
            await transaction.CommitAsync(token);
        }
        // Filesystem-backed observations are sampled after releasing the SQLite read snapshot.
        try
        {
            var backup = await recovery.StatusAsync(token);
            items.Add(new("RECOVERY", "complete-set", "Complete backup", RecoveryState(backup),
                Freshness(now, backup.LastComplete?.SnapshotAt, TimeSpan.FromHours(4)), backup.LastAttemptAt,
                backup.LastComplete?.VerifiedAt, backup.LastComplete?.SnapshotAt, null,
                backup.LastAttemptResult ?? "No completed backup evidence recorded.", backup.LastComplete?.SetId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { items.Add(new("RECOVERY", "complete-set", "Complete backup", "UNAVAILABLE", "UNKNOWN", null, null, null, null, "Backup status cannot be read; no success is inferred.")); }
        var report = artifacts.Status;
        items.Add(new("ARTIFACTS", "inventory", "Package integrity", report.Status == "COMPLETE" && report.Problems > 0 ? "PROBLEMS_FOUND" : report.Status,
            Freshness(now, report.LastSuccessfulScanAt, TimeSpan.FromHours(24)), report.StartedAt, report.LastSuccessfulScanAt,
            report.CompletedAt, null, $"Last inspection: {report.Problems} problems in {report.Total} entries. This is timestamped inspection evidence, not a current release gate."));
        var ai = aiRuntime.State;
        items.Add(new("AI", "local", "Local AI", ai.Status, Freshness(now, ai.LastSucceededAt, TimeSpan.FromMinutes(15)),
            ai.LastAttemptAt, ai.LastSucceededAt, null, ai.LastSucceededAt, ai.Detail));
        var ordered = items.Where(item => string.CompareOrdinal(item.Category + ":" + item.EntityId, after ?? "") > 0)
            .OrderBy(item => item.Category + ":" + item.EntityId, StringComparer.Ordinal).Take(limit + 1).ToArray();
        var page = ordered.Take(limit).ToArray();
        return new(now, revision, page, ordered.Length > limit ? page[^1].Category + ":" + page[^1].EntityId : null);
    }

    internal static string RecoveryState(RecoveryStatus backup) => !backup.Configured ? "NOT_CONFIGURED"
        : backup.LastAttemptResult == "In progress" ? "RUNNING"
        : backup.LastAttemptResult?.StartsWith("Previous attempt was interrupted", StringComparison.Ordinal) == true ? "INTERRUPTED"
        : backup.LastAttemptResult?.StartsWith("Failed or interrupted", StringComparison.Ordinal) == true ? "FAILED_OR_INTERRUPTED"
        : backup.RecoveryPointOverdue ? "OVERDUE" : "VERIFIED";

    private static DateTimeOffset? Date(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null
        : DateTimeOffset.TryParse(reader.GetString(index), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value.ToUniversalTime() : null;
    private static string? Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
}
