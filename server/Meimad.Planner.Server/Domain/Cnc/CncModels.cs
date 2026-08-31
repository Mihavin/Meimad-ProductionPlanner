using System.Text.Json;

namespace Meimad.Planner.Server.Domain.Cnc;

internal enum CncAdapterType
{
    HaasNgc,
    MtConnect,
    OpcUa,
    Custom
}

internal static class CncAdapterTypes
{
    internal const string HaasNgc = "HAAS_NGC";
    internal const string MtConnect = "MTCONNECT";
    internal const string OpcUa = "OPCUA";
    internal const string Custom = "CUSTOM";

    internal static string Serialize(CncAdapterType value) => value switch
    {
        CncAdapterType.HaasNgc => HaasNgc,
        CncAdapterType.MtConnect => MtConnect,
        CncAdapterType.OpcUa => OpcUa,
        CncAdapterType.Custom => Custom,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    internal static CncAdapterType Parse(string value) => value.Trim().ToUpperInvariant() switch
    {
        HaasNgc => CncAdapterType.HaasNgc,
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
    string? MacAddress = null);

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
