using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Domain.Timeline;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>A Case Operation's times and what the NC and the Machines say about them.</summary>
internal sealed record OperationTimeStatistics(
    string CaseId,
    string CaseOperationId,
    int OperationNumber,
    string Name,
    int Version,
    int? SetupSeconds,
    int? CycleSeconds,
    int QaSeconds,
    int LoadUnloadSeconds,
    bool AutomaticLoading,
    int? LoadUnloadEveryNParts,
    bool HasManagedProcess,
    int? RequiredToolCount,
    IReadOnlyList<OperationMachineTimes> Machines,
    IReadOnlyList<OperationTimeSampleView> Samples,
    IReadOnlyList<OperationTimeChange> History);

/// <summary>
/// One Machine's NC and measured times of the Operation. <see cref="SetupApplySeconds"/> is the
/// setup time applying the measured setup writes: the Operation's setup time is the fixture part
/// of the setup, so the tool loading and first piece that the setup estimate adds are taken out.
/// </summary>
internal sealed record OperationMachineTimes(
    string MachineId,
    string MachineNumber,
    string MachineName,
    string? GCodeReleaseId,
    double? NcCycleSeconds,
    double? NcSetupSeconds,
    MeasuredTime? MeasuredCycle,
    MeasuredTime? MeasuredSetup,
    MeasuredTime? MeasuredQa,
    MeasuredTime? MeasuredLoadUnload,
    int? SetupApplySeconds,
    bool LoadingIsPerPart);

internal sealed record OperationTimeSampleView(
    string Kind, string MachineId, double Seconds, DateTimeOffset MeasuredAt, string Source, string? BatchNumber, bool InMedian);

internal sealed record OperationTimeChange(
    string Id,
    string Kind,
    string Source,
    string? MachineId,
    string? MachineNumber,
    int? PreviousSeconds,
    int? NewSeconds,
    double? BasisSeconds,
    int? SampleCount,
    string? GCodeReleaseId,
    string ChangedBy,
    DateTimeOffset ChangedAt);

internal sealed class OperationTimeNotFoundException(string message) : Exception(message);

internal sealed class OperationTimeRequestException(int status, string code, string message) : Exception(message)
{
    internal int Status { get; } = status;

    internal string Code { get; } = code;
}

/// <summary>
/// The operation statistics (owner request 2026-09-29): the Operation's own times, the current NC
/// release's times per Machine and the measured medians per Machine, with the samples behind them
/// and the history of the Operation's times. Applying the NC cycle or a measured time writes it
/// to the Case Operation; pending Work Orders follow, and the change is kept in the history.
/// </summary>
internal sealed class SqliteOperationTimeStatisticsRepository(SqliteDatabase database, SetupEstimationOptions setupEstimation)
{
    /// <summary>The newest samples of one kind on one Machine returned to the statistics window.</summary>
    internal const int SamplesPerKindAndMachine = 50;

    internal async Task<OperationTimeStatistics> ReadAsync(string caseId, string caseOperationId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: true);
        return await ReadAsync(connection, transaction, caseId, caseOperationId, token);
    }

    internal async Task<OperationTimeStatistics> ApplyAsync(
        string caseId, string caseOperationId, string kind, string source, string machineId, int expectedVersion,
        EditAuthority editAuthority, DateTimeOffset now, CancellationToken token)
    {
        if (!OperationTimeKinds.All.Contains(kind))
            throw new OperationTimeRequestException(StatusCodes.Status400BadRequest, "invalid_time_kind",
                "kind must be cycle, setup, qa or load_unload.");
        if (source is not ("NC" or "MEASURED"))
            throw new OperationTimeRequestException(StatusCodes.Status400BadRequest, "invalid_time_source", "source must be NC or MEASURED.");

        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = SignedInActor.Require(editAuthority);
        var statistics = await ReadAsync(connection, transaction, caseId, caseOperationId, token);
        if (statistics.Version != expectedVersion)
        {
            var last = statistics.History.FirstOrDefault();
            throw new OperationTimeRequestException(StatusCodes.Status409Conflict, "resource_version_stale",
                $"Operation {statistics.OperationNumber} changed after you opened its statistics (version {expectedVersion} is now {statistics.Version})"
                + (last is null ? "." : $"; its last time change was by {last.ChangedBy} at {last.ChangedAt:yyyy-MM-dd HH:mm} UTC.")
                + " Reopen the statistics and apply again.");
        }
        var machine = statistics.Machines.FirstOrDefault(value => value.MachineId == machineId)
            ?? throw new OperationTimeRequestException(StatusCodes.Status422UnprocessableEntity, "machine_time_unavailable",
                "This Machine has no NC or measured time for the Operation.");

        int value;
        double basis;
        int? samples = null;
        string? releaseId = null;
        if (source == "NC")
        {
            if (kind != OperationTimeKinds.Cycle || machine.NcCycleSeconds is not { } ncCycle)
                throw new OperationTimeRequestException(StatusCodes.Status422UnprocessableEntity, "nc_time_unavailable",
                    kind == OperationTimeKinds.Cycle
                        ? $"The current NC release has no cycle time for Machine {machine.MachineNumber}."
                        : "The NC gives only the cycle time; the NC setup estimate is built from the Operation's own setup time.");
            basis = ncCycle;
            value = (int)Math.Round(ncCycle, MidpointRounding.AwayFromZero);
            releaseId = machine.GCodeReleaseId;
        }
        else
        {
            var measured = kind switch
            {
                OperationTimeKinds.Cycle => machine.MeasuredCycle,
                OperationTimeKinds.Setup => machine.MeasuredSetup,
                OperationTimeKinds.Qa => machine.MeasuredQa,
                _ => machine.MeasuredLoadUnload
            } ?? throw new OperationTimeRequestException(StatusCodes.Status422UnprocessableEntity, "measured_time_unavailable",
                $"Nothing was measured for this time on Machine {machine.MachineNumber}.");
            if (kind == OperationTimeKinds.LoadUnload && !machine.LoadingIsPerPart)
                throw new OperationTimeRequestException(StatusCodes.Status422UnprocessableEntity, "measured_loading_not_per_part",
                    "The Operation loads automatically or every N parts, so the measured gap between cycles is not its load/unload time.");
            basis = measured.MedianSeconds;
            samples = measured.SampleCount;
            value = kind == OperationTimeKinds.Setup
                ? machine.SetupApplySeconds ?? 0
                : (int)Math.Round(measured.MedianSeconds, MidpointRounding.AwayFromZero);
        }

        var previous = kind switch
        {
            OperationTimeKinds.Cycle => statistics.CycleSeconds,
            OperationTimeKinds.Setup => statistics.SetupSeconds,
            OperationTimeKinds.Qa => statistics.QaSeconds,
            _ => (int?)statistics.LoadUnloadSeconds
        };
        if (previous != value)
        {
            var column = kind switch
            {
                OperationTimeKinds.Cycle => "cycle_seconds",
                OperationTimeKinds.Setup => "setup_seconds",
                OperationTimeKinds.Qa => "qa_seconds",
                _ => "load_unload_seconds"
            };
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = $"""
                    UPDATE case_operations SET {column} = $value, version = version + 1, updated_at = $now
                    WHERE id = $id AND case_id = $caseId AND version = $expectedVersion;
                    """;
                update.Parameters.AddWithValue("$value", value);
                update.Parameters.AddWithValue("$now", Format(now));
                update.Parameters.AddWithValue("$id", caseOperationId);
                update.Parameters.AddWithValue("$caseId", caseId);
                update.Parameters.AddWithValue("$expectedVersion", expectedVersion);
                if (await update.ExecuteNonQueryAsync(token) != 1)
                    throw new OperationTimeRequestException(StatusCodes.Status409Conflict, "resource_version_stale",
                        "The Operation changed while the time was applied. Reopen the statistics and apply again.");
            }
            await RecordChangeAsync(connection, transaction, caseOperationId, kind, source, machineId, previous, value,
                basis, samples, releaseId, actor, now, token);
            // Pending Work Orders take the Case Operation's times; released ones on Refresh from Case.
            await SqliteWorkOrderRouteRefresh.RefreshCaseAsync(connection, transaction, caseId, actor, now, token);
        }
        var result = await ReadAsync(connection, transaction, caseId, caseOperationId, token);
        await transaction.CommitAsync(token);
        return result;
    }

    internal static async Task RecordChangeAsync(
        SqliteConnection connection, SqliteTransaction transaction, string caseOperationId, string kind, string source,
        string? machineId, int? previousSeconds, int? newSeconds, double? basisSeconds, int? sampleCount,
        string? gcodeReleaseId, string changedBy, DateTimeOffset changedAt, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO case_operation_time_changes (
                id, case_operation_id, time_kind, source, machine_id, previous_seconds, new_seconds,
                basis_seconds, sample_count, gcode_release_id, changed_by, changed_at)
            VALUES ($id, $operationId, $kind, $source, $machineId, $previous, $new, $basis, $samples, $releaseId, $by, $at);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$operationId", caseOperationId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$machineId", (object?)machineId ?? DBNull.Value);
        command.Parameters.AddWithValue("$previous", (object?)previousSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$new", (object?)newSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$basis", basisSeconds is { } basis ? Math.Round(basis, 3) : DBNull.Value);
        command.Parameters.AddWithValue("$samples", (object?)sampleCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$releaseId", (object?)gcodeReleaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("$by", changedBy);
        command.Parameters.AddWithValue("$at", Format(changedAt));
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<OperationTimeStatistics> ReadAsync(
        SqliteConnection connection, SqliteTransaction transaction, string caseId, string caseOperationId, CancellationToken token)
    {
        OperationTimeStatistics? operation = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT operation.operation_number, operation.name, operation.version, operation.setup_seconds,
                       operation.cycle_seconds, operation.qa_seconds, operation.load_unload_seconds,
                       operation.automatic_loading, operation.load_unload_every_n_parts,
                       process.id, tools.required_tool_count
                FROM case_operations operation
                LEFT JOIN process_revisions process ON process.case_operation_id = operation.id AND process.is_active = 1
                LEFT JOIN tool_table_releases tools ON tools.id = process.tool_table_release_id
                WHERE operation.id = $id AND operation.case_id = $caseId;
                """;
            command.Parameters.AddWithValue("$id", caseOperationId);
            command.Parameters.AddWithValue("$caseId", caseId);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                operation = new OperationTimeStatistics(
                    caseId, caseOperationId, reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2),
                    NullableInt(reader, 3), NullableInt(reader, 4), reader.GetInt32(5), reader.GetInt32(6),
                    reader.GetInt32(7) == 1, NullableInt(reader, 8), !reader.IsDBNull(9), NullableInt(reader, 10),
                    [], [], []);
            }
        }
        if (operation is null) throw new OperationTimeNotFoundException("The requested Case Operation was not found.");

        var machines = new Dictionary<string, (string Number, string Name)>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id, number, name FROM machines;";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) machines[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
        }

        // The current release of every postprocessor of the active process; the newest estimate per Machine.
        var nc = new Dictionary<string, (string ReleaseId, double Cycle)>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT estimate.machine_id, release.id, estimate.estimated_cycle_seconds
                FROM process_revisions process
                JOIN gcode_releases release ON release.process_revision_id = process.id
                JOIN gcode_machine_cycle_estimates estimate ON estimate.gcode_release_id = release.id
                WHERE process.case_operation_id = $id AND process.is_active = 1
                  AND estimate.estimated_cycle_seconds IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM gcode_releases newer
                      WHERE newer.process_revision_id = release.process_revision_id
                        AND newer.postprocessor_id = release.postprocessor_id
                        AND newer.post_specific_revision > release.post_specific_revision)
                ORDER BY release.created_at DESC, estimate.calculated_at DESC, estimate.id DESC;
                """;
            command.Parameters.AddWithValue("$id", caseOperationId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                nc.TryAdd(reader.GetString(0), (reader.GetString(1), reader.GetDouble(2)));
            }
        }

        var samples = await SqliteOperationTimeMeasurements.ReadSamplesAsync(connection, transaction, caseOperationId, token);
        var medians = SqliteOperationTimeMeasurements.Medians(samples);
        var loadingIsPerPart = SqliteOperationTimeMeasurements.LoadingIsPerPart(operation.AutomaticLoading, operation.LoadUnloadEveryNParts);
        var toolLoading = (operation.RequiredToolCount ?? 0) * setupEstimation.DefaultToolLoadTimePerToolSeconds;
        var machineTimes = nc.Keys.Concat(medians.Keys.Select(key => key.MachineId)).Distinct(StringComparer.Ordinal)
            .Select(machineId =>
            {
                var measured = medians.GetValueOrDefault((caseOperationId, machineId));
                double? ncCycle = nc.TryGetValue(machineId, out var release) ? release.Cycle : null;
                double? ncSetup = ncCycle is null || !operation.HasManagedProcess ? null
                    : SetupOccupancyEstimator.Evaluate(new SetupOccupancyInput(
                        1, operation.RequiredToolCount, operation.SetupSeconds, null, ncCycle, null,
                        setupEstimation.DefaultToolLoadTimePerToolSeconds, setupEstimation.DefaultFirstPieceFactor)).TotalSetupSeconds;
                int? setupApply = null;
                if (measured?.Setup is { } setup)
                {
                    var firstPieceCycle = measured.Cycle?.MedianSeconds ?? ncCycle ?? operation.CycleSeconds ?? 0;
                    setupApply = operation.HasManagedProcess
                        ? (int)Math.Max(0, Math.Round(setup.MedianSeconds - toolLoading - firstPieceCycle * setupEstimation.DefaultFirstPieceFactor, MidpointRounding.AwayFromZero))
                        : (int)Math.Round(setup.MedianSeconds, MidpointRounding.AwayFromZero);
                }
                var names = machines.TryGetValue(machineId, out var known) ? known : (Number: machineId, Name: machineId);
                return new OperationMachineTimes(
                    machineId, names.Number, names.Name, nc.ContainsKey(machineId) ? release.ReleaseId : null,
                    ncCycle, ncSetup, measured?.Cycle, measured?.Setup, measured?.Qa, measured?.LoadUnload,
                    setupApply, loadingIsPerPart);
            })
            .OrderBy(value => value.MachineNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sampleViews = samples
            .GroupBy(sample => (sample.Kind, sample.MachineId))
            .SelectMany(group => group.OrderByDescending(sample => sample.MeasuredAt).Take(SamplesPerKindAndMachine)
                .Select((sample, index) => new OperationTimeSampleView(
                    sample.Kind, sample.MachineId, Math.Round(sample.Seconds, 3), sample.MeasuredAt, sample.Source,
                    sample.BatchNumber, index < SqliteOperationTimeMeasurements.SampleLimit)))
            .OrderByDescending(sample => sample.MeasuredAt)
            .ToArray();

        var history = new List<OperationTimeChange>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, time_kind, source, machine_id, previous_seconds, new_seconds, basis_seconds, sample_count,
                       gcode_release_id, changed_by, changed_at
                FROM case_operation_time_changes
                WHERE case_operation_id = $id
                ORDER BY changed_at DESC, rowid DESC;
                """;
            command.Parameters.AddWithValue("$id", caseOperationId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var machineId = reader.IsDBNull(3) ? null : reader.GetString(3);
                history.Add(new OperationTimeChange(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), machineId,
                    machineId is null ? null : machines.TryGetValue(machineId, out var names) ? names.Number : machineId,
                    NullableInt(reader, 4), NullableInt(reader, 5), reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    NullableInt(reader, 7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9),
                    DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }
        }

        return operation with { Machines = machineTimes, Samples = sampleViews, History = history };
    }

    private static int? NullableInt(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
