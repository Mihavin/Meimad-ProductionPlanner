using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>Adds physical tablet identity and the separate, append-only tablet workflow projection.</summary>
internal sealed class SchemaV48TabletTerminalsMigration : IDatabaseMigration
{
    public int Version => 48;
    public string Name => "eink_physical_tablets_and_workflow";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE device_registry ADD COLUMN tablet_id TEXT;
            ALTER TABLE device_registry ADD COLUMN hardware_id TEXT;
            ALTER TABLE device_registry ADD COLUMN firmware_version TEXT;
            ALTER TABLE device_registry ADD COLUMN battery_voltage REAL;
            ALTER TABLE device_registry ADD COLUMN battery_percent INTEGER
                CHECK (battery_percent IS NULL OR battery_percent BETWEEN 0 AND 100);
            ALTER TABLE device_registry ADD COLUMN last_server_contact_at TEXT;

            -- Preserve legacy E-Ink registrations. They remain administratively visible,
            -- but cannot complete physical-tablet bootstrap until assigned a real MAC.
            UPDATE device_registry
            SET tablet_id = 'legacy-' || substr(id, 1, 8)
            WHERE device_type = 'eink' AND tablet_id IS NULL;

            CREATE UNIQUE INDEX ux_device_registry_eink_tablet_id
            ON device_registry (tablet_id)
            WHERE device_type = 'eink' AND tablet_id IS NOT NULL;

            CREATE UNIQUE INDEX ux_device_registry_eink_hardware_id
            ON device_registry (hardware_id)
            WHERE device_type = 'eink' AND hardware_id IS NOT NULL AND is_enabled = 1;

            CREATE TABLE tablet_workflow_events (
                id TEXT PRIMARY KEY,
                device_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                production_run_id TEXT NOT NULL,
                event_type TEXT NOT NULL CHECK (event_type = 'SEND_TO_QC'),
                resulting_state TEXT NOT NULL CHECK (resulting_state = 'IN_QC'),
                occurred_at TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY (device_id) REFERENCES device_registry(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                UNIQUE (production_run_id, event_type)
            );

            CREATE INDEX ix_tablet_workflow_events_device_run
            ON tablet_workflow_events (device_id, production_run_id, occurred_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
