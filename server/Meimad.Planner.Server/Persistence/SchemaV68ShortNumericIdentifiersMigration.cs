using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Adds short human-readable numeric identifiers for Production Runs and Production Packages,
/// mirroring the existing gcode_release_verification_hooks.nc_identity_token pattern, so the
/// runnable NC header can embed a 6-digit number instead of an internal GUID. Nullable because
/// existing rows are backfilled lazily on first use rather than in this migration.
/// </summary>
internal sealed class SchemaV68ShortNumericIdentifiersMigration : IDatabaseMigration
{
    public int Version => 68;
    public string Name => "short_numeric_run_and_package_identifiers";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE production_runs ADD COLUMN run_number INTEGER
                CHECK (run_number IS NULL OR (run_number BETWEEN 100000 AND 999999));

            ALTER TABLE production_packages ADD COLUMN package_number INTEGER
                CHECK (package_number IS NULL OR (package_number BETWEEN 100000 AND 999999));

            CREATE UNIQUE INDEX ux_production_runs_run_number
                ON production_runs(run_number) WHERE run_number IS NOT NULL;

            CREATE UNIQUE INDEX ux_production_packages_package_number
                ON production_packages(package_number) WHERE package_number IS NOT NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
