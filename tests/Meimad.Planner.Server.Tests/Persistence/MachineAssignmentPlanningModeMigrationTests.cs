using Microsoft.Data.Sqlite;
using Meimad.Planner.Server.Persistence;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class MachineAssignmentPlanningModeMigrationTests
{
    [Fact]
    public async Task Version_24_defaults_existing_assignments_to_manual_and_rejects_unknown_tokens()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE machine_assignments (
                    id TEXT PRIMARY KEY,
                    batch_operation_id TEXT NOT NULL,
                    machine_id TEXT NOT NULL,
                    backlog_position INTEGER NOT NULL,
                    version INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL);
                INSERT INTO machine_assignments (
                    id, batch_operation_id, machine_id, backlog_position,
                    version, created_at, updated_at)
                VALUES (
                    'assignment-legacy', 'operation-legacy', 'machine-legacy', 0,
                    7, '2026-08-13T00:00:00Z', '2026-08-13T00:00:00Z');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using (var transaction = connection.BeginTransaction())
        {
            await new SchemaV24MachineAssignmentPlanningModeMigration().ApplyAsync(
                connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        await using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT planning_mode, version
                FROM machine_assignments
                WHERE id = 'assignment-legacy';
                """;
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("manual", reader.GetString(0));
            Assert.Equal(7, reader.GetInt32(1));
        }

        await using var invalid = connection.CreateCommand();
        invalid.CommandText = """
            UPDATE machine_assignments
            SET planning_mode = 'automatic'
            WHERE id = 'assignment-legacy';
            """;
        await Assert.ThrowsAsync<SqliteException>(() => invalid.ExecuteNonQueryAsync());
    }
}
