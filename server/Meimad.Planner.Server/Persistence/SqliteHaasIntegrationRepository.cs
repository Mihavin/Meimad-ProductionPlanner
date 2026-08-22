using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Domain.Haas;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteHaasIntegrationRepository(SqliteDatabase database) : IHaasIntegrationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<HaasConnectionSettings>> ListEnabledSettingsAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = SettingsSelect + " WHERE enabled = 1 ORDER BY machine_id;";
        var values = new List<HaasConnectionSettings>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) values.Add(ReadSettings(reader));
        return values;
    }

    public async Task<HaasConnectionSettings?> GetSettingsAsync(string machineId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = SettingsSelect + " WHERE machine_id = $machineId;";
        command.Parameters.AddWithValue("$machineId", machineId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadSettings(reader) : null;
    }

    public async Task<HaasConnectionSettings> UpsertSettingsAsync(
        HaasConnectionSettings value, int expectedVersion, EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        if (!await MachineExistsAsync(connection, transaction, value.MachineId, token))
            throw new HaasValidationException("machineId", "The selected Machine does not exist.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (expectedVersion == 0)
        {
            command.CommandText = """
                INSERT INTO haas_connection_settings (
                    machine_id, host, mdc_port, mtconnect_port, local_net_share_enabled,
                    local_net_share_path, credentials_reference, production_mode_variable,
                    legacy_variable_alias, part_counter_source, polling_interval_ms,
                    connection_timeout_ms, stable_program_polls, header_line_limit,
                    header_byte_limit, header_part_patterns_json, enabled, version, created_at, updated_at)
                VALUES ($machineId, $host, $mdcPort, $mtConnectPort, $shareEnabled,
                    $sharePath, $credentials, $variable, $legacy, $counterSource, $polling,
                    $timeout, $stable, $lineLimit, $byteLimit, $patterns, $enabled, 1, $createdAt, $updatedAt)
                ON CONFLICT(machine_id) DO NOTHING;
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE haas_connection_settings SET
                    host = $host, mdc_port = $mdcPort, mtconnect_port = $mtConnectPort,
                    local_net_share_enabled = $shareEnabled, local_net_share_path = $sharePath,
                    credentials_reference = $credentials, production_mode_variable = $variable,
                    legacy_variable_alias = $legacy, part_counter_source = $counterSource,
                    polling_interval_ms = $polling, connection_timeout_ms = $timeout,
                    stable_program_polls = $stable, header_line_limit = $lineLimit,
                    header_byte_limit = $byteLimit, header_part_patterns_json = $patterns,
                    enabled = $enabled, version = version + 1, updated_at = $updatedAt
                WHERE machine_id = $machineId AND version = $expectedVersion;
                """;
            command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        }
        AddSettingsParameters(command, value);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new HaasSettingsConcurrencyException();
        await UpsertGenericConnectionAsync(connection, transaction, value, token);
        await transaction.CommitAsync(token);
        return (await GetSettingsAsync(value.MachineId, token))!;
    }

    public async Task<HaasMachineSnapshot?> GetSnapshotAsync(string machineId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        return await ReadSnapshotAsync(connection, null, machineId, token);
    }

    public async Task SaveSnapshotAsync(HaasMachineSnapshot snapshot, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await UpsertSnapshotAsync(connection, transaction, snapshot, token);
        await transaction.CommitAsync(token);
    }

    public async Task<HaasObservationResult> ApplyObservationAsync(
        HaasMachineSnapshot snapshot, DateTimeOffset observedAt, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var previous = await ReadSnapshotAsync(connection, transaction, snapshot.MachineId, token);
        var active = await ReadActiveBenchAsync(connection, transaction, snapshot.MachineId, token);
        var events = new List<string>();
        var startedNow = false;

        if (snapshot.ConnectivityState == HaasConnectivityStates.Online
            && previous is not null
            && !string.Equals(previous.ProgramNumber, snapshot.ProgramNumber, StringComparison.Ordinal))
        {
            await AppendEventAsync(connection, transaction, "ProgramChanged", snapshot.MachineId,
                active?.BenchId, observedAt,
                new { previousProgramNumber = previous.ProgramNumber, currentProgramNumber = snapshot.ProgramNumber },
                $"program-changed:{snapshot.MachineId}:{previous.ProgramNumber}:{snapshot.ProgramNumber}:{observedAt:O}", token);
            events.Add("ProgramChanged");
        }

        if (active is not null && await OperationCompletedAsync(connection, transaction, active.BatchOperationId, token))
        {
            await CloseOpenIntervalAsync(connection, transaction, active.BenchId, observedAt, token);
            await UpdateBenchCompletedAsync(connection, transaction, active.BenchId, observedAt, token);
            await AppendEventAsync(connection, transaction, "BenchCompleted", snapshot.MachineId,
                active.BenchId, observedAt, new { active.BatchOperationId }, $"bench-completed:{active.BenchId}", token);
            events.Add("BenchCompleted");
            active = null;
        }

        var match = "MONITORED";
        if (snapshot.ConnectivityState == HaasConnectivityStates.Online
            && !string.IsNullOrWhiteSpace(snapshot.MachineHeaderPartName)
            && !string.IsNullOrWhiteSpace(snapshot.ProgramNumber))
        {
            if (active is null)
            {
                var candidates = await FindCandidatesAsync(connection, transaction,
                    snapshot.MachineId, snapshot.MachineHeaderPartName, token);
                if (candidates.Count == 1)
                {
                    active = await StartBenchAsync(connection, transaction, snapshot,
                        candidates[0], observedAt, token);
                    startedNow = true;
                    events.Add("BenchAutoStarted");
                    match = "UNIQUE_MATCH";
                }
                else
                {
                    match = candidates.Count == 0 ? "NO_MATCH" : "AMBIGUOUS_MATCH";
                    var eventType = candidates.Count == 0 ? "MachineRunningUnplannedPart" : "AmbiguousBenchMatch";
                    await AppendEventAsync(connection, transaction, eventType, snapshot.MachineId,
                        null, observedAt, new { snapshot.MachineHeaderPartName, candidateCount = candidates.Count },
                        $"{eventType}:{snapshot.MachineId}:{snapshot.ProgramNumber}:{snapshot.MachineHeaderPartName}", token);
                    events.Add(eventType);
                }
            }
            else if (!string.Equals(active.MachinePartName, snapshot.MachineHeaderPartName,
                         StringComparison.OrdinalIgnoreCase))
            {
                match = "ACTIVE_MISMATCH";
                await AppendEventAsync(connection, transaction, "MachineProgramMismatch", snapshot.MachineId,
                    active.BenchId, observedAt,
                    new { previousPartName = active.MachinePartName, currentPartName = snapshot.MachineHeaderPartName,
                        snapshot.ProgramNumber },
                    $"program-mismatch:{active.BenchId}:{snapshot.ProgramNumber}:{snapshot.MachineHeaderPartName}", token);
                events.Add("MachineProgramMismatch");
            }
        }

        if (active is not null && !startedNow && previous is not null
            && snapshot.ConnectivityState == HaasConnectivityStates.Online)
        {
            if (previous.ProductionVariableValue == 0 && snapshot.ProductionVariableValue == 1
                && active.State == HaasBenchStates.Setup)
            {
                active = await EnterProductionAsync(connection, transaction, active, snapshot, observedAt, token);
                events.Add("BenchProductionStarted");
            }
            else if (previous.ProductionVariableValue == 1 && snapshot.ProductionVariableValue == 0
                     && active.State == HaasBenchStates.Production)
            {
                var confirmed = await HasConfirmedToolResetAsync(connection, transaction,
                    snapshot.MachineId, active.BenchId, previous.Timestamp, observedAt, token);
                active = await EnterSetupAsync(connection, transaction, active, observedAt,
                    confirmed ? "TOOL_TABLE_LOADED" : "UNEXPECTED_VARIABLE_RESET", token);
                var eventType = confirmed ? "ProductionVariableReset" : "UnexpectedProductionVariableReset";
                await AppendEventAsync(connection, transaction, eventType, snapshot.MachineId,
                    active.BenchId, observedAt,
                    new { variableNumber = snapshot.ProductionVariableNumber, previousValue = 1, newValue = 0 },
                    $"{eventType}:{active.BenchId}:{observedAt:O}", token);
                events.Add(eventType);
            }

            if (active.State == HaasBenchStates.Production && active.PartCountingEnabled
                && snapshot.PartCounter is { } counter && active.PreviousPartCounter is { } prior)
            {
                if (counter > prior)
                {
                    var delta = counter - prior;
                    active = await CreditPartsAsync(connection, transaction, active, counter, delta, observedAt, token);
                    await AppendEventAsync(connection, transaction, "PartCompleted", snapshot.MachineId,
                        active.BenchId, observedAt, new { counterBefore = prior, counterAfter = counter, quantityDelta = delta },
                        $"part-completed:{active.BenchId}:{prior}:{counter}", token);
                    events.Add("PartCompleted");
                }
                else if (counter < prior)
                {
                    active = await ResetCounterAsync(connection, transaction, active, counter, observedAt, token);
                    await AppendEventAsync(connection, transaction, "PartCounterReset", snapshot.MachineId,
                        active.BenchId, observedAt, new { counterBefore = prior, counterAfter = counter },
                        $"part-counter-reset:{active.BenchId}:{observedAt:O}", token);
                    events.Add("PartCounterReset");
                }
            }
        }

        await UpsertSnapshotAsync(connection, transaction, snapshot with
        {
            ProductionVariableChangedAt = previous is null
                || previous.ProductionVariableValue != snapshot.ProductionVariableValue
                    ? observedAt : previous.ProductionVariableChangedAt
        }, token);
        await transaction.CommitAsync(token);
        return new HaasObservationResult(match, active, events);
    }

    public async Task<HaasMachineMonitor?> ReadMonitorAsync(
        string machineId, DateTimeOffset now, CancellationToken token)
    {
        var settings = await GetSettingsAsync(machineId, token);
        if (settings is null) return null;
        await using var connection = await database.OpenConnectionAsync(token);
        var snapshot = await ReadSnapshotAsync(connection, null, machineId, token);
        var bench = await ReadLatestBenchAsync(connection, machineId, token);
        var intervals = bench is null ? [] : await ReadIntervalsAsync(connection, bench.BenchId, token);
        var events = await ReadEventsAsync(connection, machineId, token);
        double setup = 0, production = 0;
        foreach (var interval in intervals)
        {
            var seconds = Math.Max(0, ((interval.EndedAt ?? now) - interval.StartedAt).TotalSeconds);
            if (interval.State == HaasBenchStates.Setup) setup += seconds;
            else production += seconds;
        }
        return new HaasMachineMonitor(settings, snapshot, bench, intervals, events, setup, production);
    }

    public async Task<string> BeginMacroWriteAuditAsync(
        string machineId, string? benchId, string? toolTableId, int variableNumber,
        int? oldValue, int newValue, string reason, string initiatedBy, DateTimeOffset at,
        CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO haas_macro_write_audits
                (id, machine_id, bench_id, tool_table_id, variable_number, old_value,
                 new_value, reason, initiated_by, requested_at, status)
            VALUES ($id, $machineId, $benchId, $toolTableId, $variable, $oldValue,
                    $newValue, $reason, $initiatedBy, $at, 'PENDING');
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$benchId", Db(benchId));
        command.Parameters.AddWithValue("$toolTableId", Db(toolTableId));
        command.Parameters.AddWithValue("$variable", variableNumber);
        command.Parameters.AddWithValue("$oldValue", Db(oldValue));
        command.Parameters.AddWithValue("$newValue", newValue);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$initiatedBy", initiatedBy);
        command.Parameters.AddWithValue("$at", Format(at));
        await command.ExecuteNonQueryAsync(token);
        return id;
    }

    public async Task CompleteMacroWriteAuditAsync(
        string auditId, bool succeeded, string? rawResponse, string? errorMessage,
        DateTimeOffset at, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE haas_macro_write_audits
            SET completed_at = $at, status = $status, raw_haas_response = $raw,
                error_message = $error
            WHERE id = $id AND status = 'PENDING';
            """;
        command.Parameters.AddWithValue("$id", auditId);
        command.Parameters.AddWithValue("$at", Format(at));
        command.Parameters.AddWithValue("$status", succeeded ? "SUCCEEDED" : "FAILED");
        command.Parameters.AddWithValue("$raw", Db(rawResponse));
        command.Parameters.AddWithValue("$error", Db(errorMessage));
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new InvalidOperationException("The Haas write audit was missing or already completed.");
    }

    private static async Task<HaasBenchSession> StartBenchAsync(
        SqliteConnection connection, SqliteTransaction transaction, HaasMachineSnapshot snapshot,
        string operationId, DateTimeOffset at, CancellationToken token)
    {
        var benchId = Guid.NewGuid().ToString("N");
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO haas_bench_sessions
                    (id, batch_operation_id, machine_id, state, auto_start_source,
                     machine_program_number, machine_part_name, setup_started_at,
                     part_counting_enabled, produced_quantity, version, created_at, updated_at)
                VALUES ($id, $operationId, $machineId, 'SETUP', 'CNC_HEADER',
                        $program, $part, $at, 0, 0, 1, $at, $at);
                UPDATE batch_operations
                SET status = CASE WHEN status = 'not_started' THEN 'in_progress' ELSE status END,
                    actual_start = COALESCE(actual_start, $at),
                    actual_machine_id = COALESCE(actual_machine_id, $machineId),
                    version = version + 1, updated_at = $at
                WHERE id = $operationId AND status NOT IN ('completed', 'cancelled');
                """;
            command.Parameters.AddWithValue("$id", benchId);
            command.Parameters.AddWithValue("$operationId", operationId);
            command.Parameters.AddWithValue("$machineId", snapshot.MachineId);
            command.Parameters.AddWithValue("$program", snapshot.ProgramNumber!);
            command.Parameters.AddWithValue("$part", snapshot.MachineHeaderPartName!);
            command.Parameters.AddWithValue("$at", Format(at));
            await command.ExecuteNonQueryAsync(token);
        }
        await InsertIntervalAsync(connection, transaction, benchId, HaasBenchStates.Setup, at, "CNC_HEADER", token);
        await AppendEventAsync(connection, transaction, "BenchAutoStarted", snapshot.MachineId,
            benchId, at, new { batchOperationId = operationId, snapshot.ProgramNumber,
                partName = snapshot.MachineHeaderPartName, autoStartSource = "CNC_HEADER" },
            $"bench-auto-started:{operationId}", token);
        return (await ReadActiveBenchAsync(connection, transaction, snapshot.MachineId, token))!;
    }

    private static async Task<HaasBenchSession> EnterProductionAsync(
        SqliteConnection connection, SqliteTransaction transaction, HaasBenchSession bench,
        HaasMachineSnapshot snapshot, DateTimeOffset at, CancellationToken token)
    {
        await CloseOpenIntervalAsync(connection, transaction, bench.BenchId, at, token);
        await InsertIntervalAsync(connection, transaction, bench.BenchId, HaasBenchStates.Production, at,
            "PRODUCTION_VARIABLE_0_TO_1", token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE haas_bench_sessions SET state = 'PRODUCTION', setup_ended_at = $at,
                production_started_at = COALESCE(production_started_at, $at),
                part_counting_enabled = 1, part_counter_baseline = $counter,
                previous_part_counter = $counter, version = version + 1, updated_at = $at
            WHERE id = $id AND state = 'SETUP';
            """;
        command.Parameters.AddWithValue("$id", bench.BenchId);
        command.Parameters.AddWithValue("$at", Format(at));
        command.Parameters.AddWithValue("$counter", Db(snapshot.PartCounter));
        await command.ExecuteNonQueryAsync(token);
        await AppendEventAsync(connection, transaction, "BenchProductionStarted", bench.MachineId,
            bench.BenchId, at,
            new { benchId = bench.BenchId, machineId = bench.MachineId, timestamp = at,
                macroVariableNumber = snapshot.ProductionVariableNumber, previousValue = 0, newValue = 1,
                partCounterBaseline = snapshot.PartCounter },
            $"bench-production-started:{bench.BenchId}:{at:O}", token);
        return (await ReadActiveBenchAsync(connection, transaction, bench.MachineId, token))!;
    }

    private static async Task<HaasBenchSession> EnterSetupAsync(
        SqliteConnection connection, SqliteTransaction transaction, HaasBenchSession bench,
        DateTimeOffset at, string source, CancellationToken token)
    {
        await CloseOpenIntervalAsync(connection, transaction, bench.BenchId, at, token);
        await InsertIntervalAsync(connection, transaction, bench.BenchId, HaasBenchStates.Setup, at, source, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE haas_bench_sessions SET state = 'SETUP', setup_started_at = $at,
                setup_ended_at = NULL, part_counting_enabled = 0,
                part_counter_baseline = NULL, previous_part_counter = NULL,
                version = version + 1, updated_at = $at WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", bench.BenchId);
        command.Parameters.AddWithValue("$at", Format(at));
        await command.ExecuteNonQueryAsync(token);
        return (await ReadActiveBenchAsync(connection, transaction, bench.MachineId, token))!;
    }

    private static async Task<HaasBenchSession> CreditPartsAsync(
        SqliteConnection connection, SqliteTransaction transaction, HaasBenchSession bench,
        int counter, int delta, DateTimeOffset at, CancellationToken token) =>
        await UpdateCounterAsync(connection, transaction, bench, counter, delta, at, token);

    private static async Task<HaasBenchSession> ResetCounterAsync(
        SqliteConnection connection, SqliteTransaction transaction, HaasBenchSession bench,
        int counter, DateTimeOffset at, CancellationToken token) =>
        await UpdateCounterAsync(connection, transaction, bench, counter, 0, at, token);

    private static async Task<HaasBenchSession> UpdateCounterAsync(
        SqliteConnection connection, SqliteTransaction transaction, HaasBenchSession bench,
        int counter, int delta, DateTimeOffset at, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE haas_bench_sessions SET previous_part_counter = $counter,
                part_counter_baseline = CASE WHEN $delta = 0 THEN $counter ELSE part_counter_baseline END,
                produced_quantity = produced_quantity + $delta,
                version = version + 1, updated_at = $at WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", bench.BenchId);
        command.Parameters.AddWithValue("$counter", counter);
        command.Parameters.AddWithValue("$delta", delta);
        command.Parameters.AddWithValue("$at", Format(at));
        await command.ExecuteNonQueryAsync(token);
        return (await ReadActiveBenchAsync(connection, transaction, bench.MachineId, token))!;
    }

    private static async Task UpsertSnapshotAsync(
        SqliteConnection connection, SqliteTransaction transaction, HaasMachineSnapshot value,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO haas_machine_snapshots
                (machine_id, observed_at, connectivity_state, machine_status, program_number,
                 machine_header_part_name, machine_header_source_path, header_read_at,
                 production_variable_number, production_variable_value,
                 production_variable_changed_at, part_counter, raw_mdc_status, last_error,
                 last_seen_at, version)
            VALUES ($machineId, $at, $connectivity, $status, $program, $part, $path,
                    $headerAt, $variable, $value, $changedAt, $counter, $raw, $error,
                    $lastSeen, 1)
            ON CONFLICT(machine_id) DO UPDATE SET
                observed_at = excluded.observed_at, connectivity_state = excluded.connectivity_state,
                machine_status = excluded.machine_status, program_number = excluded.program_number,
                machine_header_part_name = excluded.machine_header_part_name,
                machine_header_source_path = excluded.machine_header_source_path,
                header_read_at = excluded.header_read_at,
                production_variable_number = excluded.production_variable_number,
                production_variable_value = excluded.production_variable_value,
                production_variable_changed_at = excluded.production_variable_changed_at,
                part_counter = excluded.part_counter, raw_mdc_status = excluded.raw_mdc_status,
                last_error = excluded.last_error, last_seen_at = excluded.last_seen_at,
                version = haas_machine_snapshots.version + 1;
            """;
        command.Parameters.AddWithValue("$machineId", value.MachineId);
        command.Parameters.AddWithValue("$at", Format(value.Timestamp));
        command.Parameters.AddWithValue("$connectivity", value.ConnectivityState);
        command.Parameters.AddWithValue("$status", Db(value.MachineStatus));
        command.Parameters.AddWithValue("$program", Db(value.ProgramNumber));
        command.Parameters.AddWithValue("$part", Db(value.MachineHeaderPartName));
        command.Parameters.AddWithValue("$path", Db(value.MachineHeaderSourcePath));
        command.Parameters.AddWithValue("$headerAt", Db(value.HeaderReadAt is { } h ? Format(h) : null));
        command.Parameters.AddWithValue("$variable", value.ProductionVariableNumber);
        command.Parameters.AddWithValue("$value", value.ProductionVariableValue);
        command.Parameters.AddWithValue("$changedAt", Db(value.ProductionVariableChangedAt is { } c ? Format(c) : null));
        command.Parameters.AddWithValue("$counter", Db(value.PartCounter));
        command.Parameters.AddWithValue("$raw", Db(value.RawMdcStatus));
        command.Parameters.AddWithValue("$error", Db(value.LastError));
        command.Parameters.AddWithValue("$lastSeen", Db(value.LastSeenAt is { } l ? Format(l) : null));
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<HaasMachineSnapshot?> ReadSnapshotAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string machineId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT machine_id, observed_at, connectivity_state, machine_status, program_number,
                   machine_header_part_name, machine_header_source_path, header_read_at,
                   production_variable_number, production_variable_value,
                   production_variable_changed_at, part_counter, raw_mdc_status, last_error,
                   last_seen_at, version
            FROM haas_machine_snapshots WHERE machine_id = $machineId;
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new HaasMachineSnapshot(reader.GetString(0), Parse(reader.GetString(1)), reader.GetString(2),
            NullableString(reader, 3), NullableString(reader, 4), NullableString(reader, 5), NullableString(reader, 6),
            NullableInstant(reader, 7), reader.GetInt32(8), reader.GetInt32(9), NullableInstant(reader, 10),
            reader.IsDBNull(11) ? null : reader.GetInt32(11), NullableString(reader, 12), NullableString(reader, 13),
            NullableInstant(reader, 14), reader.GetInt32(15));
    }

    private static async Task<HaasBenchSession?> ReadActiveBenchAsync(
        SqliteConnection connection, SqliteTransaction transaction, string machineId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BenchSelect + " WHERE machine_id = $machineId AND state IN ('SETUP', 'PRODUCTION') ORDER BY created_at DESC LIMIT 1;";
        command.Parameters.AddWithValue("$machineId", machineId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadBench(reader) : null;
    }

    private static async Task<HaasBenchSession?> ReadLatestBenchAsync(
        SqliteConnection connection, string machineId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = BenchSelect + " WHERE machine_id = $machineId ORDER BY created_at DESC LIMIT 1;";
        command.Parameters.AddWithValue("$machineId", machineId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadBench(reader) : null;
    }

    private static HaasBenchSession ReadBench(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), Parse(reader.GetString(6)), NullableInstant(reader, 7),
        NullableInstant(reader, 8), reader.GetBoolean(9), reader.IsDBNull(10) ? null : reader.GetInt32(10),
        reader.IsDBNull(11) ? null : reader.GetInt32(11), reader.GetInt32(12), NullableInstant(reader, 13),
        reader.GetInt32(14), Parse(reader.GetString(15)), Parse(reader.GetString(16)));

    private static async Task<IReadOnlyList<string>> FindCandidatesAsync(
        SqliteConnection connection, SqliteTransaction transaction, string machineId,
        string partName, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation.id
            FROM machine_assignments assignment
            JOIN batch_operations operation ON operation.id = assignment.batch_operation_id
            JOIN production_batches batch ON batch.id = operation.production_batch_id
            JOIN cases part ON part.id = batch.case_id
            WHERE assignment.machine_id = $machineId
              AND operation.status NOT IN ('completed', 'cancelled')
              AND (
                    lower(trim(part.part_number)) = lower(trim($partName))
                    OR EXISTS (
                        SELECT 1
                        FROM process_revisions process
                        JOIN gcode_releases release ON release.process_revision_id = process.id
                        JOIN nc_program_headers header ON header.gcode_release_id = release.id
                        WHERE process.case_operation_id = operation.source_case_operation_id
                          AND process.is_active = 1
                          AND header.status = 'VALID'
                          AND lower(trim(header.part_name)) = lower(trim($partName))
                          AND NOT EXISTS (
                              SELECT 1 FROM gcode_releases newer
                              WHERE newer.process_revision_id = release.process_revision_id
                                AND newer.postprocessor_id = release.postprocessor_id
                                AND newer.post_specific_revision > release.post_specific_revision)
                    )
                  )
            ORDER BY assignment.backlog_position, operation.id;
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$partName", partName);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task AppendEventAsync(
        SqliteConnection connection, SqliteTransaction transaction, string eventType,
        string machineId, string? benchId, DateTimeOffset at, object payload,
        string dedupeKey, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO haas_events
                (id, event_type, machine_id, bench_id, occurred_at, payload_json, dedupe_key)
            VALUES ($id, $type, $machineId, $benchId, $at, $payload, $dedupe)
            ON CONFLICT(dedupe_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$type", eventType);
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$benchId", Db(benchId));
        command.Parameters.AddWithValue("$at", Format(at));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload, JsonOptions));
        command.Parameters.AddWithValue("$dedupe", dedupeKey);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task InsertIntervalAsync(
        SqliteConnection connection, SqliteTransaction transaction, string benchId,
        string state, DateTimeOffset at, string source, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO haas_bench_state_intervals (id, bench_id, state, started_at, source) VALUES ($id, $benchId, $state, $at, $source);";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$benchId", benchId);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$at", Format(at));
        command.Parameters.AddWithValue("$source", source);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task CloseOpenIntervalAsync(
        SqliteConnection connection, SqliteTransaction transaction, string benchId,
        DateTimeOffset at, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE haas_bench_state_intervals SET ended_at = $at WHERE bench_id = $benchId AND ended_at IS NULL;";
        command.Parameters.AddWithValue("$benchId", benchId);
        command.Parameters.AddWithValue("$at", Format(at));
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<bool> HasConfirmedToolResetAsync(
        SqliteConnection connection, SqliteTransaction transaction, string machineId,
        string benchId, DateTimeOffset after, DateTimeOffset before, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM haas_macro_write_audits
                WHERE machine_id = $machineId AND bench_id = $benchId
                  AND reason = 'TOOL_TABLE_LOADED' AND new_value = 0 AND status = 'SUCCEEDED'
                  AND julianday(completed_at) >= julianday($after)
                  AND julianday(completed_at) <= julianday($before));
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$benchId", benchId);
        command.Parameters.AddWithValue("$after", Format(after));
        command.Parameters.AddWithValue("$before", Format(before));
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> OperationCompletedAsync(
        SqliteConnection connection, SqliteTransaction transaction, string operationId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT status = 'completed' FROM batch_operations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", operationId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token) ?? 0, CultureInfo.InvariantCulture) == 1;
    }

    private static async Task UpdateBenchCompletedAsync(
        SqliteConnection connection, SqliteTransaction transaction, string benchId, DateTimeOffset at, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE haas_bench_sessions SET state = 'COMPLETED', completed_at = $at, part_counting_enabled = 0, version = version + 1, updated_at = $at WHERE id = $id;";
        command.Parameters.AddWithValue("$id", benchId);
        command.Parameters.AddWithValue("$at", Format(at));
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<IReadOnlyList<HaasBenchStateInterval>> ReadIntervalsAsync(
        SqliteConnection connection, string benchId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, bench_id, state, started_at, ended_at, source FROM haas_bench_state_intervals WHERE bench_id = $id ORDER BY started_at, id;";
        command.Parameters.AddWithValue("$id", benchId);
        var values = new List<HaasBenchStateInterval>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) values.Add(new HaasBenchStateInterval(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), Parse(reader.GetString(3)),
            NullableInstant(reader, 4), reader.GetString(5)));
        return values;
    }

    private static async Task<IReadOnlyList<HaasEvent>> ReadEventsAsync(
        SqliteConnection connection, string machineId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_type, machine_id, bench_id, occurred_at, payload_json, dedupe_key FROM haas_events WHERE machine_id = $machineId ORDER BY occurred_at DESC, id DESC LIMIT 100;";
        command.Parameters.AddWithValue("$machineId", machineId);
        var values = new List<HaasEvent>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) values.Add(new HaasEvent(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), NullableString(reader, 3), Parse(reader.GetString(4)), reader.GetString(5), reader.GetString(6)));
        return values;
    }

    private static async Task<bool> MachineExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM machines WHERE id = $id);";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task EnsureEditAuthorityAsync(SqliteConnection connection, SqliteTransaction transaction, EditAuthority authority, CancellationToken token)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(connection, transaction, DateTimeOffset.UtcNow, token);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0)
            || reader.GetString(0) != authority.ClientId || reader.GetInt64(1) != authority.Generation)
            throw new EditModeMutationException("edit_generation_stale", "The active edit authority is no longer valid.");
    }

    private static void AddSettingsParameters(SqliteCommand command, HaasConnectionSettings value)
    {
        command.Parameters.AddWithValue("$machineId", value.MachineId);
        command.Parameters.AddWithValue("$host", value.Host);
        command.Parameters.AddWithValue("$mdcPort", value.MdcPort);
        command.Parameters.AddWithValue("$mtConnectPort", value.MtConnectPort);
        command.Parameters.AddWithValue("$shareEnabled", value.LocalNetShareEnabled);
        command.Parameters.AddWithValue("$sharePath", Db(value.LocalNetSharePath));
        command.Parameters.AddWithValue("$credentials", Db(value.CredentialsReference));
        command.Parameters.AddWithValue("$variable", value.ProductionModeVariable);
        command.Parameters.AddWithValue("$legacy", value.LegacyVariableAlias);
        command.Parameters.AddWithValue("$counterSource", value.PartCounterSource);
        command.Parameters.AddWithValue("$polling", value.PollingIntervalMs);
        command.Parameters.AddWithValue("$timeout", value.ConnectionTimeoutMs);
        command.Parameters.AddWithValue("$stable", value.StableProgramPolls);
        command.Parameters.AddWithValue("$lineLimit", value.HeaderLineLimit);
        command.Parameters.AddWithValue("$byteLimit", value.HeaderByteLimit);
        command.Parameters.AddWithValue("$patterns", JsonSerializer.Serialize(value.HeaderPartPatterns, JsonOptions));
        command.Parameters.AddWithValue("$enabled", value.Enabled);
        command.Parameters.AddWithValue("$createdAt", Format(value.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", Format(value.UpdatedAt));
    }

    private static async Task UpsertGenericConnectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HaasConnectionSettings value,
        CancellationToken token)
    {
        var configuration = new HaasNgcConnectionConfiguration(
            value.Host,
            new HaasMdcConfiguration(value.MdcPort, value.ConnectionTimeoutMs),
            new HaasProgramAccessConfiguration(
                value.LocalNetShareEnabled ? "HAAS_LOCAL_NET_SHARE" : "NONE",
                value.LocalNetShareEnabled,
                value.LocalNetSharePath,
                value.CredentialsReference,
                null,
                value.HeaderLineLimit,
                value.HeaderByteLimit,
                value.HeaderPartPatterns),
            new HaasProductionConfiguration(
                value.ProductionModeVariable,
                value.LegacyVariableAlias,
                value.PartCounterSource),
            new HaasMonitoringConfiguration(
                value.PollingIntervalMs,
                value.StableProgramPolls,
                30000,
                14));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO machine_connections (
                id, machine_id, adapter_type, enabled, connection_status,
                polling_interval_ms, connection_timeout_ms, maximum_reconnect_backoff_ms,
                allow_read, allow_write, configuration_json, username_secret_id,
                password_secret_id, raw_telemetry_retention_days, version, created_at, updated_at)
            VALUES ($id, $machineId, 'HAAS_NGC', $enabled, $status,
                $polling, $timeout, 30000, 1, 1, $configuration, $credential,
                NULL, 14, 1, $createdAt, $updatedAt)
            ON CONFLICT(machine_id) DO UPDATE SET
                adapter_type = 'HAAS_NGC', enabled = excluded.enabled,
                connection_status = CASE WHEN excluded.enabled = 0 THEN 'DISABLED' ELSE machine_connections.connection_status END,
                polling_interval_ms = excluded.polling_interval_ms,
                connection_timeout_ms = excluded.connection_timeout_ms,
                configuration_json = excluded.configuration_json,
                username_secret_id = excluded.username_secret_id,
                allow_read = 1, allow_write = 1,
                version = machine_connections.version + 1,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", $"cnc-{value.MachineId}");
        command.Parameters.AddWithValue("$machineId", value.MachineId);
        command.Parameters.AddWithValue("$enabled", value.Enabled);
        command.Parameters.AddWithValue("$status", value.Enabled ? CncConnectionStates.Offline : CncConnectionStates.Disabled);
        command.Parameters.AddWithValue("$polling", value.PollingIntervalMs);
        command.Parameters.AddWithValue("$timeout", value.ConnectionTimeoutMs);
        command.Parameters.AddWithValue("$configuration", JsonSerializer.Serialize(configuration, CncJson.Options));
        command.Parameters.AddWithValue("$credential", Db(value.CredentialsReference));
        command.Parameters.AddWithValue("$createdAt", Format(value.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", Format(value.UpdatedAt));
        await command.ExecuteNonQueryAsync(token);
    }

    private static HaasConnectionSettings ReadSettings(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetBoolean(4),
        NullableString(reader, 5), NullableString(reader, 6), reader.GetInt32(7), reader.GetInt32(8), reader.GetString(9),
        reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14),
        JsonSerializer.Deserialize<string[]>(reader.GetString(15), JsonOptions) ?? [], reader.GetBoolean(16),
        reader.GetInt32(17), Parse(reader.GetString(18)), Parse(reader.GetString(19)));

    private const string SettingsSelect = """
        SELECT machine_id, host, mdc_port, mtconnect_port, local_net_share_enabled,
               local_net_share_path, credentials_reference, production_mode_variable,
               legacy_variable_alias, part_counter_source, polling_interval_ms,
               connection_timeout_ms, stable_program_polls, header_line_limit,
               header_byte_limit, header_part_patterns_json, enabled, version, created_at, updated_at
        FROM haas_connection_settings
        """;
    private const string BenchSelect = """
        SELECT id, batch_operation_id, machine_id, state, machine_program_number,
               machine_part_name, setup_started_at, setup_ended_at, production_started_at,
               part_counting_enabled, part_counter_baseline, previous_part_counter,
               produced_quantity, completed_at, version, created_at, updated_at
        FROM haas_bench_sessions
        """;

    private static object Db(object? value) => value ?? DBNull.Value;
    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset? NullableInstant(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
