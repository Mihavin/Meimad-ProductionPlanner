using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class DatabaseMigrator
{
    private static readonly IReadOnlyList<IDatabaseMigration> Migrations =
    [
        new SchemaV1Migration(),
        new SchemaV2CaseDetailsMigration(),
        new SchemaV3OrderNotesMigration(),
        new SchemaV4MachineMasterMigration(),
        new SchemaV5SingleEditModeMigration(),
        new SchemaV6EInkPackagesMigration(),
        new SchemaV7JobPackageGenerationMigration(),
        new SchemaV8MachinePictureMigration(),
        new SchemaV9BatchLifecycleMigration(),
        new SchemaV10SetupMachineTypesAndOrderLifecycleMigration(),
        new SchemaV11AdministrativeSetupMigration(),
        new SchemaV12EmployeeResourceDetailsMigration(),
        new SchemaV13EmployeeCalendarExceptionsMigration(),
        new SchemaV14IsraeliHolidayCacheMigration(),
        new SchemaV15MachineAssignmentOverridesMigration(),
        new SchemaV16OperationTimeModelMigration(),
        new SchemaV17MachineDowntimeMigration(),
        new SchemaV18OperationPauseEventsMigration(),
        new SchemaV19EInkSetupPackageMigration(),
        new SchemaV20WeeklyMaterialReportMigration(),
        new SchemaV21WeeklyEmployeeEfficiencyReportMigration(),
        new SchemaV22StructuredEventLogMigration(),
        new SchemaV23OperationActualTimesMigration(),
        new SchemaV24MachineAssignmentPlanningModeMigration(),
        new SchemaV25LegacyWorkingPlanImportMigration(),
        new SchemaV26CriticalityExternalDelayCalendarMigration(),
        new SchemaV27IncrementalCaseOrderImportReceiptsMigration(),
        new SchemaV28KitaronConnectionMigration(),
        new SchemaV29KitaronMappingMigration(),
        new SchemaV30KitaronSyncMigration(),
        new SchemaV31CaseComponentsMigration(),
        new SchemaV32UnifiedCasePoolMigration(),
        new SchemaV33SynchronizeNotStartedBatchOperationTimesMigration()
    ];

    private readonly SqliteDatabase database;
    private readonly ILogger<DatabaseMigrator> logger;

    public DatabaseMigrator(
        SqliteDatabase database,
        ILogger<DatabaseMigrator> logger)
    {
        this.database = database;
        this.logger = logger;
        ValidateMigrationSequence();
    }

    internal async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await EnsureMigrationTableAsync(connection, cancellationToken);

        var appliedMigrations = await ReadAppliedMigrationsAsync(connection, cancellationToken);
        var databaseVersion = await ReadUserVersionAsync(connection, cancellationToken);
        var latestKnownVersion = Migrations[^1].Version;

        if (databaseVersion > latestKnownVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {databaseVersion} is newer than supported version {latestKnownVersion}.");
        }

        foreach (var migration in Migrations)
        {
            if (appliedMigrations.TryGetValue(migration.Version, out var appliedName))
            {
                if (!string.Equals(appliedName, migration.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Migration {migration.Version} was recorded as '{appliedName}', not '{migration.Name}'.");
                }

                continue;
            }

            await ApplyMigrationAsync(connection, migration, cancellationToken);
        }

        var finalVersion = await ReadUserVersionAsync(connection, cancellationToken);
        if (finalVersion != latestKnownVersion)
        {
            throw new InvalidOperationException(
                $"Database reports schema version {finalVersion}; expected {latestKnownVersion}.");
        }
    }

    private static async Task EnsureMigrationTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<int, string>> ReadAppliedMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, name FROM schema_migrations ORDER BY version;";

        var result = new Dictionary<int, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetInt32(0), reader.GetString(1));
        }

        return result;
    }

    private static async Task<int> ReadUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ApplyMigrationAsync(
        SqliteConnection connection,
        IDatabaseMigration migration,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Applying database migration {MigrationVersion}: {MigrationName}.",
            migration.Version,
            migration.Name);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await migration.ApplyAsync(connection, transaction, cancellationToken);

            await using var historyCommand = connection.CreateCommand();
            historyCommand.Transaction = transaction;
            historyCommand.CommandText = """
                INSERT INTO schema_migrations (version, name)
                VALUES ($version, $name);
                """;
            historyCommand.Parameters.AddWithValue("$version", migration.Version);
            historyCommand.Parameters.AddWithValue("$name", migration.Name);
            await historyCommand.ExecuteNonQueryAsync(cancellationToken);

            await using var versionCommand = connection.CreateCommand();
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = $"PRAGMA user_version = {migration.Version};";
            await versionCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static void ValidateMigrationSequence()
    {
        for (var index = 0; index < Migrations.Count; index++)
        {
            var expectedVersion = index + 1;
            if (Migrations[index].Version != expectedVersion)
            {
                throw new InvalidOperationException(
                    $"Database migrations must be contiguous. Expected version {expectedVersion}.");
            }
        }
    }
}
