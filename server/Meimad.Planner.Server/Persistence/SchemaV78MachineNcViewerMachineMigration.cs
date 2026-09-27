using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Adds the Machine's NC viewer machine: the id of the NC engine machine definition (vendored or
/// Meimad, for example <c>mazak-variaxis-i-500</c>) that the Windows NC viewer and the Server NC
/// analysis interpret this Machine's programs with. Null keeps the engine's automatic detection.
/// The Server validates the id against the installed definitions; the column only constrains
/// the identifier grammar the engine registry accepts.
/// </summary>
internal sealed class SchemaV78MachineNcViewerMachineMigration : IDatabaseMigration
{
    public int Version => 78;
    public string Name => "machine_nc_viewer_machine";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE machines ADD COLUMN nc_viewer_machine TEXT NULL
                CHECK (nc_viewer_machine IS NULL
                       OR (length(nc_viewer_machine) BETWEEN 1 AND 100
                           AND nc_viewer_machine GLOB '[a-z0-9]*'
                           AND nc_viewer_machine NOT GLOB '*[^a-z0-9-]*'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
