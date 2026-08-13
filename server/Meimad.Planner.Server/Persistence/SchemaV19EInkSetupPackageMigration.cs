using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV19EInkSetupPackageMigration : IDatabaseMigration
{
    public int Version => 19;

    public string Name => "eink_setup_package_definition";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE eink_package_revisions ADD COLUMN setup_worker_id TEXT;
            ALTER TABLE eink_package_revisions ADD COLUMN setup_worker_first_name TEXT;
            ALTER TABLE eink_package_revisions ADD COLUMN setup_worker_last_name TEXT;
            ALTER TABLE eink_package_revisions ADD COLUMN setup_worker_photo_file_id TEXT;
            ALTER TABLE eink_package_revisions ADD COLUMN planned_setup_starts_at TEXT;
            ALTER TABLE eink_package_revisions ADD COLUMN planned_setup_ends_at TEXT;
            ALTER TABLE eink_package_revisions ADD COLUMN job_tools_json TEXT NOT NULL DEFAULT '[]';
            ALTER TABLE eink_package_revisions ADD COLUMN expected_machine_tools_json TEXT NOT NULL DEFAULT '[]';
            ALTER TABLE eink_package_revisions ADD COLUMN local_checklist_items_json TEXT NOT NULL DEFAULT '[]';
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
