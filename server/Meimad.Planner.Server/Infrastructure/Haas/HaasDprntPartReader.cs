using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Meimad.Planner.Server.Infrastructure.Haas;

/// <summary>Maintains the read-only Haas DPRNT stream and accepts only part-number-shaped lines.</summary>
internal sealed class HaasDprntPartReader : IAsyncDisposable
{
    private static readonly Regex PartNumber = new(
        @"^(?!.*\.CNC$)(?=.*\d)[A-Z0-9]+(?:[-.][A-Z0-9]+)+$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private TcpClient? client;
    private NetworkStream? stream;
    private readonly StringBuilder pending = new();

    internal async Task<HaasDprntDrainResult> DrainAsync(string host, int port, int timeoutMs, CancellationToken token)
    {
        try
        {
            if (client?.Connected != true)
            {
                await DisposeConnectionAsync();
                client = new TcpClient();
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectTimeout.CancelAfter(timeoutMs);
                await client.ConnectAsync(host, port, connectTimeout.Token);
                stream = client.GetStream();
            }
            if (stream is null || !stream.DataAvailable) return new(null, []);
            var buffer = new byte[4096];
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) { await DisposeConnectionAsync(); return new(null, []); }
            pending.Append(Encoding.UTF8.GetString(buffer, 0, count));
            string? latest = null;
            var eventLines = new List<string>();
            var text = pending.ToString();
            var lines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
            pending.Clear();
            pending.Append(lines[^1]);
            foreach (var line in lines[..^1])
            {
                if (TryParsePartName(line, out var value)) latest = value;
                else if (line.TrimStart().StartsWith("MEIMAD/", StringComparison.Ordinal))
                    eventLines.Add(line.Trim());
            }
            return new(latest, eventLines);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException && !token.IsCancellationRequested)
        {
            await DisposeConnectionAsync();
            return new(null, []);
        }
    }

    internal static bool TryParsePartName(string? line, out string? partName)
    {
        var value = line?.Trim();
        if (!string.IsNullOrWhiteSpace(value) && PartNumber.IsMatch(value))
        {
            partName = value.ToUpperInvariant();
            return true;
        }
        partName = null;
        return false;
    }

    private async Task DisposeConnectionAsync()
    {
        if (stream is not null) await stream.DisposeAsync();
        client?.Dispose(); stream = null; client = null; pending.Clear();
    }
    public async ValueTask DisposeAsync() => await DisposeConnectionAsync();
}

internal sealed record HaasDprntDrainResult(string? PartName, IReadOnlyList<string> EventLines);
