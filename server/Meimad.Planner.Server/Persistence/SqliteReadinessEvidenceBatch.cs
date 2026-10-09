using System.Diagnostics;
using Meimad.Planner.Server.Domain.Readiness;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal static partial class SqliteProductionReadinessContextReader
{
    private sealed record EvidenceInput(int Index, ProductionReadinessContext Context, string? Release, string? OutputId);

    private static async Task<IReadOnlyList<ProductionReadinessContext>> WithOffsetModesAsync(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<ProductionReadinessContext> contexts,
        CancellationToken token, IReadOnlySet<(string BatchOperationId, string MachineId)>? verifiedPackages,
        IReadOnlyList<string>? outputIds = null)
    {
        var started = Stopwatch.GetTimestamp();
        var result = contexts.ToArray();
        var inputs = contexts.Select((context, index) => (context, index))
            .Where(value => value.context.MachineAssignmentId is not null)
            .Select(value => new EvidenceInput(value.index, value.context,
                ProductionReadinessEvaluator.Evaluate(value.context).EffectiveGCodeReleaseId, outputIds?[value.index])).ToArray();
        if (inputs.Length == 0) return result;
        verifiedPackages ??= await SqliteMachineWorkflowReporting.ReadVerifiedPackagesAsync(connection, transaction, token);
        // Eight parameters per row; stay below 999 even on a conservative SQLite build.
        foreach (var chunk in inputs.Chunk(100))
        {
            token.ThrowIfCancellationRequested();
            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            var requested = BindEvidenceInputs(query, chunk);
            query.CommandText = requested + " SELECT request.i, " + ExecutionEvidenceColumns + "\n"
                + ExecutionEvidenceRowsSql(batch: true) + " ORDER BY request.i;";
            var observations = new Dictionary<int, object[]>();
            var ambiguous = new HashSet<int>();
            await using (var reader = await query.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token))
                {
                    var index = reader.GetInt32(0);
                    var values = Enumerable.Range(1, 8).Select(reader.GetValue).ToArray();
                    if (!observations.TryAdd(index, values)) ambiguous.Add(index);
                }
            foreach (var input in chunk)
                if (observations.TryGetValue(input.Index, out var values) && !ambiguous.Contains(input.Index))
                    result[input.Index] = ApplyExecutionEvidence(input.Context, values, verifiedPackages);

            query.CommandText = requested + ", matches AS (SELECT request.i, COUNT(*) AS amount, "
                + "COALESCE(MIN(package.tool_offset_mode='MANUAL_DUMMY'),0) AS manual\n"
                + OffsetModeMatchingRowsSql(batch: true) + " GROUP BY request.i) "
                + """
                    SELECT request.i, COALESCE(matches.amount,0), COALESCE(matches.manual,0),
                        (SELECT COUNT(*) FROM tool_table_release_tools WHERE tool_table_release_id=request.tools AND is_active=1)
                    FROM requested request LEFT JOIN matches ON matches.i=request.i;
                    """;
            await using (var reader = await query.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token))
                {
                    var index = reader.GetInt32(0);
                    result[index] = result[index] with
                    {
                        ReleasedToolCount = reader.GetInt32(3),
                        ToolOffsetMode = reader.GetInt32(1) == 1 && reader.GetBoolean(2) ? "MANUAL_DUMMY" : "MEASURED"
                    };
                }
            // Preserve the scalar reader's behavior if historical/corrupt data has multiple
            // eligible sessions for one binding; never choose a different session via join order.
            foreach (var input in chunk.Where(input => ambiguous.Contains(input.Index)))
                result[input.Index] = await WithOffsetModeAsync(connection, transaction, input.Context, token, input.OutputId,
                    verifiedPackages: verifiedPackages);
        }
        Application.Projections.PlannerPerformanceMetrics.ReadinessEnrichment.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    private static string BindEvidenceInputs(SqliteCommand query, IReadOnlyList<EvidenceInput> inputs)
    {
        var rows = new List<string>(inputs.Count);
        for (var row = 0; row < inputs.Count; row++)
        {
            var input = inputs[row];
            object?[] values = [input.Index, input.Context.MachineAssignmentId, input.Context.BatchOperationId,
                input.OutputId, input.Context.MachineId, input.Context.ActiveProcessRevisionId, input.Release, input.Context.ActiveToolTableReleaseId];
            var names = new string[values.Length];
            for (var column = 0; column < values.Length; column++)
            {
                names[column] = $"$r{row}c{column}";
                query.Parameters.AddWithValue(names[column], values[column] ?? DBNull.Value);
            }
            rows.Add("(" + string.Join(",", names) + ")");
        }
        return "WITH requested(i,assignment,operation,output,machine,process,release,tools) AS (VALUES "
            + string.Join(",", rows) + ")";
    }
}
