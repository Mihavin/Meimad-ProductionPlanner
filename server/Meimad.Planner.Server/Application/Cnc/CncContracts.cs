using System.Threading.Channels;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Cnc;

namespace Meimad.Planner.Server.Application.Cnc;

internal interface ICncMachineAdapter : IAsyncDisposable
{
    string ConnectionId { get; }
    string MachineId { get; }
    CncAdapterType AdapterType { get; }
    CncAdapterCapabilities GetCapabilities();
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<CncConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<CncAdapterSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default);
    Task<CncOperationResult<CncProgramSnapshot>> ReadActiveProgramInfoAsync(CancellationToken cancellationToken = default);
    Task<CncOperationResult<int>> ReadVariableAsync(int variable, CancellationToken cancellationToken = default);
    Task<CncOperationResult<string>> WriteVariableAsync(int variable, int value, CancellationToken cancellationToken = default);
    Task<CncOperationResult<int>> ReadPartCounterAsync(CancellationToken cancellationToken = default);
}

internal interface ICncAdapterFactory
{
    ICncMachineAdapter CreateAdapter(MachineConnection connection);
}

internal interface INcProgramFileProvider
{
    Task<Domain.Haas.MachineNcHeader> ReadActiveProgramHeaderAsync(
        Domain.Haas.HaasConnectionSettings settings,
        string programNumber,
        CancellationToken cancellationToken = default);
}

internal interface ICncConnectionRepository
{
    Task<IReadOnlyList<MachineConnection>> ListConnectionsAsync(bool enabledOnly, CancellationToken cancellationToken);
    Task<MachineConnection?> GetConnectionAsync(string machineId, CancellationToken cancellationToken);
    Task<MachineConnection> UpsertConnectionAsync(
        MachineConnection connection, int expectedVersion, EditAuthority authority, CancellationToken cancellationToken);
    Task UpdateConnectionStateAsync(
        string connectionId, string state, DateTimeOffset at, bool successfulPoll,
        string? error, CancellationToken cancellationToken);
    Task<MachineSnapshot?> GetCurrentSnapshotAsync(string machineId, CancellationToken cancellationToken);
    Task<bool> SaveSnapshotAsync(
        MachineConnection connection, MachineSnapshot snapshot,
        IReadOnlyList<RawCncTelemetry> rawTelemetry, CancellationToken cancellationToken);
    Task<IReadOnlyList<RawCncTelemetry>> ReadDiagnosticsAsync(
        string machineId, int limit, CancellationToken cancellationToken);
}

internal interface ICncSnapshotConsumer
{
    Task<CncSnapshotConsumptionResult> ConsumeAsync(MachineSnapshot snapshot, CancellationToken cancellationToken);
}

internal interface ICncRawTelemetryConsumer
{
    Task ConsumeAsync(
        string machineId, IReadOnlyList<RawCncTelemetry> telemetry,
        CancellationToken cancellationToken);
}

internal sealed record CncSnapshotConsumptionResult(IReadOnlyList<string> DomainEvents)
{
    internal static readonly CncSnapshotConsumptionResult None = new([]);
}

internal interface ICncConnectionManager
{
    Task RequestReconnectAsync(string machineId, CancellationToken cancellationToken = default);
    Task<CncConnectionTestResult> TestConnectionAsync(string machineId, CancellationToken cancellationToken = default);
    Task<MachineSnapshot?> GetCurrentSnapshotAsync(string machineId, CancellationToken cancellationToken = default);
}

internal sealed record CncLiveMessage(
    string Type,
    string MachineId,
    DateTimeOffset Timestamp,
    object Payload);

internal interface ICncLivePublisher
{
    ValueTask PublishAsync(CncLiveMessage message, CancellationToken cancellationToken = default);
    CncLiveSubscription Subscribe(IReadOnlySet<string> machineIds);
}

internal sealed class CncLiveSubscription(
    ChannelReader<CncLiveMessage> reader,
    Func<ValueTask> dispose) : IAsyncDisposable
{
    internal ChannelReader<CncLiveMessage> Reader { get; } = reader;
    public ValueTask DisposeAsync() => dispose();
}

internal sealed class CncValidationException(string field, string message) : Exception(message)
{
    internal string Field { get; } = field;
}

internal sealed class CncConnectionNotFoundException(string machineId)
    : Exception($"A CNC connection is not configured for Machine '{machineId}'.");

internal sealed class CncConnectionConcurrencyException()
    : Exception("The CNC connection was changed by another editor.");

internal sealed class CncAdapterUnsupportedException(string adapterType)
    : Exception($"CNC adapter '{adapterType}' is registered for future use but is not implemented.");
