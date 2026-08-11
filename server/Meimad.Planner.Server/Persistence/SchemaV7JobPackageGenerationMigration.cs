using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV7JobPackageGenerationMigration : IDatabaseMigration
{
    public int Version => 7;

    public string Name => "job_package_generation";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE eink_package_revisions ADD COLUMN machine_id TEXT
                REFERENCES machines (id) ON DELETE RESTRICT;
            ALTER TABLE eink_package_revisions ADD COLUMN machine_number TEXT;
            ALTER TABLE eink_package_revisions ADD COLUMN machine_name TEXT;
            ALTER TABLE eink_package_revisions ADD COLUMN case_id TEXT
                REFERENCES cases (id) ON DELETE RESTRICT;
            ALTER TABLE eink_package_revisions ADD COLUMN part_number TEXT;
            ALTER TABLE eink_package_revisions ADD COLUMN part_name TEXT;
            ALTER TABLE eink_package_revisions ADD COLUMN part_revision TEXT;
            ALTER TABLE eink_package_revisions ADD COLUMN customer TEXT;
            ALTER TABLE eink_package_revisions ADD COLUMN production_batch_id TEXT
                REFERENCES production_batches (id) ON DELETE RESTRICT;
            ALTER TABLE eink_package_revisions ADD COLUMN batch_number TEXT;
            ALTER TABLE eink_package_revisions ADD COLUMN planned_quantity INTEGER
                CHECK (planned_quantity IS NULL OR planned_quantity > 0);
            ALTER TABLE eink_package_revisions ADD COLUMN operation_number INTEGER
                CHECK (operation_number IS NULL OR operation_number > 0);
            ALTER TABLE eink_package_revisions ADD COLUMN operation_name TEXT;

            ALTER TABLE eink_package_files ADD COLUMN asset_type TEXT NOT NULL DEFAULT 'other'
                CHECK (asset_type IN (
                    'preview', 'tool_table', 'nc', 'text', 'offsets',
                    'instructions', 'other'));

            CREATE INDEX ix_eink_package_revisions_machine_published
            ON eink_package_revisions (machine_id, published_at DESC, id);
            CREATE INDEX ix_eink_package_revisions_batch
            ON eink_package_revisions (production_batch_id, batch_operation_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
