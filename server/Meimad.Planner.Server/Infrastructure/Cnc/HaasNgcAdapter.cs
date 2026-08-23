using System.Text.Json;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Domain.Haas;
using Meimad.Planner.Server.Infrastructure.Haas;

namespace Meimad.Planner.Server.Infrastructure.Cnc;

internal sealed class HaasNgcAdapter : ICncMachineAdapter
{
    private readonly MachineConnection connection;
    private readonly HaasNgcConnectionConfiguration configuration;
    private readonly IHaasMdcClient client;
    private readonly IHaasMtConnectReader mtConnectReader;
    private readonly INcProgramFileProvider programProvider;
    private readonly INcHeaderParser headerParser;
    private readonly TimeProvider timeProvider;
    private readonly HaasDprntPartReader dprntPartReader = new();
    private string? candidateProgram;
    private int candidatePolls;
    private string? cachedProgram;
    private CncFreshValue<string> cachedPart = new(null, null, false);
    private bool cachedPartFromDprnt;
    private CncFreshValue<string> cachedHeaderPath = new(null, null, false);
    private HaasMtConnectRead? pendingMtConnectRead;

    internal HaasNgcAdapter(
        MachineConnection connection,
        IHaasMdcClientFactory clientFactory,
        IHaasMtConnectReader mtConnectReader,
        INcProgramFileProvider programProvider,
        INcHeaderParser headerParser,
        TimeProvider timeProvider)
    {
        this.connection = connection;
        configuration = JsonSerializer.Deserialize<HaasNgcConnectionConfiguration>(
            connection.ConfigurationJson, CncJson.Options)
            ?? throw new CncValidationException("configuration", "Haas NGC configuration is invalid.");
        Validate(configuration);
        client = clientFactory.Create(ToLegacySettings(connection, configuration, timeProvider.GetUtcNow()));
        this.mtConnectReader = mtConnectReader;
        this.programProvider = programProvider;
        this.headerParser = headerParser;
        this.timeProvider = timeProvider;
    }

    public string ConnectionId => connection.Id;
    public string MachineId => connection.MachineId;
    public CncAdapterType AdapterType => CncAdapterType.HaasNgc;

    public CncAdapterCapabilities GetCapabilities() => new(
        connection.AllowRead,
        connection.AllowRead,
        connection.AllowRead && configuration.ProgramAccess.Enabled
            && configuration.ProgramAccess.Provider == "HAAS_LOCAL_NET_SHARE",
        connection.AllowRead,
        connection.AllowWrite && !UsesMtConnect,
        connection.AllowRead,
        false, false, false, false, false, false, false);

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (UsesMtConnect)
            pendingMtConnectRead = await ReadMtConnectAsync(configuration.Production.VariableNumber, cancellationToken);
        else
            await client.ConnectAsync(cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => UsesMtConnect
        ? Task.CompletedTask : client.DisconnectAsync(cancellationToken);

    public async Task<CncConnectionTestResult> TestConnectionAsync(CancellationToken token = default)
    {
        if (UsesMtConnect) return await TestMtConnectConnectionAsync(token);
        var checks = new List<CncAdapterCheck>();
        HaasProgramStatus? status = null;
        try
        {
            await client.ConnectAsync(token);
            status = await client.GetMachineStatusAsync(token);
            checks.Add(new("mdc", true, CncComponentStates.Available, "TCP/MDC returned a valid Q500 response."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            checks.Add(new("mdc", false, CncComponentStates.Unavailable, Safe(exception.Message)));
        }

        if (status is not null)
        {
            try
            {
                await client.ReadMacroAsync(configuration.Production.VariableNumber, token);
                checks.Add(new("variableRead", true, CncComponentStates.Available, "Configured production variable is readable."));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                checks.Add(new("variableRead", false, CncComponentStates.Unavailable, Safe(exception.Message)));
            }
        }
        else
        {
            checks.Add(new("variableRead", false, CncComponentStates.Unavailable, "MDC is unavailable."));
        }

        if (!GetCapabilities().CanReadProgramHeader)
        {
            checks.Add(new("programAccess", true, CncComponentStates.Unsupported,
                "Program-header access is not configured; this capability is unavailable."));
        }
        else if (status?.ProgramNumber is null)
        {
            checks.Add(new("programAccess", true, CncComponentStates.Available,
                "Program access is configured; no active O-number was reported for a header test."));
        }
        else
        {
            try
            {
                var header = await programProvider.ReadActiveProgramHeaderAsync(
                    ToLegacySettings(connection, configuration, timeProvider.GetUtcNow()),
                    status.ProgramNumber, token);
                var metadata = headerParser.Parse(header.FirstLines, configuration.ProgramAccess.HeaderPartPatterns);
                if (!metadata.IsValid) throw new IOException("The active NC header did not contain a valid Part identity.");
                checks.Add(new("programAccess", true, CncComponentStates.Available,
                    "Active NC header and Part identity were read successfully."));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                checks.Add(new("programAccess", false, CncComponentStates.Unavailable, Safe(exception.Message)));
            }
        }

        var mdcOkay = checks.First(value => value.Id == "mdc").Succeeded;
        var failedOptional = checks.Any(value => !value.Succeeded);
        var statusValue = !mdcOkay ? CncConnectionStates.Offline
            : failedOptional ? CncConnectionStates.Degraded : CncConnectionStates.Online;
        return new(mdcOkay && !failedOptional, statusValue, checks);
    }

    private async Task<CncConnectionTestResult> TestMtConnectConnectionAsync(CancellationToken token)
    {
        var checks = new List<CncAdapterCheck>();
        HaasMtConnectRead? status = null;
        try
        {
            status = await ReadMtConnectAsync(configuration.Production.VariableNumber, token);
            var available = status.Availability == "AVAILABLE";
            checks.Add(new("mtconnect", available,
                available ? CncComponentStates.Available : CncComponentStates.Unavailable,
                available
                    ? $"MTConnect /probe and /current returned machine '{status.DeviceName ?? status.DeviceId ?? "unknown"}'."
                    : $"MTConnect agent responded, but machine availability is {status.Availability}."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            checks.Add(new("mtconnect", false, CncComponentStates.Unavailable, Safe(exception.Message)));
        }

        if (status?.ProductionVariableValue is not null)
        {
            checks.Add(new("variableRead", true, CncComponentStates.Available,
                $"Configured production variable #{configuration.Production.VariableNumber} is readable through MTConnect."));
        }
        else
        {
            checks.Add(new("variableRead", false, CncComponentStates.Unavailable,
                status?.ProductionVariableError ?? "MTConnect is unavailable."));
        }

        if (!GetCapabilities().CanReadProgramHeader)
        {
            checks.Add(new("programAccess", true, CncComponentStates.Unsupported,
                "Program-header access is not configured; this capability is unavailable."));
        }
        else if (status?.ProgramNumber is null)
        {
            checks.Add(new("programAccess", true, CncComponentStates.Available,
                "Program access is configured; MTConnect did not report an active program for a header test."));
        }
        else
        {
            try
            {
                var header = await programProvider.ReadActiveProgramHeaderAsync(
                    ToLegacySettings(connection, configuration, timeProvider.GetUtcNow()),
                    status.ProgramNumber, token);
                var metadata = headerParser.Parse(header.FirstLines, configuration.ProgramAccess.HeaderPartPatterns);
                if (!metadata.IsValid)
                    throw new IOException("The active NC header did not contain a valid Part identity.");
                checks.Add(new("programAccess", true, CncComponentStates.Available,
                    "Active NC header and Part identity were read successfully."));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                checks.Add(new("programAccess", false, CncComponentStates.Unavailable, Safe(exception.Message)));
            }
        }

        var providerOkay = checks.First(value => value.Id == "mtconnect").Succeeded;
        var failedOptional = checks.Any(value => !value.Succeeded);
        var connectionStatus = !providerOkay ? CncConnectionStates.Offline
            : failedOptional ? CncConnectionStates.Degraded : CncConnectionStates.Online;
        return new(providerOkay, connectionStatus, checks);
    }

    private async Task<CncAdapterSnapshot> ReadMtConnectSnapshotAsync(CancellationToken token)
    {
        var status = pendingMtConnectRead
            ?? await ReadMtConnectAsync(configuration.Production.VariableNumber, token);
        pendingMtConnectRead = null;
        var at = status.ReadAt;
        var available = status.Availability == "AVAILABLE";
        var raw = new List<RawCncTelemetry>
        {
            new(MachineId, ConnectionId, CncAdapterTypes.HaasNgc, at,
                "MTCONNECT_CURRENT", status.DiagnosticPayload)
        };
        var components = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MTCONNECT"] = available ? CncComponentStates.Available : CncComponentStates.Unavailable,
            ["MDC"] = CncComponentStates.Unsupported
        };
        var health = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["machineState"] = available && status.MachineStatus is not null
                ? CncComponentStates.Available : CncComponentStates.Unavailable,
            ["activeProgram"] = available && status.ProgramNumber is not null
                ? CncComponentStates.Available : CncComponentStates.Unavailable,
            ["macroVariables"] = status.ProductionVariableValue is null
                ? CncComponentStates.Unavailable : CncComponentStates.Available,
            ["partCounter"] = status.Parts is null
                ? CncComponentStates.Unavailable : CncComponentStates.Available,
            ["programHeader"] = GetCapabilities().CanReadProgramHeader
                ? CncComponentStates.Unavailable : CncComponentStates.Unsupported
        };
        string? error = status.ProductionVariableError;
        var program = status.ProgramNumber;
        var dprntPart = await dprntPartReader.DrainAsync(configuration.Host,
            (configuration.MtConnect ?? new HaasMtConnectConfiguration(8082, connection.ConnectionTimeoutMs)).DprntPort,
            connection.ConnectionTimeoutMs, token);

        if (program != candidateProgram)
        {
            candidateProgram = program;
            candidatePolls = 1;
            if (program != cachedProgram)
            {
                cachedPart = new(null, null, false);
                cachedPartFromDprnt = false;
                cachedHeaderPath = new(null, null, false);
            }
        }
        else
        {
            candidatePolls++;
        }

        if (program is null)
        {
            cachedProgram = null;
            if (!cachedPartFromDprnt) cachedPart = new(null, null, false);
            cachedHeaderPath = new(null, null, false);
        }
        if (dprntPart is not null)
        {
            cachedPart = new(dprntPart, at, false);
            cachedPartFromDprnt = true;
            cachedHeaderPath = new(null, null, false);
            components["DPRNT"] = CncComponentStates.Available;
            health["programHeader"] = CncComponentStates.Available;
        }
        else if (program is not null && GetCapabilities().CanReadProgramHeader
                 && (program != cachedProgram || cachedPart.Stale)
                 && candidatePolls >= configuration.Monitoring.StableProgramPolls)
        {
            try
            {
                var header = await programProvider.ReadActiveProgramHeaderAsync(
                    ToLegacySettings(connection, configuration, at), program, token);
                var metadata = headerParser.Parse(header.FirstLines, configuration.ProgramAccess.HeaderPartPatterns);
                if (!metadata.IsValid)
                    throw new IOException("Part name could not be extracted from the active NC header.");
                cachedProgram = program;
                cachedPart = new(metadata.PartName, header.ReadTimestamp, false);
                cachedPartFromDprnt = false;
                cachedHeaderPath = new(header.SourcePath, header.ReadTimestamp, false);
                components["PROGRAM_ACCESS"] = CncComponentStates.Available;
                health["programHeader"] = CncComponentStates.Available;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                cachedPart = cachedPart with { Stale = cachedPart.Value is not null };
                cachedHeaderPath = cachedHeaderPath with { Stale = cachedHeaderPath.Value is not null };
                components["PROGRAM_ACCESS"] = CncComponentStates.Unavailable;
                health["programHeader"] = CncComponentStates.Unavailable;
                error ??= Safe(exception.Message);
            }
        }
        else if (GetCapabilities().CanReadProgramHeader)
        {
            components["PROGRAM_ACCESS"] = cachedPart.Value is not null
                ? CncComponentStates.Available : CncComponentStates.Unavailable;
            health["programHeader"] = cachedPart.Value is not null
                ? CncComponentStates.Available : CncComponentStates.Unavailable;
        }
        else
        {
            components["PROGRAM_ACCESS"] = CncComponentStates.Unsupported;
        }

        var degraded = health.Values.Any(value => value == CncComponentStates.Unavailable);
        var connectionStatus = !available ? CncConnectionStates.Offline
            : degraded ? CncConnectionStates.Degraded : CncConnectionStates.Online;
        var macro = status.ProductionVariableValue;
        var snapshot = new MachineSnapshot(
            MachineId, ConnectionId, CncAdapterTypes.HaasNgc, at,
            connectionStatus,
            available ? at : null,
            new(status.MachineStatus, status.MachineStatus is null ? null : at, false),
            new(new(program, program is null ? null : at, false), cachedPart, cachedHeaderPath),
            new(macro switch { 0 => "SETUP", 1 => "PRODUCTION", _ => null },
                configuration.Production.VariableNumber, new(macro, macro is null ? null : at, false)),
            new(status.Parts, status.Parts is null ? null : at, false),
            new(status.SpindleRpm is null ? null : status.SpindleRpm > 0,
                status.SpindleRpm, status.FeedRate, status.ActiveAlarmCount),
            components,
            health,
            error is null ? null : Safe(error));
        return new(snapshot, raw);
    }

    public async Task<CncAdapterSnapshot> ReadSnapshotAsync(CancellationToken token = default)
    {
        if (!connection.AllowRead)
            throw new InvalidOperationException("Read access is disabled for this CNC connection.");
        if (UsesMtConnect) return await ReadMtConnectSnapshotAsync(token);
        var at = timeProvider.GetUtcNow();
        var status = await client.GetMachineStatusAsync(token);
        var raw = new List<RawCncTelemetry>
        {
            new(MachineId, ConnectionId, CncAdapterTypes.HaasNgc, at, "Q500", status.RawResponse)
        };
        var components = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MDC"] = CncComponentStates.Available
        };
        var health = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["machineState"] = CncComponentStates.Available,
            ["activeProgram"] = CncComponentStates.Available,
            ["macroVariables"] = CncComponentStates.Available,
            ["partCounter"] = CncComponentStates.Available,
            ["programHeader"] = GetCapabilities().CanReadProgramHeader
                ? CncComponentStates.Unavailable : CncComponentStates.Unsupported
        };
        string? error = null;
        int? macro = null;
        try
        {
            macro = await client.ReadMacroAsync(configuration.Production.VariableNumber, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            health["macroVariables"] = CncComponentStates.Unavailable;
            error = Safe(exception.Message);
        }

        int? counter = status.Parts;
        if (configuration.Production.PartCounterSource != HaasPartCounterSources.Q500)
        {
            try { counter = await client.GetPartCounterAsync(configuration.Production.PartCounterSource, token); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                counter = null;
                health["partCounter"] = CncComponentStates.Unavailable;
                error ??= Safe(exception.Message);
            }
        }

        var program = status.ProgramNumber;
        if (program != candidateProgram)
        {
            candidateProgram = program;
            candidatePolls = 1;
            if (program != cachedProgram)
            {
                cachedPart = new(null, null, false);
                cachedHeaderPath = new(null, null, false);
            }
        }
        else
        {
            candidatePolls++;
        }
        if (program is null)
        {
            cachedProgram = null;
            cachedPart = new(null, null, false);
            cachedHeaderPath = new(null, null, false);
        }
        else if (GetCapabilities().CanReadProgramHeader
                 && (program != cachedProgram || cachedPart.Stale)
                 && candidatePolls >= configuration.Monitoring.StableProgramPolls)
        {
            try
            {
                var header = await programProvider.ReadActiveProgramHeaderAsync(
                    ToLegacySettings(connection, configuration, at), program, token);
                var metadata = headerParser.Parse(header.FirstLines, configuration.ProgramAccess.HeaderPartPatterns);
                if (!metadata.IsValid) throw new IOException("Part name could not be extracted from the active NC header.");
                cachedProgram = program;
                cachedPart = new(metadata.PartName, header.ReadTimestamp, false);
                cachedHeaderPath = new(header.SourcePath, header.ReadTimestamp, false);
                components["PROGRAM_ACCESS"] = CncComponentStates.Available;
                health["programHeader"] = CncComponentStates.Available;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                cachedPart = cachedPart with { Stale = cachedPart.Value is not null };
                cachedHeaderPath = cachedHeaderPath with { Stale = cachedHeaderPath.Value is not null };
                components["PROGRAM_ACCESS"] = CncComponentStates.Unavailable;
                health["programHeader"] = CncComponentStates.Unavailable;
                error ??= Safe(exception.Message);
            }
        }
        else if (GetCapabilities().CanReadProgramHeader)
        {
            components["PROGRAM_ACCESS"] = cachedPart.Value is not null
                ? CncComponentStates.Available : CncComponentStates.Unavailable;
            health["programHeader"] = cachedPart.Value is not null
                ? CncComponentStates.Available : CncComponentStates.Unavailable;
        }
        else
        {
            components["PROGRAM_ACCESS"] = CncComponentStates.Unsupported;
        }

        var degraded = health.Values.Any(value => value == CncComponentStates.Unavailable);
        var snapshot = new MachineSnapshot(
            MachineId, ConnectionId, CncAdapterTypes.HaasNgc, at,
            degraded ? CncConnectionStates.Degraded : CncConnectionStates.Online,
            at,
            new(status.MachineStatus, at, false),
            new(new(program, at, false), cachedPart, cachedHeaderPath),
            new(macro switch { 0 => "SETUP", 1 => "PRODUCTION", _ => null },
                configuration.Production.VariableNumber, new(macro, macro is null ? null : at, false)),
            new(counter, counter is null ? null : at, false),
            new(null, null, null, null),
            components,
            health,
            error);
        return new(snapshot, raw);
    }

    public async Task<CncOperationResult<CncProgramSnapshot>> ReadActiveProgramInfoAsync(CancellationToken token = default)
    {
        if (!GetCapabilities().CanReadActiveProgram) return CncOperationResult<CncProgramSnapshot>.Unsupported();
        try
        {
            await ReadSnapshotAsync(token);
            return CncOperationResult<CncProgramSnapshot>.Success(new(
                new(candidateProgram, timeProvider.GetUtcNow(), false), cachedPart, cachedHeaderPath));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CncOperationResult<CncProgramSnapshot>.Failure(Safe(exception.Message));
        }
    }

    public async Task<CncOperationResult<int>> ReadVariableAsync(int variable, CancellationToken token = default)
    {
        if (!GetCapabilities().CanReadVariables) return CncOperationResult<int>.Unsupported();
        try
        {
            if (!UsesMtConnect)
                return CncOperationResult<int>.Success(await client.ReadMacroAsync(variable, token));
            var status = await ReadMtConnectAsync(variable, token);
            return status.ProductionVariableValue is { } value
                ? CncOperationResult<int>.Success(value)
                : CncOperationResult<int>.Failure(status.ProductionVariableError
                    ?? $"MTConnect did not expose variable #{variable}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return CncOperationResult<int>.Failure(Safe(exception.Message)); }
    }

    public async Task<CncOperationResult<string>> WriteVariableAsync(int variable, int value, CancellationToken token = default)
    {
        if (!GetCapabilities().CanWriteVariables) return CncOperationResult<string>.Unsupported();
        if (variable != configuration.Production.VariableNumber || value != 0)
            return CncOperationResult<string>.Failure("Only ResetProductionMode may write the configured variable to 0.");
        try { return CncOperationResult<string>.Success(await client.WriteMacroAsync(variable, value, token)); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return CncOperationResult<string>.Failure(Safe(exception.Message)); }
    }

    public async Task<CncOperationResult<int>> ReadPartCounterAsync(CancellationToken token = default)
    {
        if (!GetCapabilities().CanReadPartCounter) return CncOperationResult<int>.Unsupported();
        try
        {
            if (UsesMtConnect)
            {
                var status = await ReadMtConnectAsync(configuration.Production.VariableNumber, token);
                return status.Parts is { } value
                    ? CncOperationResult<int>.Success(value)
                    : CncOperationResult<int>.Failure(
                        $"MTConnect did not expose counter source {configuration.Production.PartCounterSource}.");
            }
            return CncOperationResult<int>.Success(await client.GetPartCounterAsync(
                configuration.Production.PartCounterSource, token));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return CncOperationResult<int>.Failure(Safe(exception.Message)); }
    }

    public async ValueTask DisposeAsync()
    {
        await dprntPartReader.DisposeAsync();
        await client.DisposeAsync();
    }

    private Task<HaasMtConnectRead> ReadMtConnectAsync(
        int productionVariableNumber,
        CancellationToken token)
    {
        var mtConnect = configuration.MtConnect ?? new HaasMtConnectConfiguration(8082, connection.ConnectionTimeoutMs);
        return mtConnectReader.ReadAsync(
            configuration.Host,
            mtConnect.Port,
            mtConnect.TimeoutMs,
            productionVariableNumber,
            configuration.Production.PartCounterSource,
            token);
    }

    private bool UsesMtConnect => string.Equals(
        configuration.TelemetryProvider,
        HaasTelemetryProviders.MtConnect,
        StringComparison.OrdinalIgnoreCase);

    private static HaasConnectionSettings ToLegacySettings(
        MachineConnection connection, HaasNgcConnectionConfiguration config, DateTimeOffset now) => new(
            connection.MachineId, config.Host, config.Mdc.Port, config.MtConnect?.Port ?? 8082,
            config.MtConnect?.DprntPort ?? 8080,
            config.ProgramAccess.Enabled, config.ProgramAccess.SharePath,
            config.ProgramAccess.UsernameSecretId, config.Production.VariableNumber,
            config.Production.LegacyVariableAlias, config.Production.PartCounterSource,
            config.Monitoring.PollingIntervalMs, config.Mdc.TimeoutMs,
            config.Monitoring.StableProgramPolls, config.ProgramAccess.HeaderLineLimit,
            config.ProgramAccess.HeaderByteLimit, config.ProgramAccess.HeaderPartPatterns,
            connection.Enabled, connection.Version, connection.CreatedAt, now,
            string.IsNullOrWhiteSpace(config.TelemetryProvider)
                ? HaasTelemetryProviders.Mdc : config.TelemetryProvider);

    private static void Validate(HaasNgcConnectionConfiguration value)
    {
        if (string.IsNullOrWhiteSpace(value.Host)) throw new CncValidationException("host", "Host is required.");
        if (value.Mdc.Port is < 1 or > 65535) throw new CncValidationException("mdc.port", "MDC port is invalid.");
        if (value.MtConnect is { Port: < 1 or > 65535 })
            throw new CncValidationException("mtConnect.port", "MTConnect port is invalid.");
        if (value.MtConnect is { DprntPort: < 1 or > 65535 })
            throw new CncValidationException("mtConnect.dprntPort", "DPRNT port is invalid.");
        var provider = string.IsNullOrWhiteSpace(value.TelemetryProvider)
            ? HaasTelemetryProviders.Mdc : value.TelemetryProvider.Trim().ToUpperInvariant();
        if (!HaasTelemetryProviders.IsSupported(provider))
            throw new CncValidationException("telemetryProvider", "Telemetry provider must be MDC or MTCONNECT.");
        if (provider == HaasTelemetryProviders.MtConnect && value.MtConnect is null)
            throw new CncValidationException("mtConnect", "MTConnect configuration is required when MTCONNECT is the telemetry provider.");
        if (value.Production.VariableNumber is < 10000 or > 10999)
            throw new CncValidationException("production.variableNumber", "Production variable is invalid.");
        if (value.Production.VariableNumber != value.Production.LegacyVariableAlias + 10000)
            throw new CncValidationException("production.legacyVariableAlias", "Legacy alias does not map to the NGC variable.");
    }

    private static string Safe(string value) => value.Length <= 500 ? value : value[..500];
}

internal sealed class CncAdapterFactory(
    IHaasMdcClientFactory haasClientFactory,
    IHaasMtConnectReader mtConnectReader,
    INcProgramFileProvider programProvider,
    INcHeaderParser headerParser,
    TimeProvider timeProvider) : ICncAdapterFactory
{
    public ICncMachineAdapter CreateAdapter(MachineConnection connection) => connection.AdapterType switch
    {
        CncAdapterType.HaasNgc => new HaasNgcAdapter(
            connection, haasClientFactory, mtConnectReader, programProvider, headerParser, timeProvider),
        _ => throw new CncAdapterUnsupportedException(CncAdapterTypes.Serialize(connection.AdapterType))
    };
}
