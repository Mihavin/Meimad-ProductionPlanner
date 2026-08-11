namespace Meimad.Planner.Server.Persistence;

internal sealed class DatabaseInitializationService : IHostedService
{
    private readonly DatabaseMigrator migrator;
    private readonly SqliteDatabase database;
    private readonly ILogger<DatabaseInitializationService> logger;

    public DatabaseInitializationService(
        DatabaseMigrator migrator,
        SqliteDatabase database,
        ILogger<DatabaseInitializationService> logger)
    {
        this.migrator = migrator;
        this.database = database;
        this.logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await migrator.MigrateAsync(cancellationToken);
        logger.LogInformation(
            "SQLite database initialized at {DatabasePath}.",
            database.DatabasePath);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
