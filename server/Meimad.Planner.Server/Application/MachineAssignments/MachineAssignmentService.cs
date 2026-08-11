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

        return repository.AssignOrMoveAsync(
            batchOperationId.Trim(),
            machineId.Trim(),
            backlogPosition,
            timeProvider.GetUtcNow(),
            editAuthority,
            cancellationToken);
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

    internal Task<BatchOperationExecutionResult> ChangeExecutionStatusAsync(
        string batchOperationId,
        BatchOperationExecutionAction action,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(batchOperationId))
        {
            throw new MachineAssignmentValidationException(
                "batchOperationId", "required", "batchOperationId is required.");
        }

        return repository.ChangeExecutionStatusAsync(
            batchOperationId.Trim(),
            action,
            timeProvider.GetUtcNow(),
            editAuthority,
            cancellationToken);
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
