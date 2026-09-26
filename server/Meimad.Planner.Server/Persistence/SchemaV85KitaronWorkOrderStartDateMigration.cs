using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// The open Kitaron work order snapshot keeps the planned production start (TRootCard.StartDate)
/// so the Case pool can filter by Work Order production start.
/// </summary>
internal sealed class SchemaV85KitaronWorkOrderStartDateMigration : IDatabaseMigration
{
    public int Version => 85;

    public string Name => "kitaron_work_order_start_date";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE kitaron_work_orders ADD COLUMN start_date TEXT NULL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
