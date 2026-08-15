using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV25LegacyWorkingPlanImportMigration : IDatabaseMigration
{
    public int Version => 25;

    public string Name => "legacy_working_plan_import_receipts";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE legacy_working_plan_imports (
                id TEXT PRIMARY KEY,
                workbook_sha256 TEXT NOT NULL UNIQUE,
                approved_request_sha256 TEXT NOT NULL,
                response_json TEXT NOT NULL,
                committed_by_client_id TEXT NOT NULL,
                committed_by_user_id TEXT NOT NULL,
                committed_at TEXT NOT NULL,
                CHECK (length(workbook_sha256) = 64),
                CHECK (length(approved_request_sha256) = 64)
            );

            CREATE INDEX ix_legacy_working_plan_imports_committed_at
            ON legacy_working_plan_imports (committed_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
