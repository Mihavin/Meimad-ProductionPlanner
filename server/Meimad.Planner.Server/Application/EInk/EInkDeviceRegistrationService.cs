using System.Security.Cryptography;
using Meimad.Planner.Server.Application.EditMode;

namespace Meimad.Planner.Server.Application.EInk;

internal sealed class EInkDeviceRegistrationService
{
    private readonly IEInkDeviceRegistrationRepository repository;
    private readonly TimeProvider timeProvider;

    public EInkDeviceRegistrationService(
        IEInkDeviceRegistrationRepository repository,
        TimeProvider timeProvider)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
    }

    internal async Task<EInkDeviceRegistration> CreateAsync(
        CreateEInkDeviceRegistrationCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var name = RequiredName(command.DeviceName);
        var hardwareId = RequiredHardwareId(command.HardwareId);
        var now = timeProvider.GetUtcNow();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var registration = new EInkDeviceRegistration(
                Guid.NewGuid().ToString("N"),
                CreateTabletId(),
                hardwareId,
                name,
                NormalizeMachineId(command.MachineId),
                true,
                1,
                now,
                now);
            try
            {
                var created = await repository.CreateAsync(
                    registration,
                    editAuthority,
                    cancellationToken);
                return created;
            }
            catch (EInkDeviceBindingException exception) when (exception.Code == "tablet_id_conflict")
            {
                // A short human-readable ID is deliberately retried rather than widened.
            }
        }

        throw new EInkDeviceRegistrationValidationException(
            "Could not allocate a unique tablet ID. Please retry registration.");
    }

    internal async Task<EInkDeviceRegistration> UpdateAsync(
        string deviceId,
        UpdateEInkDeviceRegistrationCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var updated = await repository.UpdateAsync(
            deviceId,
            NormalizeMachineId(command.MachineId),
            command.IsEnabled,
            timeProvider.GetUtcNow(),
            editAuthority,
            cancellationToken)
            ?? throw new EInkDeviceRegistrationNotFoundException(deviceId);
        return updated;
    }

    internal Task<IReadOnlyList<EInkDeviceRegistration>> ListAsync(
        CancellationToken cancellationToken = default) => repository.ListAsync(cancellationToken);

    internal async Task<EInkDeviceRegistration> BootstrapAsync(
        string? hardwareId,
        decimal? batteryVoltage,
        int? batteryPercent,
        string? firmwareVersion,
        string? wifiIpAddress,
        int? wifiRssi,
        CancellationToken cancellationToken = default)
    {
        var registration = await repository.FindEnabledByHardwareAsync(
            RequiredHardwareId(hardwareId), cancellationToken)
            ?? throw new EInkDeviceRegistrationNotFoundException("tablet hardware mapping");
        await repository.RecordContactAsync(
            registration.DeviceId, timeProvider.GetUtcNow(), batteryVoltage, batteryPercent,
            firmwareVersion, wifiIpAddress, wifiRssi, cancellationToken);
        return registration;
    }

    private static string RequiredName(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 120)
        {
            throw new EInkDeviceRegistrationValidationException(
                "Device name is required and must not exceed 120 characters.");
        }

        return normalized;
    }

    private static string? NormalizeMachineId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string RequiredHardwareId(string? value)
    {
        var compact = value?.Trim().Replace("-", string.Empty).Replace(":", string.Empty);
        if (string.IsNullOrWhiteSpace(compact)
            || compact.Length != 12
            || !compact.All(Uri.IsHexDigit))
        {
            throw new EInkDeviceRegistrationValidationException(
                "Hardware ID must be a Wi-Fi MAC address, for example A4:CF:12:83:76:91.");
        }

        return string.Join(':', Enumerable.Range(0, 6)
            .Select(index => compact.Substring(index * 2, 2).ToUpperInvariant()));
    }

    private static string CreateTabletId() => RandomNumberGenerator.GetInt32(1000, 10000)
        .ToString(System.Globalization.CultureInfo.InvariantCulture);

}

internal sealed class EInkDeviceRegistrationValidationException : Exception
{
    internal EInkDeviceRegistrationValidationException(string message) : base(message) { }
}

internal sealed class EInkDeviceRegistrationNotFoundException : Exception
{
    internal EInkDeviceRegistrationNotFoundException(string deviceId)
        : base($"E-Ink device registration '{deviceId}' was not found.") { }
}

internal sealed class EInkDeviceBindingException : Exception
{
    internal EInkDeviceBindingException(string code, string message) : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
}
