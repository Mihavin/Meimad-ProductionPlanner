using System.Text.Json;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Infrastructure.Cnc;
using Meimad.Planner.Server.Tests.Haas;

namespace Meimad.Planner.Server.Tests.Cnc;

public sealed class CncAdapterTests
{
    [Fact]
    public async Task Haas_adapter_composes_normalized_snapshot_and_reports_capabilities()
    {
        var mdc = new FakeHaasMdcClient { ProgramNumber = "O1234", MacroValue = 0, Counter = 27 };
        await using var adapter = new HaasNgcAdapter(Connection(),
            new FakeHaasMdcClientFactory(mdc),
            new FakeHaasMtConnectReader(),
            new FakeHaasProgramReader("MACHINE.NC", ["O1234", "(PART: PART-A)"]),
            new NcHeaderParser(), TimeProvider.System);

        Assert.True(adapter.GetCapabilities().CanReadMachineState);
        Assert.True(adapter.GetCapabilities().CanReadProgramHeader);
        Assert.False(adapter.GetCapabilities().CanReadToolData);

        var first = await adapter.ReadSnapshotAsync();
        Assert.Equal(CncConnectionStates.Degraded, first.Snapshot.ConnectionStatus);
        var second = await adapter.ReadSnapshotAsync();

        Assert.Equal(CncConnectionStates.Online, second.Snapshot.ConnectionStatus);
        Assert.Equal("RUNNING", second.Snapshot.MachineState.Value);
        Assert.Equal("O1234", second.Snapshot.Program.ProgramNumber.Value);
        Assert.Equal("PART-A", second.Snapshot.Program.PartName.Value);
        Assert.Equal(27, second.Snapshot.PartCounter.Value);
        Assert.Equal(CncComponentStates.Available, second.Snapshot.ComponentHealth["MDC"]);
        Assert.Single(second.RawTelemetry, item => item.Operation == "Q500");
    }

    [Fact]
    public async Task Optional_program_access_failure_keeps_MDC_snapshot_degraded()
    {
        var mdc = new FakeHaasMdcClient { ProgramNumber = "O1234", MacroValue = 1, Counter = 9 };
        await using var adapter = new HaasNgcAdapter(Connection(),
            new FakeHaasMdcClientFactory(mdc), new FakeHaasMtConnectReader(), new FailingProgramProvider(),
            new NcHeaderParser(), TimeProvider.System);

        await adapter.ReadSnapshotAsync();
        var result = await adapter.ReadSnapshotAsync();

        Assert.Equal(CncConnectionStates.Degraded, result.Snapshot.ConnectionStatus);
        Assert.Equal("RUNNING", result.Snapshot.MachineState.Value);
        Assert.Equal(9, result.Snapshot.PartCounter.Value);
        Assert.Null(result.Snapshot.Program.PartName.Value);
        Assert.Equal(CncComponentStates.Available, result.Snapshot.ComponentHealth["MDC"]);
        Assert.Equal(CncComponentStates.Unavailable, result.Snapshot.ComponentHealth["PROGRAM_ACCESS"]);
    }

    [Fact]
    public async Task Generic_write_contract_rejects_all_persistent_mode_variable_writes()
    {
        var mdc = new FakeHaasMdcClient { MacroValue = 1 };
        await using var adapter = new HaasNgcAdapter(Connection(),
            new FakeHaasMdcClientFactory(mdc),
            new FakeHaasMtConnectReader(),
            new FakeHaasProgramReader("MACHINE.NC", ["O1", "(PART: A)"]),
            new NcHeaderParser(), TimeProvider.System);

        var deniedVariable = await adapter.WriteVariableAsync(10606, 0);
        var deniedValue = await adapter.WriteVariableAsync(10605, 1);
        var reset = await adapter.WriteVariableAsync(10605, 0);

        Assert.False(deniedVariable.Supported);
        Assert.False(deniedValue.Supported);
        Assert.False(reset.Supported);
        Assert.Equal(1, mdc.MacroValue);
    }

    [Fact]
    public async Task Haas_adapter_uses_explicit_MTConnect_read_source_without_opening_MDC()
    {
        var mdc = new FakeHaasMdcClient { Disconnected = true };
        var mtConnect = new FakeHaasMtConnectReader
        {
            Result = new("dev1", "VF-3SS", "AVAILABLE", "ACTIVE", "AUTOMATIC",
                "1500.CNC", 9302, DateTimeOffset.UtcNow,
                4200m, 1250m, 0, "{\"lastSequence\":27778}")
        };
        await using var adapter = new HaasNgcAdapter(Connection("MTCONNECT", programAccess: false),
            new FakeHaasMdcClientFactory(mdc), mtConnect,
            new FakeHaasProgramReader("unused.nc", []),
            new NcHeaderParser(), TimeProvider.System);

        await adapter.ConnectAsync();
        var result = await adapter.ReadSnapshotAsync();

        Assert.Equal(1, mtConnect.CallCount);
        Assert.Equal(CncConnectionStates.Online, result.Snapshot.ConnectionStatus);
        Assert.Equal("ACTIVE", result.Snapshot.MachineState.Value);
        Assert.Equal("1500.CNC", result.Snapshot.Program.ProgramNumber.Value);
        Assert.Null(result.Snapshot.Program.PartName.Value);
        Assert.Equal(9302, result.Snapshot.PartCounter.Value);
        Assert.Equal(4200m, result.Snapshot.Telemetry.SpindleRpm);
        Assert.Equal(CncComponentStates.Available, result.Snapshot.ComponentHealth["MTCONNECT"]);
        Assert.Equal(CncComponentStates.Unsupported, result.Snapshot.ComponentHealth["MDC"]);
        Assert.False(adapter.GetCapabilities().CanWriteVariables);
        Assert.False((await adapter.WriteVariableAsync(10605, 0)).Supported);
        Assert.Single(result.RawTelemetry, value => value.Operation == "MTCONNECT_CURRENT");
        Assert.True(result.RawTelemetry[0].RawPayload.Length < 4096);
    }

    [Fact]
    public async Task MTConnect_snapshot_health_does_not_depend_on_a_mode_variable()
    {
        var mtConnect = new FakeHaasMtConnectReader
        {
            Result = new("dev1", "VF-3SS", "AVAILABLE", "STOPPED", "AUTOMATIC",
                "1500.CNC", 9302, DateTimeOffset.UtcNow, 0, null, 0, "{}")
        };
        await using var adapter = new HaasNgcAdapter(Connection("MTCONNECT", programAccess: false),
            new FakeHaasMdcClientFactory(new FakeHaasMdcClient { Disconnected = true }), mtConnect,
            new FakeHaasProgramReader("unused.nc", []),
            new NcHeaderParser(), TimeProvider.System);

        var snapshot = await adapter.ReadSnapshotAsync();
        var test = await adapter.TestConnectionAsync();

        Assert.Equal(CncConnectionStates.Online, snapshot.Snapshot.ConnectionStatus);
        Assert.Null(snapshot.Snapshot.LastError);
        Assert.True(test.OverallSuccess);
        Assert.Equal(CncConnectionStates.Online, test.ConnectionStatus);
        Assert.DoesNotContain(test.Checks, value => value.Id == "variableRead");
    }

    [Fact]
    public async Task Missing_MTConnect_state_or_program_is_not_reported_as_healthy_or_online()
    {
        var mtConnect = new FakeHaasMtConnectReader
        {
            Result = new("dev1", "VF-3SS", "AVAILABLE", null, "AUTOMATIC",
                null, 9302, DateTimeOffset.UtcNow,
                0, null, 0, "{}")
        };
        await using var adapter = new HaasNgcAdapter(Connection("MTCONNECT", programAccess: false),
            new FakeHaasMdcClientFactory(new FakeHaasMdcClient { Disconnected = true }), mtConnect,
            new FakeHaasProgramReader("unused.nc", []),
            new NcHeaderParser(), TimeProvider.System);

        var result = await adapter.ReadSnapshotAsync();

        Assert.Equal(CncConnectionStates.Degraded, result.Snapshot.ConnectionStatus);
        Assert.Null(result.Snapshot.MachineState.Value);
        Assert.Equal(CncComponentStates.Unavailable,
            result.Snapshot.CapabilityHealth["machineState"]);
        Assert.Equal(CncComponentStates.Unavailable,
            result.Snapshot.CapabilityHealth["activeProgram"]);
    }

    [Fact]
    public async Task Existing_Haas_JSON_without_MTConnect_provider_remains_MDC_after_upgrade()
    {
        var oldJson = """
            {
              "host":"192.0.2.1",
              "macAddress":"44:B1:76:B0:26:68",
              "mdc":{"port":5051,"timeoutMs":3000},
              "programAccess":{"provider":"NONE","enabled":false,"sharePath":null,
                "usernameSecretId":null,"passwordSecretId":null,"headerLineLimit":50,
                "headerByteLimit":32768,"headerPartPatterns":[]},
              "production":{"variableNumber":10605,"legacyVariableAlias":605,"partCounterSource":"Q500"},
              "monitoring":{"pollingIntervalMs":500,"stableProgramPolls":2,
                "maximumReconnectBackoffMs":30000,"rawTelemetryRetentionDays":14}
            }
            """;
        var connection = Connection() with { ConfigurationJson = oldJson };
        var mtConnect = new FakeHaasMtConnectReader { Error = new InvalidOperationException("must not be used") };
        await using var adapter = new HaasNgcAdapter(connection,
            new FakeHaasMdcClientFactory(new FakeHaasMdcClient
                { ProgramNumber = "O1234", MacroValue = 0, Counter = 7 }),
            mtConnect, new FakeHaasProgramReader("unused.nc", []),
            new NcHeaderParser(), TimeProvider.System);

        var snapshot = await adapter.ReadSnapshotAsync();

        Assert.Equal(0, mtConnect.CallCount);
        Assert.Equal(CncConnectionStates.Online, snapshot.Snapshot.ConnectionStatus);
        Assert.Equal(7, snapshot.Snapshot.PartCounter.Value);
        Assert.Equal(CncComponentStates.Available, snapshot.Snapshot.ComponentHealth["MDC"]);
    }

    [Fact]
    public async Task DPRNT_only_adapter_reads_part_identity_and_events_from_a_controller_print_file()
    {
        var directory = Directory.CreateTempSubdirectory("MeimadPlanner.DprntAdapter");
        try
        {
            var path = Path.Combine(directory.FullName, "print.txt");
            await File.WriteAllTextAsync(path, "30P647004101-001\r\nMEIMAD/V/1/OLC/AAA\r\n");
            var mtConnect = new FakeHaasMtConnectReader { Error = new InvalidOperationException("must not be used") };
            await using var adapter = new HaasNgcAdapter(
                Connection("DPRNT", programAccess: false, dprnt: new("FILE", path, "AFTER_READ")),
                new FakeHaasMdcClientFactory(new FakeHaasMdcClient { Disconnected = true }), mtConnect,
                new FakeHaasProgramReader("unused.nc", []),
                new NcHeaderParser(), TimeProvider.System);

            await adapter.ConnectAsync();
            var first = await adapter.ReadSnapshotAsync();
            var quiet = await adapter.ReadSnapshotAsync();
            var test = await adapter.TestConnectionAsync();

            Assert.Equal(0, mtConnect.CallCount);
            Assert.False(adapter.GetCapabilities().CanReadMachineState);
            Assert.False(adapter.GetCapabilities().CanReadPartCounter);
            Assert.Equal(CncConnectionStates.Online, first.Snapshot.ConnectionStatus);
            Assert.Null(first.Snapshot.MachineState.Value);
            Assert.Null(first.Snapshot.Program.ProgramNumber.Value);
            Assert.Equal("30P647004101-001", first.Snapshot.Program.PartName.Value);
            Assert.Null(first.Snapshot.PartCounter.Value);
            Assert.Equal(CncComponentStates.Available, first.Snapshot.ComponentHealth["DPRNT"]);
            Assert.Equal(CncComponentStates.Unsupported, first.Snapshot.ComponentHealth["MTCONNECT"]);
            Assert.Equal(CncComponentStates.Unsupported, first.Snapshot.CapabilityHealth["machineState"]);
            Assert.Single(first.RawTelemetry, value => value.Operation == "DPRINT_EVENT" && value.RawPayload == "MEIMAD/V/1/OLC/AAA");
            Assert.Equal("30P647004101-001", quiet.Snapshot.Program.PartName.Value);
            Assert.Empty(quiet.RawTelemetry);
            Assert.True(test.OverallSuccess);
            Assert.Contains(test.Checks, value => value.Id == "dprnt" && value.Succeeded);
            Assert.False((await adapter.ReadPartCounterAsync()).Supported);
            Assert.False(adapter.GetCapabilities().CanReadProgramHeader);
            Assert.Equal(0L, new FileInfo(path).Length);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task DPRNT_only_adapter_reports_a_missing_print_file_as_offline_without_throwing()
    {
        var path = Path.Combine(Path.GetTempPath(), "MeimadPlanner.DprntMissing", Guid.NewGuid().ToString("N"), "print.txt");
        await using var adapter = new HaasNgcAdapter(
            Connection("DPRNT", programAccess: false, dprnt: new("FILE", path, "AFTER_READ")),
            new FakeHaasMdcClientFactory(new FakeHaasMdcClient { Disconnected = true }),
            new FakeHaasMtConnectReader(),
            new FakeHaasProgramReader("unused.nc", []),
            new NcHeaderParser(), TimeProvider.System);

        var snapshot = await adapter.ReadSnapshotAsync();
        var test = await adapter.TestConnectionAsync();

        Assert.Equal(CncConnectionStates.Offline, snapshot.Snapshot.ConnectionStatus);
        Assert.Null(snapshot.Snapshot.LastSeenAt);
        Assert.Null(snapshot.Snapshot.Program.PartName.Value);
        Assert.Equal(CncComponentStates.Unavailable, snapshot.Snapshot.ComponentHealth["DPRNT"]);
        Assert.Contains("print.txt", snapshot.Snapshot.LastError, StringComparison.Ordinal);
        Assert.False(test.OverallSuccess);
        Assert.Equal(CncConnectionStates.Offline, test.ConnectionStatus);
    }

    [Fact]
    public async Task MTConnect_adapter_with_a_DPRNT_file_source_degrades_when_the_file_is_unreadable()
    {
        var path = Path.Combine(Path.GetTempPath(), "MeimadPlanner.DprntMissing", Guid.NewGuid().ToString("N"), "print.txt");
        var mtConnect = new FakeHaasMtConnectReader
        {
            Result = new("dev1", "VF-3SS", "AVAILABLE", "ACTIVE", "AUTOMATIC",
                "1500.CNC", 9302, DateTimeOffset.UtcNow, 4200m, 1250m, 0, "{}")
        };
        await using var adapter = new HaasNgcAdapter(
            Connection("MTCONNECT", programAccess: false, dprnt: new("FILE", path, "NEVER")),
            new FakeHaasMdcClientFactory(new FakeHaasMdcClient { Disconnected = true }), mtConnect,
            new FakeHaasProgramReader("unused.nc", []),
            new NcHeaderParser(), TimeProvider.System);

        var snapshot = await adapter.ReadSnapshotAsync();
        var test = await adapter.TestConnectionAsync();

        Assert.Equal(CncConnectionStates.Degraded, snapshot.Snapshot.ConnectionStatus);
        Assert.Equal("ACTIVE", snapshot.Snapshot.MachineState.Value);
        Assert.Equal(CncComponentStates.Unavailable, snapshot.Snapshot.ComponentHealth["DPRNT"]);
        Assert.Equal(CncComponentStates.Unavailable, snapshot.Snapshot.CapabilityHealth["dprntFile"]);
        Assert.Contains("print.txt", snapshot.Snapshot.LastError, StringComparison.Ordinal);
        Assert.False(test.OverallSuccess);
        Assert.Equal(CncConnectionStates.Degraded, test.ConnectionStatus);
        Assert.Contains(test.Checks, value => value.Id == "dprntFile" && !value.Succeeded);
    }

    [Fact]
    public void Adapter_rejects_a_FILE_DPRNT_source_without_a_path_and_an_unknown_provider()
    {
        var factory = new FakeHaasMdcClientFactory(new FakeHaasMdcClient { Disconnected = true });
        Assert.Throws<CncValidationException>(() => new HaasNgcAdapter(
            Connection("DPRNT", programAccess: false, dprnt: new("FILE", null, "NEVER")),
            factory, new FakeHaasMtConnectReader(), new FakeHaasProgramReader("unused.nc", []),
            new NcHeaderParser(), TimeProvider.System));
        Assert.Throws<CncValidationException>(() => new HaasNgcAdapter(
            Connection("SERIAL", programAccess: false),
            factory, new FakeHaasMtConnectReader(), new FakeHaasProgramReader("unused.nc", []),
            new NcHeaderParser(), TimeProvider.System));
    }

    private static MachineConnection Connection(
        string telemetryProvider = "MDC", bool programAccess = true, CncDprntConfiguration? dprnt = null)
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = new HaasNgcConnectionConfiguration(
            "192.0.2.1", new(5051, 3000),
            new(programAccess ? "HAAS_LOCAL_NET_SHARE" : "NONE", programAccess,
                programAccess ? @"\\haas\User Data" : null, null, null,
                50, 32768, NcHeaderParser.DefaultPartPatterns),
            new("Q500"), new(500, 2, 30000, 14),
            new(8082, 3000), telemetryProvider, "44:B1:76:B0:26:68", dprnt);
        return new("cnc-machine-a", "machine-a", CncAdapterType.HaasNgc, true,
            CncConnectionStates.Offline, null, null, null, null, 500, 3000, 30000,
            true, false, JsonSerializer.Serialize(configuration, CncJson.Options),
            null, null, 14, 1, now, now);
    }

    private sealed class FailingProgramProvider : INcProgramFileProvider
    {
        public Task<Meimad.Planner.Server.Domain.Haas.MachineNcHeader> ReadActiveProgramHeaderAsync(
            Meimad.Planner.Server.Domain.Haas.HaasConnectionSettings settings, string programNumber,
            CancellationToken cancellationToken = default) =>
            throw new IOException("Share unavailable");
    }
}
