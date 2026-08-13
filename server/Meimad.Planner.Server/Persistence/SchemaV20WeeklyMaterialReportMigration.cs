using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV20WeeklyMaterialReportMigration : IDatabaseMigration
{
    public int Version => 20;
    public string Name => "weekly_material_order_report";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE report_email_settings ADD COLUMN weekly_material_report_enabled INTEGER NOT NULL DEFAULT 0 CHECK (weekly_material_report_enabled IN (0,1));
            ALTER TABLE report_email_settings ADD COLUMN weekly_material_report_send_day TEXT NOT NULL DEFAULT 'thursday' CHECK (weekly_material_report_send_day IN ('sunday','monday','tuesday','wednesday','thursday','friday','saturday'));
            ALTER TABLE report_email_settings ADD COLUMN weekly_material_report_time_local TEXT NOT NULL DEFAULT '08:00';

            CREATE TABLE weekly_material_report_deliveries (
                period_key TEXT PRIMARY KEY,
                sent_at TEXT NOT NULL,
                recipient_count INTEGER NOT NULL CHECK (recipient_count > 0)
            );
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
