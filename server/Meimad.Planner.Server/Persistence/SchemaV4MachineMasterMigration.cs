using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV4MachineMasterMigration : IDatabaseMigration
{
    public int Version => 4;

    public string Name => "machine_master_fields";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE machines ADD COLUMN axis_type TEXT;
            ALTER TABLE machines ADD COLUMN is_active INTEGER NOT NULL DEFAULT 1
                CHECK (is_active IN (0, 1));
            ALTER TABLE machines ADD COLUMN display_enabled INTEGER NOT NULL DEFAULT 0
                CHECK (display_enabled IN (0, 1));

            UPDATE machines
            SET is_active = CASE
                WHEN lower(status) IN ('inactive', 'retired') THEN 0
                ELSE 1
            END;

            CREATE UNIQUE INDEX ux_device_registry_eink_machine
            ON device_registry (machine_id)
            WHERE device_type = 'eink' AND machine_id IS NOT NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
