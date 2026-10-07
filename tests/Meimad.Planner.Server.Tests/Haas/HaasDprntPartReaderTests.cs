using Meimad.Planner.Server.Infrastructure.Haas;

namespace Meimad.Planner.Server.Tests.Haas;

public sealed class HaasDprntPartReaderTests
{
    [Fact]
    public async Task Detects_a_peer_closed_socket_instead_of_trusting_connected_flag()
    {
        var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (System.Net.IPEndPoint)listener.LocalEndpoint;
        using var client = new System.Net.Sockets.TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, endpoint.Port);
        using var peer = await accept;
        peer.Dispose();
        listener.Stop();

        Assert.True(SpinWait.SpinUntil(
            () => HaasDprntPartReader.IsDisconnected(client.Client),
            TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task The_TCP_stream_returns_every_complete_line_for_the_DPRNT_log()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        await using var reader = new HaasDprntPartReader();
        var accept = listener.AcceptTcpClientAsync();
        await reader.DrainAsync("127.0.0.1", port, 2000, CancellationToken.None);
        using var machine = await accept;
        var bytes = System.Text.Encoding.ASCII.GetBytes(
            "pingret\r\n30P647004101-001\r\n\r\nMEIMAD/V/1/EVENT/CST/ID/X/SEQ/1\r\nT12 D=10.000\r\npart");
        await machine.GetStream().WriteAsync(bytes);

        HaasDprntDrainResult? result = null;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            result = reader.DrainAsync("127.0.0.1", port, 2000, CancellationToken.None).GetAwaiter().GetResult();
            return result.AllLines.Count > 0;
        }, TimeSpan.FromSeconds(3)));
        listener.Stop();

        Assert.Equal(new[] { "pingret", "30P647004101-001", "MEIMAD/V/1/EVENT/CST/ID/X/SEQ/1", "T12 D=10.000" }, result!.AllLines);
        Assert.Equal("30P647004101-001", result.PartName);
        Assert.Equal(new[] { "MEIMAD/V/1/EVENT/CST/ID/X/SEQ/1" }, result.EventLines);
    }

    [Theory]
    [InlineData("30P647004101-001", "30P647004101-001")]
    [InlineData(" 16e2509-7psofi-1 ", "16E2509-7PSOFI-1")]
    public void Parses_control_authored_part_number_lines(string line, string expected)
    {
        Assert.True(HaasDprntPartReader.TryParsePartName(line, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("pingret")]
    [InlineData("1500.CNC")]
    [InlineData("O1500")]
    [InlineData("Part Name: 30P647004101-001")]
    [InlineData("PART=30P647004101-001")]
    [InlineData("ABC123")]
    public void Rejects_non_part_or_unstructured_protocol_lines(string line)
    {
        Assert.False(HaasDprntPartReader.TryParsePartName(line, out var actual));
        Assert.Null(actual);
    }

    [Theory]
    [InlineData("30P647004101-001", "30P647004101-001")]
    [InlineData("MEIMAD/V/1/EVENT/CST/ID/NC-1-S-1/SEQ/1/MACROVERSION/6/PROGRAM/654321", "MEIMAD/V/1/EVENT/CST/ID/NC-1-S-1/SEQ/1/MACROVERSION/6/PROGRAM/654321")]
    [InlineData("\0\t30P647004101-001", "\t30P647004101-001")]
    [InlineData("30P647004101-001", "30P647004101-001")]
    public void Strips_FANUC_POPEN_PCLOS_control_codes_and_padding_but_keeps_tabs(string line, string expected)
    {
        Assert.Equal(expected, HaasDprntPartReader.StripControlCharacters(line));
    }
}
