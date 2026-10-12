using System.Net;
using System.Net.Sockets;
using System.Text;
using Meimad.Planner.Server.Application.MachineEvidence;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Infrastructure.Cnc;
using Meimad.Planner.Server.Infrastructure.Haas;

namespace Meimad.Planner.Server.Tests.MachineEvidence;

/// <summary>
/// Exporter definition §20.15 acceptance tests 4 and 5: the work offset frame arrives through the
/// existing DPRNT sources (TCP serial-to-Ethernet bridge, Mazak print file) exactly as the PartName and
/// Offset Loader lines do, and the parser result does not depend on the transport or the control.
/// </summary>
public sealed class WorkOffsetTransportTests
{
    [Fact]
    public async Task Acceptance_4_a_FANUC_frame_through_a_serial_to_LAN_bridge_equals_the_Haas_result()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            await using var source = new CncDprntSource("10.0.0.11", 8193, 2000,
                new CncDprntConfiguration(CncDprntSources.Tcp, Port: port, Host: "127.0.0.1"));
            var accept = listener.AcceptTcpClientAsync();
            await source.DrainAsync(allowClear: true, CancellationToken.None);
            using var bridge = await accept;

            // FANUC: DC2 from POPEN, LF + CR after each DPRNT (parameter 6001#4), blanks before numbers
            // (6001#1), DC4 from PCLOS; the bridge delivers it in arbitrary pieces.
            var fanuc = "\u0012MEIMAD/OFFSET-START/SESSION/WO-10-20261012- 63000-1/MACHINE/10/PROGRAM/654321/OPERATION/20/RELEASE/654321\n\r"
                + "MEIMAD/OFFSET/SESSION/WO-10-20261012- 63000-1/OFFSET/G54/UNITS/  21./AXES/XYZ\n\r"
                + "MEIMAD/OFFSET/SESSION/WO-10-20261012- 63000-1/X/  125.4020/Y/ -80.0160/Z/   42.6910\n\r"
                + "MEIMAD/OFFSET-END/SESSION/WO-10-20261012- 63000-1\n\r\u0014";
            var bytes = Encoding.ASCII.GetBytes(fanuc);
            var stream = bridge.GetStream();
            var eventLines = new List<string>();
            for (var offset = 0; offset < bytes.Length; offset += 37)
            {
                await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(37, bytes.Length - offset)));
                await stream.FlushAsync();
                await Task.Delay(20);
                eventLines.AddRange((await source.DrainAsync(allowClear: true, CancellationToken.None)).Result.EventLines);
            }
            for (var attempt = 0; attempt < 50 && eventLines.Count < 4; attempt++)
            {
                await Task.Delay(20);
                eventLines.AddRange((await source.DrainAsync(allowClear: true, CancellationToken.None)).Result.EventLines);
            }

            Assert.Equal(4, eventLines.Count);
            var fromBridge = Parse(eventLines);
            var fromHaas = Parse(OffsetEvidenceParserTests.HaasFrame);
            Assert.Equal(OffsetEvidenceParseOutcome.Completed, fromBridge.Outcome);
            Assert.Equal(fromHaas.Message!.ContentHash(), fromBridge.Message!.ContentHash());
            Assert.Equal(fromHaas.Message.Values, fromBridge.Message.Values);
            Assert.Equal(fromHaas.Message.Units, fromBridge.Message.Units);

            // And through the Server pipeline both store the same current measurement.
            await using var fixture = await WorkOffsetIngestTests.CreateAsync();
            await fixture.FeedAsync("machine-10", eventLines);
            var stored = Assert.Single((await fixture.ReadAsync("machine-10")).Current);
            Assert.Equal(fromHaas.Message.ContentHash(), stored.RawMessageHash);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Acceptance_5_lines_the_Mazak_appends_to_the_print_file_during_a_read_are_not_lost()
    {
        var folder = Path.Combine(Path.GetTempPath(), "MeimadPlanner.WorkOffsetFile.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "print.txt");
        try
        {
            var frame = WorkOffsetTestData.Frame("WO-12-20261012-63000-1", machine: "12");
            await File.WriteAllTextAsync(path, "30P647004101-001\r\n" + frame[0] + "\r\n" + frame[1] + "\r\n");
            var reader = new HaasDprntFileReader();
            var collected = new List<string>();

            // The Server empties the file after every read (AFTER_READ): the first read sees two lines.
            collected.AddRange((await reader.DrainAsync(path, replayExistingContent: true, CancellationToken.None)).EventLines);
            Assert.Equal(2, collected.Count);

            // The control appends a line - and half of the next one - between the read and the clear.
            await File.AppendAllTextAsync(path, frame[2] + "\r\n" + frame[3][..20]);
            Assert.False(reader.TryTruncateConsumed(path));

            collected.AddRange((await reader.DrainAsync(path, replayExistingContent: true, CancellationToken.None)).EventLines);
            Assert.Equal(3, collected.Count);
            Assert.False(reader.TryTruncateConsumed(path)); // a line is still half-written

            await File.AppendAllTextAsync(path, frame[3][20..] + "\r\n");
            collected.AddRange((await reader.DrainAsync(path, replayExistingContent: true, CancellationToken.None)).EventLines);
            Assert.True(reader.TryTruncateConsumed(path));
            Assert.Equal(0, new FileInfo(path).Length);
            Assert.Equal(frame, collected);

            await using var fixture = await WorkOffsetIngestTests.CreateAsync();
            await fixture.FeedAsync("machine-12", collected);
            var stored = Assert.Single(await fixture.Service.ListAsync("VALID", "machine-12", null, null));
            Assert.Equal("G54", stored.WorkOffset);
            Assert.Equal(125.402, stored.PositionMm.X!.Value, 6);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_frame_split_over_several_polls_of_the_FILE_source_is_joined_by_the_consumer()
    {
        await using var fixture = await WorkOffsetIngestTests.CreateAsync();
        var frame = WorkOffsetTestData.Frame("WO-12-20261012-63000-1", machine: "12");
        foreach (var line in frame)
        {
            Assert.Empty(await fixture.Service.ListAsync(null, "machine-12", null, null));
            await fixture.FeedAsync("machine-12", [line]);
        }
        Assert.Single(await fixture.Service.ListAsync("VALID", "machine-12", null, null));
    }

    private static OffsetEvidenceParseResult Parse(IEnumerable<string> lines)
    {
        var parser = new OffsetEvidenceParser();
        var result = OffsetEvidenceParseResult.Ignored;
        foreach (var line in lines) result = parser.Accept(line);
        return result;
    }
}
