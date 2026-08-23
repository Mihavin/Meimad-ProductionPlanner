using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>Adds the dedicated, read-only DPRNT TCP port to Haas connection settings.</summary>
internal sealed class SchemaV44HaasDprntPortMigration : IDatabaseMigration
{
    public int Version => 44;
    public string Name => "haas_dprnt_part_port";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE haas_connection_settings
                ADD COLUMN dprnt_port INTEGER NOT NULL DEFAULT 8080
                CHECK (dprnt_port BETWEEN 1 AND 65535);
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
