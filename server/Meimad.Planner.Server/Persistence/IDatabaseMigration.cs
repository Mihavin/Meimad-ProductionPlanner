using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal interface IDatabaseMigration
{
    int Version { get; }

    string Name { get; }

    /// <summary>
    /// True for a migration that rebuilds a table other tables reference. The migrator then
    /// turns foreign-key enforcement off around the migration transaction, so dropping the old
    /// table does not cascade into dependents, and verifies integrity before committing.
    /// </summary>
    bool DisablesForeignKeyEnforcement => false;

    Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken);
}
