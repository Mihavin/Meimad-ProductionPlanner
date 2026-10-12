using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.MachineEvidence;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Infrastructure.Cnc;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Cnc;

/// <summary>The SERIAL DPRNT source (a control wired to a COM port of the Server PC), with a fake serial line.</summary>
public sealed class CncSerialDprntSourceTests
{
    [Fact]
    public async Task The_serial_source_returns_lines_like_the_TCP_stream_and_joins_partial_lines()
    {
        var link = new FakeLink();
        await using var source = new CncDprntSource("10.0.0.11", 8193, 2000,
            new CncDprntConfiguration(CncDprntSources.Serial, SerialPort: "com3", BaudRate: 4800, Parity: "even", DataBits: 7, StopBits: 2),
            settings =>
            {
                Assert.Equal(new CncSerialDprntSettings("COM3", 4800, "EVEN", 7, 2), settings);
                return link;
            });
        Assert.True(source.UsesSerial);
        Assert.Equal("DPRNT serial port COM3", source.Description);

        link.Send("\u001230P647004101-001\n\rMEIMAD/OFFSET-START/SESSION/S1/MACHINE/11/PROGRAM/654321/OPER");
        var first = await source.DrainAsync(allowClear: true, CancellationToken.None);
        Assert.True(first.Available);
        Assert.Equal("30P647004101-001", first.Result.PartName);
        Assert.Empty(first.Result.EventLines);

        link.Send("ATION/20/RELEASE/654321\n\rMEIMAD/OFFSET/SESSION/S1/OFFSET/G54/UNITS/  21./X/  1.0000/Y/ 2.0000/Z/ -3.0000\n\r"
                  + "MEIMAD/OFFSET-END/SESSION/S1\n\r\u0014");
        var second = await source.DrainAsync(allowClear: true, CancellationToken.None);
        Assert.Equal(3, second.Result.EventLines.Count);

        var parser = new OffsetEvidenceParser();
        var result = second.Result.EventLines.Select(parser.Accept).ToList()[^1];
        Assert.Equal(OffsetEvidenceParseOutcome.Completed, result.Outcome);
        Assert.Equal(-3.0, result.Message!.Values["Z"]);

        var components = new Dictionary<string, string>();
        var health = new Dictionary<string, string>();
        string? error = null;
        source.ApplyFileHealth(second, components, health, ref error);
        Assert.Equal(CncComponentStates.Available, components["DPRNT"]);
        Assert.Null(error);
    }

    [Fact]
    public async Task A_port_that_cannot_be_opened_is_unavailable_and_opened_again_on_the_next_poll()
    {
        var attempts = 0;
        var link = new FakeLink();
        await using var source = new CncDprntSource("10.0.0.11", 8193, 2000,
            new CncDprntConfiguration(CncDprntSources.Serial, SerialPort: "COM9"),
            _ => ++attempts == 1 ? throw new UnauthorizedAccessException("Access to the port 'COM9' is denied.") : link);

        var failed = await source.DrainAsync(allowClear: true, CancellationToken.None);
        Assert.False(failed.Available);
        Assert.Contains("COM9", failed.Error, StringComparison.Ordinal);

        link.Send("30P647004101-002\r\n");
        var recovered = await source.DrainAsync(allowClear: true, CancellationToken.None);
        Assert.True(recovered.Available);
        Assert.Null(recovered.Error);
        Assert.Equal("30P647004101-002", recovered.Result.PartName);
        Assert.Equal(2, attempts);

        link.Fail = true;
        var broken = await source.DrainAsync(allowClear: true, CancellationToken.None);
        Assert.False(broken.Available);
        Assert.True(link.Disposed);
    }

    [Fact]
    public async Task A_FANUC_FOCAS_connection_saves_a_SERIAL_source_and_refuses_invalid_line_settings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.SerialDprnt.Api", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build([
            "--Server:Host=127.0.0.1", "--Server:Port=5099",
            $"--Database:Path={Path.Combine(directory, "test.db")}"], builder => builder.UseSignedInTestServer());
        try
        {
            await application.StartAsync();
            await using (var connection = await application.Services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-serial', 'Serial', 'UTC');
                    INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active, nc_dialect)
                    VALUES ('machine-serial', '11', 'FANUC 0i-F', 'mill', 'calendar-serial', 'active', 1, 'FANUC_MACRO_B');
                    """;
                await command.ExecuteNonQueryAsync();
            }
            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "serial-client");

            foreach (var (dprnt, field) in new (object Dprnt, string Field)[]
            {
                (new { source = "SERIAL" }, "serialPort"),
                (new { source = "SERIAL", serialPort = "LPT1" }, "serialPort"),
                (new { source = "SERIAL", serialPort = "COM3", baudRate = 1000 }, "baudRate"),
                (new { source = "SERIAL", serialPort = "COM3", parity = "MARK" }, "parity"),
                (new { source = "SERIAL", serialPort = "COM3", dataBits = 6 }, "dataBits"),
                (new { source = "SERIAL", serialPort = "COM3", stopBits = 3 }, "stopBits")
            })
            {
                using var refused = await client.PutAsJsonAsync("/api/v1/machines/machine-serial/cnc-connection", Body(dprnt));
                var text = await refused.Content.ReadAsStringAsync();
                Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, text);
                Assert.Contains(field, text, StringComparison.Ordinal);
            }

            using var saved = await client.PutAsJsonAsync("/api/v1/machines/machine-serial/cnc-connection",
                Body(new { source = "serial", serialPort = "com3", baudRate = 4800, parity = "even", dataBits = 7, stopBits = 2 }));
            var body = await saved.Content.ReadAsStringAsync();
            Assert.True(saved.IsSuccessStatusCode, body);
            var stored = JsonDocument.Parse(body).RootElement.GetProperty("configuration").GetProperty("dprnt");
            Assert.Equal("SERIAL", stored.GetProperty("source").GetString());
            Assert.Equal("COM3", stored.GetProperty("serialPort").GetString());
            Assert.Equal(4800, stored.GetProperty("baudRate").GetInt32());
            Assert.Equal("EVEN", stored.GetProperty("parity").GetString());
            Assert.Equal(7, stored.GetProperty("dataBits").GetInt32());
            Assert.Equal(2, stored.GetProperty("stopBits").GetInt32());
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static object Body(object dprnt) => new
    {
        adapterType = "FANUC_FOCAS", enabled = false,
        pollingIntervalMs = 2000, connectionTimeoutMs = 3000,
        maximumReconnectBackoffMs = 30000, allowRead = true, allowWrite = false,
        rawTelemetryRetentionDays = 14, version = 0,
        configuration = new
        {
            host = "192.168.0.77", macAddress = "00:E0:E4:12:34:56", port = 8193, timeoutMs = 3000,
            partCounterSource = "PARTS_COUNT_6711",
            dprnt,
            programAccess = new { provider = "NONE", enabled = false }
        }
    };

    private sealed class FakeLink : ICncSerialLink
    {
        private readonly Queue<byte> bytes = new();
        internal bool Fail { get; set; }
        internal bool Disposed { get; private set; }

        internal void Send(string text)
        {
            foreach (var value in Encoding.ASCII.GetBytes(text)) bytes.Enqueue(value);
        }

        public bool IsOpen => !Disposed;

        public int BytesToRead => Fail ? throw new IOException("The serial line failed.") : bytes.Count;

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = 0;
            while (read < count && bytes.Count > 0) buffer[offset + read++] = bytes.Dequeue();
            return read;
        }

        public void Dispose() => Disposed = true;
    }
}
