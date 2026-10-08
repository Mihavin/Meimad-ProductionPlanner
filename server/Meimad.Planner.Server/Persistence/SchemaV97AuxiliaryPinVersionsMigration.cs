using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV97AuxiliaryPinVersionsMigration : IDatabaseMigration
{
    public int Version => 97;
    public string Name => "auxiliary_pin_versions";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE auxiliary_pin_versions (
                batch_operation_id TEXT NOT NULL REFERENCES batch_operations(id) ON DELETE CASCADE,
                requirement_id TEXT NOT NULL REFERENCES operation_resource_requirements(id) ON DELETE CASCADE,
                version INTEGER NOT NULL CHECK(version > 0),
                updated_by TEXT,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(batch_operation_id, requirement_id)
            );
            INSERT INTO auxiliary_pin_versions(batch_operation_id, requirement_id, version, updated_by, updated_at)
            SELECT work.batch_operation_id, work.requirement_id, MAX(work.version),
                (SELECT assigned_by FROM resource_schedule_assignments a WHERE a.schedule_work_id = work.id ORDER BY a.created_at DESC, a.id LIMIT 1),
                MAX(work.updated_at)
            FROM resource_schedule_work work WHERE work.state = 'PINNED'
            GROUP BY work.batch_operation_id, work.requirement_id;
            CREATE INDEX ix_resource_schedule_work_pin_context
                ON resource_schedule_work(batch_operation_id, requirement_id, state);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
