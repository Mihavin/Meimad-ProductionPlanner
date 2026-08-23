namespace Meimad.Planner.Server.Infrastructure.MtConnect;

internal sealed record MtConnectHeader(
    DateTimeOffset? CreationTime,
    string? Sender,
    string? InstanceId,
    string? Version,
    long? BufferSize,
    long? FirstSequence,
    long? LastSequence,
    long? NextSequence);

internal sealed record MtConnectDeviceIdentity(
    string? Id,
    string? Name,
    string? Uuid);

internal sealed record MtConnectDataItemDefinition(
    string Id,
    string? DeviceId,
    string? Name,
    string? Type,
    string? SubType,
    string? Category,
    string? Units,
    string? Source);

internal sealed record MtConnectProbeDocument(
    MtConnectHeader Header,
    IReadOnlyList<MtConnectDeviceIdentity> Devices,
    IReadOnlyList<MtConnectDataItemDefinition> DataItems,
    string RawXml);

internal sealed record MtConnectObservation(
    string ElementName,
    string DataItemId,
    string? Name,
    string Value,
    DateTimeOffset? Timestamp,
    long? Sequence,
    IReadOnlyDictionary<string, string> Attributes,
    MtConnectDataItemDefinition? Definition);

internal sealed record MtConnectCounterObservation(
    MtConnectObservation Observation,
    long? NumericValue);

internal sealed record MtConnectMacroObservation(
    int VariableNumber,
    decimal? NumericValue,
    string RawValue,
    MtConnectObservation RangeObservation);

internal sealed record MtConnectDeviceState(
    MtConnectDeviceIdentity Identity,
    MtConnectObservation? Availability,
    MtConnectObservation? Execution,
    MtConnectObservation? ControllerMode,
    MtConnectObservation? Program,
    IReadOnlyList<MtConnectObservation> Observations,
    IReadOnlyList<MtConnectCounterObservation> Counters,
    IReadOnlyList<MtConnectMacroObservation> MacroVariables);

internal sealed record MtConnectCurrentDocument(
    MtConnectHeader Header,
    IReadOnlyList<MtConnectDeviceState> Devices,
    string RawXml);

internal sealed class MtConnectProtocolException(string message, Exception? innerException = null)
    : Exception(message, innerException);
