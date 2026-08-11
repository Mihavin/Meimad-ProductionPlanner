using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV3OrderNotesMigration : IDatabaseMigration
{
    public int Version => 3;

    public string Name => "order_notes";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE orders ADD COLUMN notes TEXT;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
