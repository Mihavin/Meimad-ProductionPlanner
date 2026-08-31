using System.Net;
using System.Text.RegularExpressions;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Haas;

namespace Meimad.Planner.Server.Application.Haas;

internal sealed class HaasIntegrationService(
    IHaasIntegrationRepository repository,
    IHaasMdcClientFactory clientFactory,
    IHaasMtConnectReader mtConnectReader,
    IHaasProgramReader programReader,
    INcHeaderParser headerParser,
    TimeProvider timeProvider)
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
            throw new HaasValidationException("telemetryProvider", "Telemetry provider must be MDC or MTCONNECT.");
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
            current?.Version + 1 ?? 1, current?.CreatedAt ?? now, now, telemetryProvider);
        return await repository.UpsertSettingsAsync(value, update.ExpectedVersion, authority, token);
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
