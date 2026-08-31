using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Domain.Haas;

namespace Meimad.Planner.Server.Application.Haas;

internal interface INcHeaderParser
{
    NcHeaderMetadata Parse(IEnumerable<string> lines, IReadOnlyList<string>? partPatterns = null);
}

internal interface IHaasMdcClient : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<HaasProgramStatus> GetMachineStatusAsync(CancellationToken cancellationToken = default);
    Task<string?> GetCurrentProgramAsync(CancellationToken cancellationToken = default);
    Task<int> GetPartCounterAsync(string source, CancellationToken cancellationToken = default);
    Task<int> ReadMacroAsync(int variableNumber, CancellationToken cancellationToken = default);
    Task<string> WriteMacroAsync(int variableNumber, int value, CancellationToken cancellationToken = default);
}

internal interface IHaasMdcClientFactory
{
    IHaasMdcClient Create(HaasConnectionSettings settings);
}

internal interface IHaasMtConnectReader
{
    Task<HaasMtConnectRead> ReadAsync(
        string host,
        int port,
        int timeoutMs,
        string partCounterSource,
        CancellationToken cancellationToken = default);
}

internal sealed record HaasMtConnectRead(
    string? DeviceId,
    string? DeviceName,
    string Availability,
    string? MachineStatus,
    string? ControllerMode,
    string? ProgramNumber,
    int? Parts,
    DateTimeOffset ReadAt,
    decimal? SpindleRpm,
    decimal? FeedRate,
    int? ActiveAlarmCount,
    string DiagnosticPayload);

internal interface IHaasProgramReader : INcProgramFileProvider;

internal interface IHaasIntegrationRepository
{
    Task<IReadOnlyList<HaasConnectionSettings>> ListEnabledSettingsAsync(CancellationToken cancellationToken);
    Task<HaasConnectionSettings?> GetSettingsAsync(string machineId, CancellationToken cancellationToken);
    Task<HaasConnectionSettings> UpsertSettingsAsync(
        HaasConnectionSettings settings, int expectedVersion, EditAuthority authority, CancellationToken cancellationToken);
    Task<HaasMachineMonitor?> ReadMonitorAsync(string machineId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<HaasMachineSnapshot?> GetSnapshotAsync(string machineId, CancellationToken cancellationToken);
    Task SaveSnapshotAsync(HaasMachineSnapshot snapshot, CancellationToken cancellationToken);
    Task<HaasObservationResult> ApplyObservationAsync(
        HaasMachineSnapshot snapshot, DateTimeOffset observedAt, CancellationToken cancellationToken);
}

internal sealed record HaasObservationResult(
    string MatchResult,
    HaasBenchSession? ActiveBench,
    IReadOnlyList<string> CreatedEventTypes);

internal sealed record HaasSettingsUpdate(
    string? Host,
    string? MacAddress,
    int MdcPort,
    int MtConnectPort,
    int DprntPort,
    bool LocalNetShareEnabled,
    string? LocalNetSharePath,
    string? CredentialsReference,
    string? PartCounterSource,
    int PollingIntervalMs,
    int ConnectionTimeoutMs,
    int StableProgramPolls,
    int HeaderLineLimit,
    int HeaderByteLimit,
    IReadOnlyList<string>? HeaderPartPatterns,
    bool Enabled,
    int ExpectedVersion,
    string? TelemetryProvider);

internal sealed class HaasValidationException(string field, string message) : Exception(message)
{
    internal string Field { get; } = field;
}

internal sealed class HaasSettingsNotFoundException(string machineId)
    : Exception($"Haas settings for Machine '{machineId}' were not found.");

internal sealed class HaasSettingsConcurrencyException()
    : Exception("The Haas settings were changed by another editor.");

internal sealed class HaasProgramHeaderUnavailableException(string message) : Exception(message);
