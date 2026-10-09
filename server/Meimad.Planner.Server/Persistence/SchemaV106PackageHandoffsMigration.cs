using Microsoft.Data.Sqlite;
namespace Meimad.Planner.Server.Persistence;
internal sealed class SchemaV106PackageHandoffsMigration : IDatabaseMigration
{
    public int Version => 106;
    public string Name => "physical_package_handoff_tracking";
    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE production_package_handoffs (
                id TEXT PRIMARY KEY, request_id TEXT NOT NULL UNIQUE, actor TEXT NOT NULL, created_at TEXT NOT NULL,
                package_id TEXT NOT NULL REFERENCES production_packages(id),
                employee_id TEXT NOT NULL REFERENCES employee_resources(id), employee_name TEXT NOT NULL,
                qualification_stamp TEXT NOT NULL, context_json TEXT NOT NULL CHECK(json_valid(context_json)),
                machine_assignment_id TEXT NOT NULL, program_id TEXT NOT NULL, output_id TEXT NOT NULL,
                sequence INTEGER NOT NULL CHECK(sequence>0), request_fingerprint TEXT NOT NULL,
                UNIQUE(machine_assignment_id,program_id,output_id,sequence)
            );
            CREATE INDEX package_handoff_history ON production_package_handoffs(package_id,sequence);
            CREATE TRIGGER package_handoff_immutable_update BEFORE UPDATE ON production_package_handoffs
                BEGIN SELECT RAISE(ABORT,'Physical handoff evidence is immutable'); END;
            CREATE TRIGGER package_handoff_immutable_delete BEFORE DELETE ON production_package_handoffs
                BEGIN SELECT RAISE(ABORT,'Physical handoff evidence is immutable'); END;
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            command.CommandText = $"""
                CREATE TRIGGER projection_revision_production_package_handoffs_{action.ToLowerInvariant()}
                AFTER {action} ON production_package_handoffs BEGIN UPDATE projection_revision SET revision=revision+1 WHERE id=1; END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
