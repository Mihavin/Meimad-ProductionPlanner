using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Persistence;

internal sealed class TemporaryDatabase : IAsyncDisposable
{
    private readonly string directoryPath;

    private TemporaryDatabase(string directoryPath, SqliteDatabase database)
    {
        this.directoryPath = directoryPath;
        Database = database;
    }

    internal SqliteDatabase Database { get; }

    internal string DatabasePath => Database.DatabasePath;

    internal static async Task<TemporaryDatabase> CreateAsync()
    {
        var fixture = CreateUnmigrated();
        var migrator = new DatabaseMigrator(
            fixture.Database,
            NullLogger<DatabaseMigrator>.Instance);

        await migrator.MigrateAsync();
        return fixture;
    }

    internal static TemporaryDatabase CreateUnmigrated()
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner.Tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directoryPath, "test.db");
        var database = new SqliteDatabase(new DatabaseOptions(databasePath));
        return new TemporaryDatabase(directoryPath, database);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
