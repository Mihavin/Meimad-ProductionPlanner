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
            new FakeHaasMdcClientFactory(mdc), new FailingProgramProvider(),
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

    private static MachineConnection Connection()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = new HaasNgcConnectionConfiguration(
            "127.0.0.1", new(5051, 3000),
            new("HAAS_LOCAL_NET_SHARE", true, @"\\haas\User Data", null, null,
                50, 32768, NcHeaderParser.DefaultPartPatterns),
            new(10605, 605, "Q500"), new(500, 2, 30000, 14));
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
