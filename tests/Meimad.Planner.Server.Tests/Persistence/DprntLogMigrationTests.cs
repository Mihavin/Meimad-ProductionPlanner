using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class DprntLogMigrationTests
{
    [Fact]
    public async Task Version_96_starts_the_DPRNT_log_with_the_event_lines_still_in_raw_telemetry()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        var migrator = new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(95);
        await using (var v95 = await fixture.Database.OpenConnectionAsync())
        {
            await ExecuteAsync(v95, """
                INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-1', 'Day', 'UTC');
                INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
                VALUES ('machine-15', '15', 'Haas UMC-500ss', 'mill', 'calendar-1', 'active', 1);
                INSERT INTO machine_connections (
                    id, machine_id, adapter_type, enabled, connection_status, polling_interval_ms,
                    connection_timeout_ms, maximum_reconnect_backoff_ms, allow_read, allow_write,
                    configuration_json, raw_telemetry_retention_days, version, created_at, updated_at)
                VALUES ('cnc-15', 'machine-15', 'HAAS_NGC', 0, 'DISABLED', 1000, 3000, 30000, 1, 0, '{}', 14, 1,
                        '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
                INSERT INTO machine_telemetry_raw (id, connection_id, machine_id, adapter_type, observed_at, operation, raw_payload)
                VALUES ('r2', 'cnc-15', 'machine-15', 'HAAS_NGC', '2026-10-05T13:19:44.0000000+00:00', 'DPRINT_EVENT', 'MEIMAD/V/1/EVENT/CST/ID/B'),
                       ('r1', 'cnc-15', 'machine-15', 'HAAS_NGC', '2026-10-04T10:54:48.0000000+00:00', 'DPRINT_EVENT', 'MEIMAD/V/2/CONTEXT/A'),
                       ('r3', 'cnc-15', 'machine-15', 'HAAS_NGC', '2026-10-05T13:20:00.0000000+00:00', 'MTCONNECT_CURRENT', '<MTConnectStreams/>');
                """);
        }
        SqliteConnection.ClearAllPools();

        await migrator.MigrateAsync();

        await using var v96 = await fixture.Database.OpenConnectionAsync();
        Assert.Equal("2026-10-04T10:54:48.0000000+00:00|MEIMAD/V/2/CONTEXT/A;2026-10-05T13:19:44.0000000+00:00|MEIMAD/V/1/EVENT/CST/ID/B",
            await ScalarAsync(v96, "SELECT group_concat(received_at || '|' || line, ';') FROM (SELECT received_at, line FROM machine_dprnt_lines ORDER BY id);"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(v96,
            "INSERT INTO machine_dprnt_lines (machine_id, connection_id, received_at, line) VALUES ('machine-15', 'cnc-15', '2026-10-06T00:00:00Z', '');"));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
