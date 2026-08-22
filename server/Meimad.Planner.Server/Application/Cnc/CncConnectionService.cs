using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Cnc;

namespace Meimad.Planner.Server.Application.Cnc;

internal sealed record CncConnectionUpdate(
    string AdapterType,
    bool Enabled,
    int PollingIntervalMs,
    int ConnectionTimeoutMs,
    int MaximumReconnectBackoffMs,
    bool AllowRead,
    bool AllowWrite,
    int RawTelemetryRetentionDays,
    JsonElement Configuration,
    string? UsernameSecretId,
    string? PasswordSecretId,
    int ExpectedVersion);

internal sealed class CncConnectionService(
    ICncConnectionRepository repository,
    ICncConnectionManager manager,
    CncAdapterRegistry registry,
    TimeProvider timeProvider)
{
    internal Task<MachineConnection?> GetConnectionAsync(string machineId, CancellationToken token) =>
        repository.GetConnectionAsync(Required(machineId, "machineId"), token);

    internal IReadOnlyList<CncAdapterDefinition> ListAdapterDefinitions() => registry.List();

    internal async Task<MachineConnection> UpdateConnectionAsync(
        string machineId, CncConnectionUpdate update, EditAuthority authority, CancellationToken token)
    {
        machineId = Required(machineId, "machineId");
        CncAdapterType adapter;
        try { adapter = CncAdapterTypes.Parse(update.AdapterType); }
        catch (ArgumentOutOfRangeException)
        { throw new CncValidationException("adapterType", "Select a registered CNC adapter type."); }
        var definition = registry.Get(adapter);
        if (update.Enabled && !definition.Implemented)
            throw new CncValidationException("adapterType", $"{definition.DisplayName} is not implemented and cannot be enabled.");
        Range(update.PollingIntervalMs, 500, 60000, "pollingIntervalMs");
        Range(update.ConnectionTimeoutMs, 250, 60000, "connectionTimeoutMs");
        Range(update.MaximumReconnectBackoffMs, 1000, 300000, "maximumReconnectBackoffMs");
        Range(update.RawTelemetryRetentionDays, 1, 90, "rawTelemetryRetentionDays");
        if (update.Enabled && !update.AllowRead)
            throw new CncValidationException("allowRead", "An enabled MVP CNC connection requires read permission.");
        if (adapter != CncAdapterType.HaasNgc && update.AllowWrite)
            throw new CncValidationException("allowWrite", "Write permission is unavailable for unimplemented adapters.");

        var configurationJson = adapter switch
        {
            CncAdapterType.HaasNgc => ValidateHaas(update.Configuration, update),
            _ => "{}"
        };
        var now = timeProvider.GetUtcNow();
        var current = await repository.GetConnectionAsync(machineId, token);
        var value = new MachineConnection(
            current?.Id ?? $"cnc-{machineId}", machineId, adapter, update.Enabled,
            update.Enabled ? current?.ConnectionStatus ?? CncConnectionStates.Offline : CncConnectionStates.Disabled,
            current?.LastConnectionAttemptAt, current?.LastConnectedAt, current?.LastDisconnectedAt,
            current?.LastSuccessfulPollAt, update.PollingIntervalMs, update.ConnectionTimeoutMs,
            update.MaximumReconnectBackoffMs, update.AllowRead, update.AllowWrite,
            configurationJson, Optional(update.UsernameSecretId), Optional(update.PasswordSecretId),
            update.RawTelemetryRetentionDays, current?.Version + 1 ?? 1,
            current?.CreatedAt ?? now, now);
        return await repository.UpsertConnectionAsync(value, update.ExpectedVersion, authority, token);
    }

    internal Task<CncConnectionTestResult> TestConnectionAsync(string machineId, CancellationToken token) =>
        manager.TestConnectionAsync(Required(machineId, "machineId"), token);

    internal Task ReconnectAsync(string machineId, CancellationToken token) =>
        manager.RequestReconnectAsync(Required(machineId, "machineId"), token);

    internal Task<MachineSnapshot?> GetSnapshotAsync(string machineId, CancellationToken token) =>
        repository.GetCurrentSnapshotAsync(Required(machineId, "machineId"), token);

    internal Task<IReadOnlyList<RawCncTelemetry>> GetDiagnosticsAsync(
        string machineId, int limit, CancellationToken token) =>
        repository.ReadDiagnosticsAsync(Required(machineId, "machineId"), limit, token);

    private static string ValidateHaas(JsonElement json, CncConnectionUpdate update)
    {
        HaasNgcConnectionConfiguration value;
        try
        {
            value = json.Deserialize<HaasNgcConnectionConfiguration>(CncJson.Options)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new CncValidationException("configuration", "Haas NGC configuration is invalid.");
        }
        if (string.IsNullOrWhiteSpace(value.Host) || value.Host.Length > 255)
            throw new CncValidationException("configuration.host", "Haas host is required.");
        Range(value.Mdc.Port, 1, 65535, "configuration.mdc.port");
        if (value.Mdc.TimeoutMs != update.ConnectionTimeoutMs)
            throw new CncValidationException("configuration.mdc.timeoutMs", "MDC timeout must match the connection timeout.");
        if (value.Production.VariableNumber is < 10000 or > 10999
            || value.Production.LegacyVariableAlias is < 600 or > 699
            || value.Production.VariableNumber != value.Production.LegacyVariableAlias + 10000)
            throw new CncValidationException("configuration.production.variableNumber", "The Haas production variable mapping is invalid.");
        if (value.ProgramAccess.Enabled
            && value.ProgramAccess.Provider != "HAAS_LOCAL_NET_SHARE")
            throw new CncValidationException("configuration.programAccess.provider", "Only Haas Local Net Share program access is implemented.");
        if (value.ProgramAccess.Enabled && string.IsNullOrWhiteSpace(value.ProgramAccess.SharePath))
            throw new CncValidationException("configuration.programAccess.sharePath", "Share path is required when program access is enabled.");
        var sanitized = value with
        {
            ProgramAccess = value.ProgramAccess with
            {
                UsernameSecretId = Optional(update.UsernameSecretId),
                PasswordSecretId = Optional(update.PasswordSecretId)
            },
            Monitoring = value.Monitoring with
            {
                PollingIntervalMs = update.PollingIntervalMs,
                MaximumReconnectBackoffMs = update.MaximumReconnectBackoffMs,
                RawTelemetryRetentionDays = update.RawTelemetryRetentionDays
            }
        };
        return JsonSerializer.Serialize(sanitized, CncJson.Options);
    }

    private static string Required(string? value, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 2048)
            throw new CncValidationException(field, $"{field} is required.");
        return trimmed;
    }
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void Range(int value, int min, int max, string field)
    {
        if (value < min || value > max)
            throw new CncValidationException(field, $"{field} must be between {min} and {max}.");
    }
}
