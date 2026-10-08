using Meimad.Planner.Server.Application.ProductionPackages;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal static class SqliteProductionPackageContext
{
    internal static async Task<IReadOnlyList<ProductionPackageContext>> ListAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string? operationId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT assignment.id,run.id,program.id,output.id,output.batch_operation_id,assignment.machine_id,
                COALESCE(program.production_process_revision_id,program.process_revision_id),
                assignment.version || ':' || run.version || ':' || program.version || ':' || output.version
                    || ':' || program.target_cycle_count || ':' ||
                    (SELECT group_concat(evidence, '|') FROM
                        (SELECT sibling.id || ':' || sibling.version || ':' || sibling.target_quantity || ':' || sibling.status AS evidence
                         FROM production_run_outputs sibling WHERE sibling.production_run_program_id=program.id ORDER BY sibling.id)),
                output.target_quantity, program.sequence_position + 1,
                (SELECT group_concat(evidence, '|') FROM
                    (SELECT sibling.id || ':' || sibling.target_quantity || ':' || sibling.quantity_per_cycle
                        || ':' || COALESCE(sibling.revision_output_id,'') AS evidence
                     FROM production_run_outputs sibling WHERE sibling.production_run_program_id=program.id ORDER BY sibling.id))
            FROM machine_assignments assignment
            JOIN production_runs run ON run.id=assignment.production_run_id
            JOIN production_run_programs program ON program.production_run_id=run.id
            JOIN production_run_outputs output ON output.production_run_program_id=program.id
            WHERE assignment.released_at IS NULL
              AND run.status IN ('DRAFT','PLANNED','IN_PROGRESS','SUSPENDED')
              AND program.status IN ('PLANNED','ACTIVE','SUSPENDED')
              AND output.status IN ('ALLOCATED','IN_PRODUCTION')
              AND output.target_quantity > output.produced_quantity
              AND ($operationId IS NULL OR output.batch_operation_id=$operationId)
            ORDER BY assignment.id,program.sequence_position,program.id,output.id;
            """;
        command.Parameters.AddWithValue("$operationId", (object?)operationId ?? DBNull.Value);
        var rows = new List<ProductionPackageContext>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),
            reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.IsDBNull(6)?null:reader.GetString(6),
            reader.GetString(7),reader.GetInt32(8),reader.GetInt32(9),reader.GetString(10)));
        return rows;
    }

    internal static async Task<ProductionPackageContext?> ResolveAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string operationId,
        ProductionPackageSelection? selection, CancellationToken token)
    {
        var rows = await ListAsync(connection, transaction, operationId, token);
        if (selection is not null)
        {
            var row = rows.SingleOrDefault(row => row.MachineAssignmentId == selection.MachineAssignmentId
                && row.ProductionRunId == selection.ProductionRunId
                && row.ProductionRunProgramId == selection.ProductionRunProgramId
                && row.ProductionRunOutputId == selection.ProductionRunOutputId);
            if (row is null || selection.ContextStamp is not null && selection.ContextStamp != row.ContextStamp)
                throw new ProductionPackageBuildException("production_package_context_changed",
                    "The selected Run/program/output is no longer current. Refresh the preparation queue and review its assignment.");
            await ValidateAsync(connection, transaction, row, token);
            return row;
        }
        if (rows.Count > 1)
            throw new ProductionPackageBuildException("production_package_context_ambiguous",
                "This Operation has multiple live Run/program/output contexts. Select a preparation queue row using an updated client.");
        var resolved = rows.SingleOrDefault();
        if (resolved is not null) await ValidateAsync(connection, transaction, resolved, token);
        return resolved;
    }

    internal static async Task ValidateAsync(SqliteConnection connection, SqliteTransaction? transaction,
        ProductionPackageContext context, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM production_run_programs program
                JOIN production_run_outputs output ON output.production_run_program_id=program.id
                JOIN batch_operations operation ON operation.id=output.batch_operation_id
                LEFT JOIN manufacturing_program_revision_outputs recipe ON recipe.id=output.revision_output_id
                WHERE program.id=$program AND (
                    output.target_quantity<>program.target_cycle_count*output.quantity_per_cycle
                    OR (program.legacy_unmanaged=0 AND (
                        recipe.id IS NULL OR recipe.process_revision_id<>program.process_revision_id
                        OR recipe.case_operation_id<>operation.source_case_operation_id
                        OR recipe.quantity_per_cycle<>output.quantity_per_cycle))))
            """;
        command.Parameters.AddWithValue("$program", context.ProductionRunProgramId);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 0)
            throw new ProductionPackageBuildException("production_package_context_invalid",
                "The selected program's outputs do not match its recipe and cycle quantities. Review the Run allocations.");
    }
}
