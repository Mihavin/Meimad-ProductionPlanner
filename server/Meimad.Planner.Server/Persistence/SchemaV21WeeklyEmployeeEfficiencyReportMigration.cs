using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV21WeeklyEmployeeEfficiencyReportMigration : IDatabaseMigration
{
    public int Version => 21;
    public string Name => "weekly_employee_efficiency_report";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE report_email_settings ADD COLUMN weekly_employee_efficiency_enabled INTEGER NOT NULL DEFAULT 0 CHECK (weekly_employee_efficiency_enabled IN (0,1));
            ALTER TABLE report_email_settings ADD COLUMN weekly_employee_efficiency_send_day TEXT NOT NULL DEFAULT 'sunday' CHECK (weekly_employee_efficiency_send_day IN ('sunday','monday','tuesday','wednesday','thursday','friday','saturday'));
            ALTER TABLE report_email_settings ADD COLUMN weekly_employee_efficiency_time_local TEXT NOT NULL DEFAULT '08:00';

            CREATE TABLE employee_work_measurements (
                id TEXT PRIMARY KEY,
                employee_resource_id TEXT NOT NULL,
                work_date TEXT NOT NULL,
                planned_seconds INTEGER NOT NULL CHECK (planned_seconds >= 0),
                actual_seconds INTEGER NOT NULL CHECK (actual_seconds >= 0),
                source_reference TEXT NULL,
                notes TEXT NULL,
                recorded_by TEXT NOT NULL,
                recorded_at TEXT NOT NULL,
                FOREIGN KEY (employee_resource_id) REFERENCES employee_resources(id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_employee_work_measurements_week
                ON employee_work_measurements(work_date, employee_resource_id);

            CREATE TABLE weekly_employee_efficiency_deliveries (
                period_key TEXT PRIMARY KEY,
                sent_at TEXT NOT NULL,
                recipient_count INTEGER NOT NULL CHECK (recipient_count > 0)
            );
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
