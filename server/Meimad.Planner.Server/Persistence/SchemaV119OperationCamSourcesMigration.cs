using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// The CAM source of a Case Operation (Import from Cimatron, specification of 2026-10-13): the Cimatron document the
/// Operation's data set was imported from and the CAM operations chosen there, by Cimatron id. One row per Case Operation,
/// written in the same transaction as the simulation package import it describes; a re-import replaces it (version + 1).
/// The document path is stored relative to the network folder root like other shared paths; a local path stays absolute
/// and is only usable on the workstation it names (<c>source_host</c>). Not a planning input: the triggers advance the
/// all-input and non-planning revisions only.
/// </summary>
internal sealed class SchemaV119OperationCamSourcesMigration : IDatabaseMigration
{
    public int Version => 119;
    public string Name => "case_operation_cam_sources";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE case_operation_cam_sources (
                case_operation_id TEXT PRIMARY KEY REFERENCES case_operations (id) ON DELETE CASCADE,
                application TEXT NOT NULL CHECK (application IN ('cimatron')),
                document_path TEXT NOT NULL CHECK (length(document_path) BETWEEN 1 AND 1024),
                document_name TEXT NULL,
                document_id TEXT NULL,
                setup_name TEXT NULL,
                operations_json TEXT NOT NULL,
                postprocessor TEXT NULL,
                client_import_id TEXT NULL,
                simulation_package_import_id TEXT NOT NULL REFERENCES simulation_package_imports (id),
                source_host TEXT NULL,
                imported_at TEXT NOT NULL,
                imported_by TEXT NULL,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0)
            );
            CREATE INDEX ix_case_operation_cam_sources_import ON case_operation_cam_sources (simulation_package_import_id);
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            command.CommandText = $"""
                CREATE TRIGGER projection_revision_case_operation_cam_sources_{action.ToLowerInvariant()}
                AFTER {action} ON case_operation_cam_sources
                BEGIN UPDATE projection_revision
                      SET revision = revision + 1, non_planning_revision = non_planning_revision + 1
                      WHERE id = 1; END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
