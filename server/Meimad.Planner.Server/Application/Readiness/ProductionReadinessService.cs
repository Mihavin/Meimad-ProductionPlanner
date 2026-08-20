using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Readiness;

namespace Meimad.Planner.Server.Application.Readiness;

internal interface IProductionReadinessRepository
{
    Task<ProductionReadinessResult> ReadAsync(
        string batchOperationId,
        CancellationToken cancellationToken);

    Task<ProductionReadinessResult> UpdateInputsAsync(
        string batchOperationId,
        ProductionReadinessInputUpdate update,
        DateTimeOffset now,
        EditAuthority authority,
        CancellationToken cancellationToken);
}

internal sealed record ProductionReadinessInputUpdate(
    string? SelectedGCodeReleaseId,
    string MaterialStatus,
    string? MaterialComment,
    string ToolOffsetStatus,
    string? ToolOffsetComment);

internal sealed class ProductionReadinessService(
    IProductionReadinessRepository repository,
    TimeProvider timeProvider)
{
    internal Task<ProductionReadinessResult> ReadAsync(
        string batchOperationId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(batchOperationId);
        return repository.ReadAsync(batchOperationId.Trim(), cancellationToken);
    }

    internal Task<ProductionReadinessResult> UpdateInputsAsync(
        string batchOperationId,
        ProductionReadinessInputUpdate update,
        EditAuthority authority,
        CancellationToken cancellationToken = default)
    {
        ValidateId(batchOperationId);
        var materialStatus = ValidateStatus(update.MaterialStatus, "materialStatus");
        var offsetStatus = ValidateStatus(update.ToolOffsetStatus, "toolOffsetStatus");
        var materialComment = CleanComment(update.MaterialComment, "materialComment");
        var offsetComment = CleanComment(update.ToolOffsetComment, "toolOffsetComment");
        var selectedReleaseId = string.IsNullOrWhiteSpace(update.SelectedGCodeReleaseId)
            ? null
            : update.SelectedGCodeReleaseId.Trim();
        return repository.UpdateInputsAsync(
            batchOperationId.Trim(),
            update with
            {
                SelectedGCodeReleaseId = selectedReleaseId,
                MaterialStatus = materialStatus,
                MaterialComment = materialComment,
                ToolOffsetStatus = offsetStatus,
                ToolOffsetComment = offsetComment
            },
            timeProvider.GetUtcNow(),
            authority,
            cancellationToken);
    }

    private static void ValidateId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ProductionReadinessValidationException(
                "batchOperationId", "required", "batchOperationId is required.");
        }
    }

    private static string ValidateStatus(string? value, string field)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (normalized is not (ReadinessStates.Ready or ReadinessStates.Missing or ReadinessStates.Unverified))
        {
            throw new ProductionReadinessValidationException(
                field, "invalid_status", $"{field} must be READY, MISSING, or UNVERIFIED.");
        }
        return normalized;
    }

    private static string? CleanComment(string? value, string field)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (clean?.Length > 2_000)
        {
            throw new ProductionReadinessValidationException(
                field, "too_long", $"{field} must be 2,000 characters or fewer.");
        }
        return clean;
    }
}

internal sealed class ProductionReadinessValidationException(
    string field,
    string code,
    string message) : Exception(message)
{
    internal string Field { get; } = field;
    internal string Code { get; } = code;
}
