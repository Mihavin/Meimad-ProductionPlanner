namespace Meimad.Planner.Server.Domain.Haas;

internal static class HaasConnectivityStates
{
    internal const string Online = "ONLINE";
    internal const string Offline = "OFFLINE";
    internal const string Error = "ERROR";
}

internal static class HaasBenchStates
{
    internal const string Waiting = "WAITING";
    internal const string Setup = "SETUP";
    internal const string Production = "PRODUCTION";
    internal const string Completed = "COMPLETED";
}

internal static class HaasPartCounterSources
{
    internal const string Q500 = "Q500";
    internal const string M30Counter1 = "M30_COUNTER_1";
    internal const string M30Counter2 = "M30_COUNTER_2";

    internal static bool IsSupported(string value) =>
        value is Q500 or M30Counter1 or M30Counter2;
}

internal static class HaasTelemetryProviders
{
    internal const string Mdc = "MDC";
    internal const string MtConnect = "MTCONNECT";

    internal static bool IsSupported(string value) => value is Mdc or MtConnect;
}

internal sealed record HaasConnectionSettings(
    string MachineId,
    string Host,
    int MdcPort,
    int MtConnectPort,
    int DprntPort,
    bool LocalNetShareEnabled,
    string? LocalNetSharePath,
    string? CredentialsReference,
    int ProductionModeVariable,
    int LegacyVariableAlias,
    string PartCounterSource,
    int PollingIntervalMs,
    int ConnectionTimeoutMs,
    int StableProgramPolls,
    int HeaderLineLimit,
    int HeaderByteLimit,
    IReadOnlyList<string> HeaderPartPatterns,
    bool Enabled,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string TelemetryProvider = HaasTelemetryProviders.Mdc);

internal sealed record HaasProgramStatus(
    string? ProgramNumber,
    string MachineStatus,
    int Parts,
    DateTimeOffset Timestamp,
    string RawResponse);

internal sealed record MachineNcHeader(
    string ProgramNumber,
    IReadOnlyList<string> FirstLines,
    string SourcePath,
    DateTimeOffset ReadTimestamp);

internal sealed record NcHeaderMetadata(
    string Status,
    string? PartName,
    string? CaseNumber,
    string? Operation,
    string? Revision,
    string? ProgramNumber,
    string RawHeader,
    string ParserVersion)
{
    internal bool IsValid => Status == "VALID" && !string.IsNullOrWhiteSpace(PartName);
}

internal sealed record HaasMachineSnapshot(
    string MachineId,
    DateTimeOffset Timestamp,
    string ConnectivityState,
    string? MachineStatus,
    string? ProgramNumber,
    string? MachineHeaderPartName,
    string? MachineHeaderSourcePath,
    DateTimeOffset? HeaderReadAt,
    int ProductionVariableNumber,
    int ProductionVariableValue,
    DateTimeOffset? ProductionVariableChangedAt,
    int? PartCounter,
    string? RawMdcStatus,
    string? LastError,
    DateTimeOffset? LastSeenAt,
    int Version = 1);

internal sealed record HaasBenchSession(
    string BenchId,
    string BatchOperationId,
    string MachineId,
    string State,
    string MachineProgramNumber,
    string MachinePartName,
    DateTimeOffset SetupStartedAt,
    DateTimeOffset? SetupEndedAt,
    DateTimeOffset? ProductionStartedAt,
    bool PartCountingEnabled,
    int? PartCounterBaseline,
    int? PreviousPartCounter,
    int ProducedQuantity,
    DateTimeOffset? CompletedAt,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record HaasBenchStateInterval(
    string IntervalId,
    string BenchId,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Source);

internal sealed record HaasMachineMonitor(
    HaasConnectionSettings Settings,
    HaasMachineSnapshot? Snapshot,
    HaasBenchSession? ActiveBench,
    IReadOnlyList<HaasBenchStateInterval> Intervals,
    IReadOnlyList<HaasEvent> RecentEvents,
    double ActualSetupSeconds,
    double ActualProductionSeconds);

internal sealed record HaasEvent(
    string EventId,
    string EventType,
    string MachineId,
    string? BenchId,
    DateTimeOffset Timestamp,
    string PayloadJson,
    string DedupeKey);

internal sealed record HaasMacroWriteResult(
    bool Succeeded,
    int VariableNumber,
    int? PreviousValue,
    int RequestedValue,
    string RawResponse,
    string? ErrorMessage);
