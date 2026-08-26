using System.Security.Cryptography;
using System.Text;

namespace Meimad.Planner.Server.Application.EInk;

internal sealed record SubmitTabletEventCommand(
    string TabletId,
    string BearerToken,
    string EventType,
    decimal? BatteryVoltage,
    int? BatteryPercent,
    string? FirmwareVersion,
    string? WifiIpAddress,
    int? WifiRssi);

internal sealed record TabletEventResult(
    string TabletId,
    string EventType,
    DateTimeOffset Timestamp,
    bool WasDuplicate);

internal interface ITabletEventRepository
{
    Task<TabletEventResult> SubmitSendToQcAsync(
        SubmitTabletEventCommand command,
        string credentialHash,
        DateTimeOffset serverReceivedAt,
        CancellationToken cancellationToken);
}

internal sealed class TabletEventService(
    ITabletEventRepository repository,
    TimeProvider timeProvider)
{
    internal Task<TabletEventResult> SubmitAsync(
        SubmitTabletEventCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(command.EventType, "SEND_TO_QC", StringComparison.Ordinal))
        {
            throw new TabletEventValidationException(
                "unsupported_tablet_event",
                "SEND_TO_QC is the only supported tablet event.");
        }

        return repository.SubmitSendToQcAsync(
            command,
            HashToken(command.BearerToken),
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    internal static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string HashToken(string token) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty))).ToLowerInvariant();
}

internal sealed class TabletEventValidationException(string code, string message)
    : Exception(message)
{
    internal string Code { get; } = code;
}

internal sealed class TabletEventResourceNotFoundException : Exception;

internal sealed class TabletEventStateException(string code, string message)
    : Exception(message)
{
    internal string Code { get; } = code;
}
