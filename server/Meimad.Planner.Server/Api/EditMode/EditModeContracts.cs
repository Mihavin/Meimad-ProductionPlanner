using Meimad.Planner.Server.Application.EditMode;

namespace Meimad.Planner.Server.Api.EditMode;

internal sealed record EditModeHolderResponse(
    string ClientId,
    string UserId,
    long Generation,
    DateTimeOffset AcquiredAt)
{
    internal static EditModeHolderResponse FromDomain(EditModeHolder holder) => new(
        holder.ClientId,
        holder.UserId,
        holder.Generation,
        holder.AcquiredAt);
}

internal sealed record EditTransferRequestResponse(
    string RequestId,
    string RequesterClientId,
    string RequesterUserId,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset DecisionDeadline,
    DateTimeOffset? DecidedAt,
    long? GrantedGeneration)
{
    internal static EditTransferRequestResponse FromDomain(EditTransferRequest request) => new(
        request.RequestId,
        request.RequesterClientId,
        request.RequesterUserId,
        ToContract(request.Status),
        request.RequestedAt,
        request.DecisionDeadline,
        request.DecidedAt,
        request.GrantedGeneration);

    private static string ToContract(EditRequestStatus status) => status switch
    {
        EditRequestStatus.Pending => "pending",
        EditRequestStatus.Transferred => "transferred",
        EditRequestStatus.Rejected => "rejected",
        EditRequestStatus.AutoTransferred => "autoTransferred",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}

internal sealed record EditModeResponse(
    string State,
    long Generation,
    EditModeHolderResponse? Holder,
    EditTransferRequestResponse? PendingRequest,
    DateTimeOffset ServerTime,
    int TransferTimeoutSeconds)
{
    internal static EditModeResponse FromDomain(EditModeSnapshot snapshot) => new(
        snapshot.CallerState switch
        {
            EditClientState.Viewer => "viewer",
            EditClientState.Editor => "editor",
            EditClientState.RequestingEdit => "requestingEdit",
            _ => throw new ArgumentOutOfRangeException(nameof(snapshot), snapshot.CallerState, null)
        },
        snapshot.Generation,
        snapshot.Holder is null ? null : EditModeHolderResponse.FromDomain(snapshot.Holder),
        snapshot.PendingRequest is null
            ? null
            : EditTransferRequestResponse.FromDomain(snapshot.PendingRequest),
        snapshot.ServerTime,
        snapshot.TransferTimeoutSeconds);
}

internal sealed record EditDecisionRequest(string? Decision)
{
    internal EditDecision ToDomain()
    {
        return Decision?.Trim().ToLowerInvariant() switch
        {
            "release" => EditDecision.Release,
            "reject" => EditDecision.Reject,
            _ => throw new EditModeCommandException(
                "invalid_edit_mode_request",
                "Decision must be 'release' or 'reject'.")
        };
    }
}
