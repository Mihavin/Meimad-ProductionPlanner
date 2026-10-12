using System.IO.Ports;
using System.Text;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Infrastructure.Haas;

namespace Meimad.Planner.Server.Infrastructure.Cnc;

/// <summary>The serial line settings of a DPRNT source wired straight to a COM port of the Server PC.</summary>
internal sealed record CncSerialDprntSettings(string PortName, int BaudRate, string Parity, int DataBits, int StopBits)
{
    internal static CncSerialDprntSettings From(CncDprntConfiguration value) => new(
        value.SerialPort!.Trim().ToUpperInvariant(),
        value.BaudRate ?? 9600,
        string.IsNullOrWhiteSpace(value.Parity) ? CncSerialParities.None : value.Parity.Trim().ToUpperInvariant(),
        value.DataBits ?? 8,
        value.StopBits ?? 1);
}

/// <summary>An open serial line the reader takes the controller's bytes from (System.IO.Ports in production, a fake in tests).</summary>
internal interface ICncSerialLink : IDisposable
{
    bool IsOpen { get; }
    int BytesToRead { get; }
    int Read(byte[] buffer, int offset, int count);
}

/// <summary>
/// The SERIAL DPRNT source: the control's RS-232 DPRNT/PUT output read from a local COM port (exporter
/// definition §20.8 <c>SerialDprntSource</c>), with the same line semantics as the TCP stream
/// (<see cref="HaasDprntPartReader"/>): control codes removed, Part identity lines, <c>MEIMAD/</c> event
/// lines (workflow events and WORK_OFFSET evidence) and every line for the DPRNT log. Each poll reads
/// the bytes that have arrived; a partial line waits for its terminator. A port that cannot be opened
/// or fails is reported unavailable and opened again on the next poll; bytes the control sent while
/// the port was closed are lost, exactly as on a serial-to-Ethernet bridge without a buffer.
/// </summary>
internal sealed class CncSerialDprntReader(Func<CncSerialDprntSettings, ICncSerialLink>? openLink = null) : IDisposable
{
    private const int MaximumBytesPerPoll = 64 * 1024;
    private readonly Func<CncSerialDprntSettings, ICncSerialLink> open = openLink ?? SystemSerialLink.Open;
    private readonly StringBuilder pending = new();
    private ICncSerialLink? link;

    internal bool Connected => link?.IsOpen == true;

    internal string? LastError { get; private set; }

    internal HaasDprntDrainResult Drain(CncSerialDprntSettings settings)
    {
        try
        {
            if (link?.IsOpen != true)
            {
                Close();
                link = open(settings);
            }
            LastError = null;
            var available = Math.Min(link.BytesToRead, MaximumBytesPerPoll);
            if (available <= 0) return new(null, []);
            var buffer = new byte[available];
            var count = link.Read(buffer, 0, available);
            if (count <= 0) return new(null, []);
            pending.Append(Encoding.ASCII.GetString(buffer, 0, count));
            var lines = pending.ToString().Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
            pending.Clear();
            pending.Append(lines[^1]);
            string? latest = null;
            var eventLines = new List<string>();
            var allLines = new List<string>();
            foreach (var raw in lines[..^1])
            {
                var line = HaasDprntPartReader.StripControlCharacters(raw);
                if (!string.IsNullOrWhiteSpace(line)) allLines.Add(line);
                if (HaasDprntPartReader.TryParsePartName(line, out var value)) latest = value;
                else if (line.TrimStart().StartsWith("MEIMAD/", StringComparison.Ordinal)) eventLines.Add(line.Trim());
            }
            return new(latest, eventLines, allLines);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or TimeoutException)
        {
            LastError = $"Serial port {settings.PortName} is unavailable: {exception.Message}";
            Close();
            return new(null, []);
        }
    }

    public void Dispose() => Close();

    private void Close()
    {
        link?.Dispose();
        link = null;
        pending.Clear();
    }

    private sealed class SystemSerialLink(SerialPort port) : ICncSerialLink
    {
        internal static ICncSerialLink Open(CncSerialDprntSettings settings)
        {
            var port = new SerialPort(
                settings.PortName,
                settings.BaudRate,
                settings.Parity switch
                {
                    CncSerialParities.Even => Parity.Even,
                    CncSerialParities.Odd => Parity.Odd,
                    _ => Parity.None
                },
                settings.DataBits,
                settings.StopBits == 2 ? StopBits.Two : StopBits.One)
            {
                Handshake = Handshake.None,
                ReadTimeout = 500,
                Encoding = Encoding.ASCII
            };
            try
            {
                port.Open();
                return new SystemSerialLink(port);
            }
            catch
            {
                port.Dispose();
                throw;
            }
        }

        public bool IsOpen => port.IsOpen;
        public int BytesToRead => port.BytesToRead;
        public int Read(byte[] buffer, int offset, int count) => port.Read(buffer, offset, count);
        public void Dispose() => port.Dispose();
    }
}
