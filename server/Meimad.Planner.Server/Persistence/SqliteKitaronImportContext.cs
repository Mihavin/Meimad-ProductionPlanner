using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Meimad.Planner.Server.Application.Kitaron;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteKitaronImportContext(SqliteDatabase database) : IKitaronImportContextReader
{
    // Tables that background work rewrites on its own (CNC polling and connection checks, diagnostics,
    // sessions) are left out: a reviewed preview is bound to the planning facts, not to the last poll.
    // The connection and mapping configuration are compared by their versions instead.
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        "projection_revision", "schema_migrations", "structured_event_log", "kitaron_sync_state",
        "kitaron_connection_settings", "kitaron_mapping_settings", "kitaron_push_runs",
        "user_accounts", "user_sessions", "user_types", "user_type_permissions", "user_account_types",
        "machine_telemetry_raw", "machine_dprnt_lines", "machine_work_offsets", "machine_connection_events", "machine_state_history",
        "machine_current_state", "machine_connections", "haas_machine_snapshots", "haas_connection_settings",
        "haas_events", "haas_bench_sessions", "haas_bench_state_intervals", "operational_anomalies",
        "local_ai_requests", "local_ai_feedback", "wipe_v82_archive"
    };

    public async Task<string> ReadAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var result = await ReadAsync(connection, transaction, token);
        await transaction.CommitAsync(token);
        return result;
    }

    internal static async Task<string> ReadAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        var tables = new List<string>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                if (!Excluded.Contains(reader.GetString(0))) tables.Add(reader.GetString(0));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string? value)
        {
            var bytes = value is null ? [] : Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(value is null ? -1 : bytes.Length));
            hash.AppendData(bytes);
        }
        foreach (var table in tables)
        {
            Add(table);
            command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\" ORDER BY rowid";
            await using var reader = await command.ExecuteReaderAsync(token);
            for (var i = 0; i < reader.FieldCount; i++) Add(reader.GetName(i));
            while (await reader.ReadAsync(token))
            {
                Add("row");
                for (var i = 0; i < reader.FieldCount; i++)
                    Add(reader.IsDBNull(i) ? null : reader.GetValue(i) is byte[] blob ? Convert.ToHexString(blob)
                        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
