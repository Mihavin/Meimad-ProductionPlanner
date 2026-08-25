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
    DateTimeOffset UpdatedAt);

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
