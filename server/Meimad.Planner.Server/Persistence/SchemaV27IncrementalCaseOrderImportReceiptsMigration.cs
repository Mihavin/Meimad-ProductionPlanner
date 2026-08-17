using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV27IncrementalCaseOrderImportReceiptsMigration : IDatabaseMigration
{
    public int Version => 27;

    public string Name => "incremental_case_order_import_receipts";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP INDEX IF EXISTS ix_legacy_working_plan_imports_committed_at;

            CREATE TABLE legacy_working_plan_imports_v27 (
                id TEXT PRIMARY KEY,
                workbook_sha256 TEXT NOT NULL,
                approved_request_sha256 TEXT NOT NULL,
                response_json TEXT NOT NULL,
                committed_by_client_id TEXT NOT NULL,
                committed_by_user_id TEXT NOT NULL,
                committed_at TEXT NOT NULL,
                CHECK (length(workbook_sha256) = 64),
                CHECK (length(approved_request_sha256) = 64),
                UNIQUE (workbook_sha256, approved_request_sha256)
            );

            INSERT INTO legacy_working_plan_imports_v27 (
                id, workbook_sha256, approved_request_sha256, response_json,
                committed_by_client_id, committed_by_user_id, committed_at)
            SELECT
                id, workbook_sha256, approved_request_sha256, response_json,
                committed_by_client_id, committed_by_user_id, committed_at
            FROM legacy_working_plan_imports;

            DROP TABLE legacy_working_plan_imports;
            ALTER TABLE legacy_working_plan_imports_v27 RENAME TO legacy_working_plan_imports;

            CREATE INDEX ix_legacy_working_plan_imports_committed_at
            ON legacy_working_plan_imports (committed_at);

            CREATE INDEX ix_legacy_working_plan_imports_workbook
            ON legacy_working_plan_imports (workbook_sha256);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
