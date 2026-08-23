using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Domain.Haas;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task Degraded_stale_missing_and_nonbinary_macro_snapshots_cannot_start_Bench()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedBenchAsync(fixture.Database);
        var repository = new SqliteHaasIntegrationRepository(fixture.Database);
        var consumer = new BenchAutomationService(repository);

        var degraded = AutomationSnapshot(null, 10) with
        {
            ConnectionStatus = CncConnectionStates.Degraded
        };
        var stale = AutomationSnapshot(0, 10);
        stale = stale with
        {
            Production = stale.Production with
            {
                ModeVariableValue = new(0, DateTimeOffset.UtcNow, true)
            }
        };
        var missing = AutomationSnapshot(null, 10);
        var missingNumber = AutomationSnapshot(0, 10);
        missingNumber = missingNumber with
        {
            Production = missingNumber.Production with { ModeVariableNumber = null }
        };
        var nonbinary = AutomationSnapshot(5, 10);

        foreach (var snapshot in new[] { degraded, stale, missing, missingNumber, nonbinary })
        {
            var result = await consumer.ConsumeAsync(snapshot, default);
            Assert.Empty(result.DomainEvents);
            var monitor = await repository.ReadMonitorAsync(
                "machine-live", DateTimeOffset.UtcNow, default);
            Assert.Null(monitor!.ActiveBench);
        }

        var valid = await consumer.ConsumeAsync(AutomationSnapshot(0, 10), default);
        Assert.Contains("BenchAutoStarted", valid.DomainEvents);
    }

    [Fact]
    public async Task Invalid_current_macro_snapshots_never_use_previous_value_or_credit_parts()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedBenchAsync(fixture.Database);
        var repository = new SqliteHaasIntegrationRepository(fixture.Database);
        var consumer = new BenchAutomationService(repository);

        await consumer.ConsumeAsync(AutomationSnapshot(0, 10), default);
        var production = await consumer.ConsumeAsync(AutomationSnapshot(1, 10), default);
        Assert.Contains("BenchProductionStarted", production.DomainEvents);

        var degraded = AutomationSnapshot(null, 11) with
        {
            ConnectionStatus = CncConnectionStates.Degraded
        };
        var stale = AutomationSnapshot(1, 12);
        stale = stale with
        {
            Production = stale.Production with
            {
                ModeVariableValue = stale.Production.ModeVariableValue with { Stale = true }
            }
        };
        var missing = AutomationSnapshot(null, 13);
        var nonbinary = AutomationSnapshot(5, 14);
        var staleCounter = AutomationSnapshot(1, 15);
        staleCounter = staleCounter with
        {
            PartCounter = staleCounter.PartCounter with { Stale = true }
        };

        foreach (var snapshot in new[] { degraded, stale, missing, nonbinary, staleCounter })
        {
            var result = await consumer.ConsumeAsync(snapshot, default);
            Assert.Empty(result.DomainEvents);
        }

        var unchanged = await repository.ReadMonitorAsync(
            "machine-live", DateTimeOffset.UtcNow, default);
        Assert.Equal(HaasBenchStates.Production, unchanged!.ActiveBench!.State);
        Assert.Equal(0, unchanged.ActiveBench.ProducedQuantity);
        Assert.Equal(10, unchanged.ActiveBench.PreviousPartCounter);

        var recovered = await consumer.ConsumeAsync(AutomationSnapshot(1, 15), default);
        Assert.Contains("PartCompleted", recovered.DomainEvents);
        var monitor = await repository.ReadMonitorAsync(
            "machine-live", DateTimeOffset.UtcNow, default);
        Assert.Equal(5, monitor!.ActiveBench!.ProducedQuantity);
    }

    [Fact]
    public async Task Degraded_optional_capability_does_not_discard_fresh_binary_macro_and_counter()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedBenchAsync(fixture.Database);
        var repository = new SqliteHaasIntegrationRepository(fixture.Database);
        var consumer = new BenchAutomationService(repository);

        await consumer.ConsumeAsync(AutomationSnapshot(0, 10), default);
        await consumer.ConsumeAsync(AutomationSnapshot(1, 10), default);
        var degraded = AutomationSnapshot(1, 11) with
        {
            ConnectionStatus = CncConnectionStates.Degraded,
            LastError = "Optional program-header access is unavailable."
        };

        var result = await consumer.ConsumeAsync(degraded, default);

        Assert.Contains("PartCompleted", result.DomainEvents);
        var monitor = await repository.ReadMonitorAsync(
            "machine-live", DateTimeOffset.UtcNow, default);
        Assert.Equal(1, monitor!.ActiveBench!.ProducedQuantity);
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

    [Fact]
    public async Task Reconcile_restarts_a_faulted_same_version_worker_lease()
    {
        var replacement = new LifecycleAdapter("machine-lifecycle", "cnc-machine-lifecycle");
        var factory = new LifecycleAdapterFactory((invocation, _) => invocation == 1
            ? throw new InvalidOperationException("Adapter construction failed.")
            : replacement);
        var manager = LifecycleManager(factory);
        var connection = LifecycleConnection(1, "MDC");

        await manager.ReconcileWorkersAsync([connection]);
        Assert.Equal(1, factory.CreateCount);

        await manager.ReconcileWorkersAsync([connection]);
        await replacement.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, factory.CreateCount);
        Assert.Equal([1, 1], factory.CreatedVersions);
        await manager.ReconcileWorkersAsync([]);
    }

    [Fact]
    public async Task Changed_provider_waits_for_old_worker_to_finish_before_starting_new_version()
    {
        var oldAdapter = new LifecycleAdapter(
            "machine-lifecycle", "cnc-machine-lifecycle", blockDisposal: true);
        var newAdapter = new LifecycleAdapter("machine-lifecycle", "cnc-machine-lifecycle");
        var factory = new LifecycleAdapterFactory((invocation, _) =>
            invocation == 1 ? oldAdapter : newAdapter);
        var manager = LifecycleManager(factory);

        await manager.ReconcileWorkersAsync([LifecycleConnection(1, "MDC")]);
        await oldAdapter.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var replacement = manager.ReconcileWorkersAsync([LifecycleConnection(2, "MTCONNECT")]);
        await oldAdapter.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(replacement.IsCompleted);
        Assert.Equal(1, factory.CreateCount);

        oldAdapter.ReleaseDisposal();
        await replacement.WaitAsync(TimeSpan.FromSeconds(5));
        await newAdapter.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, factory.CreateCount);
        Assert.Equal([1, 2], factory.CreatedVersions);
        Assert.Equal(["MDC", "MTCONNECT"], factory.CreatedProviders);
        await manager.ReconcileWorkersAsync([]);
    }

    private static CncConnectionManager LifecycleManager(ICncAdapterFactory factory) => new(
        new LifecycleRepository(), factory, [], new CncLivePublisher(), TimeProvider.System,
        NullLoggerFactory.Instance, NullLogger<CncConnectionManager>.Instance);

    private static MachineConnection LifecycleConnection(int version, string provider)
    {
        var now = DateTimeOffset.UtcNow;
        return new("cnc-machine-lifecycle", "machine-lifecycle", CncAdapterType.HaasNgc, true,
            CncConnectionStates.Offline, null, null, null, null, 60000, 3000, 30000,
            true, provider == "MDC", JsonSerializer.Serialize(new { telemetryProvider = provider }),
            null, null, 14, version, now, now);
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

    private static MachineSnapshot AutomationSnapshot(int? variable, int counter)
    {
        var at = DateTimeOffset.UtcNow;
        return new MachineSnapshot(
            "machine-live", "cnc-machine-live", CncAdapterTypes.HaasNgc, at,
            CncConnectionStates.Online, at,
            new("RUNNING", at, false),
            new(new("O1234", at, false), new("PART-LIVE", at, false),
                new("MACHINE.NC", at, false)),
            new(variable switch { 0 => "SETUP", 1 => "PRODUCTION", _ => null },
                10605, new(variable, variable is null ? null : at, false)),
            new(counter, at, false),
            new(null, null, null, null),
            new Dictionary<string, string>
            {
                ["MTCONNECT"] = CncComponentStates.Available,
                ["PROGRAM_ACCESS"] = CncComponentStates.Available
            },
            new Dictionary<string, string>
            {
                ["machineState"] = CncComponentStates.Available,
                ["programHeader"] = CncComponentStates.Available,
                ["macroVariables"] = variable is 0 or 1
                    ? CncComponentStates.Available : CncComponentStates.Unavailable,
                ["partCounter"] = CncComponentStates.Available
            },
            variable is 0 or 1 ? null : "Production variable is unavailable or nonbinary.");
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

    private sealed class LifecycleRepository : ICncConnectionRepository
    {
        public Task<IReadOnlyList<MachineConnection>> ListConnectionsAsync(
            bool enabledOnly, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MachineConnection>>([]);

        public Task<MachineConnection?> GetConnectionAsync(
            string machineId, CancellationToken cancellationToken) =>
            Task.FromResult<MachineConnection?>(null);

        public Task<MachineConnection> UpsertConnectionAsync(
            MachineConnection connection, int expectedVersion, EditAuthority authority,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpdateConnectionStateAsync(
            string connectionId, string state, DateTimeOffset at, bool successfulPoll,
            string? error, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<MachineSnapshot?> GetCurrentSnapshotAsync(
            string machineId, CancellationToken cancellationToken) =>
            Task.FromResult<MachineSnapshot?>(null);

        public Task<bool> SaveSnapshotAsync(
            MachineConnection connection, MachineSnapshot snapshot,
            IReadOnlyList<RawCncTelemetry> rawTelemetry, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<RawCncTelemetry>> ReadDiagnosticsAsync(
            string machineId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RawCncTelemetry>>([]);
    }

    private sealed class LifecycleAdapterFactory(
        Func<int, MachineConnection, ICncMachineAdapter> create) : ICncAdapterFactory
    {
        private readonly List<int> createdVersions = [];
        private readonly List<string> createdProviders = [];
        private int createCount;

        internal int CreateCount => Volatile.Read(ref createCount);
        internal IReadOnlyList<int> CreatedVersions
        {
            get { lock (createdVersions) return createdVersions.ToArray(); }
        }
        internal IReadOnlyList<string> CreatedProviders
        {
            get { lock (createdVersions) return createdProviders.ToArray(); }
        }

        public ICncMachineAdapter CreateAdapter(MachineConnection connection)
        {
            var invocation = Interlocked.Increment(ref createCount);
            using var json = JsonDocument.Parse(connection.ConfigurationJson);
            lock (createdVersions)
            {
                createdVersions.Add(connection.Version);
                createdProviders.Add(json.RootElement.GetProperty("telemetryProvider").GetString()!);
            }
            return create(invocation, connection);
        }
    }

    private sealed class LifecycleAdapter(
        string machineId,
        string connectionId,
        bool blockDisposal = false) : ICncMachineAdapter
    {
        private readonly TaskCompletionSource disposeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ConnectionId { get; } = connectionId;
        public string MachineId { get; } = machineId;
        public CncAdapterType AdapterType => CncAdapterType.HaasNgc;

        public CncAdapterCapabilities GetCapabilities() => new(
            true, false, false, false, false, false, false,
            false, false, false, false, false, false);

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CncConnectionTestResult> TestConnectionAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<CncAdapterSnapshot> ReadSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The lifecycle test adapter should be canceled.");
        }

        public Task<CncOperationResult<CncProgramSnapshot>> ReadActiveProgramInfoAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CncOperationResult<CncProgramSnapshot>.Unsupported());

        public Task<CncOperationResult<int>> ReadVariableAsync(
            int variable, CancellationToken cancellationToken = default) =>
            Task.FromResult(CncOperationResult<int>.Unsupported());

        public Task<CncOperationResult<string>> WriteVariableAsync(
            int variable, int value, CancellationToken cancellationToken = default) =>
            Task.FromResult(CncOperationResult<string>.Unsupported());

        public Task<CncOperationResult<int>> ReadPartCounterAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CncOperationResult<int>.Unsupported());

        public async ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            if (blockDisposal) await disposeRelease.Task;
        }

        internal void ReleaseDisposal() => disposeRelease.TrySetResult();
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
