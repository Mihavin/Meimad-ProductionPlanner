using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Cnc;

public sealed class CncPlatformTests
{
    [Fact]
    public async Task Current_state_is_upserted_identical_polls_do_not_create_history_and_raw_retention_is_bounded()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var connection = await SeedConnectionAsync(fixture.Database);
        var repository = new SqliteCncConnectionRepository(fixture.Database);
        var snapshot = FakeCncMachineAdapter.Snapshot("SETUP", 0, 10);
        var raw = new RawCncTelemetry("machine-cnc", connection.Id, "HAAS_NGC",
            snapshot.Timestamp, "Q500", ">PROGRAM,O1234,RUNNING,PARTS,10");
        await using (var seeded = await fixture.Database.OpenConnectionAsync())
        await using (var old = seeded.CreateCommand())
        {
            old.CommandText = """
                INSERT INTO machine_telemetry_raw
                    (id, connection_id, machine_id, adapter_type, observed_at, operation, raw_payload)
                VALUES ('old-raw', 'cnc-machine-cnc', 'machine-cnc', 'HAAS_NGC', $at, 'OLD', 'expired');
                """;
            old.Parameters.AddWithValue("$at", snapshot.Timestamp.AddDays(-20).ToString("O"));
            await old.ExecuteNonQueryAsync();
        }

        Assert.True(await repository.SaveSnapshotAsync(connection, snapshot, [raw], default));
        Assert.False(await repository.SaveSnapshotAsync(connection,
            snapshot with { Timestamp = snapshot.Timestamp.AddSeconds(1) }, [raw with { Timestamp = snapshot.Timestamp.AddSeconds(1) }], default));
        Assert.True(await repository.SaveSnapshotAsync(connection,
            FakeCncMachineAdapter.Snapshot("PRODUCTION", 1, 10), [raw], default));

        await using var db = await fixture.Database.OpenConnectionAsync();
        await using var history = db.CreateCommand();
        history.CommandText = "SELECT COUNT(*) FROM machine_state_history WHERE machine_id = 'machine-cnc';";
        Assert.Equal(2L, (long)(await history.ExecuteScalarAsync())!);
        await using var current = db.CreateCommand();
        current.CommandText = "SELECT COUNT(*), production_variable_value FROM machine_current_state WHERE machine_id = 'machine-cnc';";
        await using var reader = await current.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        await reader.DisposeAsync();
        await using var expired = db.CreateCommand();
        expired.CommandText = "SELECT COUNT(*) FROM machine_telemetry_raw WHERE operation = 'OLD';";
        Assert.Equal(0L, (long)(await expired.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Fake_adapter_setup_to_production_updates_Bench_and_WebSocket_without_refresh()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.Cnc.Live", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build([
            "--Server:Host=127.0.0.1", "--Server:Port=5099",
            $"--Database:Path={Path.Combine(directory, "test.db")}"], builder => builder.UseTestServer());
        try
        {
            await application.StartAsync();
            await SeedBenchAsync(application.Services.GetRequiredService<SqliteDatabase>());
            var socketClient = application.GetTestServer().CreateWebSocketClient();
            using var socket = await socketClient.ConnectAsync(
                new Uri("ws://localhost/api/v1/machines/live"), default);
            await SendAsync(socket, new { type = "subscribe", machineIds = new[] { "machine-live" } });
            var adapter = new FakeCncMachineAdapter("machine-live", "cnc-machine-live");
            var consumer = application.Services.GetServices<ICncSnapshotConsumer>().Single();
            var publisher = application.Services.GetRequiredService<ICncLivePublisher>();

            adapter.SetProgram("O1234", "PART-LIVE");
            adapter.SetVariable(0);
            var setup = (await adapter.ReadSnapshotAsync()).Snapshot;
            var setupResult = await consumer.ConsumeAsync(setup, default);
            Assert.Contains("BenchAutoStarted", setupResult.DomainEvents);
            await publisher.PublishAsync(new("MachineSnapshotUpdated", "machine-live", setup.Timestamp, setup));
            var first = await ReceiveAsync(socket);
            Assert.Equal("MachineSnapshotUpdated", first.GetProperty("type").GetString());

            adapter.SetVariable(1);
            var production = (await adapter.ReadSnapshotAsync()).Snapshot;
            var productionResult = await consumer.ConsumeAsync(production, default);
            Assert.Contains("BenchProductionStarted", productionResult.DomainEvents);
            await publisher.PublishAsync(new("BenchStateChanged", "machine-live", production.Timestamp,
                new { eventTypes = productionResult.DomainEvents }), default);
            var second = await ReceiveAsync(socket);
            Assert.Equal("BenchStateChanged", second.GetProperty("type").GetString());

            var monitor = await application.Services.GetRequiredService<IHaasIntegrationRepository>()
                .ReadMonitorAsync("machine-live", DateTimeOffset.UtcNow, default);
            Assert.Equal("PRODUCTION", monitor!.ActiveBench!.State);
        }
        finally
        {
            await application.StopAsync();
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Fake_adapter_contract_supports_connect_disconnect_failure_and_capabilities()
    {
        await using var adapter = new FakeCncMachineAdapter("machine", "connection");
        Assert.False(adapter.Connected);
        await adapter.ConnectAsync();
        Assert.True(adapter.Connected);
        Assert.True(adapter.GetCapabilities().CanReadMachineState);
        adapter.SetOnline(false);
        await Assert.ThrowsAsync<IOException>(() => adapter.ReadSnapshotAsync());
        await adapter.DisconnectAsync();
        Assert.False(adapter.Connected);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.ConnectAsync(new CancellationToken(canceled: true)));
    }

    private static async Task<MachineConnection> SeedConnectionAsync(SqliteDatabase database)
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = JsonSerializer.Serialize(new
        {
            host = "127.0.0.1",
            mdc = new { port = 5051, timeoutMs = 3000 },
            programAccess = new
            {
                provider = "NONE", enabled = false, sharePath = (string?)null,
                usernameSecretId = (string?)null, passwordSecretId = (string?)null,
                headerLineLimit = 50, headerByteLimit = 32768,
                headerPartPatterns = new[] { "PART" }
            },
            production = new { variableNumber = 10605, legacyVariableAlias = 605, partCounterSource = "Q500" },
            monitoring = new { pollingIntervalMs = 1000, stableProgramPolls = 2, maximumReconnectBackoffMs = 30000, rawTelemetryRetentionDays = 14 }
        });
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-cnc', 'CNC', 'UTC');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-cnc', 'CNC', 'CNC', 'mill', 'calendar-cnc', 'active', 1);
            INSERT INTO machine_connections (
                id, machine_id, adapter_type, enabled, connection_status, polling_interval_ms,
                connection_timeout_ms, maximum_reconnect_backoff_ms, allow_read, allow_write,
                configuration_json, raw_telemetry_retention_days, version, created_at, updated_at)
            VALUES ('cnc-machine-cnc', 'machine-cnc', 'HAAS_NGC', 0, 'DISABLED', 1000,
                3000, 30000, 1, 0, $configuration, 14, 1, $at, $at);
            """;
        command.Parameters.AddWithValue("$configuration", configuration);
        command.Parameters.AddWithValue("$at", now.ToString("O"));
        await command.ExecuteNonQueryAsync();
        return new("cnc-machine-cnc", "machine-cnc", CncAdapterType.HaasNgc, false,
            CncConnectionStates.Disabled, null, null, null, null, 1000, 3000, 30000,
            true, false, configuration, null, null, 14, 1, now, now);
    }

    private static async Task SeedBenchAsync(SqliteDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
            VALUES ('calendar-live', 'Live', 'UTC', '{"availability":[]}');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-live', 'LIVE', 'Live CNC', 'mill', 'calendar-live', 'active', 1);
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-live', 'PART-LIVE', 'Part Live', 'C:\Cases\PART-LIVE');
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-live', 'case-live', 'B-LIVE', 'waiting', 10);
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name, required_machine_type)
            VALUES ('case-op-live', 'case-live', 10, 0, 'Mill', 'mill');
            INSERT INTO batch_operations
                (id, production_batch_id, source_case_operation_id, operation_number,
                 route_position, name, required_machine_type, status)
            VALUES ('operation-live', 'batch-live', 'case-op-live', 10, 0, 'Mill', 'mill', 'not_started');
            INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
            VALUES ('assignment-live', 'operation-live', 'machine-live', 0);
            INSERT INTO haas_connection_settings (
                machine_id, host, mdc_port, mtconnect_port, local_net_share_enabled,
                production_mode_variable, legacy_variable_alias, part_counter_source,
                polling_interval_ms, connection_timeout_ms, stable_program_polls,
                header_line_limit, header_byte_limit, header_part_patterns_json,
                enabled, version, created_at, updated_at)
            VALUES ('machine-live', '127.0.0.1', 5051, 8082, 0, 10605, 605, 'Q500',
                2000, 3000, 2, 50, 32768, '["PART"]', 0, 1, $at, $at);
            """;
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static Task SendAsync(WebSocket socket, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, default);
    }

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        var bytes = new byte[65536];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await socket.ReceiveAsync(bytes, timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        using var document = JsonDocument.Parse(bytes.AsMemory(0, result.Count));
        return document.RootElement.Clone();
    }
}

internal sealed class FakeCncMachineAdapter(string machineId, string connectionId) : ICncMachineAdapter
{
    private bool online = true;
    private string machineState = "RUNNING";
    private string? program;
    private string? part;
    private int variable;
    private int counter;

    internal bool Connected { get; private set; }
    public string ConnectionId { get; } = connectionId;
    public string MachineId { get; } = machineId;
    public CncAdapterType AdapterType => CncAdapterType.HaasNgc;
    public CncAdapterCapabilities GetCapabilities() => new(
        true, true, true, true, true, true, false, false, false, false, false, false, false);
    public Task ConnectAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); Connected = true; return Task.CompletedTask; }
    public Task DisconnectAsync(CancellationToken token = default)
    { Connected = false; return Task.CompletedTask; }
    public Task<CncConnectionTestResult> TestConnectionAsync(CancellationToken token = default) =>
        Task.FromResult(new CncConnectionTestResult(online, online ? "ONLINE" : "OFFLINE",
            [new("fake", online, online ? "AVAILABLE" : "UNAVAILABLE", "Fake adapter")]));
    public Task<CncAdapterSnapshot> ReadSnapshotAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!online) throw new IOException("Simulated connection failure.");
        return Task.FromResult(new CncAdapterSnapshot(Snapshot(
            variable == 1 ? "PRODUCTION" : "SETUP", variable, counter,
            MachineId, ConnectionId, machineState, program, part), []));
    }
    public Task<CncOperationResult<CncProgramSnapshot>> ReadActiveProgramInfoAsync(CancellationToken token = default) =>
        Task.FromResult(CncOperationResult<CncProgramSnapshot>.Success(new(
            new(program, DateTimeOffset.UtcNow, false), new(part, DateTimeOffset.UtcNow, false), new(null, null, false))));
    public Task<CncOperationResult<int>> ReadVariableAsync(int variableNumber, CancellationToken token = default) =>
        Task.FromResult(CncOperationResult<int>.Success(variable));
    public Task<CncOperationResult<string>> WriteVariableAsync(int variableNumber, int value, CancellationToken token = default)
    { variable = value; return Task.FromResult(CncOperationResult<string>.Success("fake")); }
    public Task<CncOperationResult<int>> ReadPartCounterAsync(CancellationToken token = default) =>
        Task.FromResult(CncOperationResult<int>.Success(counter));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    internal void SetOnline(bool value) => online = value;
    internal void SetMachineState(string value) => machineState = value;
    internal void SetProgram(string value, string partName) { program = value; part = partName; }
    internal void SetVariable(int value) => variable = value;
    internal void IncrementPartCounter(int amount = 1) => counter += amount;

    internal static MachineSnapshot Snapshot(
        string mode, int variable, int counter, string machineId = "machine-cnc",
        string connectionId = "cnc-machine-cnc", string state = "RUNNING",
        string? program = "O1234", string? part = "PART-A")
    {
        var at = DateTimeOffset.UtcNow;
        return new(machineId, connectionId, "HAAS_NGC", at, "ONLINE", at,
            new(state, at, false),
            new(new(program, at, false), new(part, at, false), new("MACHINE.NC", at, false)),
            new(mode, 10605, new(variable, at, false)), new(counter, at, false),
            new(null, null, null, null),
            new Dictionary<string, string> { ["MDC"] = "AVAILABLE", ["PROGRAM_ACCESS"] = "AVAILABLE" },
            new Dictionary<string, string> { ["machineState"] = "AVAILABLE", ["programHeader"] = "AVAILABLE", ["macroVariables"] = "AVAILABLE", ["partCounter"] = "AVAILABLE" },
            null);
    }
}
