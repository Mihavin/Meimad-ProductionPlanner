using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Links CAD model files (STEP part models, STL rest-material stock, fixtures, ...) to a Case and
/// optionally to one of its route Operations. Files stay where the factory keeps them; the row only
/// records the path, exactly like the Case preview picture. One file per Case can be the primary
/// part model that "View in 3D" opens first.
/// </summary>
internal sealed class SchemaV74CaseModelFilesMigration : IDatabaseMigration
{
    public int Version => 74;
    public string Name => "case_model_files";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE case_model_files (
                id TEXT NOT NULL PRIMARY KEY,
                case_id TEXT NOT NULL REFERENCES cases (id) ON DELETE RESTRICT,
                case_operation_id TEXT NULL REFERENCES case_operations (id) ON DELETE SET NULL,
                kind TEXT NOT NULL CHECK (kind IN ('part', 'stock', 'fixture', 'other')),
                format TEXT NOT NULL CHECK (format IN ('step', 'stl')),
                file_path TEXT NOT NULL,
                label TEXT NOT NULL,
                is_primary INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0, 1)),
                sort_order INTEGER NOT NULL DEFAULT 0,
                version INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX ix_case_model_files_case ON case_model_files (case_id, sort_order);
            CREATE INDEX ix_case_model_files_operation ON case_model_files (case_operation_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
