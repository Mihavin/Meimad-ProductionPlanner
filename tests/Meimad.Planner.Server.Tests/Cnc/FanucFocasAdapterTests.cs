using System.Text.Json;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.Fanuc;
using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Infrastructure.Cnc;

namespace Meimad.Planner.Server.Tests.Cnc;

public sealed class FanucFocasAdapterTests
{
    private const string OffsetLoader =
        "MEIMAD/V/1/EVENT/OLC/ID/OLC-1/SEQ/1/MACROVERSION/6/PROGRAM/654321/OFFSETRELEASE/483920/NONCE/731841";

    [Fact]
    public async Task FOCAS_adapter_composes_normalized_snapshot_from_status_counter_and_print_file()
    {
        var directory = Directory.CreateTempSubdirectory("MeimadPlanner.FocasAdapter");
        try
        {
            var path = Path.Combine(directory.FullName, "print.txt");
            await File.WriteAllTextAsync(path, "30P647004101-001\r\n");
            var client = new FakeFocasClient();
            await using var adapter = new FanucFocasAdapter(
                Connection(dprnt: new("FILE", path, "ON_OFFSET_LOADER")),
                new FakeFocasClientFactory(client), new NcHeaderParser(), TimeProvider.System);

            await adapter.ConnectAsync();
            var first = await adapter.ReadSnapshotAsync();
            await File.AppendAllTextAsync(path, OffsetLoader + "\r\n");
            var second = await adapter.ReadSnapshotAsync();
            var test = await adapter.TestConnectionAsync();

            Assert.True(client.Connected);
            Assert.Equal(CncConnectionStates.Online, first.Snapshot.ConnectionStatus);
            Assert.Equal("ACTIVE", first.Snapshot.MachineState.Value);
            Assert.Equal("O1234", first.Snapshot.Program.ProgramNumber.Value);
            Assert.Equal("30P647004101-001", first.Snapshot.Program.PartName.Value);
            Assert.Equal(27, first.Snapshot.PartCounter.Value);
            Assert.Equal(4200m, first.Snapshot.Telemetry.SpindleRpm);
            Assert.Equal(1250m, first.Snapshot.Telemetry.FeedRate);
            Assert.Equal(0, first.Snapshot.Telemetry.ActiveAlarmCount);
            Assert.Equal(CncComponentStates.Available, first.Snapshot.ComponentHealth["FOCAS"]);
            Assert.Equal(CncComponentStates.Available, first.Snapshot.ComponentHealth["DPRNT"]);
            Assert.Equal(CncComponentStates.Unsupported, first.Snapshot.ComponentHealth["PROGRAM_ACCESS"]);
            Assert.Single(first.RawTelemetry, value => value.Operation == "FOCAS_STATUS");
            Assert.Single(second.RawTelemetry, value => value.Operation == "DPRINT_EVENT" && value.RawPayload == OffsetLoader);
            Assert.Equal(CncAdapterTypes.FanucFocas, second.RawTelemetry[0].AdapterType);
            Assert.Equal(0L, new FileInfo(path).Length);
            Assert.Equal("30P647004101-001", second.Snapshot.Program.PartName.Value);
            Assert.False(adapter.GetCapabilities().CanWriteVariables);
            Assert.False(adapter.GetCapabilities().CanReadVariables);
            Assert.False((await adapter.WriteVariableAsync(500, 1)).Supported);
            Assert.True(test.OverallSuccess);
            Assert.Equal(CncConnectionStates.Online, test.ConnectionStatus);
            Assert.Contains(test.Checks, value => value.Id == "focas" && value.Succeeded && value.Message.Contains("O1234", StringComparison.Ordinal));
            Assert.Contains(test.Checks, value => value.Id == "dprnt" && value.Succeeded);
            Assert.Equal(27, (await adapter.ReadPartCounterAsync()).Value);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Missing_FOCAS_library_or_unreachable_controller_fails_the_focas_check_without_throwing()
    {
        var client = new FakeFocasClient
        {
            ConnectError = new FocasLibraryUnavailableException("FOCAS library Fwlib64.dll was not found.")
        };
        await using var adapter = new FanucFocasAdapter(
            Connection(dprnt: new("NONE")),
            new FakeFocasClientFactory(client), new NcHeaderParser(), TimeProvider.System);

        var test = await adapter.TestConnectionAsync();
        var poll = await Assert.ThrowsAsync<FocasLibraryUnavailableException>(() => adapter.ConnectAsync());

        Assert.False(test.OverallSuccess);
        Assert.Equal(CncConnectionStates.Offline, test.ConnectionStatus);
        Assert.Contains(test.Checks, value => value.Id == "focas" && !value.Succeeded
            && value.Message.Contains("Fwlib64.dll", StringComparison.Ordinal));
        Assert.Contains(test.Checks, value => value.Id == "dprnt" && value.Status == CncComponentStates.Unsupported);
        Assert.Contains("Fwlib64.dll", poll.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Program_upload_supplies_the_header_Part_when_no_DPRNT_line_exists()
    {
        var client = new FakeFocasClient { ProgramHead = ["O1234", "(PART: PART-A)", "G90 G54"] };
        await using var adapter = new FanucFocasAdapter(
            Connection(dprnt: new("NONE"), programUpload: true),
            new FakeFocasClientFactory(client), new NcHeaderParser(), TimeProvider.System);

        await adapter.ConnectAsync();
        var first = await adapter.ReadSnapshotAsync();
        var second = await adapter.ReadSnapshotAsync();
        var test = await adapter.TestConnectionAsync();

        Assert.True(adapter.GetCapabilities().CanReadProgramHeader);
        Assert.Equal(CncConnectionStates.Degraded, first.Snapshot.ConnectionStatus);
        Assert.Null(first.Snapshot.Program.PartName.Value);
        Assert.Equal(CncConnectionStates.Online, second.Snapshot.ConnectionStatus);
        Assert.Equal("PART-A", second.Snapshot.Program.PartName.Value);
        Assert.Equal("//CNC_MEM/USER/PATH1/O1234", second.Snapshot.Program.HeaderSourcePath.Value);
        Assert.Equal("//CNC_MEM/USER/PATH1/O1234", client.LastProgramPath);
        Assert.Equal(CncComponentStates.Available, second.Snapshot.ComponentHealth["PROGRAM_ACCESS"]);
        Assert.Equal(CncComponentStates.Unsupported, second.Snapshot.ComponentHealth["DPRNT"]);
        Assert.Contains(test.Checks, value => value.Id == "programAccess" && value.Succeeded
            && value.Message.Contains("PART-A", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Controller_alarm_and_counter_fault_are_reported_without_discarding_state()
    {
        var client = new FakeFocasClient
        {
            Status = new("ALARM", "MEM", "O1234", "O1234", false, true, 2, 0m, 0m, "{}"),
            CounterError = new FocasException("FOCAS cnc_rdparam 6711 returned 6.", 6)
        };
        await using var adapter = new FanucFocasAdapter(
            Connection(dprnt: new("NONE")),
            new FakeFocasClientFactory(client), new NcHeaderParser(), TimeProvider.System);

        var snapshot = await adapter.ReadSnapshotAsync();

        Assert.Equal(CncConnectionStates.Degraded, snapshot.Snapshot.ConnectionStatus);
        Assert.Equal("ALARM", snapshot.Snapshot.MachineState.Value);
        Assert.Equal(2, snapshot.Snapshot.Telemetry.ActiveAlarmCount);
        Assert.Null(snapshot.Snapshot.PartCounter.Value);
        Assert.Equal(CncComponentStates.Unavailable, snapshot.Snapshot.CapabilityHealth["partCounter"]);
        Assert.Contains("6711", snapshot.Snapshot.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapter_rejects_a_missing_MAC_address_an_unknown_counter_source_and_a_FILE_source_without_a_path()
    {
        var factory = new FakeFocasClientFactory(new FakeFocasClient());
        Assert.Throws<CncValidationException>(() => new FanucFocasAdapter(
            Connection(macAddress: null), factory, new NcHeaderParser(), TimeProvider.System));
        Assert.Throws<CncValidationException>(() => new FanucFocasAdapter(
            Connection(partCounterSource: "MACRO_3901"), factory, new NcHeaderParser(), TimeProvider.System));
        Assert.Throws<CncValidationException>(() => new FanucFocasAdapter(
            Connection(dprnt: new("FILE", null, "NEVER")), factory, new NcHeaderParser(), TimeProvider.System));
        Assert.Throws<CncValidationException>(() => new FanucFocasAdapter(
            Connection(dprnt: new("FILE", @"\\fanuc\print\print.txt", "SOMETIMES")), factory, new NcHeaderParser(), TimeProvider.System));
    }

    private static MachineConnection Connection(
        CncDprntConfiguration? dprnt = null,
        bool programUpload = false,
        string? macAddress = "44:B1:76:B0:26:69",
        string partCounterSource = "PARTS_COUNT_6711")
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = new FanucFocasConnectionConfiguration(
            "192.0.2.2", macAddress, 8193, 3000, partCounterSource, dprnt,
            new FocasProgramAccessConfiguration(
                programUpload ? FocasProgramAccessConfiguration.UploadProvider : "NONE", programUpload,
                FocasProgramAccessConfiguration.DefaultProgramFolder, 50, 32768, NcHeaderParser.DefaultPartPatterns),
            new HaasMonitoringConfiguration(500, 2, 30000, 14));
        return new("cnc-machine-f", "machine-f", CncAdapterType.FanucFocas, true,
            CncConnectionStates.Offline, null, null, null, null, 500, 3000, 30000,
            true, false, JsonSerializer.Serialize(configuration, CncJson.Options),
            null, null, 14, 1, now, now);
    }
}

internal sealed class FakeFocasClientFactory(FakeFocasClient client) : IFocasClientFactory
{
    public IFocasClient Create(FanucFocasConnectionConfiguration configuration) => client;
}

internal sealed class FakeFocasClient : IFocasClient
{
    public FocasControllerStatus Status { get; set; } =
        new("ACTIVE", "MEM", "O1234", "O1234", false, false, 0, 4200m, 1250m, "{\"run\":3}");
    public int Counter { get; set; } = 27;
    public Exception? ConnectError { get; set; }
    public Exception? CounterError { get; set; }
    public IReadOnlyList<string> ProgramHead { get; set; } = [];
    public string? LastProgramPath { get; private set; }
    public bool Connected { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (ConnectError is not null) throw ConnectError;
        Connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Connected = false;
        return Task.CompletedTask;
    }

    public Task<FocasControllerStatus> ReadStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Status);

    public Task<int> ReadPartCounterAsync(string source, CancellationToken cancellationToken = default) =>
        CounterError is null ? Task.FromResult(Counter) : Task.FromException<int>(CounterError);

    public Task<IReadOnlyList<string>> ReadProgramHeadAsync(
        string programPath, int byteLimit, int lineLimit, CancellationToken cancellationToken = default)
    {
        LastProgramPath = programPath;
        return Task.FromResult(ProgramHead);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
