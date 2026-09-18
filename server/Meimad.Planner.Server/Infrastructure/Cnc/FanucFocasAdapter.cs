using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.Fanuc;
using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Cnc;

namespace Meimad.Planner.Server.Infrastructure.Cnc;

/// <summary>
/// Read-only FANUC FOCAS 2 adapter. FOCAS supplies controller state, executing program, part
/// counter, spindle/feed, and alarms; the shared DPRNT source (serial-to-Ethernet TCP bridge or a
/// controller-written print file) supplies Part identity and <c>MEIMAD/</c> workflow events; an
/// optional bounded FOCAS program upload parses the NC header when no DPRNT PartName exists.
/// File name and O-number never become Part identity.
/// </summary>
internal sealed class FanucFocasAdapter : ICncMachineAdapter
{
    private readonly MachineConnection connection;
    private readonly FanucFocasConnectionConfiguration configuration;
    private readonly FocasProgramAccessConfiguration programAccess;
    private readonly HaasMonitoringConfiguration monitoring;
    private readonly IFocasClient client;
    private readonly INcHeaderParser headerParser;
    private readonly TimeProvider timeProvider;
    private readonly CncDprntSource dprntSource;
    private string? candidateProgram;
    private int candidatePolls;
    private string? cachedProgram;
    private CncFreshValue<string> cachedPart = new(null, null, false);
    private bool cachedPartFromDprnt;
    private CncFreshValue<string> cachedHeaderPath = new(null, null, false);

    internal FanucFocasAdapter(
        MachineConnection connection,
        IFocasClientFactory clientFactory,
        INcHeaderParser headerParser,
        TimeProvider timeProvider)
    {
        this.connection = connection;
        configuration = JsonSerializer.Deserialize<FanucFocasConnectionConfiguration>(
            connection.ConfigurationJson, CncJson.Options)
            ?? throw new CncValidationException("configuration", "FANUC FOCAS configuration is invalid.");
        Validate(configuration);
        programAccess = configuration.ProgramAccess ?? new FocasProgramAccessConfiguration();
        monitoring = configuration.Monitoring ?? new HaasMonitoringConfiguration(
            connection.PollingIntervalMs, 2, connection.MaximumReconnectBackoffMs, connection.RawTelemetryRetentionDays);
        client = clientFactory.Create(configuration with { TimeoutMs = connection.ConnectionTimeoutMs });
        dprntSource = new CncDprntSource(
            configuration.Host, 8080, connection.ConnectionTimeoutMs, configuration.Dprnt);
        this.headerParser = headerParser;
        this.timeProvider = timeProvider;
    }

    public string ConnectionId => connection.Id;
    public string MachineId => connection.MachineId;
    public CncAdapterType AdapterType => CncAdapterType.FanucFocas;

    private bool HeaderUploadEnabled => programAccess.Enabled
        && programAccess.Provider == FocasProgramAccessConfiguration.UploadProvider;

    public CncAdapterCapabilities GetCapabilities() => new(
        connection.AllowRead,
        connection.AllowRead,
        connection.AllowRead && HeaderUploadEnabled,
        false,
        false,
        connection.AllowRead,
        false, false,
        connection.AllowRead, connection.AllowRead, connection.AllowRead,
        false, false);

    public Task ConnectAsync(CancellationToken cancellationToken = default) => client.ConnectAsync(cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => client.DisconnectAsync(cancellationToken);

    public async Task<CncConnectionTestResult> TestConnectionAsync(CancellationToken token = default)
    {
        var checks = new List<CncAdapterCheck>();
        FocasControllerStatus? status = null;
        try
        {
            await client.ConnectAsync(token);
            status = await client.ReadStatusAsync(token);
            checks.Add(new("focas", true, CncComponentStates.Available,
                $"FOCAS returned controller state {status.MachineState}"
                + (status.ProgramNumber is null ? " with no executing program." : $" with executing program {status.ProgramNumber}.")));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            checks.Add(new("focas", false, CncComponentStates.Unavailable, Safe(exception.Message)));
        }

        if (!GetCapabilities().CanReadProgramHeader)
        {
            checks.Add(new("programAccess", true, CncComponentStates.Unsupported,
                "FOCAS program-header upload is not enabled; Part identity comes from DPRNT lines."));
        }
        else if (status?.ProgramNumber is null)
        {
            checks.Add(new("programAccess", true, CncComponentStates.Available,
                "Program upload is enabled; FOCAS did not report an executing program for a header test."));
        }
        else
        {
            try
            {
                var metadata = await ReadHeaderAsync(status.ProgramNumber, token);
                checks.Add(new("programAccess", true, CncComponentStates.Available,
                    $"Program head of {status.ProgramNumber} was uploaded and Part {metadata.PartName} was parsed."));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                checks.Add(new("programAccess", false, CncComponentStates.Unavailable, Safe(exception.Message)));
            }
        }

        if (dprntSource.Enabled)
        {
            var dprnt = await dprntSource.DrainAsync(allowClear: false, token);
            checks.Add(new("dprnt", dprnt.Available,
                dprnt.Available ? CncComponentStates.Available : CncComponentStates.Unavailable,
                dprnt.Available ? dprntSource.SuccessMessage(dprnt) : dprnt.Error ?? "The DPRNT source is unavailable."));
        }
        else
        {
            checks.Add(new("dprnt", true, CncComponentStates.Unsupported,
                "No DPRNT source is configured; workflow events cannot be observed on this Machine."));
        }

        var focasOkay = checks.First(value => value.Id == "focas").Succeeded;
        var failedOptional = checks.Any(value => !value.Succeeded);
        var connectionStatus = !focasOkay ? CncConnectionStates.Offline
            : failedOptional ? CncConnectionStates.Degraded : CncConnectionStates.Online;
        return new(focasOkay && !failedOptional, connectionStatus, checks);
    }

    public async Task<CncAdapterSnapshot> ReadSnapshotAsync(CancellationToken token = default)
    {
        if (!connection.AllowRead)
            throw new InvalidOperationException("Read access is disabled for this CNC connection.");
        var at = timeProvider.GetUtcNow();
        var status = await client.ReadStatusAsync(token);
        var raw = new List<RawCncTelemetry>
        {
            new(MachineId, ConnectionId, CncAdapterTypes.FanucFocas, at, "FOCAS_STATUS", status.RawPayload)
        };
        var components = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FOCAS"] = CncComponentStates.Available
        };
        if (!GetCapabilities().CanReadProgramHeader)
            components["PROGRAM_ACCESS"] = CncComponentStates.Unsupported;
        var health = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["machineState"] = CncComponentStates.Available,
            ["activeProgram"] = status.ProgramNumber is null
                ? CncComponentStates.Unavailable : CncComponentStates.Available,
            ["partCounter"] = CncComponentStates.Available,
            ["programHeader"] = GetCapabilities().CanReadProgramHeader
                ? CncComponentStates.Unavailable : CncComponentStates.Unsupported
        };
        string? error = null;
        int? counter;
        try { counter = await client.ReadPartCounterAsync(configuration.PartCounterSource, token); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            counter = null;
            health["partCounter"] = CncComponentStates.Unavailable;
            error ??= Safe(exception.Message);
        }

        var dprnt = await dprntSource.DrainAsync(allowClear: true, token);
        foreach (var eventLine in dprnt.Result.EventLines)
            raw.Add(new(MachineId, ConnectionId, CncAdapterTypes.FanucFocas, at, "DPRINT_EVENT", eventLine));
        dprntSource.ApplyFileHealth(dprnt, components, health, ref error);
        if (!dprntSource.Enabled) components["DPRNT"] = CncComponentStates.Unsupported;

        var program = status.ProgramNumber;
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
        if (dprnt.Result.PartName is not null)
        {
            cachedPart = new(dprnt.Result.PartName, at, false);
            cachedPartFromDprnt = true;
            cachedHeaderPath = new(null, null, false);
            components["DPRNT"] = CncComponentStates.Available;
            health["programHeader"] = CncComponentStates.Available;
        }
        else if (program is not null && GetCapabilities().CanReadProgramHeader
                 && (program != cachedProgram || cachedPart.Stale)
                 && candidatePolls >= monitoring.StableProgramPolls)
        {
            try
            {
                var metadata = await ReadHeaderAsync(program, token);
                cachedProgram = program;
                cachedPart = new(metadata.PartName, at, false);
                cachedPartFromDprnt = false;
                cachedHeaderPath = new(ProgramPath(program), at, false);
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
            MachineId, ConnectionId, CncAdapterTypes.FanucFocas, at,
            degraded ? CncConnectionStates.Degraded : CncConnectionStates.Online,
            at,
            new(status.MachineState, at, false),
            new(new(program, program is null ? null : at, false), cachedPart, cachedHeaderPath),
            new(counter, counter is null ? null : at, false),
            new(status.SpindleRpm is null ? null : status.SpindleRpm > 0,
                status.SpindleRpm, status.FeedRate, status.ActiveAlarmCount),
            components,
            health,
            error is null ? null : Safe(error));
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

    public Task<CncOperationResult<int>> ReadVariableAsync(int variable, CancellationToken token = default) =>
        Task.FromResult(CncOperationResult<int>.Unsupported());

    public Task<CncOperationResult<string>> WriteVariableAsync(int variable, int value, CancellationToken token = default) =>
        Task.FromResult(CncOperationResult<string>.Unsupported());

    public async Task<CncOperationResult<int>> ReadPartCounterAsync(CancellationToken token = default)
    {
        if (!GetCapabilities().CanReadPartCounter) return CncOperationResult<int>.Unsupported();
        try
        {
            return CncOperationResult<int>.Success(
                await client.ReadPartCounterAsync(configuration.PartCounterSource, token));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return CncOperationResult<int>.Failure(Safe(exception.Message)); }
    }

    public async ValueTask DisposeAsync()
    {
        await dprntSource.DisposeAsync();
        await client.DisposeAsync();
    }

    private string ProgramPath(string program)
    {
        var folder = string.IsNullOrWhiteSpace(programAccess.ProgramFolder)
            ? FocasProgramAccessConfiguration.DefaultProgramFolder : programAccess.ProgramFolder.Trim();
        if (!folder.EndsWith('/')) folder += "/";
        return folder + program.Trim();
    }

    private async Task<Domain.Haas.NcHeaderMetadata> ReadHeaderAsync(string program, CancellationToken token)
    {
        var lines = await client.ReadProgramHeadAsync(
            ProgramPath(program), programAccess.HeaderByteLimit, programAccess.HeaderLineLimit, token);
        var metadata = headerParser.Parse(lines, programAccess.HeaderPartPatterns);
        if (!metadata.IsValid)
            throw new IOException("Part name could not be extracted from the executing NC program head.");
        return metadata;
    }

    private static void Validate(FanucFocasConnectionConfiguration value)
    {
        if (string.IsNullOrWhiteSpace(value.Host)
            || !IPAddress.TryParse(value.Host, out var address)
            || IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
            throw new CncValidationException("host", "A fixed, non-loopback CNC IP address is required.");
        if (string.IsNullOrWhiteSpace(value.MacAddress)
            || !Regex.IsMatch(value.MacAddress.Replace('-', ':').ToUpperInvariant(),
                "^[0-9A-F]{2}(:[0-9A-F]{2}){5}$", RegexOptions.CultureInvariant))
            throw new CncValidationException("macAddress", "A six-octet CNC MAC address is required.");
        if (value.Port is < 1 or > 65535) throw new CncValidationException("port", "FOCAS port is invalid.");
        if (!FocasPartCounterSources.IsSupported(value.PartCounterSource?.Trim().ToUpperInvariant() ?? string.Empty))
            throw new CncValidationException("partCounterSource", "Part counter source must be PARTS_COUNT_6711 or PARTS_TOTAL_6712.");
        if (value.Dprnt is { } dprnt)
        {
            var source = dprnt.Source?.Trim().ToUpperInvariant() ?? string.Empty;
            if (!CncDprntSources.IsSupported(source))
                throw new CncValidationException("dprnt.source", "DPRNT source must be TCP, FILE, or NONE.");
            if (source == CncDprntSources.File && string.IsNullOrWhiteSpace(dprnt.FilePath))
                throw new CncValidationException("dprnt.filePath", "A DPRNT file path is required when the DPRNT source is FILE.");
            if (!CncDprntClearPolicies.IsSupported(dprnt.ClearPolicy?.Trim().ToUpperInvariant() ?? string.Empty))
                throw new CncValidationException("dprnt.clearPolicy", "DPRNT file clear policy must be NEVER, ON_OFFSET_LOADER, or AFTER_READ.");
            if (dprnt.Port is < 1 or > 65535)
                throw new CncValidationException("dprnt.port", "DPRNT port is invalid.");
            if (!string.IsNullOrWhiteSpace(dprnt.Host) && Uri.CheckHostName(dprnt.Host.Trim()) == UriHostNameType.Unknown)
                throw new CncValidationException("dprnt.host", "DPRNT TCP host must be an IP address or host name.");
        }
        if (value.ProgramAccess is { Enabled: true } access
            && access.Provider != FocasProgramAccessConfiguration.UploadProvider)
            throw new CncValidationException("programAccess.provider", "Only FOCAS program upload is implemented for FANUC header access.");
    }

    private static string Safe(string value) => value.Length <= 500 ? value : value[..500];
}
