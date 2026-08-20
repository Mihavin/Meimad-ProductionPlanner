using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Domain.GCode;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal static class SqliteNcCycleEstimateStore
{
    internal static async Task<IReadOnlyList<NcMachineCycleEstimate>> InsertAnalysisAndEstimatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GCodeRelease release,
        NcProgramAnalysis analysis,
        CancellationToken token)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO gcode_release_analyses (
                    gcode_release_id, parser_version, status, raw_feed_seconds,
                    rapid_distance_mm, tool_change_count, dwell_seconds,
                    detected_units, warnings_json, unsupported_constructs_json,
                    confidence, analyzed_at)
                VALUES ($releaseId, $parserVersion, $status, $feed, $rapidDistance,
                        $toolChanges, $dwell, $units, $warnings, $unsupported,
                        $confidence, $at);
                """;
            command.Parameters.AddWithValue("$releaseId", release.GCodeReleaseId);
            command.Parameters.AddWithValue("$parserVersion", analysis.ParserVersion);
            command.Parameters.AddWithValue("$status", analysis.Status);
            command.Parameters.AddWithValue("$feed", analysis.FeedMotionSeconds);
            command.Parameters.AddWithValue("$rapidDistance", analysis.RapidDistanceMillimeters);
            command.Parameters.AddWithValue("$toolChanges", analysis.ToolChangeCount);
            command.Parameters.AddWithValue("$dwell", analysis.DwellSeconds);
            command.Parameters.AddWithValue("$units", (object?)analysis.DetectedUnits ?? DBNull.Value);
            command.Parameters.AddWithValue("$warnings", JsonSerializer.Serialize(analysis.Warnings));
            command.Parameters.AddWithValue("$unsupported", JsonSerializer.Serialize(analysis.UnsupportedConstructs));
            command.Parameters.AddWithValue("$confidence", analysis.Confidence);
            command.Parameters.AddWithValue("$at", Format(analysis.AnalyzedAt));
            await command.ExecuteNonQueryAsync(token);
        }

        await using var machines = connection.CreateCommand();
        machines.Transaction = transaction;
        machines.CommandText = """
            SELECT machines.id, machines.rapid_rate_mm_per_min,
                   machines.tool_change_time_seconds, machines.machine_time_factor
            FROM machines
            JOIN machine_supported_postprocessors compatibility
              ON compatibility.machine_id = machines.id
            WHERE compatibility.postprocessor_id = $postprocessorId
            ORDER BY machines.id;
            """;
        machines.Parameters.AddWithValue("$postprocessorId", release.PostprocessorId);
        var timings = new List<NcMachineTiming>();
        await using (var reader = await machines.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                timings.Add(new NcMachineTiming(
                    reader.GetString(0), NullableDouble(reader, 1), NullableDouble(reader, 2),
                    reader.GetDouble(3)));
            }
        }

        var estimates = new List<NcMachineCycleEstimate>();
        foreach (var timing in timings)
        {
            var estimate = NcCycleTimeEstimator.Evaluate(
                release.GCodeReleaseId, analysis, timing, analysis.AnalyzedAt);
            await InsertEstimateAsync(connection, transaction, estimate, token);
            estimates.Add(estimate);
        }
        return estimates;
    }

    internal static async Task RecalculateForMachineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string machineId,
        DateTimeOffset calculatedAt,
        CancellationToken token)
    {
        NcMachineTiming? timing;
        await using (var machine = connection.CreateCommand())
        {
            machine.Transaction = transaction;
            machine.CommandText = """
                SELECT id, rapid_rate_mm_per_min, tool_change_time_seconds, machine_time_factor
                FROM machines WHERE id = $machineId;
                """;
            machine.Parameters.AddWithValue("$machineId", machineId);
            await using var reader = await machine.ExecuteReaderAsync(token);
            timing = await reader.ReadAsync(token)
                ? new NcMachineTiming(reader.GetString(0), NullableDouble(reader, 1),
                    NullableDouble(reader, 2), reader.GetDouble(3))
                : null;
        }
        if (timing is null) return;

        var analyses = new List<(string ReleaseId, NcProgramAnalysis Analysis)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT analysis.gcode_release_id, analysis.parser_version, analysis.status,
                       analysis.raw_feed_seconds, analysis.rapid_distance_mm,
                       analysis.tool_change_count, analysis.dwell_seconds,
                       analysis.detected_units, analysis.warnings_json,
                       analysis.unsupported_constructs_json, analysis.confidence,
                       analysis.analyzed_at
                FROM gcode_release_analyses analysis
                JOIN gcode_releases release ON release.id = analysis.gcode_release_id
                JOIN machine_supported_postprocessors compatibility
                  ON compatibility.postprocessor_id = release.postprocessor_id
                 AND compatibility.machine_id = $machineId
                ORDER BY analysis.gcode_release_id, analysis.parser_version;
                """;
            command.Parameters.AddWithValue("$machineId", machineId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                analyses.Add((reader.GetString(0), ReadAnalysis(reader, 1)));
            }
        }

        foreach (var value in analyses)
        {
            await InsertEstimateAsync(connection, transaction,
                NcCycleTimeEstimator.Evaluate(
                    value.ReleaseId, value.Analysis, timing, calculatedAt), token);
        }
    }

    internal static async Task<(
        IReadOnlyDictionary<string, NcProgramAnalysis> Analyses,
        IReadOnlyDictionary<string, IReadOnlyList<NcMachineCycleEstimate>> Estimates)>
        ReadForOperationAsync(
            SqliteConnection connection,
            string operationId,
            CancellationToken token)
    {
        var analyses = new Dictionary<string, NcProgramAnalysis>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT analysis.gcode_release_id, analysis.parser_version, analysis.status,
                       analysis.raw_feed_seconds, analysis.rapid_distance_mm,
                       analysis.tool_change_count, analysis.dwell_seconds,
                       analysis.detected_units, analysis.warnings_json,
                       analysis.unsupported_constructs_json, analysis.confidence,
                       analysis.analyzed_at
                FROM gcode_release_analyses analysis
                JOIN gcode_releases release ON release.id = analysis.gcode_release_id
                WHERE release.case_operation_id = $operationId
                  AND analysis.parser_version = $parserVersion;
                """;
            command.Parameters.AddWithValue("$operationId", operationId);
            command.Parameters.AddWithValue("$parserVersion", NcProgramParser.CurrentVersion);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                analyses[reader.GetString(0)] = ReadAnalysis(reader, 1);
            }
        }

        var estimates = new Dictionary<string, List<NcMachineCycleEstimate>>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT estimate.gcode_release_id, estimate.machine_id,
                       estimate.parser_version, estimate.raw_feed_seconds,
                       estimate.rapid_distance_mm, estimate.rapid_seconds,
                       estimate.tool_change_count, estimate.tool_change_seconds,
                       estimate.dwell_seconds, estimate.machine_rapid_rate_mm_per_min,
                       estimate.machine_tool_change_time_seconds,
                       estimate.machine_time_factor, estimate.raw_cycle_seconds,
                       estimate.estimated_cycle_seconds, estimate.warnings_json,
                       estimate.confidence, estimate.calculated_at
                FROM gcode_machine_cycle_estimates estimate
                JOIN gcode_releases release ON release.id = estimate.gcode_release_id
                WHERE release.case_operation_id = $operationId
                ORDER BY estimate.calculated_at DESC, estimate.id DESC;
                """;
            command.Parameters.AddWithValue("$operationId", operationId);
            await using var reader = await command.ExecuteReaderAsync(token);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(token))
            {
                var releaseId = reader.GetString(0);
                var key = $"{releaseId}\0{reader.GetString(1)}\0{reader.GetString(2)}";
                if (!seen.Add(key)) continue;
                if (!estimates.TryGetValue(releaseId, out var values))
                {
                    values = [];
                    estimates.Add(releaseId, values);
                }
                values.Add(ReadEstimate(reader));
            }
        }

        return (analyses, estimates.ToDictionary(
            value => value.Key,
            value => (IReadOnlyList<NcMachineCycleEstimate>)value.Value,
            StringComparer.Ordinal));
    }

    private static async Task InsertEstimateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NcMachineCycleEstimate value,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO gcode_machine_cycle_estimates (
                id, gcode_release_id, machine_id, parser_version,
                raw_feed_seconds, rapid_distance_mm, rapid_seconds,
                tool_change_count, tool_change_seconds, dwell_seconds,
                machine_rapid_rate_mm_per_min, machine_tool_change_time_seconds,
                machine_time_factor, raw_cycle_seconds, estimated_cycle_seconds,
                warnings_json, confidence, calculated_at)
            VALUES ($id, $releaseId, $machineId, $parserVersion, $feed,
                    $rapidDistance, $rapidSeconds, $toolChanges, $toolChangeSeconds,
                    $dwell, $rapidRate, $toolChangeTime, $factor, $raw, $estimated,
                    $warnings, $confidence, $at);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$releaseId", value.GCodeReleaseId);
        command.Parameters.AddWithValue("$machineId", value.MachineId);
        command.Parameters.AddWithValue("$parserVersion", value.ParserVersion);
        command.Parameters.AddWithValue("$feed", value.RawFeedSeconds);
        command.Parameters.AddWithValue("$rapidDistance", value.RapidDistanceMillimeters);
        AddNullable(command, "$rapidSeconds", value.RapidSeconds);
        command.Parameters.AddWithValue("$toolChanges", value.ToolChangeCount);
        AddNullable(command, "$toolChangeSeconds", value.ToolChangeSeconds);
        command.Parameters.AddWithValue("$dwell", value.DwellSeconds);
        AddNullable(command, "$rapidRate", value.MachineRapidRateMillimetersPerMinute);
        AddNullable(command, "$toolChangeTime", value.MachineToolChangeTimeSeconds);
        command.Parameters.AddWithValue("$factor", value.MachineTimeFactor);
        AddNullable(command, "$raw", value.RawCycleSeconds);
        AddNullable(command, "$estimated", value.EstimatedCycleSeconds);
        command.Parameters.AddWithValue("$warnings", JsonSerializer.Serialize(value.Warnings));
        command.Parameters.AddWithValue("$confidence", value.Confidence);
        command.Parameters.AddWithValue("$at", Format(value.CalculatedAt));
        await command.ExecuteNonQueryAsync(token);
    }

    private static NcProgramAnalysis ReadAnalysis(SqliteDataReader reader, int offset) => new(
        reader.GetString(offset), reader.GetString(offset + 1), reader.GetDouble(offset + 2),
        reader.GetDouble(offset + 3), reader.GetInt32(offset + 4), reader.GetDouble(offset + 5),
        reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6),
        JsonSerializer.Deserialize<string[]>(reader.GetString(offset + 7)) ?? [],
        JsonSerializer.Deserialize<string[]>(reader.GetString(offset + 8)) ?? [],
        reader.GetString(offset + 9), Parse(reader.GetString(offset + 10)));

    private static NcMachineCycleEstimate ReadEstimate(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3),
        reader.GetDouble(4), NullableDouble(reader, 5), reader.GetInt32(6),
        NullableDouble(reader, 7), reader.GetDouble(8), NullableDouble(reader, 9),
        NullableDouble(reader, 10), reader.GetDouble(11), NullableDouble(reader, 12),
        NullableDouble(reader, 13),
        JsonSerializer.Deserialize<string[]>(reader.GetString(14)) ?? [],
        reader.GetString(15), Parse(reader.GetString(16)));

    private static double? NullableDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static void AddNullable(SqliteCommand command, string name, double? value) =>
        command.Parameters.AddWithValue(name, value.HasValue ? value.Value : DBNull.Value);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
