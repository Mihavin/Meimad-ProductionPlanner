using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV66KitaronCaseOrderAuthorityMigration : IDatabaseMigration
{
    public int Version => 66;

    public string Name => "Kitaron Case and Order authority with Order price";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE orders ADD COLUMN price NUMERIC NULL;
            ALTER TABLE orders ADD COLUMN kitaron_status TEXT NULL
                CHECK (kitaron_status IS NULL OR kitaron_status IN ('active', 'inactive', 'cancelled'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
