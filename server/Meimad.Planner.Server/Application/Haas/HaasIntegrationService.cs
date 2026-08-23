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
        Port(update.MdcPort, "mdcPort");
        Port(update.MtConnectPort, "mtConnectPort");
        Port(update.DprntPort, "dprntPort");
        var current = await repository.GetSettingsAsync(machineId, token);
        var telemetryProvider = update.TelemetryProvider?.Trim().ToUpperInvariant()
            ?? current?.TelemetryProvider
            ?? HaasTelemetryProviders.Mdc;
        if (!HaasTelemetryProviders.IsSupported(telemetryProvider))
            throw new HaasValidationException("telemetryProvider", "Telemetry provider must be MDC or MTCONNECT.");
        if (update.ProductionModeVariable is < 10000 or > 10999)
            throw new HaasValidationException("productionModeVariable", "NGC production variable must be between 10000 and 10999.");
        if (update.LegacyVariableAlias is < 600 or > 699
            || update.ProductionModeVariable != update.LegacyVariableAlias + 10000)
            throw new HaasValidationException("legacyVariableAlias", "Legacy alias must be 600-699 and map to the NGC variable by adding 10000.");
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
        var monitor = current is null ? null : await repository.ReadMonitorAsync(machineId, now, token);
        if (monitor?.ActiveBench is { State: HaasBenchStates.Setup or HaasBenchStates.Production }
            && current!.ProductionModeVariable != update.ProductionModeVariable)
            throw new HaasValidationException("productionModeVariable", "Finish the active Bench before changing its production variable.");
        var value = new HaasConnectionSettings(machineId, host, update.MdcPort, update.MtConnectPort, update.DprntPort,
            update.LocalNetShareEnabled, Optional(update.LocalNetSharePath), Optional(update.CredentialsReference),
            update.ProductionModeVariable, update.LegacyVariableAlias, counterSource,
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
            var message = available && status.ProductionVariableValue is not null
                ? $"Connected to MTConnect for {identity}; production telemetry is ready."
                : available
                    ? $"Connected to MTConnect for {identity}, but production telemetry is DEGRADED: "
                      + $"{status.ProductionVariableError ?? "the production variable is unavailable"} "
                      + "Machine status remains readable; Bench automation is blocked."
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

    internal async Task<HaasVariableRead> ReadProductionVariableAsync(string machineId, CancellationToken token = default)
    {
        var settings = await RequiredSettingsAsync(machineId, token);
        int value;
        if (UsesMtConnect(settings))
        {
            var status = await ReadMtConnectAsync(settings, token);
            value = status.ProductionVariableValue
                ?? throw new IOException(status.ProductionVariableError
                    ?? $"MTConnect did not expose variable #{settings.ProductionModeVariable}.");
        }
        else
        {
            await using var client = clientFactory.Create(settings);
            value = await client.ReadMacroAsync(settings.ProductionModeVariable, token);
        }
        return new HaasVariableRead(settings.ProductionModeVariable, settings.LegacyVariableAlias,
            value, timeProvider.GetUtcNow());
    }

    internal async Task<HaasMachineMonitor?> ReadMonitorAsync(string machineId, CancellationToken token = default) =>
        await repository.ReadMonitorAsync(Required(machineId, "machineId"), timeProvider.GetUtcNow(), token);

    internal async Task<HaasMacroWriteResult> ResetProductionVariableAfterToolTableAsync(
        string machineId, string toolTableId, string initiatedBy, CancellationToken token = default)
    {
        var settings = await RequiredSettingsAsync(machineId, token);
        if (UsesMtConnect(settings))
        {
            return new HaasMacroWriteResult(false, settings.ProductionModeVariable, null, 0,
                string.Empty,
                "MTConnect is read-only. Select MDC and verify it before an audited production-variable reset.");
        }
        var monitor = await repository.ReadMonitorAsync(machineId, timeProvider.GetUtcNow(), token);
        var benchId = monitor?.ActiveBench?.BenchId;
        await using var client = clientFactory.Create(settings);
        var oldValue = await client.ReadMacroAsync(settings.ProductionModeVariable, token);
        var requestedAt = timeProvider.GetUtcNow();
        var auditId = await repository.BeginMacroWriteAuditAsync(machineId, benchId,
            Required(toolTableId, "toolTableId"), settings.ProductionModeVariable,
            oldValue, 0, "TOOL_TABLE_LOADED", Required(initiatedBy, "initiatedBy"), requestedAt, token);
        try
        {
            var response = await client.WriteMacroAsync(settings.ProductionModeVariable, 0, token);
            var readBack = await client.ReadMacroAsync(settings.ProductionModeVariable, token);
            if (readBack != 0) throw new IOException("Haas variable read-back did not confirm value 0.");
            await repository.CompleteMacroWriteAuditAsync(auditId, true, response, null,
                timeProvider.GetUtcNow(), token);
            return new HaasMacroWriteResult(true, settings.ProductionModeVariable, oldValue, 0, response, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await repository.CompleteMacroWriteAuditAsync(auditId, false, null, Safe(exception.Message),
                timeProvider.GetUtcNow(), token);
            return new HaasMacroWriteResult(false, settings.ProductionModeVariable, oldValue, 0,
                string.Empty, Safe(exception.Message));
        }
    }

    private async Task<HaasConnectionSettings> RequiredSettingsAsync(string machineId, CancellationToken token) =>
        await repository.GetSettingsAsync(Required(machineId, "machineId"), token)
        ?? throw new HaasSettingsNotFoundException(machineId);

    private Task<HaasMtConnectRead> ReadMtConnectAsync(
        HaasConnectionSettings settings,
        CancellationToken token) => mtConnectReader.ReadAsync(
            settings.Host,
            settings.MtConnectPort,
            settings.ConnectionTimeoutMs,
            settings.ProductionModeVariable,
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
internal sealed record HaasVariableRead(int VariableNumber, int LegacyAlias, int Value, DateTimeOffset ReadAt);
