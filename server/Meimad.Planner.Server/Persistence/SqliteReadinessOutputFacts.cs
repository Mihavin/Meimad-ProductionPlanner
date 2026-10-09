using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Domain.ProductionRuns;
using Meimad.Planner.Server.Domain.Readiness;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal static partial class SqliteProductionReadinessContextReader
{
    // Complete exact-output contexts, usable only by this read snapshot. Missing keys
    // retain the ordinary reader, including split/ambiguous operation assignments.
    internal sealed class OutputFacts(SqliteConnection owner, SqliteTransaction snapshot,
        IReadOnlyDictionary<ProductionPackageContext, ProductionReadinessContext> contexts)
    {
        internal ProductionReadinessContext? Find(SqliteConnection connection, SqliteTransaction transaction,
            ProductionPackageContext context)
        {
            if (!ReferenceEquals(owner, connection) || !ReferenceEquals(snapshot, transaction)
                || !ReferenceEquals(transaction.Connection, connection))
                throw new InvalidOperationException("Readiness outputs belong to a different or completed snapshot.");
            return contexts.GetValueOrDefault(context);
        }
    }

    internal static async Task<OutputFacts> ReadOutputFactsAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<ProductionRun> runs, IReadOnlyDictionary<string, ProductionReadinessContext> bases,
        ReferenceFacts references, IReadOnlySet<(string BatchOperationId, string MachineId)> verifiedPackages,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        references.ValidateScope(connection, transaction);
        var requests = runs.Where(run => run.Assignment is not null && run.Status is not ("CANCELLED" or "ABORTED"))
            .SelectMany(run => run.Programs.Where(program => program.Status is not ("COMPLETED" or "CANCELLED" or "ABORTED"))
                .SelectMany(program => program.Outputs.Select(output => new ProductionPackageContext(
                    run.Assignment!.MachineAssignmentId, run.ProductionRunId, program.ProductionRunProgramId,
                    output.ProductionRunOutputId, output.BatchOperationId, run.Assignment.MachineId,
                    program.ProcessRevisionId, "", output.TargetQuantity))))
            .Where(context => bases.TryGetValue(context.BatchOperationId, out var basis)
                && !basis.AmbiguousExecutionContext && basis.MachineAssignmentId == context.MachineAssignmentId)
            .Distinct().ToArray();
        var selected = new List<ProductionPackageContext>();
        var inputs = new List<ProductionReadinessContext>();
        foreach (var chunk in requests.Chunk(100))
        {
            token.ThrowIfCancellationRequested();
            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            var values = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                values.Add($"({i},$output{i},$machine{i})");
                query.Parameters.AddWithValue($"$output{i}", chunk[i].ProductionRunOutputId);
                query.Parameters.AddWithValue($"$machine{i}", chunk[i].MachineId);
            }
            query.CommandText = "WITH requested(i,output,machine) AS (VALUES " + string.Join(',', values)
                + ") SELECT request.i," + PackageRecipeColumns + "\n" + PackageRecipeRowsSql(batch: true);
            var recipes = new List<(int Index, PackageRecipe Recipe)>();
            await using (var reader = await query.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) recipes.Add((reader.GetInt32(0), ReadPackageRecipe(reader, 1)));
            foreach (var (index, recipe) in recipes)
            {
                token.ThrowIfCancellationRequested();
                var context = chunk[index];
                inputs.Add(await ApplyPackageRecipeAsync(connection, transaction, context,
                    bases[context.BatchOperationId], recipe, token, references));
                selected.Add(context);
            }
        }
        var enriched = await WithOffsetModesAsync(connection, transaction, inputs, token, verifiedPackages,
            selected.Select(context => context.ProductionRunOutputId).ToArray());
        return new(connection, transaction, selected.Select((context, i) => (context, readiness: enriched[i]))
            .ToDictionary(value => value.context, value => value.readiness));
    }

    private sealed record PackageRecipe(string Mode, int? Capacity, string? ProcessId, string? ToolTableId,
        int? ToolCount, string? CaseOperationId, string? ReleaseId, bool Pinned, bool SingleLegacyOutput);

    private static PackageRecipe ReadPackageRecipe(SqliteDataReader reader, int offset) => new(
        reader.GetString(offset), reader.IsDBNull(offset + 1) ? null : reader.GetInt32(offset + 1),
        reader.IsDBNull(offset + 2) ? null : reader.GetString(offset + 2),
        reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3),
        reader.IsDBNull(offset + 4) ? null : reader.GetInt32(offset + 4),
        reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5),
        reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6), reader.GetBoolean(offset + 7),
        !reader.IsDBNull(offset + 8) && reader.GetBoolean(offset + 8));

    private const string PackageRecipeColumns = """
        machine.execution_mode,machine.usable_tool_positions,
        process.id,process.tool_table_release_id,tools.required_tool_count,process.case_operation_id,
        COALESCE(program.production_gcode_release_id,program.selected_gcode_release_id),
        program.production_gcode_release_id IS NOT NULL,
        run.legacy_batch_operation_id=output.batch_operation_id
            AND (SELECT COUNT(*) FROM production_run_programs p WHERE p.production_run_id=run.id)=1
            AND (SELECT COUNT(*) FROM production_run_outputs o WHERE o.production_run_program_id=program.id)=1
        """;

    private static string PackageRecipeRowsSql(bool batch)
    {
        var source = batch ? "requested request CROSS JOIN " : "";
        var value = batch ? "request." : "$";
        return $"""
            FROM {source}production_run_outputs output
            JOIN production_run_programs program ON program.id=output.production_run_program_id
            JOIN production_runs run ON run.id=program.production_run_id
            JOIN machines machine ON machine.id={value}machine
            LEFT JOIN process_revisions process ON process.id=COALESCE(program.production_process_revision_id,program.process_revision_id)
            LEFT JOIN tool_table_releases tools ON tools.id=process.tool_table_release_id
            WHERE output.id={value}output
            """;
    }
}
