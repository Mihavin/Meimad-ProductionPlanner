using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Machines;

namespace Meimad.Planner.Server.Application.MachineAssignments;

internal sealed class MachineAssignmentService
{
    private readonly IMachineAssignmentRepository repository;
    private readonly TimeProvider timeProvider;

    public MachineAssignmentService(
        IMachineAssignmentRepository repository,
        TimeProvider timeProvider)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
    }

    internal Task<AssignmentMutationResult> AssignOrMoveAsync(
        string batchOperationId,
        string machineId,
        int backlogPosition,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default) =>
        AssignOrMoveAsync(
            batchOperationId,
            machineId,
            backlogPosition,
            overrideConfirmation: null,
            editAuthority,
            cancellationToken);

    internal Task<AssignmentMutationResult> AssignOrMoveAsync(
        string batchOperationId,
        string machineId,
        int backlogPosition,
        MachineAssignmentOverrideConfirmation? overrideConfirmation,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(batchOperationId))
        {
            throw new MachineAssignmentValidationException(
                "batchOperationId",
                "required",
                "batchOperationId is required.");
        }

        if (string.IsNullOrWhiteSpace(machineId))
        {
            throw new MachineAssignmentValidationException(
                "machineId",
                "required",
                "machineId is required.");
        }

        if (backlogPosition < 0)
        {
            throw new MachineAssignmentValidationException(
                "backlogPosition",
                "non_negative_required",
                "backlogPosition must be zero or greater.");
        }

        if (overrideConfirmation is not null)
        {
            if (!overrideConfirmation.Confirmed)
            {
                throw new MachineAssignmentValidationException(
                    "compatibilityOverride.confirmed",
                    "confirmation_required",
                    "The incompatible Machine assignment must be explicitly confirmed.");
            }

            if (string.IsNullOrWhiteSpace(overrideConfirmation.Reason))
            {
                throw new MachineAssignmentValidationException(
                    "compatibilityOverride.reason",
                    "reason_required",
                    "A reason is required for an incompatible Machine assignment.");
            }

            if (overrideConfirmation.Reason.Trim().Length > 1_000)
            {
                throw new MachineAssignmentValidationException(
                    "compatibilityOverride.reason",
                    "too_long",
                    "The override reason must be 1,000 characters or fewer.");
            }

            overrideConfirmation = overrideConfirmation with
            {
                Reason = overrideConfirmation.Reason.Trim()
            };
        }

        return repository.AssignOrMoveAsync(
            batchOperationId.Trim(),
            machineId.Trim(),
            backlogPosition,
            overrideConfirmation,
            timeProvider.GetUtcNow(),
            editAuthority,
            cancellationToken);
    }

    internal Task<IReadOnlyList<MachineAssignmentOverrideLog>> ListOverridesAsync(
        string batchOperationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(batchOperationId))
        {
            throw new MachineAssignmentValidationException(
                "batchOperationId", "required", "batchOperationId is required.");
        }

        return repository.ListOverridesAsync(batchOperationId.Trim(), cancellationToken);
    }

    internal Task<bool> UnassignAsync(
        string batchOperationId,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(batchOperationId))
        {
            throw new MachineAssignmentValidationException(
                "batchOperationId",
                "required",
                "batchOperationId is required.");
        }

        return repository.UnassignAsync(
            batchOperationId.Trim(),
            timeProvider.GetUtcNow(),
            editAuthority,
            cancellationToken);
    }

    internal Task<IReadOnlyList<MachineBacklogItem>> GetBacklogAsync(
        string machineId,
        CancellationToken cancellationToken = default) =>
        repository.GetBacklogAsync(machineId, cancellationToken);

    internal Task<MachineAssignmentPlanningModeMutationResult> ChangePlanningModeAsync(
        string machineAssignmentId,
        int expectedVersion,
        string? planningMode,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(machineAssignmentId))
        {
            throw new MachineAssignmentValidationException(
                "assignmentId",
                "required",
                "assignmentId is required.");
        }

        if (string.IsNullOrEmpty(planningMode))
        {
            throw new MachineAssignmentValidationException(
                "planningMode",
                "required",
                "planningMode is required.");
        }

        if (!MachineAssignmentPlanningModes.TryParse(planningMode, out var parsedMode))
        {
            throw new MachineAssignmentValidationException(
                "planningMode",
                "invalid_planning_mode",
                "planningMode must be exactly 'forward', 'backward', or 'manual'.");
        }

        return repository.ChangePlanningModeAsync(
            machineAssignmentId.Trim(),
            expectedVersion,
            parsedMode,
            timeProvider.GetUtcNow(),
            editAuthority,
            cancellationToken);
    }

    internal Task<BatchOperationExecutionResult> ChangeExecutionStatusAsync(
        string batchOperationId,
        BatchOperationExecutionAction action,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default) =>
        ChangeExecutionStatusAsync(batchOperationId, action, null, editAuthority, cancellationToken);

    internal Task<BatchOperationExecutionResult> ChangeExecutionStatusAsync(
        string batchOperationId,
        BatchOperationExecutionAction action,
        OperationPauseReason? pauseReason,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(batchOperationId))
        {
            throw new MachineAssignmentValidationException(
                "batchOperationId", "required", "batchOperationId is required.");
        }

        pauseReason = ValidatePauseReason(action, pauseReason);
        return repository.ChangeExecutionStatusAsync(
            batchOperationId.Trim(),
            action,
            pauseReason,
            timeProvider.GetUtcNow(),
            editAuthority,
            cancellationToken);
    }

    internal Task<ManualOperationReportResult> RecordManualReportAsync(
        string batchOperationId, string reportType, int? partTimeSeconds,
        EditAuthority editAuthority, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<ManualOperationReportType>(reportType, true, out var parsed))
            throw new MachineAssignmentValidationException("reportType", "invalid", "reportType must be setupStart, setupEnd, partTimeUpdate, or productionEnd.");
        if (parsed == ManualOperationReportType.PartTimeUpdate && (!partTimeSeconds.HasValue || partTimeSeconds <= 0))
            throw new MachineAssignmentValidationException("partTimeSeconds", "required", "partTimeSeconds must be positive for a part time update.");
        return repository.RecordManualReportAsync(batchOperationId.Trim(), parsed, partTimeSeconds,
            timeProvider.GetUtcNow(), editAuthority, cancellationToken);
    }

    private static OperationPauseReason? ValidatePauseReason(
        BatchOperationExecutionAction action,
        OperationPauseReason? value)
    {
        if (action != BatchOperationExecutionAction.Suspend)
        {
            return null;
        }

        if (value is null)
        {
            throw new MachineAssignmentValidationException("pauseReason", "required", "A structured pause reason is required.");
        }

        static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        value = value with
        {
            ReasonType = Clean(value.ReasonType) ?? string.Empty,
            ProblemDescription = Clean(value.ProblemDescription),
            ToolingItemDescription = Clean(value.ToolingItemDescription),
            CustomerContactName = Clean(value.CustomerContactName),
            RequestDescription = Clean(value.RequestDescription),
            Comment = Clean(value.Comment)
        };
        var required = value.ReasonType switch
        {
            OperationPauseReasonTypes.AdditionalQa when value.ProblemDescription is null => "problemDescription",
            OperationPauseReasonTypes.ToolingProblem when value.ToolingItemDescription is null => "toolingItemDescription",
            OperationPauseReasonTypes.CustomerRequest when value.CustomerContactName is null => "customerContactName",
            OperationPauseReasonTypes.CustomerRequest when value.RequestDescription is null => "requestDescription",
            OperationPauseReasonTypes.Other when value.Comment is null => "comment",
            OperationPauseReasonTypes.AdditionalQa or OperationPauseReasonTypes.ToolingProblem
                or OperationPauseReasonTypes.CustomerRequest or OperationPauseReasonTypes.Other => null,
            _ => "reasonType"
        };
        if (required is not null)
        {
            throw new MachineAssignmentValidationException(required, "required", $"{required} is required for this pause reason.");
        }

        if (new[] { value.ProblemDescription, value.ToolingItemDescription, value.CustomerContactName, value.RequestDescription, value.Comment }
            .Any(text => text?.Length > 2_000))
        {
            throw new MachineAssignmentValidationException("pauseReason", "too_long", "Pause text fields must be 2,000 characters or fewer.");
        }
        return value;
    }
}

internal sealed class MachineAssignmentValidationException : Exception
{
    internal MachineAssignmentValidationException(string field, string code, string message)
        : base(message)
    {
        Field = field;
        Code = code;
    }

    internal string Field { get; }

    internal string Code { get; }
}

internal sealed class BatchOperationNotFoundException : Exception
{
    internal BatchOperationNotFoundException(string batchOperationId)
        : base($"Batch Operation '{batchOperationId}' was not found.")
    {
    }
}

internal sealed class AssignmentMachineNotFoundException : Exception
{
    internal AssignmentMachineNotFoundException(string machineId)
        : base($"Machine '{machineId}' was not found.")
    {
    }
}

internal sealed class IncompatibleMachineException : Exception
{
    internal IncompatibleMachineException(string batchOperationId, string machineId)
        : base($"Batch Operation '{batchOperationId}' is not compatible with active Machine '{machineId}'.")
    {
    }
}

internal sealed class MachineAssignmentOverrideRequiredException : Exception
{
    internal MachineAssignmentOverrideRequiredException(
        string batchOperationId,
        string machineId,
        string requiredMachineType,
        string selectedMachineType)
        : base($"{batchOperationId} requires '{requiredMachineType}', while Machine '{machineId}' is type '{selectedMachineType}'. Confirm the override and provide a reason to continue.")
    {
        RequiredMachineType = requiredMachineType;
        SelectedMachineType = selectedMachineType;
    }

    internal string RequiredMachineType { get; }

    internal string SelectedMachineType { get; }
}

internal sealed class BacklogPositionOutOfRangeException : Exception
{
    internal BacklogPositionOutOfRangeException(int position, int maximum)
        : base($"Backlog position {position} is outside the allowed range 0 through {maximum}.")
    {
    }
}

internal sealed class BatchOperationNotAssignedException : Exception
{
    internal BatchOperationNotAssignedException(string batchOperationId)
        : base($"Batch Operation '{batchOperationId}' is not assigned to a Machine.") { }
}

internal sealed class BatchOperationTransitionException : Exception
{
    internal BatchOperationTransitionException(string currentStatus, BatchOperationExecutionAction action)
        : base($"Cannot {action.ToString().ToLowerInvariant()} an operation in status '{currentStatus}'.") { }
}

internal sealed class BatchOperationNotFirstException : Exception
{
    internal BatchOperationNotFirstException(string batchOperationId)
        : base($"Batch Operation '{batchOperationId}' must be first in its Machine backlog before it can start.") { }
}

internal sealed class MachineAlreadyRunningOperationException : Exception
{
    internal MachineAlreadyRunningOperationException(string machineId)
        : base($"Machine '{machineId}' already has an operation in progress.") { }
}

internal sealed class ProductionReadinessException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

internal sealed class CompletedBatchOperationCannotBeAssignedException : Exception
{
    internal CompletedBatchOperationCannotBeAssignedException(string batchOperationId)
        : base($"Completed Batch Operation '{batchOperationId}' cannot be assigned to a Machine.") { }
}

internal sealed class RunningBatchOperationCannotMoveException : Exception
{
    internal RunningBatchOperationCannotMoveException(string batchOperationId)
        : base($"In-progress Batch Operation '{batchOperationId}' must be suspended before its Machine assignment can change.") { }
}

internal sealed class MachineAssignmentNotFoundException : Exception
{
    internal MachineAssignmentNotFoundException(string machineAssignmentId)
        : base($"Machine Assignment '{machineAssignmentId}' was not found.") { }
}

internal sealed class MachineAssignmentVersionConflictException : Exception
{
    internal MachineAssignmentVersionConflictException(string machineAssignmentId, int expectedVersion)
        : base($"Machine Assignment '{machineAssignmentId}' is no longer at version {expectedVersion}.") { }
}

internal sealed class RunningMachineAssignmentPlanningModeException : Exception
{
    internal RunningMachineAssignmentPlanningModeException(string machineAssignmentId)
        : base($"In-progress Machine Assignment '{machineAssignmentId}' cannot change planning mode because its actual start is authoritative.") { }
}
