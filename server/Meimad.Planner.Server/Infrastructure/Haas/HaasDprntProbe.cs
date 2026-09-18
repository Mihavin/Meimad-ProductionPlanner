using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Domain.Haas;

namespace Meimad.Planner.Server.Infrastructure.Haas;

/// <summary>Setup-time DPRNT check. Never truncates the file and never shares the polling reader's state.</summary>
internal sealed class HaasDprntProbe : IHaasDprntProbe
{
    public async Task<HaasDprntProbeResult> ProbeAsync(
        HaasConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        if (string.Equals(settings.DprntSource, CncDprntSources.File, StringComparison.OrdinalIgnoreCase))
        {
            var path = settings.DprntFilePath;
            if (string.IsNullOrWhiteSpace(path))
                return new(false, "A DPRNT file path is not configured for this Machine.", null);
            try
            {
                var result = await new HaasDprntFileReader().DrainAsync(path, replayExistingContent: false, cancellationToken);
                var message = result.PartName is null
                    ? $"DPRNT file '{path}' is readable; it contains no part-number-shaped line yet."
                    : $"DPRNT file '{path}' is readable; last PartName is {result.PartName}.";
                return new(true, message, result.PartName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return new(false, $"DPRNT file '{path}' is unavailable to the Server service account: {exception.Message}", null);
            }
        }

        await using var reader = new HaasDprntPartReader();
        await reader.DrainAsync(settings.Host, settings.DprntPort, settings.ConnectionTimeoutMs, cancellationToken);
        return reader.Connected
            ? new(true, $"DPRNT TCP port {settings.DprntPort} on {settings.Host} accepted the read-only connection.", null)
            : new(false, $"DPRNT TCP port {settings.DprntPort} on {settings.Host} refused or dropped the connection.", null);
    }
}
