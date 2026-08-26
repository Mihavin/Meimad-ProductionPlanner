namespace Meimad.Planner.Server.Application.EInk;

internal interface ITabletStatusRepository
{
    Task<TabletStatusSource?> ReadAsync(string tabletId, CancellationToken cancellationToken);
}

internal sealed record TabletStatusSource(
    string DeviceId,
    string TabletId,
    string? CredentialHash,
    bool IsEnabled,
    TabletStatusMachineSource? Machine,
    TabletStatusRunSource? Run,
    IReadOnlyList<TabletStatusOutputSource> Outputs,
    TabletStatusWorkflowSource? Workflow,
    TabletVerificationSessionSource? VerificationSession);

internal sealed record TabletStatusMachineSource(
    string MachineId,
    string Number,
    string Name,
    bool IsActive);

internal sealed record TabletStatusRunSource(
    string RunId,
    string Status,
    int Version,
    string ProgramId,
    string ProgramStatus,
    int CompletedCycleCount);

internal sealed record TabletStatusOutputSource(
    string PartNumber,
    string PartName,
    int OperationNumber,
    string OperationName);

internal sealed record TabletStatusWorkflowSource(
    string EventId,
    string EventType,
    DateTimeOffset OccurredAt);

internal sealed record TabletVerificationSessionSource(
    string SessionId,
    string State,
    int Nonce,
    int OffsetLoaderReleaseToken,
    int NcIdentityToken,
    int MacroVersion,
    int ResponseCodeDigits,
    DateTimeOffset ExpiresAt,
    string ProtectedSecret,
    bool ContextIsValid);
