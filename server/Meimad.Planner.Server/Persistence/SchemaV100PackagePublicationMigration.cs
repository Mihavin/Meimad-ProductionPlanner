using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV100PackagePublicationMigration : IDatabaseMigration
{
    public int Version => 100;
    public string Name => "package_publication_and_request_receipts";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE production_package_publication_versions (
                machine_assignment_id TEXT NOT NULL,
                production_run_program_id TEXT NOT NULL,
                production_run_output_id TEXT NOT NULL,
                version INTEGER NOT NULL CHECK(version > 0),
                PRIMARY KEY(machine_assignment_id,production_run_program_id,production_run_output_id)
            );
            INSERT INTO production_package_publication_versions
            SELECT machine_assignment_id,production_run_program_id,production_run_output_id,1
            FROM production_package_context_current;

            CREATE TRIGGER package_publication_insert AFTER INSERT ON production_package_context_current
            BEGIN
                INSERT INTO production_package_publication_versions VALUES
                    (NEW.machine_assignment_id,NEW.production_run_program_id,NEW.production_run_output_id,1)
                ON CONFLICT(machine_assignment_id,production_run_program_id,production_run_output_id)
                DO UPDATE SET version=version+1;
            END;
            CREATE TRIGGER package_publication_update AFTER UPDATE ON production_package_context_current
            BEGIN
                UPDATE production_package_publication_versions SET version=version+1
                WHERE machine_assignment_id=NEW.machine_assignment_id
                    AND production_run_program_id=NEW.production_run_program_id
                    AND production_run_output_id=NEW.production_run_output_id;
            END;
            CREATE TRIGGER package_publication_delete AFTER DELETE ON production_package_context_current
            BEGIN
                UPDATE production_package_publication_versions SET version=version+1
                WHERE machine_assignment_id=OLD.machine_assignment_id
                    AND production_run_program_id=OLD.production_run_program_id
                    AND production_run_output_id=OLD.production_run_output_id;
            END;

            CREATE TABLE production_package_requests (
                actor_id TEXT NOT NULL,
                request_id TEXT NOT NULL,
                request_hash TEXT NOT NULL CHECK(length(request_hash)=64),
                production_package_id TEXT NOT NULL REFERENCES production_packages(id),
                result_json TEXT NOT NULL CHECK(json_valid(result_json)),
                completed_at TEXT NOT NULL,
                PRIMARY KEY(actor_id,request_id)
            );
            CREATE TRIGGER package_request_immutable_update BEFORE UPDATE ON production_package_requests
            BEGIN SELECT RAISE(ABORT,'Package request receipt is immutable'); END;
            CREATE TRIGGER package_request_immutable_delete BEFORE DELETE ON production_package_requests
            BEGIN SELECT RAISE(ABORT,'Package request receipt is immutable'); END;
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
