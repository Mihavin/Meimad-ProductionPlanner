using System.Security.Cryptography;
using System.Text;
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

    internal async Task<EInkDeviceRegistrationResult> CreateAsync(
        CreateEInkDeviceRegistrationCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var name = RequiredName(command.DeviceName);
        var token = CreateToken();
        var now = timeProvider.GetUtcNow();
        var registration = new EInkDeviceRegistration(
            Guid.NewGuid().ToString("N"),
            name,
            NormalizeMachineId(command.MachineId),
            true,
            1,
            now,
            now);
        var created = await repository.CreateAsync(
            registration,
            HashToken(token),
            editAuthority,
            cancellationToken);
        return new EInkDeviceRegistrationResult(created, token);
    }

    internal async Task<EInkDeviceRegistrationResult> UpdateAsync(
        string deviceId,
        UpdateEInkDeviceRegistrationCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var token = command.RotateCredential ? CreateToken() : null;
        var updated = await repository.UpdateAsync(
            deviceId,
            NormalizeMachineId(command.MachineId),
            command.IsEnabled,
            token is null ? null : HashToken(token),
            timeProvider.GetUtcNow(),
            editAuthority,
            cancellationToken)
            ?? throw new EInkDeviceRegistrationNotFoundException(deviceId);
        return new EInkDeviceRegistrationResult(updated, token);
    }

    internal Task<IReadOnlyList<EInkDeviceRegistration>> ListAsync(
        CancellationToken cancellationToken = default) => repository.ListAsync(cancellationToken);

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

    private static string CreateToken() =>
        "mp_eink_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string HashToken(string token) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
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
