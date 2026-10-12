using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.MachineEvidence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class WorkOffsetMigrationTests
{
    [Fact]
    public async Task Version_116_adds_append_only_measured_work_offsets_with_one_current_record_per_key()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        var migrator = new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(115);
        await WorkOffsetTestData.SeedAsync(fixture.Database);
        SqliteConnection.ClearAllPools();

        await migrator.MigrateAsync();

        await using var connection = await fixture.Database.OpenConnectionAsync();
        Assert.Equal(116L, await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(3L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'trigger' AND name LIKE 'projection_revision_machine_work_offsets_%';"));

        const string insert = """
            INSERT INTO machine_work_offsets (id, machine_id, case_operation_id, program_release_id, work_offset,
                x_mm, y_mm, z_mm, units, source, source_session, session_key, raw_message, raw_message_hash,
                measured_at, received_at, received_by, initial_status, status)
            VALUES ($id, 'machine-10', 'case-operation-wo', 'gcode-wo-current', 'G54', 1, 2, 3, 'MM', 'DPRNT_TCP',
                $id, $id, 'MEIMAD/OFFSET...', $hash, '2026-10-12T06:00:00Z', '2026-10-12T06:00:00Z', 'DPRNT:cnc-10',
                $status, $status);
            """;
        await ExecuteAsync(connection, insert, ("$id", "a"), ("$hash", "hash-aaaaaaaa"), ("$status", "VALID"));
        // A second VALID record of the same key violates the current-record index.
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, insert, ("$id", "b"), ("$hash", "hash-bbbbbbbb"), ("$status", "VALID")));
        // The same message hash is stored once.
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, insert, ("$id", "c"), ("$hash", "hash-aaaaaaaa"), ("$status", "REVIEW_REQUIRED")));
        await ExecuteAsync(connection, insert, ("$id", "d"), ("$hash", "hash-dddddddd"), ("$status", "REVIEW_REQUIRED"));

        // Only the allowed status moves, each with the next version.
        await ExecuteAsync(connection, "UPDATE machine_work_offsets SET status = 'SUPERSEDED', version = 2 WHERE id = 'a';");
        await ExecuteAsync(connection, "UPDATE machine_work_offsets SET status = 'VALID', supersedes_id = 'a', version = 2 WHERE id = 'd';");
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE machine_work_offsets SET status = 'VALID', version = 3 WHERE id = 'a';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE machine_work_offsets SET supersedes_id = NULL, version = 3 WHERE id = 'd';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "DELETE FROM machine_work_offsets WHERE id = 'a';"));
        // A matched status needs the Machine, Operation, release and units.
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO machine_work_offsets (id, work_offset, source, source_session, session_key, raw_message,
                raw_message_hash, measured_at, received_at, received_by, initial_status, status)
            VALUES ('e', 'G54', 'API', 's', 's', 'raw', 'hash-eeeeeeee', '2026-10-12T06:00:00Z', '2026-10-12T06:00:00Z', 'user',
                'VALID', 'VALID');
            """));
        // The Machine cannot be deleted under its evidence.
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "DELETE FROM machines WHERE id = 'machine-10';"));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
