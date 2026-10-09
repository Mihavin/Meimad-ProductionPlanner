using System.Globalization;
using Meimad.Planner.Server.Domain.ProductionRuns;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

// Four ordered reads in one caller-owned snapshot instead of four reads per Run.
internal static class SqliteProductionRunGraphReader
{
    internal static async Task<IReadOnlyList<ProductionRun>> ReadAllAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        var outputs = new Dictionary<string, List<ProductionRunOutput>>(StringComparer.Ordinal);
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT output.id,output.production_run_program_id,output.batch_operation_id,
                output.revision_output_id,output.quantity_per_cycle,output.target_quantity,
                output.produced_quantity,output.status,output.version,output.created_at,output.updated_at
            FROM production_run_outputs output
            JOIN production_run_programs program ON program.id=output.production_run_program_id
            ORDER BY program.sequence_position,output.id;
            """;
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                var program = reader.GetString(1);
                if (!outputs.TryGetValue(program, out var list)) outputs[program] = list = [];
                list.Add(new(reader.GetString(0), program, reader.GetString(2), Text(reader, 3),
                    reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetString(7),
                    reader.GetInt32(8), Instant(reader, 9), Instant(reader, 10)));
            }

        var programs = new Dictionary<string, List<ProductionRunProgram>>(StringComparer.Ordinal);
        query.CommandText = """
            SELECT id,production_run_id,manufacturing_program_id,process_revision_id,selected_gcode_release_id,
                sequence_position,target_cycle_count,completed_cycle_count,status,cycle_seconds_snapshot,
                production_process_revision_id,production_gcode_release_id,production_tool_table_release_id,
                production_gcode_file_hash,production_tool_table_file_hash,legacy_unmanaged,version,created_at,updated_at
            FROM production_run_programs ORDER BY sequence_position;
            """;
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                var id = reader.GetString(0);
                var run = reader.GetString(1);
                if (!programs.TryGetValue(run, out var list)) programs[run] = list = [];
                list.Add(new(id, run, reader.GetString(2), Text(reader, 3), Text(reader, 4),
                    reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetDouble(9),
                    new(Text(reader, 10), Text(reader, 11), Text(reader, 12), Text(reader, 13), Text(reader, 14)),
                    reader.GetBoolean(15), reader.GetInt32(16), Instant(reader, 17), Instant(reader, 18),
                    outputs.GetValueOrDefault(id, [])));
            }

        var assignments = new Dictionary<string, ProductionRunAssignment>(StringComparer.Ordinal);
        query.CommandText = """
            SELECT production_run_id,id,machine_id,backlog_position,planning_mode,version
            FROM machine_assignments WHERE released_at IS NULL AND production_run_id IS NOT NULL;
            """;
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                assignments.Add(reader.GetString(0), new(reader.GetString(1), reader.GetString(2),
                    reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5)));

        var result = new List<ProductionRun>();
        query.CommandText = """
            SELECT id,status,shared_setup_seconds,setup_snapshot_json,structure_locked_at,
                legacy_batch_operation_id,version,created_at,updated_at
            FROM production_runs ORDER BY created_at,id;
            """;
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                var id = reader.GetString(0);
                result.Add(new(id, reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : Instant(reader, 4), Text(reader, 5), reader.GetInt32(6),
                    Instant(reader, 7), Instant(reader, 8), programs.GetValueOrDefault(id, []),
                    assignments.GetValueOrDefault(id)));
            }
        return result;
    }

    private static string? Text(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
    private static DateTimeOffset Instant(SqliteDataReader reader, int column) =>
        DateTimeOffset.Parse(reader.GetString(column), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
