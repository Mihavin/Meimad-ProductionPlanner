using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Infrastructure.Cnc;

namespace Meimad.Planner.Server.Tests.Cnc;

public sealed class CncDprntSourceTests : IDisposable
{
    private const string OffsetLoaderOne =
        "MEIMAD/V/1/EVENT/OLC/ID/OLC-1/SEQ/1/MACROVERSION/6/PROGRAM/654321/OFFSETRELEASE/483920/NONCE/731841";
    private const string OffsetLoaderTwo =
        "MEIMAD/V/1/EVENT/OLC/ID/OLC-2/SEQ/4/MACROVERSION/6/PROGRAM/654321/OFFSETRELEASE/483921/NONCE/731842";
    private const string CycleStart =
        "MEIMAD/V/1/EVENT/CST/ID/NC-654321-S-1/SEQ/2/MACROVERSION/6/PROGRAM/654321";
    private readonly string path = Path.Combine(
        Path.GetTempPath(), "MeimadPlanner.DprntSource", Guid.NewGuid().ToString("N"), "print.txt");

    public CncDprntSourceTests() => Directory.CreateDirectory(Path.GetDirectoryName(path)!);

    [Fact]
    public async Task ON_OFFSET_LOADER_empties_the_print_file_only_after_a_new_Offset_Loader_completion()
    {
        await File.WriteAllTextAsync(path, "30P647004101-001\r\n");
        await using var source = new CncDprntSource("192.0.2.1", 8080, 3000,
            new CncDprntConfiguration("FILE", path, "ON_OFFSET_LOADER"));

        var initial = await source.DrainAsync(allowClear: true, CancellationToken.None);
        Assert.Equal("30P647004101-001", initial.Result.PartName);
        Assert.True(new FileInfo(path).Length > 0);

        await File.AppendAllTextAsync(path, OffsetLoaderOne + "\r\n");
        var loader = await source.DrainAsync(allowClear: true, CancellationToken.None);
        Assert.Equal(new[] { OffsetLoaderOne }, loader.Result.EventLines);
        Assert.Equal(0L, new FileInfo(path).Length);
        Assert.Null(loader.Error);

        await File.AppendAllTextAsync(path, CycleStart + "\r\n");
        var cycle = await source.DrainAsync(allowClear: true, CancellationToken.None);
        Assert.Equal(new[] { CycleStart }, cycle.Result.EventLines);
        Assert.True(new FileInfo(path).Length > 0);

        // A controller retry of the same Offset Loader line is the same run: nothing is emptied.
        await File.AppendAllTextAsync(path, OffsetLoaderOne + "\r\n");
        var retry = await source.DrainAsync(allowClear: true, CancellationToken.None);
        Assert.Equal(new[] { OffsetLoaderOne }, retry.Result.EventLines);
        Assert.True(new FileInfo(path).Length > 0);

        await File.AppendAllTextAsync(path, "16E2509-7PSOFI-1\r\n" + OffsetLoaderTwo + "\r\n");
        var next = await source.DrainAsync(allowClear: true, CancellationToken.None);
        Assert.Equal("16E2509-7PSOFI-1", next.Result.PartName);
        Assert.Equal(new[] { OffsetLoaderTwo }, next.Result.EventLines);
        Assert.Equal(0L, new FileInfo(path).Length);
    }

    [Fact]
    public async Task A_diagnostics_probe_never_empties_the_file()
    {
        await File.WriteAllTextAsync(path, "30P647004101-001\r\n");
        await using var source = new CncDprntSource("192.0.2.1", 8080, 3000,
            new CncDprntConfiguration("FILE", path, "AFTER_READ"));

        var probe = await source.DrainAsync(allowClear: false, CancellationToken.None);

        Assert.Equal("30P647004101-001", probe.Result.PartName);
        Assert.True(probe.Available);
        Assert.True(new FileInfo(path).Length > 0);
        Assert.Contains("print.txt", source.SuccessMessage(probe), StringComparison.Ordinal);
        Assert.Contains("30P647004101-001", source.SuccessMessage(probe), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFTER_READ_replays_existing_content_and_empties_after_every_consumed_poll()
    {
        await File.WriteAllTextAsync(path, "30P647004101-001\r\n" + CycleStart + "\r\n");
        await using var source = new CncDprntSource("192.0.2.1", 8080, 3000,
            new CncDprntConfiguration("FILE", path, "AFTER_READ"));

        var first = await source.DrainAsync(allowClear: true, CancellationToken.None);

        Assert.Equal("30P647004101-001", first.Result.PartName);
        Assert.Equal(new[] { CycleStart }, first.Result.EventLines);
        Assert.Equal(0L, new FileInfo(path).Length);
    }

    [Fact]
    public async Task NEVER_keeps_the_file_and_an_unreadable_file_is_reported_as_unavailable()
    {
        await File.WriteAllTextAsync(path, "30P647004101-001\r\n" + OffsetLoaderOne + "\r\n");
        await using var source = new CncDprntSource("192.0.2.1", 8080, 3000,
            new CncDprntConfiguration("FILE", path, "NEVER"));

        var first = await source.DrainAsync(allowClear: true, CancellationToken.None);
        Assert.Equal("30P647004101-001", first.Result.PartName);
        Assert.True(new FileInfo(path).Length > 0);

        var components = new Dictionary<string, string>();
        var health = new Dictionary<string, string>();
        string? error = null;
        source.ApplyFileHealth(first, components, health, ref error);
        Assert.Equal(CncComponentStates.Available, components["DPRNT"]);
        Assert.Equal(CncComponentStates.Available, health["dprntFile"]);
        Assert.Null(error);

        File.Delete(path);
        var missing = await source.DrainAsync(allowClear: true, CancellationToken.None);
        Assert.False(missing.Available);
        Assert.Contains("print.txt", missing.Error, StringComparison.Ordinal);
        source.ApplyFileHealth(missing, components, health, ref error);
        Assert.Equal(CncComponentStates.Unavailable, components["DPRNT"]);
        Assert.Contains("print.txt", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NONE_source_is_disabled_and_a_TCP_source_uses_the_configured_port()
    {
        await using var none = new CncDprntSource("192.0.2.1", 8080, 3000, new CncDprntConfiguration("NONE"));
        await using var tcp = new CncDprntSource("192.0.2.1", 8080, 3000, new CncDprntConfiguration("TCP", Port: 9100));
        await using var legacy = new CncDprntSource("192.0.2.1", 8080, 3000, null);

        var disabled = await none.DrainAsync(allowClear: true, CancellationToken.None);

        Assert.False(none.Enabled);
        Assert.True(disabled.Available);
        Assert.Null(disabled.Result.PartName);
        Assert.Equal(9100, tcp.TcpPort);
        Assert.True(tcp.UsesTcp);
        Assert.Equal(8080, legacy.TcpPort);
        Assert.Equal(CncDprntClearPolicies.Never, legacy.ClearPolicy);
        Assert.Contains("9100", tcp.Description, StringComparison.Ordinal);
        Assert.Equal("192.0.2.1", tcp.TcpHost);
    }

    [Fact]
    public async Task A_serial_to_Ethernet_bridge_address_replaces_the_controller_address_for_the_TCP_source()
    {
        await using var bridged = new CncDprntSource("192.0.2.1", 8080, 3000,
            new CncDprntConfiguration("TCP", Port: 4001, Host: " 192.0.2.90 "));

        Assert.Equal("192.0.2.90", bridged.TcpHost);
        Assert.Equal(4001, bridged.TcpPort);
        Assert.Contains("192.0.2.90", bridged.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.1", bridged.Description, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
    }
}
