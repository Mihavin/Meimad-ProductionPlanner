using Meimad.Planner.Server.Configuration;

namespace Meimad.Planner.Server.Application.EditMode;

internal sealed class EditModeService
{
    private const int MaximumIdentifierLength = 200;
    private readonly IEditModeRepository repository;
    private readonly EditModeOptions options;
    private readonly TimeProvider timeProvider;

    public EditModeService(
        IEditModeRepository repository,
        EditModeOptions options,
        TimeProvider timeProvider)
    {
        this.repository = repository;
        this.options = options;
        this.timeProvider = timeProvider;
    }

    internal Task<EditModeSnapshot> GetStatusAsync(
        string callerClientId,
        CancellationToken cancellationToken = default) =>
        repository.GetStatusAsync(
            NormalizeIdentifier(callerClientId, "client ID"),
            timeProvider.GetUtcNow(),
            options.TransferTimeoutSeconds,
            cancellationToken);

    internal Task<EditModeSnapshot> RequestEditAsync(
        string requesterClientId,
        string requesterUserId,
        CancellationToken cancellationToken = default) =>
        repository.RequestEditAsync(
            NormalizeIdentifier(requesterClientId, "client ID"),
            NormalizeIdentifier(requesterUserId, "user ID"),
            timeProvider.GetUtcNow(),
            options.TransferTimeout,
            cancellationToken);

    internal Task<EditTransferRequest?> GetRequestAsync(
        string requestId,
        string callerClientId,
        CancellationToken cancellationToken = default) =>
        repository.GetRequestAsync(
            NormalizeIdentifier(requestId, "request ID"),
            NormalizeIdentifier(callerClientId, "client ID"),
            timeProvider.GetUtcNow(),
            cancellationToken);

    internal Task<EditModeSnapshot> DecideAsync(
        string requestId,
        EditAuthority authority,
        EditDecision decision,
        CancellationToken cancellationToken = default) =>
        repository.DecideAsync(
            NormalizeIdentifier(requestId, "request ID"),
            authority with { ClientId = NormalizeIdentifier(authority.ClientId, "client ID") },
            decision,
            timeProvider.GetUtcNow(),
            options.TransferTimeoutSeconds,
            cancellationToken);

    internal Task<EditModeSnapshot> ReleaseAsync(
        EditAuthority authority,
        CancellationToken cancellationToken = default) =>
        repository.ReleaseAsync(
            authority with { ClientId = NormalizeIdentifier(authority.ClientId, "client ID") },
            timeProvider.GetUtcNow(),
            options.TransferTimeoutSeconds,
            cancellationToken);

    private static string NormalizeIdentifier(string? value, string field)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > MaximumIdentifierLength)
        {
            throw new EditModeCommandException(
                "invalid_edit_mode_request",
                $"The {field} must contain between 1 and {MaximumIdentifierLength} characters.");
        }

        return normalized;
    }
}
