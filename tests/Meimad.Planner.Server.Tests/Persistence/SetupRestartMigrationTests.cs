using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class SetupRestartMigrationTests
{
    [Fact]
    public async Task Version_90_keeps_the_workflow_events_and_their_triggers_and_admits_setup_restarts()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        var migrator = new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(89);
        await using (var v89 = await fixture.Database.OpenConnectionAsync())
        {
            await ExecuteAsync(v89, """
                INSERT INTO working_calendars(id,name,time_zone_id,calendar_json) VALUES('calendar-1','Day','UTC','{}');
                INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active,display_enabled,execution_mode)
                VALUES('machine-1','M-1','Mill','mill','calendar-1','active',1,1,'CNC_GCODE');
                INSERT INTO production_runs(id,status,structure_locked_at) VALUES('run-1','IN_PROGRESS','2026-09-01T08:00:00Z');
                INSERT INTO production_run_workflow_events(id,production_run_id,machine_id,event_type,source,source_event_id,source_sequence,server_received_at)
                VALUES('event-1','run-1','machine-1','CYCLE_START','CNC','cnc-1',1,'2026-09-01T09:00:00.0000000+00:00');
                """);
            Assert.Equal(1L, await ScalarAsync(v89, "SELECT COUNT(*) FROM production_run_cycle_attempts;"));
            await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(v89, """
                INSERT INTO production_run_workflow_events(id,production_run_id,machine_id,event_type,source,server_received_at)
                VALUES('event-x','run-1','machine-1','SETUP_RESTARTED','PLANNER','2026-09-01T10:00:00.0000000+00:00');
                """));
        }
        SqliteConnection.ClearAllPools();

        await migrator.MigrateAsync();

        await using var v90 = await fixture.Database.OpenConnectionAsync();
        Assert.Equal("CYCLE_START", await ScalarAsync(v90, "SELECT event_type FROM production_run_workflow_events WHERE id = 'event-1';"));
        await ExecuteAsync(v90, """
            INSERT INTO production_run_workflow_events(id,production_run_id,machine_id,event_type,source,server_received_at)
            VALUES('event-2','run-1','machine-1','SETUP_RESTARTED','PLANNER','2026-09-01T10:00:00.0000000+00:00');
            INSERT INTO production_run_workflow_events(id,production_run_id,machine_id,event_type,source,source_event_id,source_sequence,server_received_at)
            VALUES('event-3','run-1','machine-1','CYCLE_START','CNC','cnc-2',2,'2026-09-01T11:00:00.0000000+00:00');
            """);
        // The table's own triggers came back: a cycle start still opens an attempt, events stay immutable,
        // and the source identity is still unique.
        Assert.Equal(2L, await ScalarAsync(v90, "SELECT COUNT(*) FROM production_run_cycle_attempts;"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(v90,
            "UPDATE production_run_workflow_events SET source = 'OTHER' WHERE id = 'event-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(v90, """
            INSERT INTO production_run_workflow_events(id,production_run_id,machine_id,event_type,source,source_event_id,server_received_at)
            VALUES('event-4','run-1','machine-1','CYCLE_END','CNC','cnc-2','2026-09-01T12:00:00.0000000+00:00');
            """));
        Assert.Equal(0L, await ScalarAsync(v90, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(1L, await ScalarAsync(v90,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'batch_operation_setup_restarts';"));
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
