using Meimad.Planner.Server.Domain.ToolPreparations;

namespace Meimad.Planner.Server.Application.ToolPreparations;

internal interface IToolPreparationRepository
{
    /// <summary>
    /// The Operation's assigned Machine, its current released Tool Table with the active tool rows,
    /// and the latest saved preparation for that Machine; null when the Operation has no current
    /// Machine assignment.
    /// </summary>
    Task<ToolPreparationView?> ReadViewAsync(string batchOperationId, CancellationToken cancellationToken);

    /// <summary>Appends the next version; the caller has already checked the expected version.</summary>
    Task<ToolPreparation> SaveAsync(
        ToolPreparation preparation,
        int expectedVersion,
        CancellationToken cancellationToken);
}

internal sealed class ToolPreparationNotFoundException(string message) : Exception(message);

internal sealed class ToolPreparationConflictException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

/// <summary>
/// Tool Room measurements of a Batch Operation on its assigned Machine. Reading needs no Edit
/// Mode; saving records the client identity like Production Package creation and appends an
/// immutable version, so a package can prove which measurements it used and becomes stale when
/// newer ones are saved.
/// </summary>
internal sealed class ToolPreparationService(IToolPreparationRepository repository, TimeProvider timeProvider)
{
    internal async Task<ToolPreparationView> ReadAsync(string batchOperationId, CancellationToken cancellationToken = default)
    {
        var operationId = Required(batchOperationId);
        return await repository.ReadViewAsync(operationId, cancellationToken)
            ?? throw new ToolPreparationNotFoundException("The Batch Operation has no current Machine assignment.");
    }

    internal async Task<ToolPreparationView> SaveAsync(
        string batchOperationId,
        ToolPreparationUpdate update,
        string savedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var view = await ReadAsync(batchOperationId, cancellationToken);
        var actor = savedBy?.Trim();
        if (string.IsNullOrEmpty(actor))
            throw new ToolPreparationValidationException("tool_preparation_user_required", "The saving user identity is required.");
        if (!string.Equals(update.ToolTableReleaseId?.Trim(), view.ToolTableReleaseId, StringComparison.Ordinal))
            throw new ToolPreparationConflictException(
                "tool_preparation_tool_table_changed",
                "The Operation's current Tool Table release changed; reload the tool table and enter the measurements again.");
        var currentVersion = view.Current?.VersionNumber ?? 0;
        if (update.ExpectedVersion != currentVersion)
            throw new ToolPreparationConflictException(
                "tool_preparation_version_conflict",
                $"The tool preparation was saved by someone else (version {currentVersion}); reload it before saving.");

        var tools = ToolPreparationValidator.Validate(update, view.ReleasedTools);
        var preparation = new ToolPreparation(
            Guid.NewGuid().ToString("N"),
            view.BatchOperationId,
            view.MachineId,
            view.ToolTableReleaseId,
            currentVersion + 1,
            timeProvider.GetUtcNow(),
            actor,
            string.IsNullOrWhiteSpace(update.Comment) ? null : update.Comment.Trim(),
            ToolPreparationValidator.ContentHash(tools),
            tools);
        var saved = await repository.SaveAsync(preparation, currentVersion, cancellationToken);
        return view with { Current = saved };
    }

    private static string Required(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ToolPreparationValidationException("tool_preparation_operation_required", "batchOperationId is required.")
            : value.Trim();
}
