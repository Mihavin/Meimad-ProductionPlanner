using System.Text.Json;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Domain.Haas;

namespace Meimad.Planner.Server.Infrastructure.Cnc;

internal sealed class HaasNgcAdapter : ICncMachineAdapter
{
    private readonly MachineConnection connection;
    private readonly HaasNgcConnectionConfiguration configuration;
    private readonly IHaasMdcClient client;
    private readonly INcProgramFileProvider programProvider;
    private readonly INcHeaderParser headerParser;
    private readonly TimeProvider timeProvider;
    private string? candidateProgram;
    private int candidatePolls;
    private string? cachedProgram;
    private CncFreshValue<string> cachedPart = new(null, null, false);
    private CncFreshValue<string> cachedHeaderPath = new(null, null, false);

    internal HaasNgcAdapter(
        MachineConnection connection,
        IHaasMdcClientFactory clientFactory,
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
        connection.AllowWrite,
        connection.AllowRead,
        false, false, false, false, false, false, false);

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        client.ConnectAsync(cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        client.DisconnectAsync(cancellationToken);

    public async Task<CncConnectionTestResult> TestConnectionAsync(CancellationToken token = default)
    {
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

    public async Task<CncAdapterSnapshot> ReadSnapshotAsync(CancellationToken token = default)
    {
        if (!connection.AllowRead)
            throw new InvalidOperationException("Read access is disabled for this CNC connection.");
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
        try { return CncOperationResult<int>.Success(await client.ReadMacroAsync(variable, token)); }
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
            return CncOperationResult<int>.Success(await client.GetPartCounterAsync(
                configuration.Production.PartCounterSource, token));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return CncOperationResult<int>.Failure(Safe(exception.Message)); }
    }

    public async ValueTask DisposeAsync() => await client.DisposeAsync();

    private static HaasConnectionSettings ToLegacySettings(
        MachineConnection connection, HaasNgcConnectionConfiguration config, DateTimeOffset now) => new(
            connection.MachineId, config.Host, config.Mdc.Port, 8082,
            config.ProgramAccess.Enabled, config.ProgramAccess.SharePath,
            config.ProgramAccess.UsernameSecretId, config.Production.VariableNumber,
            config.Production.LegacyVariableAlias, config.Production.PartCounterSource,
            config.Monitoring.PollingIntervalMs, config.Mdc.TimeoutMs,
            config.Monitoring.StableProgramPolls, config.ProgramAccess.HeaderLineLimit,
            config.ProgramAccess.HeaderByteLimit, config.ProgramAccess.HeaderPartPatterns,
            connection.Enabled, connection.Version, connection.CreatedAt, now);

    private static void Validate(HaasNgcConnectionConfiguration value)
    {
        if (string.IsNullOrWhiteSpace(value.Host)) throw new CncValidationException("host", "Host is required.");
        if (value.Mdc.Port is < 1 or > 65535) throw new CncValidationException("mdc.port", "MDC port is invalid.");
        if (value.Production.VariableNumber is < 10000 or > 10999)
            throw new CncValidationException("production.variableNumber", "Production variable is invalid.");
        if (value.Production.VariableNumber != value.Production.LegacyVariableAlias + 10000)
            throw new CncValidationException("production.legacyVariableAlias", "Legacy alias does not map to the NGC variable.");
    }

    private static string Safe(string value) => value.Length <= 500 ? value : value[..500];
}

internal sealed class CncAdapterFactory(
    IHaasMdcClientFactory haasClientFactory,
    INcProgramFileProvider programProvider,
    INcHeaderParser headerParser,
    TimeProvider timeProvider) : ICncAdapterFactory
{
    public ICncMachineAdapter CreateAdapter(MachineConnection connection) => connection.AdapterType switch
    {
        CncAdapterType.HaasNgc => new HaasNgcAdapter(
            connection, haasClientFactory, programProvider, headerParser, timeProvider),
        _ => throw new CncAdapterUnsupportedException(CncAdapterTypes.Serialize(connection.AdapterType))
    };
}
