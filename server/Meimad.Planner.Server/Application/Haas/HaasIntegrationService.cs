using System.Net;
using System.Text.RegularExpressions;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Domain.Haas;

namespace Meimad.Planner.Server.Application.Haas;

internal sealed class HaasIntegrationService(
    IHaasIntegrationRepository repository,
    IHaasMdcClientFactory clientFactory,
    IHaasMtConnectReader mtConnectReader,
    IHaasProgramReader programReader,
    INcHeaderParser headerParser,
    TimeProvider timeProvider,
    IHaasDprntProbe? dprntProbe = null)
{
    internal async Task<HaasConnectionSettings?> GetSettingsAsync(
        string machineId, CancellationToken token = default) =>
        await repository.GetSettingsAsync(Required(machineId, "machineId"), token);

    internal async Task<HaasConnectionSettings> UpdateSettingsAsync(
        string machineId, HaasSettingsUpdate update, EditAuthority authority,
        CancellationToken token = default)
    {
        machineId = Required(machineId, "machineId");
        var host = Required(update.Host, "host");
        if (!IPAddress.TryParse(host, out var fixedAddress)
            || IPAddress.IsLoopback(fixedAddress)
            || fixedAddress.Equals(IPAddress.Any)
            || fixedAddress.Equals(IPAddress.IPv6Any))
            throw new HaasValidationException("host", "A fixed, non-loopback CNC IP address is required.");
        var macAddress = Required(update.MacAddress, "macAddress").ToUpperInvariant().Replace('-', ':');
        if (!Regex.IsMatch(macAddress, "^[0-9A-F]{2}(:[0-9A-F]{2}){5}$",
                RegexOptions.CultureInvariant))
            throw new HaasValidationException("macAddress", "MAC address must use six hexadecimal octets.");
        Port(update.MdcPort, "mdcPort");
        Port(update.MtConnectPort, "mtConnectPort");
        Port(update.DprntPort, "dprntPort");
        var current = await repository.GetSettingsAsync(machineId, token);
        var telemetryProvider = update.TelemetryProvider?.Trim().ToUpperInvariant()
            ?? current?.TelemetryProvider
            ?? HaasTelemetryProviders.Mdc;
        if (!HaasTelemetryProviders.IsSupported(telemetryProvider))
            throw new HaasValidationException("telemetryProvider", "Telemetry provider must be MDC, MTCONNECT, or DPRNT.");
        // A pre-DPRNT-source client omits these fields; keep the explicit Server-side choice.
        var dprntSource = update.DprntSource?.Trim().ToUpperInvariant()
            ?? current?.DprntSource
            ?? CncDprntSources.Tcp;
        if (dprntSource is not (CncDprntSources.Tcp or CncDprntSources.File))
            throw new HaasValidationException("dprntSource", "DPRNT source must be TCP or FILE.");
        var dprntFilePath = update.DprntFilePath is null ? current?.DprntFilePath : Optional(update.DprntFilePath);
        if (dprntFilePath is { Length: > 1024 })
            throw new HaasValidationException("dprntFilePath", "DPRNT file path must be 1024 characters or fewer.");
        if (dprntSource == CncDprntSources.File && string.IsNullOrWhiteSpace(dprntFilePath))
            throw new HaasValidationException("dprntFilePath", "A DPRNT file path (UNC share path to the controller's print file) is required when the DPRNT source is FILE.");
        var dprntFileClearPolicy = Optional(update.DprntFileClearPolicy)?.ToUpperInvariant()
            ?? current?.DprntFileClearPolicy
            ?? CncDprntClearPolicies.Never;
        if (!CncDprntClearPolicies.IsSupported(dprntFileClearPolicy))
            throw new HaasValidationException("dprntFileClearPolicy", "DPRNT file clear policy must be NEVER, ON_OFFSET_LOADER, or AFTER_READ.");
        var counterSource = update.PartCounterSource?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!HaasPartCounterSources.IsSupported(counterSource))
            throw new HaasValidationException("partCounterSource", "Part counter source must be Q500, M30_COUNTER_1, or M30_COUNTER_2.");
        Range(update.PollingIntervalMs, 500, 60000, "pollingIntervalMs");
        Range(update.ConnectionTimeoutMs, 250, 60000, "connectionTimeoutMs");
        Range(update.StableProgramPolls, 1, 10, "stableProgramPolls");
        Range(update.HeaderLineLimit, 1, 200, "headerLineLimit");
        Range(update.HeaderByteLimit, 1024, 262144, "headerByteLimit");
        if (update.LocalNetShareEnabled && string.IsNullOrWhiteSpace(update.LocalNetSharePath))
            throw new HaasValidationException("localNetSharePath", "A Local Net Share path is required when machine-side header access is enabled.");
        var patterns = update.HeaderPartPatterns is { Count: > 0 }
            ? update.HeaderPartPatterns.Select(pattern => Required(pattern, "headerPartPatterns")).Distinct().ToArray()
            : NcHeaderParser.DefaultPartPatterns;
        // Compile/validate configurable expressions through the exact shared parser.
        headerParser.Parse(["O1", "(PART: validation)"], patterns);
        var now = timeProvider.GetUtcNow();
        var value = new HaasConnectionSettings(machineId, host, macAddress,
            update.MdcPort, update.MtConnectPort, update.DprntPort,
            update.LocalNetShareEnabled, Optional(update.LocalNetSharePath), Optional(update.CredentialsReference),
            counterSource,
            update.PollingIntervalMs, update.ConnectionTimeoutMs, update.StableProgramPolls,
            update.HeaderLineLimit, update.HeaderByteLimit, patterns, update.Enabled,
            current?.Version + 1 ?? 1, current?.CreatedAt ?? now, now, telemetryProvider,
            dprntSource, dprntFilePath, dprntFileClearPolicy);
        return await repository.UpsertSettingsAsync(value, update.ExpectedVersion, authority, token);
    }

    /// <summary>Reads the configured DPRNT source once without truncating or changing planning state.</summary>
    internal async Task<HaasConnectionTest> TestDprntAsync(string machineId, CancellationToken token = default)
    {
        var settings = await RequiredSettingsAsync(machineId, token);
        if (dprntProbe is null)
            return new HaasConnectionTest(false, "DPRNT probing is not available on this Server.", null, null, null, null);
        try
        {
            var probe = await dprntProbe.ProbeAsync(settings, token);
            return new HaasConnectionTest(probe.Available, probe.Message, null, null, null, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HaasConnectionTest(false, Safe(exception.Message), null, null, null, null);
        }
    }

    internal async Task<HaasConnectionTest> TestMdcAsync(string machineId, CancellationToken token = default)
    {
        var settings = await RequiredSettingsAsync(machineId, token);
        try
        {
            await using var client = clientFactory.Create(settings);
            await client.ConnectAsync(token);
            var status = await client.GetMachineStatusAsync(token);
            return new HaasConnectionTest(true, "MDC connection succeeded.", status.ProgramNumber,
                status.MachineStatus, status.Parts, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HaasConnectionTest(false, Safe(exception.Message), null, null, null, null);
        }
    }

    internal async Task<HaasConnectionTest> TestMtConnectAsync(
        string machineId,
        CancellationToken token = default)
    {
        var settings = await RequiredSettingsAsync(machineId, token);
        try
        {
            var status = await ReadMtConnectAsync(settings, token);
            var available = status.Availability == "AVAILABLE";
            var identity = status.DeviceName ?? status.DeviceId ?? "the configured machine";
            var message = available
                ? $"Connected to MTConnect for {identity}; machine and counter telemetry are ready."
                : $"MTConnect agent responded, but machine availability is {status.Availability}.";
            return new HaasConnectionTest(available, message, status.ProgramNumber,
                status.MachineStatus ?? status.Availability, status.Parts, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HaasConnectionTest(false, Safe(exception.Message), null, null, null, null);
        }
    }

    internal async Task<HaasConnectionTest> TestNetShareAsync(string machineId, CancellationToken token = default)
    {
        var settings = await RequiredSettingsAsync(machineId, token);
        try
        {
            string? program;
            if (string.Equals(settings.TelemetryProvider, HaasTelemetryProviders.DprntOnly, StringComparison.OrdinalIgnoreCase))
            {
                throw new HaasProgramHeaderUnavailableException(
                    "A DPRNT-only connection reports no active program, so the machine-side NC header cannot be located.");
            }
            if (UsesMtConnect(settings))
            {
                program = (await ReadMtConnectAsync(settings, token)).ProgramNumber;
            }
            else
            {
                await using var client = clientFactory.Create(settings);
                program = await client.GetCurrentProgramAsync(token);
            }
            var activeProgram = program ?? throw new HaasProgramHeaderUnavailableException(
                "The selected Haas telemetry provider did not report an active program.");
            var header = await programReader.ReadActiveProgramHeaderAsync(settings, activeProgram, token);
            var metadata = headerParser.Parse(header.FirstLines, settings.HeaderPartPatterns);
            if (!metadata.IsValid)
                return new HaasConnectionTest(false, "Part name could not be extracted from NC header.",
                    activeProgram, null, null, metadata);
            return new HaasConnectionTest(true, "Machine-side NC header read succeeded.",
                activeProgram, null, null, metadata);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HaasConnectionTest(false, Safe(exception.Message), null, null, null, null);
        }
    }

    internal async Task<HaasMachineMonitor?> ReadMonitorAsync(string machineId, CancellationToken token = default) =>
        await repository.ReadMonitorAsync(Required(machineId, "machineId"), timeProvider.GetUtcNow(), token);

    private async Task<HaasConnectionSettings> RequiredSettingsAsync(string machineId, CancellationToken token) =>
        await repository.GetSettingsAsync(Required(machineId, "machineId"), token)
        ?? throw new HaasSettingsNotFoundException(machineId);

    private Task<HaasMtConnectRead> ReadMtConnectAsync(
        HaasConnectionSettings settings,
        CancellationToken token) => mtConnectReader.ReadAsync(
            settings.Host,
            settings.MtConnectPort,
            settings.ConnectionTimeoutMs,
            settings.PartCounterSource,
            token);

    private static bool UsesMtConnect(HaasConnectionSettings settings) => string.Equals(
        settings.TelemetryProvider,
        HaasTelemetryProviders.MtConnect,
        StringComparison.OrdinalIgnoreCase);

    private static string Required(string? value, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 2048)
            throw new HaasValidationException(field, $"{field} is required.");
        return trimmed;
    }
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void Port(int value, string field) => Range(value, 1, 65535, field);
    private static void Range(int value, int minimum, int maximum, string field)
    {
        if (value < minimum || value > maximum)
            throw new HaasValidationException(field, $"{field} must be between {minimum} and {maximum}.");
    }
    private static string Safe(string message) => message.Length <= 500 ? message : message[..500];
}

internal sealed record HaasConnectionTest(
    bool Succeeded, string Message, string? ProgramNumber, string? MachineStatus,
    int? Parts, NcHeaderMetadata? Header);
