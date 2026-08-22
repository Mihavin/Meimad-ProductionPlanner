using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Cnc;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteCncConnectionRepository(SqliteDatabase database) : ICncConnectionRepository
{
    public async Task<IReadOnlyList<MachineConnection>> ListConnectionsAsync(
        bool enabledOnly, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = ConnectionSelect
            + (enabledOnly ? " WHERE enabled = 1" : string.Empty)
            + " ORDER BY machine_id;";
        var values = new List<MachineConnection>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) values.Add(ReadConnection(reader));
        return values;
    }

    public async Task<MachineConnection?> GetConnectionAsync(string machineId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = ConnectionSelect + " WHERE machine_id = $machineId;";
        command.Parameters.AddWithValue("$machineId", machineId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadConnection(reader) : null;
    }

    public async Task<MachineConnection> UpsertConnectionAsync(
        MachineConnection value, int expectedVersion, EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        if (!await MachineExistsAsync(connection, transaction, value.MachineId, token))
            throw new CncValidationException("machineId", "The selected Machine does not exist.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = expectedVersion == 0
            ? """
                INSERT INTO machine_connections (
                    id, machine_id, adapter_type, enabled, connection_status,
                    polling_interval_ms, connection_timeout_ms, maximum_reconnect_backoff_ms,
                    allow_read, allow_write, configuration_json, username_secret_id,
                    password_secret_id, raw_telemetry_retention_days, version, created_at, updated_at)
                VALUES ($id, $machineId, $adapter, $enabled, $status, $polling, $timeout,
                    $backoff, $allowRead, $allowWrite, $configuration, $usernameSecret,
                    $passwordSecret, $retention, 1, $createdAt, $updatedAt)
                ON CONFLICT(machine_id) DO NOTHING;
                """
            : """
                UPDATE machine_connections SET adapter_type = $adapter, enabled = $enabled,
                    connection_status = CASE WHEN $enabled = 0 THEN 'DISABLED' ELSE connection_status END,
                    polling_interval_ms = $polling, connection_timeout_ms = $timeout,
                    maximum_reconnect_backoff_ms = $backoff, allow_read = $allowRead,
                    allow_write = $allowWrite, configuration_json = $configuration,
                    username_secret_id = $usernameSecret, password_secret_id = $passwordSecret,
                    raw_telemetry_retention_days = $retention,
                    version = version + 1, updated_at = $updatedAt
                WHERE machine_id = $machineId AND version = $expectedVersion;
                """;
        AddConnectionParameters(command, value);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new CncConnectionConcurrencyException();
        if (value.AdapterType == CncAdapterType.HaasNgc)
            await UpsertHaasCompatibilityProjectionAsync(connection, transaction, value, token);
        await transaction.CommitAsync(token);
        return (await GetConnectionAsync(value.MachineId, token))!;
    }

    public async Task UpdateConnectionStateAsync(
        string connectionId, string state, DateTimeOffset at, bool successfulPoll,
        string? error, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        string? previous;
        string machineId;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT machine_id, connection_status FROM machine_connections WHERE id = $id;";
            read.Parameters.AddWithValue("$id", connectionId);
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return;
            machineId = reader.GetString(0);
            previous = reader.GetString(1);
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE machine_connections SET connection_status = $state,
                    last_connection_attempt_at = CASE WHEN $state = 'CONNECTING' THEN $at ELSE last_connection_attempt_at END,
                    last_connected_at = CASE WHEN $state IN ('ONLINE', 'DEGRADED') AND connection_status NOT IN ('ONLINE', 'DEGRADED') THEN $at ELSE last_connected_at END,
                    last_disconnected_at = CASE WHEN $state IN ('OFFLINE', 'ERROR') AND connection_status NOT IN ('OFFLINE', 'ERROR') THEN $at ELSE last_disconnected_at END,
                    last_successful_poll_at = CASE WHEN $successfulPoll = 1 THEN $at ELSE last_successful_poll_at END,
                    updated_at = $at
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", connectionId);
            update.Parameters.AddWithValue("$state", state);
            update.Parameters.AddWithValue("$at", Format(at));
            update.Parameters.AddWithValue("$successfulPoll", successfulPoll);
            await update.ExecuteNonQueryAsync(token);
        }
        if (!string.Equals(previous, state, StringComparison.Ordinal))
        {
            await using var append = connection.CreateCommand();
            append.Transaction = transaction;
            append.CommandText = """
                INSERT INTO machine_connection_events
                    (id, connection_id, machine_id, event_type, occurred_at, detail_json)
                VALUES ($eventId, $id, $machineId, 'MachineConnectionChanged', $at, $detail);
                """;
            append.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("N"));
            append.Parameters.AddWithValue("$id", connectionId);
            append.Parameters.AddWithValue("$machineId", machineId);
            append.Parameters.AddWithValue("$at", Format(at));
            append.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(new
            {
                previousStatus = previous,
                currentStatus = state,
                error = Safe(error)
            }, CncJson.Options));
            await append.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    public async Task<MachineSnapshot?> GetCurrentSnapshotAsync(string machineId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM machine_current_state WHERE machine_id = $machineId;";
        command.Parameters.AddWithValue("$machineId", machineId);
        var json = await command.ExecuteScalarAsync(token) as string;
        return json is null ? null : JsonSerializer.Deserialize<MachineSnapshot>(json, CncJson.Options);
    }

    public async Task<bool> SaveSnapshotAsync(
        MachineConnection connectionValue, MachineSnapshot value,
        IReadOnlyList<RawCncTelemetry> rawTelemetry, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        MachineSnapshot? previous = null;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT snapshot_json FROM machine_current_state WHERE machine_id = $machineId;";
            read.Parameters.AddWithValue("$machineId", value.MachineId);
            if (await read.ExecuteScalarAsync(token) is string json)
                previous = JsonSerializer.Deserialize<MachineSnapshot>(json, CncJson.Options);
        }
        var changed = MeaningfullyChanged(previous, value);
        var snapshot = value with { Version = previous?.Version + 1 ?? 1 };
        var snapshotJson = JsonSerializer.Serialize(snapshot, CncJson.Options);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO machine_current_state (
                    machine_id, connection_id, adapter_type, observed_at, connection_status,
                    last_seen_at, machine_state, program_number, program_number_read_at,
                    part_name, part_name_read_at, part_name_stale, header_source_path,
                    header_read_at, production_mode, production_variable_number,
                    production_variable_value, production_variable_read_at, part_counter,
                    part_counter_read_at, spindle_running, spindle_rpm, feed_rate,
                    active_alarm_count, component_health_json, capability_health_json,
                    snapshot_json, last_error, version)
                VALUES ($machineId, $connectionId, $adapter, $at, $status, $lastSeen,
                    $machineState, $program, $programAt, $part, $partAt, $partStale,
                    $headerPath, $headerAt, $mode, $variable, $variableValue, $variableAt,
                    $counter, $counterAt, $spindleRunning, $spindleRpm, $feedRate, $alarms,
                    $components, $capabilities, $snapshot, $error, 1)
                ON CONFLICT(machine_id) DO UPDATE SET
                    connection_id = excluded.connection_id, adapter_type = excluded.adapter_type,
                    observed_at = excluded.observed_at, connection_status = excluded.connection_status,
                    last_seen_at = excluded.last_seen_at, machine_state = excluded.machine_state,
                    program_number = excluded.program_number, program_number_read_at = excluded.program_number_read_at,
                    part_name = excluded.part_name, part_name_read_at = excluded.part_name_read_at,
                    part_name_stale = excluded.part_name_stale, header_source_path = excluded.header_source_path,
                    header_read_at = excluded.header_read_at, production_mode = excluded.production_mode,
                    production_variable_number = excluded.production_variable_number,
                    production_variable_value = excluded.production_variable_value,
                    production_variable_read_at = excluded.production_variable_read_at,
                    part_counter = excluded.part_counter, part_counter_read_at = excluded.part_counter_read_at,
                    spindle_running = excluded.spindle_running, spindle_rpm = excluded.spindle_rpm,
                    feed_rate = excluded.feed_rate, active_alarm_count = excluded.active_alarm_count,
                    component_health_json = excluded.component_health_json,
                    capability_health_json = excluded.capability_health_json,
                    snapshot_json = excluded.snapshot_json, last_error = excluded.last_error,
                    version = machine_current_state.version + 1;
                """;
            AddSnapshotParameters(command, snapshot, snapshotJson);
            await command.ExecuteNonQueryAsync(token);
        }
        if (changed)
        {
            await using var history = connection.CreateCommand();
            history.Transaction = transaction;
            history.CommandText = """
                INSERT INTO machine_state_history
                    (id, machine_id, connection_id, observed_at, change_kind, snapshot_json)
                VALUES ($id, $machineId, $connectionId, $at, 'MEANINGFUL_CHANGE', $snapshot);
                """;
            history.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            history.Parameters.AddWithValue("$machineId", snapshot.MachineId);
            history.Parameters.AddWithValue("$connectionId", snapshot.ConnectionId);
            history.Parameters.AddWithValue("$at", Format(snapshot.Timestamp));
            history.Parameters.AddWithValue("$snapshot", snapshotJson);
            await history.ExecuteNonQueryAsync(token);
        }
        foreach (var raw in rawTelemetry)
        {
            await using var telemetry = connection.CreateCommand();
            telemetry.Transaction = transaction;
            telemetry.CommandText = """
                INSERT INTO machine_telemetry_raw
                    (id, connection_id, machine_id, adapter_type, observed_at, operation, raw_payload)
                VALUES ($id, $connectionId, $machineId, $adapter, $at, $operation, $raw);
                """;
            telemetry.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            telemetry.Parameters.AddWithValue("$connectionId", raw.ConnectionId);
            telemetry.Parameters.AddWithValue("$machineId", raw.MachineId);
            telemetry.Parameters.AddWithValue("$adapter", raw.AdapterType);
            telemetry.Parameters.AddWithValue("$at", Format(raw.Timestamp));
            telemetry.Parameters.AddWithValue("$operation", raw.Operation);
            telemetry.Parameters.AddWithValue("$raw", raw.RawPayload.Length <= 65536 ? raw.RawPayload : raw.RawPayload[..65536]);
            await telemetry.ExecuteNonQueryAsync(token);
        }
        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = """
                DELETE FROM machine_telemetry_raw
                WHERE connection_id = $connectionId
                  AND julianday(observed_at) < julianday($cutoff);
                """;
            prune.Parameters.AddWithValue("$connectionId", value.ConnectionId);
            prune.Parameters.AddWithValue("$cutoff", Format(value.Timestamp.AddDays(-connectionValue.RawTelemetryRetentionDays)));
            await prune.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        return changed;
    }

    public async Task<IReadOnlyList<RawCncTelemetry>> ReadDiagnosticsAsync(
        string machineId, int limit, CancellationToken token)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT machine_id, connection_id, adapter_type, observed_at, operation, raw_payload
            FROM machine_telemetry_raw WHERE machine_id = $machineId
            ORDER BY observed_at DESC, id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$limit", limit);
        var values = new List<RawCncTelemetry>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) values.Add(new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), Parse(reader.GetString(3)),
            reader.GetString(4), reader.GetString(5)));
        return values;
    }

    private static bool MeaningfullyChanged(MachineSnapshot? old, MachineSnapshot value) => old is null
        || old.ConnectionStatus != value.ConnectionStatus
        || old.MachineState.Value != value.MachineState.Value
        || old.Program.ProgramNumber.Value != value.Program.ProgramNumber.Value
        || old.Program.PartName.Value != value.Program.PartName.Value
        || old.Program.PartName.Stale != value.Program.PartName.Stale
        || old.Production.ModeVariableValue.Value != value.Production.ModeVariableValue.Value
        || old.PartCounter.Value != value.PartCounter.Value
        || old.LastError != value.LastError;

    private static void AddConnectionParameters(SqliteCommand command, MachineConnection value)
    {
        command.Parameters.AddWithValue("$id", value.Id);
        command.Parameters.AddWithValue("$machineId", value.MachineId);
        command.Parameters.AddWithValue("$adapter", CncAdapterTypes.Serialize(value.AdapterType));
        command.Parameters.AddWithValue("$enabled", value.Enabled);
        command.Parameters.AddWithValue("$status", value.Enabled ? value.ConnectionStatus : CncConnectionStates.Disabled);
        command.Parameters.AddWithValue("$polling", value.PollingIntervalMs);
        command.Parameters.AddWithValue("$timeout", value.ConnectionTimeoutMs);
        command.Parameters.AddWithValue("$backoff", value.MaximumReconnectBackoffMs);
        command.Parameters.AddWithValue("$allowRead", value.AllowRead);
        command.Parameters.AddWithValue("$allowWrite", value.AllowWrite);
        command.Parameters.AddWithValue("$configuration", value.ConfigurationJson);
        command.Parameters.AddWithValue("$usernameSecret", Db(value.UsernameSecretId));
        command.Parameters.AddWithValue("$passwordSecret", Db(value.PasswordSecretId));
        command.Parameters.AddWithValue("$retention", value.RawTelemetryRetentionDays);
        command.Parameters.AddWithValue("$createdAt", Format(value.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", Format(value.UpdatedAt));
    }

    private static async Task UpsertHaasCompatibilityProjectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MachineConnection value,
        CancellationToken token)
    {
        var configuration = JsonSerializer.Deserialize<HaasNgcConnectionConfiguration>(
            value.ConfigurationJson, CncJson.Options)
            ?? throw new CncValidationException("configuration", "Haas configuration is invalid.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO haas_connection_settings (
                machine_id, host, mdc_port, mtconnect_port, local_net_share_enabled,
                local_net_share_path, credentials_reference, production_mode_variable,
                legacy_variable_alias, part_counter_source, polling_interval_ms,
                connection_timeout_ms, stable_program_polls, header_line_limit,
                header_byte_limit, header_part_patterns_json, enabled, version, created_at, updated_at)
            VALUES ($machineId, $host, $mdcPort, 8082, $shareEnabled, $sharePath,
                $credential, $variable, $legacy, $counterSource, $polling, $timeout,
                $stable, $lineLimit, $byteLimit, $patterns, $enabled, 1, $createdAt, $updatedAt)
            ON CONFLICT(machine_id) DO UPDATE SET
                host = excluded.host, mdc_port = excluded.mdc_port,
                local_net_share_enabled = excluded.local_net_share_enabled,
                local_net_share_path = excluded.local_net_share_path,
                credentials_reference = excluded.credentials_reference,
                production_mode_variable = excluded.production_mode_variable,
                legacy_variable_alias = excluded.legacy_variable_alias,
                part_counter_source = excluded.part_counter_source,
                polling_interval_ms = excluded.polling_interval_ms,
                connection_timeout_ms = excluded.connection_timeout_ms,
                stable_program_polls = excluded.stable_program_polls,
                header_line_limit = excluded.header_line_limit,
                header_byte_limit = excluded.header_byte_limit,
                header_part_patterns_json = excluded.header_part_patterns_json,
                enabled = excluded.enabled, version = haas_connection_settings.version + 1,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$machineId", value.MachineId);
        command.Parameters.AddWithValue("$host", configuration.Host);
        command.Parameters.AddWithValue("$mdcPort", configuration.Mdc.Port);
        command.Parameters.AddWithValue("$shareEnabled", configuration.ProgramAccess.Enabled);
        command.Parameters.AddWithValue("$sharePath", Db(configuration.ProgramAccess.SharePath));
        command.Parameters.AddWithValue("$credential", Db(value.UsernameSecretId));
        command.Parameters.AddWithValue("$variable", configuration.Production.VariableNumber);
        command.Parameters.AddWithValue("$legacy", configuration.Production.LegacyVariableAlias);
        command.Parameters.AddWithValue("$counterSource", configuration.Production.PartCounterSource);
        command.Parameters.AddWithValue("$polling", value.PollingIntervalMs);
        command.Parameters.AddWithValue("$timeout", value.ConnectionTimeoutMs);
        command.Parameters.AddWithValue("$stable", configuration.Monitoring.StableProgramPolls);
        command.Parameters.AddWithValue("$lineLimit", configuration.ProgramAccess.HeaderLineLimit);
        command.Parameters.AddWithValue("$byteLimit", configuration.ProgramAccess.HeaderByteLimit);
        command.Parameters.AddWithValue("$patterns", JsonSerializer.Serialize(
            configuration.ProgramAccess.HeaderPartPatterns, CncJson.Options));
        command.Parameters.AddWithValue("$enabled", value.Enabled);
        command.Parameters.AddWithValue("$createdAt", Format(value.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", Format(value.UpdatedAt));
        await command.ExecuteNonQueryAsync(token);
    }

    private static void AddSnapshotParameters(SqliteCommand command, MachineSnapshot value, string json)
    {
        command.Parameters.AddWithValue("$machineId", value.MachineId);
        command.Parameters.AddWithValue("$connectionId", value.ConnectionId);
        command.Parameters.AddWithValue("$adapter", value.AdapterType);
        command.Parameters.AddWithValue("$at", Format(value.Timestamp));
        command.Parameters.AddWithValue("$status", value.ConnectionStatus);
        command.Parameters.AddWithValue("$lastSeen", Db(Instant(value.LastSeenAt)));
        command.Parameters.AddWithValue("$machineState", Db(value.MachineState.Value));
        command.Parameters.AddWithValue("$program", Db(value.Program.ProgramNumber.Value));
        command.Parameters.AddWithValue("$programAt", Db(Instant(value.Program.ProgramNumber.ReadAt)));
        command.Parameters.AddWithValue("$part", Db(value.Program.PartName.Value));
        command.Parameters.AddWithValue("$partAt", Db(Instant(value.Program.PartName.ReadAt)));
        command.Parameters.AddWithValue("$partStale", value.Program.PartName.Stale);
        command.Parameters.AddWithValue("$headerPath", Db(value.Program.HeaderSourcePath.Value));
        command.Parameters.AddWithValue("$headerAt", Db(Instant(value.Program.HeaderSourcePath.ReadAt)));
        command.Parameters.AddWithValue("$mode", Db(value.Production.Mode));
        command.Parameters.AddWithValue("$variable", Db(value.Production.ModeVariableNumber));
        command.Parameters.AddWithValue("$variableValue", Db(value.Production.ModeVariableValue.Value));
        command.Parameters.AddWithValue("$variableAt", Db(Instant(value.Production.ModeVariableValue.ReadAt)));
        command.Parameters.AddWithValue("$counter", Db(value.PartCounter.Value));
        command.Parameters.AddWithValue("$counterAt", Db(Instant(value.PartCounter.ReadAt)));
        command.Parameters.AddWithValue("$spindleRunning", Db(value.Telemetry.SpindleRunning));
        command.Parameters.AddWithValue("$spindleRpm", Db(value.Telemetry.SpindleRpm));
        command.Parameters.AddWithValue("$feedRate", Db(value.Telemetry.FeedRate));
        command.Parameters.AddWithValue("$alarms", Db(value.Telemetry.ActiveAlarmCount));
        command.Parameters.AddWithValue("$components", JsonSerializer.Serialize(value.ComponentHealth, CncJson.Options));
        command.Parameters.AddWithValue("$capabilities", JsonSerializer.Serialize(value.CapabilityHealth, CncJson.Options));
        command.Parameters.AddWithValue("$snapshot", json);
        command.Parameters.AddWithValue("$error", Db(Safe(value.LastError)));
    }

    private static MachineConnection ReadConnection(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), CncAdapterTypes.Parse(reader.GetString(2)), reader.GetBoolean(3),
        reader.GetString(4), NullableInstant(reader, 5), NullableInstant(reader, 6), NullableInstant(reader, 7),
        NullableInstant(reader, 8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
        reader.GetBoolean(12), reader.GetBoolean(13), reader.GetString(14), NullableString(reader, 15),
        NullableString(reader, 16), reader.GetInt32(17), reader.GetInt32(18),
        Parse(reader.GetString(19)), Parse(reader.GetString(20)));

    private static async Task<bool> MachineExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM machines WHERE id = $id);";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task EnsureEditAuthorityAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        EditAuthority authority, CancellationToken token)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(connection, transaction, DateTimeOffset.UtcNow, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0)
            || reader.GetString(0) != authority.ClientId || reader.GetInt64(1) != authority.Generation)
            throw new EditModeMutationException("edit_generation_stale", "The active edit authority is no longer valid.");
    }

    private const string ConnectionSelect = """
        SELECT id, machine_id, adapter_type, enabled, connection_status,
               last_connection_attempt_at, last_connected_at, last_disconnected_at,
               last_successful_poll_at, polling_interval_ms, connection_timeout_ms,
               maximum_reconnect_backoff_ms, allow_read, allow_write, configuration_json,
               username_secret_id, password_secret_id, raw_telemetry_retention_days,
               version, created_at, updated_at
        FROM machine_connections
        """;

    private static string? Safe(string? value) => value is null ? null : value.Length <= 500 ? value : value[..500];
    private static object Db(object? value) => value ?? DBNull.Value;
    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset? NullableInstant(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));
    private static string? Instant(DateTimeOffset? value) => value is null ? null : Format(value.Value);
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
