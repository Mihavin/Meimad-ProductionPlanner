namespace Meimad.Planner.Server.Application.EditMode;

internal enum EditClientState
{
    Viewer,
    Editor,
    RequestingEdit
}

internal enum EditRequestStatus
{
    Pending,
    Transferred,
    Rejected,
    AutoTransferred
}

internal enum EditDecision
{
    Release,
    Reject
}

internal sealed record EditModeHolder(
    string ClientId,
    string UserId,
    long Generation,
    DateTimeOffset AcquiredAt);

internal sealed record EditTransferRequest(
    string RequestId,
    string RequesterClientId,
    string RequesterUserId,
    long HolderGenerationAtRequest,
    EditRequestStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset DecisionDeadline,
    DateTimeOffset? DecidedAt,
    long? GrantedGeneration);

internal sealed record EditModeSnapshot(
    EditClientState CallerState,
    long Generation,
    EditModeHolder? Holder,
    EditTransferRequest? PendingRequest,
    DateTimeOffset ServerTime,
    int TransferTimeoutSeconds);

internal interface IEditModeRepository
{
    Task ProcessTimeoutAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<EditModeSnapshot> GetStatusAsync(
        string callerClientId,
        DateTimeOffset now,
        int transferTimeoutSeconds,
        CancellationToken cancellationToken);

    Task<EditModeSnapshot> RequestEditAsync(
        string requesterClientId,
        string requesterUserId,
        DateTimeOffset now,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<EditTransferRequest?> GetRequestAsync(
        string requestId,
        string callerClientId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<EditModeSnapshot> DecideAsync(
        string requestId,
        EditAuthority authority,
        EditDecision decision,
        DateTimeOffset now,
        int transferTimeoutSeconds,
        CancellationToken cancellationToken);

    Task<EditModeSnapshot> ReleaseAsync(
        EditAuthority authority,
        DateTimeOffset now,
        int transferTimeoutSeconds,
        CancellationToken cancellationToken);
}

internal sealed class EditModeCommandException : Exception
{
    internal EditModeCommandException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
}
