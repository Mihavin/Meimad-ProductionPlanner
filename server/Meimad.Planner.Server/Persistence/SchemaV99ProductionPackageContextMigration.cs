using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV99ProductionPackageContextMigration : IDatabaseMigration
{
    public int Version => 99;
    public string Name => "exact_production_package_context";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE production_package_contexts (
                production_package_id TEXT PRIMARY KEY REFERENCES production_packages(id),
                machine_assignment_id TEXT NOT NULL REFERENCES machine_assignments(id),
                production_run_program_id TEXT NOT NULL REFERENCES production_run_programs(id),
                production_run_output_id TEXT NOT NULL REFERENCES production_run_outputs(id),
                context_json TEXT NOT NULL CHECK(json_valid(context_json)),
                UNIQUE(machine_assignment_id,production_run_program_id,production_run_output_id,production_package_id)
            );
            CREATE TABLE production_package_context_current (
                machine_assignment_id TEXT NOT NULL,
                production_run_program_id TEXT NOT NULL,
                production_run_output_id TEXT NOT NULL,
                production_package_id TEXT NOT NULL,
                PRIMARY KEY(machine_assignment_id,production_run_program_id,production_run_output_id),
                FOREIGN KEY(machine_assignment_id,production_run_program_id,production_run_output_id,production_package_id)
                    REFERENCES production_package_contexts(machine_assignment_id,production_run_program_id,production_run_output_id,production_package_id)
            );
            CREATE TRIGGER production_package_context_immutable_update BEFORE UPDATE ON production_package_contexts
            BEGIN SELECT RAISE(ABORT,'Production Package context is immutable'); END;
            CREATE TRIGGER production_package_context_immutable_delete BEFORE DELETE ON production_package_contexts
            BEGIN SELECT RAISE(ABORT,'Production Package context is immutable'); END;
            -- Historical manifests did not declare program/output identity or its version.
            -- Preserve them, but do not invent that evidence or migrate an arbitrary pointer.
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
