using System.Text.Json;

namespace Meimad.Planner.Server.Domain.Cnc;

internal enum CncAdapterType
{
    HaasNgc,
    MtConnect,
    OpcUa,
    Custom,
    FanucFocas
}

internal static class CncAdapterTypes
{
    internal const string HaasNgc = "HAAS_NGC";
    internal const string FanucFocas = "FANUC_FOCAS";
    internal const string MtConnect = "MTCONNECT";
    internal const string OpcUa = "OPCUA";
    internal const string Custom = "CUSTOM";

    internal static string Serialize(CncAdapterType value) => value switch
    {
        CncAdapterType.HaasNgc => HaasNgc,
        CncAdapterType.FanucFocas => FanucFocas,
        CncAdapterType.MtConnect => MtConnect,
        CncAdapterType.OpcUa => OpcUa,
        CncAdapterType.Custom => Custom,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    internal static CncAdapterType Parse(string value) => value.Trim().ToUpperInvariant() switch
    {
        HaasNgc => CncAdapterType.HaasNgc,
        FanucFocas => CncAdapterType.FanucFocas,
        MtConnect => CncAdapterType.MtConnect,
        OpcUa => CncAdapterType.OpcUa,
        Custom => CncAdapterType.Custom,
        _ => throw new ArgumentOutOfRangeException(nameof(value), "Unknown CNC adapter type.")
    };
}

internal static class CncConnectionStates
{
    internal const string Disabled = "DISABLED";
    internal const string Connecting = "CONNECTING";
    internal const string Online = "ONLINE";
    internal const string Degraded = "DEGRADED";
    internal const string Offline = "OFFLINE";
    internal const string Error = "ERROR";
}

internal static class CncComponentStates
{
    internal const string Available = "AVAILABLE";
    internal const string Unavailable = "UNAVAILABLE";
    internal const string Unsupported = "UNSUPPORTED";
}

internal sealed record CncAdapterCapabilities(
    bool CanReadMachineState,
    bool CanReadActiveProgram,
    bool CanReadProgramHeader,
    bool CanReadVariables,
    bool CanWriteVariables,
    bool CanReadPartCounter,
    bool CanReadToolData,
    bool CanWriteToolData,
    bool CanReadAlarms,
    bool CanReadFeed,
    bool CanReadSpindle,
    bool CanUploadNcProgram,
    bool CanDownloadNcProgram);

internal sealed record CncAdapterDefinition(
    string Id,
    string DisplayName,
    bool Implemented,
    CncAdapterCapabilities Capabilities);

internal sealed record MachineConnection(
    string Id,
    string MachineId,
    CncAdapterType AdapterType,
    bool Enabled,
    string ConnectionStatus,
    DateTimeOffset? LastConnectionAttemptAt,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastDisconnectedAt,
    DateTimeOffset? LastSuccessfulPollAt,
    int PollingIntervalMs,
    int ConnectionTimeoutMs,
    int MaximumReconnectBackoffMs,
    bool AllowRead,
    bool AllowWrite,
    string ConfigurationJson,
    string? UsernameSecretId,
    string? PasswordSecretId,
    int RawTelemetryRetentionDays,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record HaasMdcConfiguration(int Port, int TimeoutMs);
internal sealed record HaasMtConnectConfiguration(int Port, int TimeoutMs, int DprntPort = 8080);
/// <summary>Where the Server reads the controller's DPRNT output from.</summary>
internal static class CncDprntSources
{
    /// <summary>Haas NGC style: the controller (or a serial-to-Ethernet bridge) accepts a TCP connection on the DPRNT port.</summary>
    internal const string Tcp = "TCP";
    /// <summary>Mazak Matrix style (DPR14 = 4): the controller appends DPRNT output to a file that the Server reads over a network share.</summary>
    internal const string File = "FILE";
    /// <summary>No DPRNT output is read; Part identity can only come from the NC header and no workflow events arrive.</summary>
    internal const string None = "NONE";

    internal static bool IsSupported(string value) => value is Tcp or File or None;
}

/// <summary>When the Server empties a controller-written DPRNT file that no G-code ever deletes.</summary>
internal static class CncDprntClearPolicies
{
    /// <summary>The Server only reads; the controller or a person keeps the file small.</summary>
    internal const string Never = "NEVER";
    /// <summary>
    /// The file is emptied after the poll that consumed a new Offset Loader completion line,
    /// so each file holds one setup session: the Offset Loader is the first program of a setup.
    /// </summary>
    internal const string OnOffsetLoader = "ON_OFFSET_LOADER";
    /// <summary>The file is emptied after every poll that consumed it completely.</summary>
    internal const string AfterRead = "AFTER_READ";

    internal static bool IsSupported(string value) => value is Never or OnOffsetLoader or AfterRead;
}

/// <summary>
/// DPRNT source shared by every adapter; absent JSON keeps the original Haas TCP behaviour.
/// <paramref name="Port"/> applies to the TCP source of adapters without a legacy
/// <c>mtConnect.dprntPort</c>; <paramref name="Host"/> replaces the controller address for
/// the TCP source when a serial-to-Ethernet bridge with its own address carries the RS-232
/// DPRNT output; <paramref name="FilePath"/> and <paramref name="ClearPolicy"/> apply to the
/// FILE source.
/// </summary>
internal sealed record CncDprntConfiguration(
    string Source = CncDprntSources.Tcp,
    string? FilePath = null,
    string ClearPolicy = CncDprntClearPolicies.Never,
    int? Port = null,
    string? Host = null);
internal sealed record HaasProgramAccessConfiguration(
    string Provider,
    bool Enabled,
    string? SharePath,
    string? UsernameSecretId,
    string? PasswordSecretId,
    int HeaderLineLimit,
    int HeaderByteLimit,
    IReadOnlyList<string> HeaderPartPatterns);
internal sealed record HaasProductionConfiguration(string PartCounterSource);
internal sealed record HaasMonitoringConfiguration(
    int PollingIntervalMs,
    int StableProgramPolls,
    int MaximumReconnectBackoffMs,
    int RawTelemetryRetentionDays);
internal sealed record HaasNgcConnectionConfiguration(
    string Host,
    HaasMdcConfiguration Mdc,
    HaasProgramAccessConfiguration ProgramAccess,
    HaasProductionConfiguration Production,
    HaasMonitoringConfiguration Monitoring,
    HaasMtConnectConfiguration? MtConnect = null,
    string TelemetryProvider = "MDC",
    string? MacAddress = null,
    CncDprntConfiguration? Dprnt = null);

/// <summary>Which FOCAS value the normalized part counter reads.</summary>
internal static class FocasPartCounterSources
{
    /// <summary>Parameter 6711: parts count since the last reset (system variable #3901).</summary>
    internal const string PartsCount6711 = "PARTS_COUNT_6711";
    /// <summary>Parameter 6712: total machined parts.</summary>
    internal const string PartsTotal6712 = "PARTS_TOTAL_6712";

    internal static bool IsSupported(string value) => value is PartsCount6711 or PartsTotal6712;

    internal static short ParameterNumber(string value) => value switch
    {
        PartsTotal6712 => 6712,
        _ => 6711
    };
}

/// <summary>
/// Optional bounded read of the executing program's head through FOCAS program upload,
/// used only to parse the NC header Part identity when no DPRNT PartName line exists.
/// </summary>
internal sealed record FocasProgramAccessConfiguration(
    string Provider = "NONE",
    bool Enabled = false,
    string ProgramFolder = FocasProgramAccessConfiguration.DefaultProgramFolder,
    int HeaderLineLimit = 50,
    int HeaderByteLimit = 32768,
    IReadOnlyList<string>? HeaderPartPatterns = null)
{
    internal const string UploadProvider = "FOCAS_PROGRAM_UPLOAD";
    internal const string DefaultProgramFolder = "//CNC_MEM/USER/PATH1/";
}

/// <summary>
/// Read-only FANUC FOCAS 2 Ethernet connection: controller state, executing program, part
/// counter, spindle/feed, and alarms come from the FOCAS library on the Server; Part identity
/// and workflow events come from the shared DPRNT source (TCP bridge or controller-written file).
/// </summary>
internal sealed record FanucFocasConnectionConfiguration(
    string Host,
    string? MacAddress = null,
    int Port = 8193,
    int TimeoutMs = 3000,
    string PartCounterSource = FocasPartCounterSources.PartsCount6711,
    CncDprntConfiguration? Dprnt = null,
    FocasProgramAccessConfiguration? ProgramAccess = null,
    HaasMonitoringConfiguration? Monitoring = null);

internal sealed record CncFreshValue<T>(T? Value, DateTimeOffset? ReadAt, bool Stale);
internal sealed record CncProgramSnapshot(
    CncFreshValue<string> ProgramNumber,
    CncFreshValue<string> PartName,
    CncFreshValue<string> HeaderSourcePath);
internal sealed record CncTelemetrySnapshot(
    bool? SpindleRunning,
    decimal? SpindleRpm,
    decimal? FeedRate,
    int? ActiveAlarmCount);

internal sealed record MachineSnapshot(
    string MachineId,
    string ConnectionId,
    string AdapterType,
    DateTimeOffset Timestamp,
    string ConnectionStatus,
    DateTimeOffset? LastSeenAt,
    CncFreshValue<string> MachineState,
    CncProgramSnapshot Program,
    CncFreshValue<int?> PartCounter,
    CncTelemetrySnapshot Telemetry,
    IReadOnlyDictionary<string, string> ComponentHealth,
    IReadOnlyDictionary<string, string> CapabilityHealth,
    string? LastError,
    int Version = 1);

internal sealed record RawCncTelemetry(
    string MachineId,
    string ConnectionId,
    string AdapterType,
    DateTimeOffset Timestamp,
    string Operation,
    string RawPayload);

internal sealed record CncAdapterCheck(string Id, bool Succeeded, string Status, string Message);
internal sealed record CncConnectionTestResult(
    bool OverallSuccess,
    string ConnectionStatus,
    IReadOnlyList<CncAdapterCheck> Checks);

internal sealed record CncOperationResult<T>(
    bool Supported,
    bool Available,
    T? Value,
    string? Error)
{
    internal static CncOperationResult<T> Success(T value) => new(true, true, value, null);
    internal static CncOperationResult<T> Failure(string error) => new(true, false, default, error);
    internal static CncOperationResult<T> Unsupported() => new(false, false, default, "unsupported");
}

internal sealed record CncAdapterSnapshot(
    MachineSnapshot Snapshot,
    IReadOnlyList<RawCncTelemetry> RawTelemetry);

internal static class CncJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
