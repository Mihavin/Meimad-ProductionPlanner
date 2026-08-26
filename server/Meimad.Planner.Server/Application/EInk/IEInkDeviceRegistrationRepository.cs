using Meimad.Planner.Server.Application.EditMode;

namespace Meimad.Planner.Server.Application.EInk;

internal interface IEInkDeviceRegistrationRepository
{
    Task<EInkDeviceRegistration> CreateAsync(
        EInkDeviceRegistration registration,
        string credentialHash,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<EInkDeviceRegistration?> UpdateAsync(
        string deviceId,
        string? machineId,
        bool isEnabled,
        string? credentialHash,
        DateTimeOffset updatedAt,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EInkDeviceRegistration>> ListAsync(CancellationToken cancellationToken);

    Task<EInkDeviceRegistration?> FindEnabledByCredentialAndHardwareAsync(
        string credentialHash,
        string hardwareId,
        CancellationToken cancellationToken);

    Task RecordContactAsync(
        string deviceId,
        DateTimeOffset contactedAt,
        decimal? batteryVoltage,
        int? batteryPercent,
        string? firmwareVersion,
        string? wifiIpAddress,
        int? wifiRssi,
        CancellationToken cancellationToken);
}

internal sealed record EInkDeviceRegistration(
    string DeviceId,
    string TabletId,
    string? HardwareId,
    string DeviceName,
    string? MachineId,
    bool IsEnabled,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastSeenAt = null,
    DateTimeOffset? LastServerContactAt = null,
    string? FirmwareVersion = null,
    decimal? BatteryVoltage = null,
    int? BatteryPercent = null,
    string? WifiIpAddress = null,
    int? WifiRssi = null,
    string? MachineNumber = null,
    string? MachineName = null,
    string? CurrentProductionRunId = null,
    string? CurrentWorkflowStatus = null,
    string? CurrentPackageRevision = null);

internal sealed record CreateEInkDeviceRegistrationCommand(
    string DeviceName,
    string? MachineId,
    string HardwareId);

internal sealed record UpdateEInkDeviceRegistrationCommand(
    string? MachineId,
    bool IsEnabled,
    bool RotateCredential);

internal sealed record EInkDeviceRegistrationResult(
    EInkDeviceRegistration Registration,
    string? RegistrationToken);
