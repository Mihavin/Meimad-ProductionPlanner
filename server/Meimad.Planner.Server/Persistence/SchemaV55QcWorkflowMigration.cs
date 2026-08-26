using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Allows a Production Run that failed QC to be sent for inspection again.
/// Retry idempotency remains database-enforced by the existing unique
/// source/source-event identity, now derived from the eligibility event.
/// </summary>
internal sealed class SchemaV55QcWorkflowMigration : IDatabaseMigration
{
    public int Version => 55;
    public string Name => "qc_workflow_repeat_inspection";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DROP INDEX ux_production_run_send_to_qc;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
