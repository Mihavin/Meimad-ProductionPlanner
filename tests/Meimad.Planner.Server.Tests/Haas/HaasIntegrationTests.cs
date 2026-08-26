using System.Net.Sockets;
using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Haas;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;

namespace Meimad.Planner.Server.Tests.Haas;

public sealed class HaasIntegrationTests
{
    [Fact]
    public async Task Header_match_starts_setup_and_legacy_macro_changes_have_no_workflow_effect()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database, ambiguous: false);
        var repository = new SqliteHaasIntegrationRepository(fixture.Database);
        var fake = new FakeHaasMdcClient { ProgramNumber = "O1234", MacroValue = 0, Counter = 380 };
        var reader = new FakeHaasProgramReader("MACHINE_JOB.NC", ["O1234", "(PART: PART-A)"]);
        var settings = (await repository.GetSettingsAsync("machine-haas", default))!;
        var worker = Worker(repository, fake, reader);

        await worker.PollOnceAsync(settings); // debounce poll 1
        Assert.Null((await repository.ReadMonitorAsync("machine-haas", DateTimeOffset.UtcNow, default))!.ActiveBench);

        await worker.PollOnceAsync(settings); // stable header poll 2
        var monitor = (await repository.ReadMonitorAsync("machine-haas", DateTimeOffset.UtcNow, default))!;
        Assert.Equal(HaasBenchStates.Setup, monitor.ActiveBench!.State);
        Assert.False(monitor.ActiveBench.PartCountingEnabled);
        Assert.Equal("PART-A", monitor.Snapshot!.MachineHeaderPartName);
        Assert.Equal("MACHINE_JOB.NC", Path.GetFileName(monitor.Snapshot.MachineHeaderSourcePath));

        fake.MacroValue = 1;
        await worker.PollOnceAsync(settings);
        monitor = (await repository.ReadMonitorAsync("machine-haas", DateTimeOffset.UtcNow, default))!;
        Assert.Equal(HaasBenchStates.Setup, monitor.ActiveBench!.State);
        Assert.False(monitor.ActiveBench.PartCountingEnabled);
        Assert.DoesNotContain(monitor.RecentEvents, value => value.EventType == "BenchProductionStarted");

        fake.Counter = 382;
        await worker.PollOnceAsync(settings);
        monitor = (await repository.ReadMonitorAsync("machine-haas", DateTimeOffset.UtcNow, default))!;
        Assert.Equal(HaasBenchStates.Setup, monitor.ActiveBench!.State);
        Assert.Equal(0, monitor.ActiveBench.ProducedQuantity);
        Assert.DoesNotContain(monitor.RecentEvents, value => value.EventType == "PartCompleted");

        fake.Disconnected = true;
        await worker.PollOnceAsync(settings);
        Assert.Equal(HaasConnectivityStates.Offline,
            (await repository.ReadMonitorAsync("machine-haas", DateTimeOffset.UtcNow, default))!.Snapshot!.ConnectivityState);
        fake.Disconnected = false;
        await worker.PollOnceAsync(settings);
        monitor = (await repository.ReadMonitorAsync("machine-haas", DateTimeOffset.UtcNow, default))!;
        Assert.Equal(HaasBenchStates.Setup, monitor.ActiveBench!.State);
        Assert.Single(monitor.RecentEvents, value => value.EventType == "BenchAutoStarted");
    }

    [Fact]
    public async Task Ambiguous_header_match_never_chooses_a_bench()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database, ambiguous: true);
        var repository = new SqliteHaasIntegrationRepository(fixture.Database);
        var fake = new FakeHaasMdcClient { ProgramNumber = "O1234", MacroValue = 0, Counter = 0 };
        var worker = Worker(repository, fake,
            new FakeHaasProgramReader("JOB.NC", ["O1234", "(PART: PART-A)"]));
        var settings = (await repository.GetSettingsAsync("machine-haas", default))!;

        await worker.PollOnceAsync(settings);
        await worker.PollOnceAsync(settings);
        var monitor = (await repository.ReadMonitorAsync("machine-haas", DateTimeOffset.UtcNow, default))!;

        Assert.Null(monitor.ActiveBench);
        Assert.Contains(monitor.RecentEvents, value => value.EventType == "AmbiguousBenchMatch");
    }

    [Fact]
    public async Task Filename_has_zero_effect_on_identity()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database, ambiguous: false);
        var repository = new SqliteHaasIntegrationRepository(fixture.Database);
        var fake = new FakeHaasMdcClient { ProgramNumber = "O1234", MacroValue = 0 };
        var worker = Worker(repository, fake,
            new FakeHaasProgramReader("SOMETHING_ELSE.NC", ["O1234", "(PART: PART-A)"]));
        var settings = (await repository.GetSettingsAsync("machine-haas", default))!;

        await worker.PollOnceAsync(settings);
        await worker.PollOnceAsync(settings);

        Assert.Equal("PART-A",
            (await repository.ReadMonitorAsync("machine-haas", DateTimeOffset.UtcNow, default))!.ActiveBench!.MachinePartName);
    }

    [Fact]
    public async Task Dedicated_MTConnect_test_uses_saved_host_and_port_and_returns_typed_status()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database, ambiguous: false);
        var repository = new SqliteHaasIntegrationRepository(fixture.Database);
        var mtConnect = new FakeHaasMtConnectReader
        {
            Result = new("dev1", "VF-3SS", "AVAILABLE", "STOPPED", "AUTOMATIC",
                "1500.CNC", 9302, DateTimeOffset.UtcNow,
                0, null, 0, "{}")
        };
        var service = new HaasIntegrationService(repository,
            new FakeHaasMdcClientFactory(new FakeHaasMdcClient { Disconnected = true }),
            mtConnect, new FakeHaasProgramReader("unused.nc", []),
            new NcHeaderParser(), TimeProvider.System);

        var result = await service.TestMtConnectAsync("machine-haas");

        Assert.True(result.Succeeded);
        Assert.Equal("1500.CNC", result.ProgramNumber);
        Assert.Equal("STOPPED", result.MachineStatus);
        Assert.Equal(9302, result.Parts);
        Assert.Contains("machine and counter telemetry are ready", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, mtConnect.CallCount);
    }

    [Fact]
    public async Task Dedicated_MTConnect_test_does_not_depend_on_a_workflow_macro()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database, ambiguous: false);
        var repository = new SqliteHaasIntegrationRepository(fixture.Database);
        var mtConnect = new FakeHaasMtConnectReader
        {
            Result = new("dev1", "VF-3SS", "AVAILABLE", "STOPPED", "AUTOMATIC",
                "1500.CNC", 9302, DateTimeOffset.UtcNow, 0, null, 0, "{}")
        };
        var service = new HaasIntegrationService(repository,
            new FakeHaasMdcClientFactory(new FakeHaasMdcClient { Disconnected = true }),
            mtConnect, new FakeHaasProgramReader("unused.nc", []),
            new NcHeaderParser(), TimeProvider.System);

        var result = await service.TestMtConnectAsync("machine-haas");

        Assert.True(result.Succeeded);
        Assert.Contains("machine and counter telemetry are ready", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HaasObservationHarness Worker(
        IHaasIntegrationRepository repository, FakeHaasMdcClient client, IHaasProgramReader reader) =>
        new(repository, client, reader, new NcHeaderParser());

    private static async Task SeedAsync(SqliteDatabase database, bool ambiguous)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
            VALUES ('calendar-haas', 'Haas calendar', 'UTC', '{"availability":[]}');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-haas', 'M-H', 'HAAS VF-3', 'mill', 'calendar-haas', 'active', 1);
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-a', 'PART-A', 'Part A', 'C:\Cases\PART-A');
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-a', 'case-a', 'B-A', 'waiting', 100);
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name, required_machine_type)
            VALUES ('case-op-a', 'case-a', 10, 0, 'Mill', 'mill');
            INSERT INTO batch_operations
                (id, production_batch_id, source_case_operation_id, operation_number,
                 route_position, name, required_machine_type, status)
            VALUES ('operation-a', 'batch-a', 'case-op-a', 10, 0, 'Mill', 'mill', 'not_started');
            INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
            VALUES ('assignment-a', 'operation-a', 'machine-haas', 0);
            INSERT INTO haas_connection_settings
                (machine_id, host, mdc_port, mtconnect_port, local_net_share_enabled,
                 local_net_share_path, part_counter_source, polling_interval_ms, connection_timeout_ms,
                 stable_program_polls, header_line_limit, header_byte_limit,
                 header_part_patterns_json, enabled, version, created_at, updated_at)
            VALUES ('machine-haas', '127.0.0.1', 5051, 8082, 1, '\\haas\User Data',
                    'Q500', 2000, 3000, 2, 50, 32768,
                    '["PART\\s*[:=]\\s*([^()]+)"]', 1, 1, $at, $at);
            """;
        if (ambiguous)
        {
            command.CommandText += """
                INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
                VALUES ('batch-b', 'case-a', 'B-B', 'waiting', 50);
                INSERT INTO batch_operations
                    (id, production_batch_id, source_case_operation_id, operation_number,
                     route_position, name, required_machine_type, status)
                VALUES ('operation-b', 'batch-b', 'case-op-a', 10, 0, 'Mill', 'mill', 'not_started');
                INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
                VALUES ('assignment-b', 'operation-b', 'machine-haas', 1);
                """;
        }
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
}

internal sealed class FakeHaasMdcClient : IHaasMdcClient
{
    internal string? ProgramNumber { get; set; }
    internal int MacroValue { get; set; }
    internal int Counter { get; set; }
    internal int CycleStarts { get; set; }
    internal bool Disconnected { get; set; }
    public Task ConnectAsync(CancellationToken cancellationToken = default) { ThrowIfDisconnected(); return Task.CompletedTask; }
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<HaasProgramStatus> GetMachineStatusAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisconnected();
        return Task.FromResult(new HaasProgramStatus(ProgramNumber, "RUNNING", Counter,
            DateTimeOffset.UtcNow, $">PROGRAM,{ProgramNumber},RUNNING,PARTS,{Counter}"));
    }
    public Task<string?> GetCurrentProgramAsync(CancellationToken cancellationToken = default) { ThrowIfDisconnected(); return Task.FromResult(ProgramNumber); }
    public Task<int> GetPartCounterAsync(string source, CancellationToken cancellationToken = default) { ThrowIfDisconnected(); return Task.FromResult(Counter); }
    public Task<int> ReadMacroAsync(int variableNumber, CancellationToken cancellationToken = default) { ThrowIfDisconnected(); return Task.FromResult(MacroValue); }
    public Task<string> WriteMacroAsync(int variableNumber, int value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisconnected();
        if (value != 0) throw new InvalidOperationException();
        MacroValue = 0;
        return Task.FromResult(">!\r\n");
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private void ThrowIfDisconnected() { if (Disconnected) throw new SocketException((int)SocketError.HostDown); }
}

internal sealed class FakeHaasMdcClientFactory(FakeHaasMdcClient client) : IHaasMdcClientFactory
{
    public IHaasMdcClient Create(HaasConnectionSettings settings) => client;
}

internal sealed class FakeHaasMtConnectReader : IHaasMtConnectReader
{
    internal HaasMtConnectRead? Result { get; set; }
    internal Exception? Error { get; set; }
    internal int CallCount { get; private set; }

    public Task<HaasMtConnectRead> ReadAsync(
        string host,
        int port,
        int timeoutMs,
        string partCounterSource,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (Error is not null) throw Error;
        return Task.FromResult(Result ?? new HaasMtConnectRead(
            "dev1", "VF-3SS", "AVAILABLE", "STOPPED", "AUTOMATIC", "O1234",
            0, DateTimeOffset.UtcNow, 0, null, 0, "{}"));
    }
}

internal sealed class FakeHaasProgramReader(string sourcePath, IReadOnlyList<string> lines) : IHaasProgramReader
{
    public Task<MachineNcHeader> ReadActiveProgramHeaderAsync(
        HaasConnectionSettings settings, string programNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MachineNcHeader(programNumber, lines, sourcePath, DateTimeOffset.UtcNow));
}

internal sealed class HaasObservationHarness(
    IHaasIntegrationRepository repository,
    FakeHaasMdcClient client,
    IHaasProgramReader programReader,
    INcHeaderParser headerParser)
{
    private string? candidateProgram;
    private int candidatePolls;

    internal async Task<HaasObservationResult?> PollOnceAsync(
        HaasConnectionSettings settings, CancellationToken token = default)
    {
        var at = DateTimeOffset.UtcNow;
        var previous = await repository.GetSnapshotAsync(settings.MachineId, token);
        try
        {
            var status = await client.GetMachineStatusAsync(token);
            var counter = settings.PartCounterSource == HaasPartCounterSources.Q500
                ? status.Parts : await client.GetPartCounterAsync(settings.PartCounterSource, token);
            var program = status.ProgramNumber;
            MachineNcHeader? header = null;
            string? part = null;
            if (program == candidateProgram) candidatePolls++;
            else { candidateProgram = program; candidatePolls = 1; }
            if (program is not null && previous?.ProgramNumber == program
                && previous.MachineHeaderPartName is not null)
            {
                part = previous.MachineHeaderPartName;
            }
            else if (program is not null && candidatePolls >= settings.StableProgramPolls)
            {
                header = await programReader.ReadActiveProgramHeaderAsync(settings, program, token);
                part = headerParser.Parse(header.FirstLines, settings.HeaderPartPatterns).PartName;
            }
            var snapshot = new HaasMachineSnapshot(
                settings.MachineId, at, HaasConnectivityStates.Online, status.MachineStatus,
                program, part, header?.SourcePath ?? previous?.MachineHeaderSourcePath,
                header?.ReadTimestamp ?? previous?.HeaderReadAt, counter, status.RawResponse,
                null, at, previous?.Version + 1 ?? 1);
            return await repository.ApplyObservationAsync(snapshot, at, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var snapshot = new HaasMachineSnapshot(
                settings.MachineId, at, HaasConnectivityStates.Offline,
                previous?.MachineStatus, previous?.ProgramNumber, previous?.MachineHeaderPartName,
                previous?.MachineHeaderSourcePath, previous?.HeaderReadAt,
                previous?.PartCounter,
                previous?.RawMdcStatus, exception.Message, previous?.LastSeenAt,
                previous?.Version + 1 ?? 1);
            return await repository.ApplyObservationAsync(snapshot, at, token);
        }
    }
}
