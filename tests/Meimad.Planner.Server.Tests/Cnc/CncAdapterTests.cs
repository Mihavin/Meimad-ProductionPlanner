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
        Assert.Equal("SETUP", second.Snapshot.Production.Mode);
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
        Assert.Equal("PRODUCTION", result.Snapshot.Production.Mode);
        Assert.Equal(9, result.Snapshot.PartCounter.Value);
        Assert.Null(result.Snapshot.Program.PartName.Value);
        Assert.Equal(CncComponentStates.Available, result.Snapshot.ComponentHealth["MDC"]);
        Assert.Equal(CncComponentStates.Unavailable, result.Snapshot.ComponentHealth["PROGRAM_ACCESS"]);
    }

    [Fact]
    public async Task Generic_write_contract_rejects_everything_except_controlled_zero_reset()
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

        Assert.False(deniedVariable.Available);
        Assert.False(deniedValue.Available);
        Assert.True(reset.Available);
        Assert.Equal(0, mdc.MacroValue);
    }

    [Fact]
    public async Task Haas_adapter_uses_explicit_MTConnect_read_source_without_opening_MDC()
    {
        var mdc = new FakeHaasMdcClient { Disconnected = true };
        var mtConnect = new FakeHaasMtConnectReader
        {
            Result = new("dev1", "VF-3SS", "AVAILABLE", "ACTIVE", "AUTOMATIC",
                "1500.CNC", 9302, 0, null, DateTimeOffset.UtcNow,
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
        Assert.Equal("SETUP", result.Snapshot.Production.Mode);
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
    public async Task Nonbinary_MTConnect_macro_keeps_connection_degraded_and_blocks_production_mode()
    {
        var mtConnect = new FakeHaasMtConnectReader
        {
            Result = new("dev1", "VF-3SS", "AVAILABLE", "STOPPED", "AUTOMATIC",
                "1500.CNC", 9302, null,
                "MTConnect reported configured variable #10605 as '5.0'; Setup/Production requires exactly 0 or 1.",
                DateTimeOffset.UtcNow, 0, null, 0, "{}")
        };
        await using var adapter = new HaasNgcAdapter(Connection("MTCONNECT", programAccess: false),
            new FakeHaasMdcClientFactory(new FakeHaasMdcClient { Disconnected = true }), mtConnect,
            new FakeHaasProgramReader("unused.nc", []),
            new NcHeaderParser(), TimeProvider.System);

        var snapshot = await adapter.ReadSnapshotAsync();
        var test = await adapter.TestConnectionAsync();

        Assert.Equal(CncConnectionStates.Degraded, snapshot.Snapshot.ConnectionStatus);
        Assert.Null(snapshot.Snapshot.Production.Mode);
        Assert.Null(snapshot.Snapshot.Production.ModeVariableValue.Value);
        Assert.Contains("exactly 0 or 1", snapshot.Snapshot.LastError, StringComparison.Ordinal);
        Assert.True(test.OverallSuccess);
        Assert.Equal(CncConnectionStates.Degraded, test.ConnectionStatus);
        Assert.Contains(test.Checks, value => value.Id == "variableRead" && !value.Succeeded);
    }

    [Fact]
    public async Task Missing_MTConnect_state_or_program_is_not_reported_as_healthy_or_online()
    {
        var mtConnect = new FakeHaasMtConnectReader
        {
            Result = new("dev1", "VF-3SS", "AVAILABLE", null, "AUTOMATIC",
                null, 9302, 0, null, DateTimeOffset.UtcNow,
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
              "host":"127.0.0.1",
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

    private static MachineConnection Connection(string telemetryProvider = "MDC", bool programAccess = true)
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = new HaasNgcConnectionConfiguration(
            "127.0.0.1", new(5051, 3000),
            new(programAccess ? "HAAS_LOCAL_NET_SHARE" : "NONE", programAccess,
                programAccess ? @"\\haas\User Data" : null, null, null,
                50, 32768, NcHeaderParser.DefaultPartPatterns),
            new(10605, 605, "Q500"), new(500, 2, 30000, 14),
            new(8082, 3000), telemetryProvider);
        return new("cnc-machine-a", "machine-a", CncAdapterType.HaasNgc, true,
            CncConnectionStates.Offline, null, null, null, null, 500, 3000, 30000,
            true, true, JsonSerializer.Serialize(configuration, CncJson.Options),
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
