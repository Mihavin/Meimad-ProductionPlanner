using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>Enforces one authoritative SEND_TO_QC event per Production Run.</summary>
internal sealed class SchemaV54TabletSendToQcMigration : IDatabaseMigration
{
    public int Version => 54;

    public string Name => "tablet_send_to_qc_idempotency";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE UNIQUE INDEX ux_production_run_send_to_qc
            ON production_run_workflow_events(production_run_id, event_type)
            WHERE event_type = 'SEND_TO_QC';
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
