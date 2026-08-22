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
    Task<string> BeginMacroWriteAuditAsync(
        string machineId, string? benchId, string? toolTableId, int variableNumber,
        int? oldValue, int newValue, string reason, string initiatedBy, DateTimeOffset at,
        CancellationToken cancellationToken);
    Task CompleteMacroWriteAuditAsync(
        string auditId, bool succeeded, string? rawResponse, string? errorMessage,
        DateTimeOffset at, CancellationToken cancellationToken);
}

internal sealed record HaasObservationResult(
    string MatchResult,
    HaasBenchSession? ActiveBench,
    IReadOnlyList<string> CreatedEventTypes);

internal sealed record HaasSettingsUpdate(
    string? Host,
    int MdcPort,
    int MtConnectPort,
    bool LocalNetShareEnabled,
    string? LocalNetSharePath,
    string? CredentialsReference,
    int ProductionModeVariable,
    int LegacyVariableAlias,
    string? PartCounterSource,
    int PollingIntervalMs,
    int ConnectionTimeoutMs,
    int StableProgramPolls,
    int HeaderLineLimit,
    int HeaderByteLimit,
    IReadOnlyList<string>? HeaderPartPatterns,
    bool Enabled,
    int ExpectedVersion);

internal sealed class HaasValidationException(string field, string message) : Exception(message)
{
    internal string Field { get; } = field;
}

internal sealed class HaasSettingsNotFoundException(string machineId)
    : Exception($"Haas settings for Machine '{machineId}' were not found.");

internal sealed class HaasSettingsConcurrencyException()
    : Exception("The Haas settings were changed by another editor.");

internal sealed class HaasProgramHeaderUnavailableException(string message) : Exception(message);
