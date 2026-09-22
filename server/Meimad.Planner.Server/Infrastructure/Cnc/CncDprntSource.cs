using System.Net;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Infrastructure.Haas;

namespace Meimad.Planner.Server.Infrastructure.Cnc;

/// <summary>One poll of the configured DPRNT source: the parsed lines plus whether the source itself was reachable.</summary>
internal sealed record CncDprntObservation(HaasDprntDrainResult Result, bool Available, string? Error)
{
    internal static readonly CncDprntObservation Disabled = new(new(null, []), true, null);
}

/// <summary>
/// The controller's DPRNT output as one adapter-independent source: the read-only TCP stream
/// (Haas port or a serial-to-Ethernet bridge), a controller-written print file tailed over a
/// network share, or that same kind of print file served by the controller's own embedded FTP
/// server (Fanuc). Line semantics are identical across sources. The FILE and FTP sources also
/// own the configured clear policy, because no G-code ever deletes that file.
/// </summary>
internal sealed class CncDprntSource : IAsyncDisposable
{
    private readonly string host;
    private readonly int timeoutMs;
    private readonly HaasDprntPartReader tcpReader = new();
    private readonly HaasDprntFileReader fileReader = new();
    private readonly CncFtpDprntReader ftpReader = new();
    private string? lastOffsetLoaderEventId;

    internal CncDprntSource(string host, int tcpPort, int timeoutMs, CncDprntConfiguration? configuration)
    {
        var value = configuration ?? new CncDprntConfiguration();
        // A serial-to-Ethernet bridge has its own address; otherwise the controller itself serves DPRNT.
        this.host = string.IsNullOrWhiteSpace(value.Host) ? host : value.Host.Trim();
        this.timeoutMs = timeoutMs;
        Source = string.IsNullOrWhiteSpace(value.Source)
            ? CncDprntSources.Tcp : value.Source.Trim().ToUpperInvariant();
        ClearPolicy = string.IsNullOrWhiteSpace(value.ClearPolicy)
            ? CncDprntClearPolicies.Never : value.ClearPolicy.Trim().ToUpperInvariant();
        FilePath = value.FilePath;
        TcpPort = value.Port ?? tcpPort;
        FtpPort = value.Port ?? 21;
        FtpUsername = value.FtpUsername;
        FtpPassword = value.FtpPassword;
    }

    internal string Source { get; }
    internal string ClearPolicy { get; }
    internal string? FilePath { get; }
    internal int TcpPort { get; }
    internal int FtpPort { get; }
    internal string? FtpUsername { get; }
    internal string? FtpPassword { get; }
    internal string TcpHost => host;
    internal bool Enabled => Source != CncDprntSources.None;
    internal bool UsesFile => Source == CncDprntSources.File;
    internal bool UsesTcp => Source == CncDprntSources.Tcp;
    internal bool UsesFtp => Source == CncDprntSources.Ftp;

    internal string Description => UsesFile
        ? $"DPRNT file '{FilePath}'"
        : UsesTcp ? $"DPRNT TCP port {TcpPort} on {host}"
        : UsesFtp ? $"DPRNT FTP file '{FilePath}' on {host}:{FtpPort}"
        : "DPRNT (not configured)";

    /// <summary>
    /// Reads new DPRNT lines without letting a source fault abort the poll. With
    /// <paramref name="allowClear"/> the FILE source applies its clear policy after the read;
    /// diagnostics probes pass false so a test never empties the file.
    /// </summary>
    internal async Task<CncDprntObservation> DrainAsync(bool allowClear, CancellationToken token)
    {
        if (!Enabled) return CncDprntObservation.Disabled;
        if (UsesTcp)
        {
            var tcp = await tcpReader.DrainAsync(host, TcpPort, timeoutMs, token);
            return new(tcp, tcpReader.Connected,
                tcpReader.Connected ? null : $"{Description} is not connected.");
        }
        if (UsesFtp)
        {
            var endpoint = FtpEndpoint();
            try
            {
                var file = await ftpReader.DrainAsync(
                    endpoint, replayExistingContent: ClearPolicy == CncDprntClearPolicies.AfterRead, token);
                if (allowClear) await ApplyClearPolicyAsync(endpoint, file, token);
                return new(file, true, ClearPolicy == CncDprntClearPolicies.Never ? null : ftpReader.LastTruncateError);
            }
            catch (Exception exception) when (exception is WebException or IOException)
            {
                return new(new(null, []), false, Safe($"{Description} is unavailable: {exception.Message}"));
            }
        }
        var path = FilePath!;
        try
        {
            var file = await fileReader.DrainAsync(
                path, replayExistingContent: ClearPolicy == CncDprntClearPolicies.AfterRead, token);
            if (allowClear) ApplyClearPolicy(path, file);
            return new(file, true, ClearPolicy == CncDprntClearPolicies.Never ? null : fileReader.LastTruncateError);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(new(null, []), false, Safe($"{Description} is unavailable: {exception.Message}"));
        }
    }

    internal string SuccessMessage(CncDprntObservation observation) => UsesFile || UsesFtp
        ? $"{Description} is readable" + (observation.Result.PartName is null ? "." : $"; last PartName {observation.Result.PartName}.")
        : $"{Description} accepted the read-only connection.";

    /// <summary>A configured DPRNT file is a required source, so its fault degrades the Machine snapshot.</summary>
    internal void ApplyFileHealth(
        CncDprntObservation observation, IDictionary<string, string> components,
        IDictionary<string, string> health, ref string? error)
    {
        if (!UsesFile && !UsesFtp) return;
        var state = observation.Available ? CncComponentStates.Available : CncComponentStates.Unavailable;
        components["DPRNT"] = state;
        health["dprntFile"] = state;
        if (observation.Error is not null) error ??= observation.Error;
    }

    public async ValueTask DisposeAsync() => await tcpReader.DisposeAsync();

    private CncFtpDprntEndpoint FtpEndpoint() =>
        new(new Uri($"ftp://{host}:{FtpPort}/{FilePath!.TrimStart('/')}"), FtpUsername, FtpPassword);

    private void ApplyClearPolicy(string path, HaasDprntDrainResult result)
    {
        switch (ClearPolicy)
        {
            case CncDprntClearPolicies.AfterRead:
                fileReader.TryTruncateConsumed(path);
                break;
            case CncDprntClearPolicies.OnOffsetLoader when ContainsNewOffsetLoaderCompletion(result.EventLines):
                fileReader.TryTruncateConsumed(path);
                break;
        }
    }

    private async Task ApplyClearPolicyAsync(CncFtpDprntEndpoint endpoint, HaasDprntDrainResult result, CancellationToken token)
    {
        switch (ClearPolicy)
        {
            case CncDprntClearPolicies.AfterRead:
                await ftpReader.TryTruncateConsumedAsync(endpoint, token);
                break;
            case CncDprntClearPolicies.OnOffsetLoader when ContainsNewOffsetLoaderCompletion(result.EventLines):
                await ftpReader.TryTruncateConsumedAsync(endpoint, token);
                break;
        }
    }

    /// <summary>
    /// A valid Offset Loader completion line with an event ID this source has not seen marks a new
    /// Offset Loader run; a controller retry of the same line is the same run.
    /// </summary>
    private bool ContainsNewOffsetLoaderCompletion(IReadOnlyList<string> eventLines)
    {
        var found = false;
        foreach (var line in eventLines)
        {
            if (!HaasDprintProtocol.TryParse(line, out var parsed, out _)
                || parsed!.EventType != "OFFSET_LOADER_COMPLETED"
                || parsed.SourceEventId == lastOffsetLoaderEventId)
                continue;
            lastOffsetLoaderEventId = parsed.SourceEventId;
            found = true;
        }
        return found;
    }

    private static string Safe(string value) => value.Length <= 500 ? value : value[..500];
}
