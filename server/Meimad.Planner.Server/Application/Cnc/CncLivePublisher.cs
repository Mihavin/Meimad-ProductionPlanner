using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Meimad.Planner.Server.Application.Cnc;

internal sealed class CncLivePublisher : ICncLivePublisher
{
    private readonly ConcurrentDictionary<Guid, Subscriber> subscribers = new();

    public ValueTask PublishAsync(CncLiveMessage message, CancellationToken cancellationToken = default)
    {
        foreach (var subscriber in subscribers.Values)
        {
            if (subscriber.MachineIds.Contains(message.MachineId))
                subscriber.Channel.Writer.TryWrite(message);
        }
        return ValueTask.CompletedTask;
    }

    public CncLiveSubscription Subscribe(IReadOnlySet<string> machineIds)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<CncLiveMessage>(new BoundedChannelOptions(100)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        subscribers[id] = new(machineIds, channel);
        return new(channel.Reader, () =>
        {
            if (subscribers.TryRemove(id, out var removed)) removed.Channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        });
    }

    private sealed record Subscriber(
        IReadOnlySet<string> MachineIds,
        Channel<CncLiveMessage> Channel);
}
