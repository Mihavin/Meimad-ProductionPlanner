using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Adds an operator-set manual setup priority to a Machine Assignment. When two operations on
/// different Machines contend for the same scarce worker (typically the single setup worker), the
/// Timeline engine ranks them by Work Finish Date, then Order number, then a deterministic
/// tie-break. That ranking has no way to express a real shop-floor decision such as "the setupist
/// started Machine 15 before Machine 14 even though Machine 14's Order is due sooner". A NULL
/// priority keeps the existing ranking; a lower non-null value wins ahead of any due date.
/// </summary>
internal sealed class SchemaV72MachineAssignmentManualPriorityMigration : IDatabaseMigration
{
    public int Version => 72;
    public string Name => "machine_assignment_manual_priority";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE machine_assignments
            ADD COLUMN manual_priority INTEGER
                CHECK (manual_priority IS NULL OR manual_priority >= 0);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
