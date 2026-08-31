namespace Meimad.Planner.Server.Application.EInk;

internal sealed record SubmitTabletEventCommand(
    string TabletId,
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
            timeProvider.GetUtcNow(),
            cancellationToken);
    }
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
