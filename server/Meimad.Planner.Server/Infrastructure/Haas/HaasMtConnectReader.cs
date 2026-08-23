using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Domain.Haas;
using Meimad.Planner.Server.Infrastructure.MtConnect;

namespace Meimad.Planner.Server.Infrastructure.Haas;

internal sealed class HaasMtConnectReader(
    IMtConnectClient client,
    TimeProvider timeProvider) : IHaasMtConnectReader
{
    private readonly ConcurrentDictionary<string, MtConnectProbeDocument> probes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> probeLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<HaasMtConnectRead> ReadAsync(
        string host,
        int port,
        int timeoutMs,
        int productionVariableNumber,
        string partCounterSource,
        CancellationToken cancellationToken = default)
    {
        var address = AgentAddress(host, port);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        try
        {
            var probe = await GetProbeAsync(address, timeout.Token);
            var current = await client.ReadCurrentAsync(address, probe, timeout.Token);
            if (probe.Header.InstanceId is not null && current.Header.InstanceId is not null
                && !string.Equals(probe.Header.InstanceId, current.Header.InstanceId, StringComparison.Ordinal))
            {
                probes.TryRemove(CacheKey(address), out _);
                probe = await GetProbeAsync(address, timeout.Token);
                current = await client.ReadCurrentAsync(address, probe, timeout.Token);
            }

            var device = SelectDevice(current);
            var macro = device.MacroVariables
                .SingleOrDefault(value => value.VariableNumber == productionVariableNumber);
            int? macroValue = null;
            string? macroError = null;
            if (macro is null)
            {
                macroError = $"MTConnect probe/current did not expose configured variable #{productionVariableNumber}.";
            }
            else if (macro.NumericValue is 0m or 1m)
            {
                macroValue = decimal.ToInt32(macro.NumericValue.Value);
            }
            else
            {
                macroError = $"MTConnect reported configured variable #{productionVariableNumber} as '{macro.RawValue}'; Setup/Production requires exactly 0 or 1.";
            }

            var latest = LatestByDataItem(device.Observations);
            var spindle = Latest(latest, observation =>
                observation.ElementName.Equals("SpindleSpeed", StringComparison.OrdinalIgnoreCase)
                && observation.Definition?.Category?.Equals("SAMPLE", StringComparison.OrdinalIgnoreCase) == true
                && (observation.Definition.SubType is null
                    || observation.Definition.SubType.Equals("ACTUAL", StringComparison.OrdinalIgnoreCase)));
            var feed = Latest(latest, observation =>
                observation.ElementName.Equals("PathFeedrate", StringComparison.OrdinalIgnoreCase)
                && observation.Definition?.Category?.Equals("SAMPLE", StringComparison.OrdinalIgnoreCase) == true
                && (observation.Definition.SubType is null
                    || observation.Definition.SubType.Equals("ACTUAL", StringComparison.OrdinalIgnoreCase)));
            // A CONDITION DataItem can legally expose several simultaneous Fault/Warning
            // elements. Do not collapse these by dataItemId before counting them.
            var alarmCount = CountAlarms(device.Observations);
            var counter = SelectCounter(device.Counters, partCounterSource);
            // Persist and drive automation with the Server receipt clock. The agent's
            // creationTime is retained in diagnostics, but an incorrect machine clock
            // must not move retention windows or Bench event timestamps.
            var readAt = timeProvider.GetUtcNow();
            var availability = Normalized(device.Availability?.Value) ?? "UNAVAILABLE";
            var execution = Normalized(device.Execution?.Value);
            var controllerMode = Normalized(device.ControllerMode?.Value);
            var program = AvailableValue(device.Program?.Value);
            var diagnostic = JsonSerializer.Serialize(new
            {
                header = new
                {
                    current.Header.CreationTime,
                    current.Header.InstanceId,
                    current.Header.FirstSequence,
                    current.Header.LastSequence,
                    current.Header.NextSequence
                },
                device = device.Identity,
                availability,
                execution,
                controllerMode,
                program,
                partCounterSource,
                parts = counter,
                productionVariableNumber,
                productionVariableRaw = macro?.RawValue,
                spindleRpm = Decimal(spindle?.Value),
                feedRate = Decimal(feed?.Value),
                activeAlarmCount = alarmCount
            }, CncJson.Options);

            return new(
                device.Identity.Id,
                device.Identity.Name,
                availability,
                execution,
                controllerMode,
                program,
                counter,
                macroValue,
                macroError,
                readAt,
                Decimal(spindle?.Value),
                Decimal(feed?.Value),
                alarmCount,
                diagnostic);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"MTConnect agent {address.Host}:{address.Port} did not respond within {timeoutMs} ms.",
                exception);
        }
    }

    private async Task<MtConnectProbeDocument> GetProbeAsync(Uri address, CancellationToken token)
    {
        var key = CacheKey(address);
        if (probes.TryGetValue(key, out var cached)) return cached;
        // Isolate cold-start and reconnect work per agent. One unreachable machine must
        // not consume the connection timeout of every other machine being polled.
        var probeLock = probeLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await probeLock.WaitAsync(token);
        try
        {
            if (probes.TryGetValue(key, out cached)) return cached;
            var value = await client.ProbeAsync(address, token);
            probes[key] = value;
            return value;
        }
        finally
        {
            probeLock.Release();
        }
    }

    private static MtConnectDeviceState SelectDevice(MtConnectCurrentDocument document) =>
        document.Devices.Count switch
        {
            1 => document.Devices[0],
            0 => throw new MtConnectProtocolException("The MTConnect current document did not contain a machine DeviceStream."),
            _ => throw new MtConnectProtocolException(
                "The MTConnect agent exposes more than one machine. Configure a dedicated single-machine agent before enabling this Haas connection.")
        };

    private static IReadOnlyList<MtConnectObservation> LatestByDataItem(
        IReadOnlyList<MtConnectObservation> observations) => observations
        .Select((observation, index) => new { observation, index })
        .GroupBy(value => value.observation.DataItemId, StringComparer.Ordinal)
        .Select(group => group.OrderBy(value => value.observation.Sequence ?? long.MinValue)
            .ThenBy(value => value.index).Last().observation)
        .ToArray();

    private static MtConnectObservation? Latest(
        IReadOnlyList<MtConnectObservation> observations,
        Func<MtConnectObservation, bool> predicate) => observations
        .Where(predicate)
        .OrderBy(value => value.Sequence ?? long.MinValue)
        .LastOrDefault();

    private static int? SelectCounter(
        IReadOnlyList<MtConnectCounterObservation> counters,
        string source)
    {
        MtConnectCounterObservation? Selected(Func<MtConnectCounterObservation, bool> predicate) =>
            counters.Where(predicate)
                .OrderBy(value => value.Observation.Sequence ?? long.MinValue)
                .LastOrDefault();
        bool Named(MtConnectCounterObservation value, string name) =>
            value.Observation.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true
            || value.Observation.Definition?.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true;
        static bool Numeric(MtConnectCounterObservation value) =>
            value.NumericValue is >= 0 and <= int.MaxValue;

        var selected = source switch
        {
            HaasPartCounterSources.M30Counter1 => Selected(value => Named(value, "M30Counter1")),
            HaasPartCounterSources.M30Counter2 => Selected(value => Named(value, "M30Counter2")),
            _ => Selected(value => Numeric(value)
                                  && value.Observation.ElementName.Equals("PartCount", StringComparison.OrdinalIgnoreCase))
                 ?? Selected(value => Numeric(value) && Named(value, "M30Counter1"))
        };
        return selected?.NumericValue is >= 0 and <= int.MaxValue
            ? checked((int)selected.NumericValue.Value) : null;
    }

    private static int? CountAlarms(IReadOnlyList<MtConnectObservation> observations)
    {
        var activeMessage = Latest(observations, value =>
            value.Name?.Equals("ActiveAlarms", StringComparison.OrdinalIgnoreCase) == true);
        if (activeMessage?.Value.Contains("NO ACTIVE ALARMS", StringComparison.OrdinalIgnoreCase) == true)
            return 0;
        var conditions = observations.Count(value =>
            value.ElementName is "Fault" or "Warning"
            && !value.Value.Equals("UNAVAILABLE", StringComparison.OrdinalIgnoreCase));
        return conditions > 0 ? conditions : activeMessage is null ? null : 1;
    }

    private static decimal? Decimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;

    private static string? AvailableValue(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized)
            || normalized.Equals("UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
            ? null : normalized;
    }

    private static string? Normalized(string? value) => AvailableValue(value)?.ToUpperInvariant();

    private static Uri AgentAddress(string host, int port)
    {
        var trimmed = host.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https")
        {
            return new UriBuilder(absolute) { Port = port, Path = "/", Query = string.Empty, Fragment = string.Empty }.Uri;
        }
        return new UriBuilder(Uri.UriSchemeHttp, trimmed, port, "/").Uri;
    }

    private static string CacheKey(Uri address) => address.GetLeftPart(UriPartial.Authority);
}
