using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Manual workflow statuses for Machines without DPRNT output (owner decisions 2026-09-29): the
/// planner reports the status on the Planning Board, and the Server records it as the Production
/// Run workflow event that telemetry would have produced. Ready for setup and setup run have no
/// telemetry event a planner may emulate (an Offset Loader event would arm CNC verification), so
/// <c>MANUAL_READY_FOR_SETUP</c> and <c>MANUAL_SETUP_RUN</c> are added; passed to QC, ready for
/// production and in production use <c>SEND_TO_QC</c>, <c>QC_PASS</c> and
/// <c>PRODUCTION_SESSION_OPENED</c>. The event table is rebuilt because its event-type check is
/// part of the table definition; columns, indexes and triggers stay as they are.
/// </summary>
internal sealed class SchemaV94ManualWorkflowStatusMigration : IDatabaseMigration
{
    public int Version => 94;

    public string Name => "manual_workflow_status";

    public bool DisablesForeignKeyEnforcement => true;

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        string tableSql;
        var dependents = new List<string>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT type, sql FROM sqlite_master
                WHERE tbl_name = 'production_run_workflow_events' AND sql IS NOT NULL
                ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'index' THEN 1 ELSE 2 END, name;
                """;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            tableSql = string.Empty;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(0) == "table") tableSql = reader.GetString(1);
                else dependents.Add(reader.GetString(1));
            }
        }

        const string lastType = "'SETUP_RESTARTED'";
        var position = tableSql.IndexOf(lastType, StringComparison.Ordinal);
        if (position < 0)
            throw new InvalidOperationException("The workflow event table has no SETUP_RESTARTED event type to extend.");
        var rebuilt = tableSql.Insert(position + lastType.Length, ",\n                        'MANUAL_READY_FOR_SETUP',\n                        'MANUAL_SETUP_RUN'");
        rebuilt = Regex.Replace(rebuilt, "^CREATE TABLE\\s+\"?production_run_workflow_events(_v90)?\"?",
            "CREATE TABLE production_run_workflow_events_v94", RegexOptions.CultureInvariant);

        await using (var rebuild = connection.CreateCommand())
        {
            rebuild.Transaction = transaction;
            rebuild.CommandText = rebuilt + ";\n" + """
                INSERT INTO production_run_workflow_events_v94 SELECT * FROM production_run_workflow_events;
                DROP TABLE production_run_workflow_events;
                -- Legacy renaming leaves the references of other tables and triggers alone; they name
                -- the table, which exists again after the rename.
                PRAGMA legacy_alter_table = ON;
                ALTER TABLE production_run_workflow_events_v94 RENAME TO production_run_workflow_events;
                PRAGMA legacy_alter_table = OFF;
                """;
            await rebuild.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var sql in dependents)
        {
            await using var recreate = connection.CreateCommand();
            recreate.Transaction = transaction;
            recreate.CommandText = sql + ";";
            await recreate.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
