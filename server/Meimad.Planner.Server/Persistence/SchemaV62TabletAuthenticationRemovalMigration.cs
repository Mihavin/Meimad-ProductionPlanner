using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV62TabletAuthenticationRemovalMigration : IDatabaseMigration
{
    public int Version => 62;
    public string Name => "Remove E-Ink tablet authentication credentials";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP TRIGGER IF EXISTS operational_anomaly_from_tablet_revoke;
            DROP TRIGGER IF EXISTS operational_anomalies_immutable_delete;
            DELETE FROM operational_anomalies
            WHERE anomaly_type = 'tablet_credential_revoked';
            CREATE TRIGGER operational_anomalies_immutable_delete
            BEFORE DELETE ON operational_anomalies
            BEGIN SELECT RAISE(ABORT,'Operational anomalies are immutable'); END;

            UPDATE device_registry
            SET credential_hash = NULL
            WHERE device_type = 'eink';
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
