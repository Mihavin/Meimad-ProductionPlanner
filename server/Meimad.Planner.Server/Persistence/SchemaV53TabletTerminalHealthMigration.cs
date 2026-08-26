using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>Adds non-secret tablet network diagnostics for administrative monitoring.</summary>
internal sealed class SchemaV53TabletTerminalHealthMigration : IDatabaseMigration
{
    public int Version => 53;

    public string Name => "tablet_terminal_health";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE device_registry ADD COLUMN wifi_ip_address TEXT;
            ALTER TABLE device_registry ADD COLUMN wifi_rssi INTEGER
                CHECK (wifi_rssi IS NULL OR wifi_rssi BETWEEN -127 AND 0);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
