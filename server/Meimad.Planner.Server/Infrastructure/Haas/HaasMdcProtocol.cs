using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Haas;

namespace Meimad.Planner.Server.Infrastructure.Haas;

internal static class HaasMdcProtocol
{
    internal static HaasProgramStatus ParseQ500(string raw, DateTimeOffset at)
    {
        var fields = Fields(raw);
        var programIndex = IndexOf(fields, "PROGRAM");
        var statusIndex = IndexOf(fields, "STATUS");
        var partsIndex = IndexOf(fields, "PARTS");
        int? parts = null;
        if (partsIndex >= 0)
        {
            if (partsIndex + 1 >= fields.Length
                || !int.TryParse(fields[partsIndex + 1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var parsedParts) || parsedParts < 0)
                throw new FormatException("Haas Q500 response did not contain a valid PARTS value.");
            parts = parsedParts;
        }

        string? program = null;
        string status;
        if (programIndex >= 0 && programIndex + 1 < fields.Length)
        {
            program = NormalizeProgram(fields[programIndex + 1]);
            status = statusIndex >= 0 && statusIndex + 1 < fields.Length
                ? fields[statusIndex + 1]
                : fields.Skip(programIndex + 2).FirstOrDefault(value => !value.Equals("PARTS", StringComparison.OrdinalIgnoreCase)) ?? "UNKNOWN";
        }
        else
        {
            // Haas documents MDI as a valid Q500 program locator.
            program = NormalizeProgram(fields.FirstOrDefault(value => value.StartsWith('O')));
            status = statusIndex >= 0 && statusIndex + 1 < fields.Length
                ? fields[statusIndex + 1]
                : fields.FirstOrDefault(value =>
                    !value.Equals("PROGRAM", StringComparison.OrdinalIgnoreCase)
                    && !value.Equals("PARTS", StringComparison.OrdinalIgnoreCase)
                    && !value.StartsWith('O')
                    && !int.TryParse(value, out _)) ?? "UNKNOWN";
        }

        return new HaasProgramStatus(program, status.Trim().ToUpperInvariant(), parts, at, raw);
    }

    internal static int ParseMacro(string raw)
    {
        var fields = Fields(raw);
        var value = fields.LastOrDefault();
        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || parsed != decimal.Truncate(parsed)
            || parsed is < int.MinValue or > int.MaxValue)
        {
            throw new FormatException("The Haas macro response was not an integer.");
        }
        return decimal.ToInt32(parsed);
    }

    internal static int ParseCounter(string raw)
    {
        var fields = Fields(raw);
        return int.TryParse(fields.LastOrDefault(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : throw new FormatException("Haas part-counter response was invalid.");
    }

    internal static bool IsWriteAccepted(string raw) => raw.Trim().TrimStart('>').StartsWith('!');

    private static string[] Fields(string raw) => raw.Trim().TrimStart('>')
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static int IndexOf(string[] fields, string value) =>
        Array.FindIndex(fields, item => item.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static string? NormalizeProgram(string? value)
    {
        var trimmed = value?.Trim().ToUpperInvariant();
        return trimmed is not null && trimmed.StartsWith('O')
            && trimmed.Skip(1).All(char.IsDigit) ? trimmed : null;
    }
}

internal sealed class HaasMdcClient : IHaasMdcClient
{
    private readonly HaasConnectionSettings settings;
    private readonly TimeProvider timeProvider;
    private TcpClient? tcpClient;
    private NetworkStream? stream;
    private StreamReader? reader;

    internal HaasMdcClient(HaasConnectionSettings settings, TimeProvider timeProvider)
    {
        this.settings = settings;
        this.timeProvider = timeProvider;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (tcpClient?.Connected == true) return;
        tcpClient = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.ConnectionTimeoutMs);
        await tcpClient.ConnectAsync(settings.Host, settings.MdcPort, timeout.Token);
        stream = tcpClient.GetStream();
        reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        reader?.Dispose();
        stream?.Dispose();
        tcpClient?.Dispose();
        reader = null;
        stream = null;
        tcpClient = null;
        return Task.CompletedTask;
    }

    public async Task<HaasProgramStatus> GetMachineStatusAsync(CancellationToken cancellationToken = default) =>
        HaasMdcProtocol.ParseQ500(await QueryAsync("?Q500", IsQ500, cancellationToken), timeProvider.GetUtcNow());

    public async Task<string?> GetCurrentProgramAsync(CancellationToken cancellationToken = default) =>
        (await GetMachineStatusAsync(cancellationToken)).ProgramNumber;

    public async Task<int> GetPartCounterAsync(string source, CancellationToken cancellationToken = default) =>
        source switch
        {
            HaasPartCounterSources.Q500 => (await GetMachineStatusAsync(cancellationToken)).Parts
                ?? throw new FormatException("The Haas Q500 response did not include a parts counter."),
            HaasPartCounterSources.M30Counter1 => HaasMdcProtocol.ParseCounter(
                await QueryAsync("?Q402", value => StartsWith(value, "M30 #1"), cancellationToken)),
            HaasPartCounterSources.M30Counter2 => HaasMdcProtocol.ParseCounter(
                await QueryAsync("?Q403", value => StartsWith(value, "M30 #2"), cancellationToken)),
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };

    public async Task<int> ReadMacroAsync(int variableNumber, CancellationToken cancellationToken = default) =>
        HaasMdcProtocol.ParseMacro(await QueryAsync(
            $"?Q600 {variableNumber}", value => StartsWith(value, "MACRO"), cancellationToken));

    public async Task<string> WriteMacroAsync(int variableNumber, int value, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new NotSupportedException(
            "Direct CNC macro writes are disabled; setup verification is performed by protected controller programs.");
    }

    private async Task<string> QueryAsync(
        string command,
        Func<string, bool> accepts,
        CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.ConnectionTimeoutMs);
        var bytes = Encoding.ASCII.GetBytes(command + "\n");
        await stream!.WriteAsync(bytes, timeout.Token);
        await stream.FlushAsync(timeout.Token);
        while (!timeout.IsCancellationRequested)
        {
            var line = await reader!.ReadLineAsync(timeout.Token);
            if (line is null)
                throw new IOException("The Haas MDC connection closed before a response was received.");
            if (accepts(line)) return line;
        }
        throw new TimeoutException($"The Haas MDC response to {command} was not received.");
    }

    private static bool IsQ500(string value) =>
        StartsWith(value, "PROGRAM") || StartsWith(value, "STATUS");

    private static bool StartsWith(string value, string field) =>
        value.Trim().TrimStart('>').TrimStart('\u0002')
            .StartsWith(field, StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}

internal sealed class HaasMdcClientFactory(TimeProvider timeProvider) : IHaasMdcClientFactory
{
    public IHaasMdcClient Create(HaasConnectionSettings settings) => new HaasMdcClient(settings, timeProvider);
}
