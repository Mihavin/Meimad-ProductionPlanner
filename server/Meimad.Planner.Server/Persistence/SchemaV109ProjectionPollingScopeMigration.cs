using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// CNC polling, connection checks and device contact rewrite their tables every few seconds; v102 gave
/// every table a planning-revision trigger, so each poll moved the planning revision and a Board or
/// Timeline read that overlapped three polls answered 503 <c>projection_changed</c> (audit finding
/// 2026-10-09: 70,905 planning bumps against 991 non-planning on the factory database, about one poll
/// row every 1.7 s). These tables are samples and diagnostics, not planning inputs: they now also
/// advance the non-planning counter, which planning subtracts (the v108 mechanism). The v102 trigger
/// stays, so AI evidence still sees every change.
/// </summary>
internal sealed class SchemaV109ProjectionPollingScopeMigration : IDatabaseMigration
{
    public int Version => 109;
    public string Name => "projection_polling_scope";

    internal static readonly string[] Tables =
    [
        "machine_telemetry_raw", "machine_connection_events", "machine_state_history", "machine_current_state",
        "machine_connections", "haas_machine_snapshots", "haas_connection_settings", "haas_events",
        "haas_bench_sessions", "haas_bench_state_intervals", "machine_dprnt_lines", "operational_anomalies",
        "kitaron_connection_settings", "kitaron_mapping_settings", "kitaron_sync_state", "kitaron_push_runs",
        "israeli_holiday_sync_state", "user_sessions", "device_registry"
    ];

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var table in Tables)
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name=$name";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 0) continue;
            command.Parameters.Clear();
            foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                command.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS projection_non_planning_{table}_{action.ToLowerInvariant()}
                    AFTER {action} ON {table}
                    BEGIN UPDATE projection_revision SET non_planning_revision=non_planning_revision+1 WHERE id=1; END;
                    """;
                await command.ExecuteNonQueryAsync(token);
            }
        }
    }
}
