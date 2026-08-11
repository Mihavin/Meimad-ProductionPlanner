using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal interface IDatabaseMigration
{
    int Version { get; }

    string Name { get; }

    Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken);
}
