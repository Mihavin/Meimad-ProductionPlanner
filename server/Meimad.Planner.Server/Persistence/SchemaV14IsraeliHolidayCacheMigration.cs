using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV14IsraeliHolidayCacheMigration : IDatabaseMigration
{
    public int Version => 14;
    public string Name => "israeli_holiday_cache";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE israeli_holidays ADD COLUMN holiday_status TEXT NOT NULL DEFAULT 'non_working'
                CHECK (holiday_status IN ('non_working', 'working', 'partial_working'));
            ALTER TABLE israeli_holidays ADD COLUMN starts_at_local TEXT;
            ALTER TABLE israeli_holidays ADD COLUMN ends_at_local TEXT;
            ALTER TABLE israeli_holidays ADD COLUMN source TEXT NOT NULL DEFAULT 'manual';
            ALTER TABLE israeli_holidays ADD COLUMN external_id TEXT;
            ALTER TABLE israeli_holidays ADD COLUMN is_manual_override INTEGER NOT NULL DEFAULT 1
                CHECK (is_manual_override IN (0, 1));

            CREATE UNIQUE INDEX ux_israeli_holidays_external_id
            ON israeli_holidays (external_id)
            WHERE external_id IS NOT NULL;

            CREATE TABLE israeli_holiday_sync_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                provider TEXT NOT NULL,
                last_attempt_at TEXT,
                last_success_at TEXT,
                last_error TEXT,
                from_year INTEGER,
                to_year INTEGER
            );
            INSERT INTO israeli_holiday_sync_state (id, provider) VALUES (1, 'hebcal');
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
