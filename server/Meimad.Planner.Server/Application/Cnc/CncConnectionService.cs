using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Domain.Haas;

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
            throw new CncValidationException("allowWrite", adapter == CncAdapterType.FanucFocas
                ? "The FANUC FOCAS adapter is read-only; write permission cannot be granted."
                : "Write permission is unavailable for unimplemented adapters.");

        var configurationJson = adapter switch
        {
            CncAdapterType.HaasNgc => ValidateHaas(update.Configuration, update),
            CncAdapterType.FanucFocas => ValidateFocas(update.Configuration, update),
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
        if (!IPAddress.TryParse(value.Host, out var address)
            || IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
            throw new CncValidationException("configuration.host", "A fixed, non-loopback CNC IP address is required.");
        if (string.IsNullOrWhiteSpace(value.MacAddress)
            || !Regex.IsMatch(value.MacAddress.Replace('-', ':').ToUpperInvariant(),
                "^[0-9A-F]{2}(:[0-9A-F]{2}){5}$", RegexOptions.CultureInvariant))
            throw new CncValidationException("configuration.macAddress",
                "A six-octet CNC MAC address is required.");
        Range(value.Mdc.Port, 1, 65535, "configuration.mdc.port");
        if (value.Mdc.TimeoutMs != update.ConnectionTimeoutMs)
            throw new CncValidationException("configuration.mdc.timeoutMs", "MDC timeout must match the connection timeout.");
        if (value.MtConnect is { } mtConnect)
        {
            Range(mtConnect.Port, 1, 65535, "configuration.mtConnect.port");
            if (mtConnect.TimeoutMs != update.ConnectionTimeoutMs)
                throw new CncValidationException("configuration.mtConnect.timeoutMs", "MTConnect timeout must match the connection timeout.");
        }
        var telemetryProvider = string.IsNullOrWhiteSpace(value.TelemetryProvider)
            ? HaasTelemetryProviders.Mdc : value.TelemetryProvider.Trim().ToUpperInvariant();
        if (!HaasTelemetryProviders.IsSupported(telemetryProvider))
            throw new CncValidationException("configuration.telemetryProvider", "Telemetry provider must be MDC, MTCONNECT, or DPRNT.");
        if (telemetryProvider == HaasTelemetryProviders.MtConnect && value.MtConnect is null)
            throw new CncValidationException("configuration.mtConnect", "MTConnect configuration is required when MTCONNECT is the telemetry provider.");
        var dprnt = value.Dprnt is null ? null : ValidateDprnt(value.Dprnt, allowNone: false);
        if (update.AllowWrite)
            throw new CncValidationException("allowWrite",
                "Direct CNC variable writes are disabled; protected setup verification executes on the controller.");
        if (value.ProgramAccess.Enabled
            && value.ProgramAccess.Provider != "HAAS_LOCAL_NET_SHARE")
            throw new CncValidationException("configuration.programAccess.provider", "Only Haas Local Net Share program access is implemented.");
        if (value.ProgramAccess.Enabled && string.IsNullOrWhiteSpace(value.ProgramAccess.SharePath))
            throw new CncValidationException("configuration.programAccess.sharePath", "Share path is required when program access is enabled.");
        var sanitized = value with
        {
            TelemetryProvider = telemetryProvider,
            Dprnt = dprnt,
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

    private static string ValidateFocas(JsonElement json, CncConnectionUpdate update)
    {
        FanucFocasConnectionConfiguration value;
        try
        {
            value = json.Deserialize<FanucFocasConnectionConfiguration>(CncJson.Options)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new CncValidationException("configuration", "FANUC FOCAS configuration is invalid.");
        }
        if (string.IsNullOrWhiteSpace(value.Host)
            || !IPAddress.TryParse(value.Host, out var address)
            || IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
            throw new CncValidationException("configuration.host", "A fixed, non-loopback CNC IP address is required.");
        if (string.IsNullOrWhiteSpace(value.MacAddress)
            || !Regex.IsMatch(value.MacAddress.Replace('-', ':').ToUpperInvariant(),
                "^[0-9A-F]{2}(:[0-9A-F]{2}){5}$", RegexOptions.CultureInvariant))
            throw new CncValidationException("configuration.macAddress",
                "A six-octet CNC MAC address is required.");
        Range(value.Port, 1, 65535, "configuration.port");
        var partCounterSource = string.IsNullOrWhiteSpace(value.PartCounterSource)
            ? FocasPartCounterSources.PartsCount6711 : value.PartCounterSource.Trim().ToUpperInvariant();
        if (!FocasPartCounterSources.IsSupported(partCounterSource))
            throw new CncValidationException("configuration.partCounterSource",
                "Part counter source must be PARTS_COUNT_6711 or PARTS_TOTAL_6712.");
        var programAccess = value.ProgramAccess ?? new FocasProgramAccessConfiguration();
        var provider = string.IsNullOrWhiteSpace(programAccess.Provider)
            ? "NONE" : programAccess.Provider.Trim().ToUpperInvariant();
        if (programAccess.Enabled && provider != FocasProgramAccessConfiguration.UploadProvider)
            throw new CncValidationException("configuration.programAccess.provider",
                "Only FOCAS program upload is implemented for FANUC header access.");
        var folder = Optional(programAccess.ProgramFolder) ?? FocasProgramAccessConfiguration.DefaultProgramFolder;
        if (folder.Length > 256)
            throw new CncValidationException("configuration.programAccess.programFolder", "Program folder must be 256 characters or fewer.");
        Range(programAccess.HeaderLineLimit, 1, 500, "configuration.programAccess.headerLineLimit");
        Range(programAccess.HeaderByteLimit, 256, 1024 * 1024, "configuration.programAccess.headerByteLimit");
        if (update.AllowWrite)
            throw new CncValidationException("allowWrite", "The FANUC FOCAS adapter is read-only; write permission cannot be granted.");
        var monitoring = value.Monitoring ?? new HaasMonitoringConfiguration(
            update.PollingIntervalMs, 2, update.MaximumReconnectBackoffMs, update.RawTelemetryRetentionDays);
        Range(monitoring.StableProgramPolls, 1, 20, "configuration.monitoring.stableProgramPolls");
        var sanitized = value with
        {
            Host = value.Host.Trim(),
            MacAddress = value.MacAddress.Trim(),
            TimeoutMs = update.ConnectionTimeoutMs,
            PartCounterSource = partCounterSource,
            Dprnt = ValidateDprnt(value.Dprnt ?? new CncDprntConfiguration(), allowNone: true),
            ProgramAccess = programAccess with
            {
                Provider = programAccess.Enabled ? FocasProgramAccessConfiguration.UploadProvider : provider,
                ProgramFolder = folder,
                HeaderPartPatterns = programAccess.HeaderPartPatterns ?? Haas.NcHeaderParser.DefaultPartPatterns
            },
            Monitoring = monitoring with
            {
                PollingIntervalMs = update.PollingIntervalMs,
                MaximumReconnectBackoffMs = update.MaximumReconnectBackoffMs,
                RawTelemetryRetentionDays = update.RawTelemetryRetentionDays
            }
        };
        return JsonSerializer.Serialize(sanitized, CncJson.Options);
    }

    /// <summary>Shared DPRNT source rules: FILE needs a path, and every value is normalized to its upper-case constant.</summary>
    private static CncDprntConfiguration ValidateDprnt(CncDprntConfiguration value, bool allowNone)
    {
        var source = string.IsNullOrWhiteSpace(value.Source)
            ? CncDprntSources.Tcp : value.Source.Trim().ToUpperInvariant();
        if (!CncDprntSources.IsSupported(source) || (source == CncDprntSources.None && !allowNone))
            throw new CncValidationException("configuration.dprnt.source",
                allowNone ? "DPRNT source must be TCP, FILE, or NONE." : "DPRNT source must be TCP or FILE.");
        var filePath = Optional(value.FilePath);
        if (filePath is { Length: > 1024 })
            throw new CncValidationException("configuration.dprnt.filePath", "DPRNT file path must be 1024 characters or fewer.");
        if (source == CncDprntSources.File && filePath is null)
            throw new CncValidationException("configuration.dprnt.filePath", "A DPRNT file path is required when the DPRNT source is FILE.");
        var clearPolicy = string.IsNullOrWhiteSpace(value.ClearPolicy)
            ? CncDprntClearPolicies.Never : value.ClearPolicy.Trim().ToUpperInvariant();
        if (!CncDprntClearPolicies.IsSupported(clearPolicy))
            throw new CncValidationException("configuration.dprnt.clearPolicy",
                "DPRNT file clear policy must be NEVER, ON_OFFSET_LOADER, or AFTER_READ.");
        if (value.Port is { } port) Range(port, 1, 65535, "configuration.dprnt.port");
        var tcpHost = Optional(value.Host);
        if (tcpHost is not null && (tcpHost.Length > 253 || Uri.CheckHostName(tcpHost) == UriHostNameType.Unknown))
            throw new CncValidationException("configuration.dprnt.host", "DPRNT TCP host must be an IP address or host name.");
        return new(source, filePath, clearPolicy, value.Port, tcpHost);
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
