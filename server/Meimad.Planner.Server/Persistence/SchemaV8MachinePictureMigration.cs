using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV8MachinePictureMigration : IDatabaseMigration
{
    public int Version => 8;

    public string Name => "machine_picture_path";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE machines ADD COLUMN picture_reference TEXT;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
