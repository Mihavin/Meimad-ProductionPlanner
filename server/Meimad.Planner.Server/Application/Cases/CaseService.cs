using Meimad.Planner.Server.Domain.Cases;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.CaseOperations;

namespace Meimad.Planner.Server.Application.Cases;

internal sealed class CaseService
{
    private readonly ICaseRepository repository;
    private readonly TimeProvider timeProvider;

    public CaseService(ICaseRepository repository, TimeProvider timeProvider)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
    }

    internal async Task<PlannerCase> CreateAsync(
        CreateCaseCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var values = CaseValidator.ValidateAndNormalize(ToValues(command));
        var now = timeProvider.GetUtcNow();
        var plannerCase = new PlannerCase(
            Guid.NewGuid().ToString("N"),
            values.PartNumber,
            values.Name,
            values.Revision,
            values.Customer,
            values.CustomerReference,
            values.PreviewPath,
            values.WorkingFolderPath,
            values.MaterialType,
            values.MaterialSpecification,
            values.RawMaterialForm,
            values.RawMaterialDimensions,
            0,
            0,
            values.Notes,
            false,
            1,
            now,
            now);

        return await repository.CreateAsync(plannerCase, editAuthority, cancellationToken);
    }

    internal Task<PlannerCase?> GetByIdAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        return repository.GetByIdAsync(caseId, cancellationToken);
    }

    internal Task<IReadOnlyList<PlannerCase>> ListAsync(
        string? search,
        string? customer,
        bool? isActive,
        CaseSortOrder sortOrder,
        CancellationToken cancellationToken = default) =>
        repository.ListAsync(search?.Trim(), customer?.Trim(), isActive, sortOrder, cancellationToken);

    internal Task<IReadOnlyList<CaseOperationDetails>> ListOperationsAsync(
        string caseId,
        CancellationToken cancellationToken = default) =>
        repository.ListOperationsAsync(caseId, cancellationToken);

    internal async Task<CaseOperationDetails> CreateOperationAsync(
        string caseId,
        CreateCaseOperationCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var values = CaseOperationValidator.ValidateAndNormalize(
            new CaseOperationCreateValues(
                caseId,
                command.OperationNumber,
                command.Name,
                command.RequiredMachineType,
                command.SetupTimeSeconds,
                command.CycleTimePerPartSeconds,
                command.DependencyType,
                command.PredecessorCaseOperationId,
                command.SimultaneousGroupKey,
                command.QaTimeAfterSetupSeconds,
                command.LoadUnloadTimeSeconds,
                command.LoadUnloadRequiresWorker,
                command.AutomaticLoading,
                command.LoadUnloadEveryNParts,
                command.DayShiftOnly));
        ValidateExternalDelay(command.HasExternalDelay, command.ExternalDelayDescription,
            command.ExternalDelayDuration, command.ExternalDelayDurationUnit,
            command.ExternalDelayCalendarId);
        var now = timeProvider.GetUtcNow();
        var operation = new NewCaseOperation(
            Guid.NewGuid().ToString("N"),
            values.CaseId,
            values.OperationNumber,
            values.Name,
            values.RequiredMachineType,
            values.SetupTimeSeconds,
            values.CycleTimePerPartSeconds,
            values.DependencyType,
            values.PredecessorCaseOperationId,
            values.SimultaneousGroupKey,
            now,
            values.QaTimeAfterSetupSeconds,
            values.LoadUnloadTimeSeconds,
            values.LoadUnloadRequiresWorker,
            values.AutomaticLoading,
            values.LoadUnloadEveryNParts,
            values.DayShiftOnly,
            command.HasExternalDelay,
            command.ExternalDelayDescription?.Trim(),
            command.ExternalDelayDuration,
            command.ExternalDelayDurationUnit.Trim().ToLowerInvariant(),
            command.ExternalDelayCalendarId?.Trim(),
            command.RespectMasterCalendar);

        return await repository.CreateOperationAsync(
                operation,
                editAuthority,
                cancellationToken)
            ?? throw new CaseNotFoundException(caseId);
    }

    private static void ValidateExternalDelay(bool enabled, string? description, double duration, string unit, string? calendarId)
    {
        if (!enabled && duration == 0) return;
        if (!enabled || duration <= 0 || string.IsNullOrWhiteSpace(description)
            || unit.Trim().ToLowerInvariant() is not ("hours" or "days" or "working_days")
            || (unit.Trim().Equals("working_days", StringComparison.OrdinalIgnoreCase)
                && (duration != Math.Truncate(duration) || string.IsNullOrWhiteSpace(calendarId))))
        {
            throw new CaseOperationValidationException([
                new CaseOperationValidationIssue("externalDelay", "invalid_external_delay",
                    "Enabled external delay requires a description and positive duration. Working days require a whole-number duration and selected Calendar.")]);
        }
    }

    internal Task<CaseOperationDetails> UpdateOperationAsync(
        string caseId,
        string operationId,
        int expectedVersion,
        UpdateCaseOperationCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default) =>
        repository.UpdateOperationAsync(
            caseId,
            operationId,
            expectedVersion,
            command,
            timeProvider.GetUtcNow(),
            editAuthority,
            cancellationToken);

    internal async Task<PlannerCase> UpdateAsync(
        string caseId,
        int expectedVersion,
        UpdateCaseCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var current = await repository.GetByIdAsync(caseId, cancellationToken)
            ?? throw new CaseNotFoundException(caseId);

        var values = CaseValidator.ValidateAndNormalize(new CaseValues(
            Select(command.PartNumber, current.PartNumber),
            Select(command.Name, current.Name),
            Select(command.Revision, current.Revision),
            Select(command.Customer, current.Customer),
            Select(command.CustomerReference, current.CustomerReference),
            Select(command.PreviewPath, current.PreviewPath),
            Select(command.WorkingFolderPath, current.WorkingFolderPath),
            Select(command.MaterialType, current.MaterialType),
            Select(command.MaterialSpecification, current.MaterialSpecification),
            Select(command.RawMaterialForm, current.RawMaterialForm),
            Select(command.RawMaterialDimensions, current.RawMaterialDimensions),
            Select(command.Notes, current.Notes)));

        var updated = current with
        {
            PartNumber = values.PartNumber,
            Name = values.Name,
            Revision = values.Revision,
            Customer = values.Customer,
            CustomerReference = values.CustomerReference,
            PreviewPath = values.PreviewPath,
            WorkingFolderPath = values.WorkingFolderPath,
            MaterialType = values.MaterialType,
            MaterialSpecification = values.MaterialSpecification,
            RawMaterialForm = values.RawMaterialForm,
            RawMaterialDimensions = values.RawMaterialDimensions,
            Notes = values.Notes,
            Version = expectedVersion + 1,
            UpdatedAt = timeProvider.GetUtcNow()
        };

        return await repository.UpdateAsync(
                updated,
                expectedVersion,
                editAuthority,
                cancellationToken)
            ?? throw new CaseVersionConflictException(caseId, expectedVersion);
    }

    private static CaseValues ToValues(CreateCaseCommand command) => new(
        command.PartNumber,
        command.Name,
        command.Revision,
        command.Customer,
        command.CustomerReference,
        command.PreviewPath,
        command.WorkingFolderPath,
        command.MaterialType,
        command.MaterialSpecification,
        command.RawMaterialForm,
        command.RawMaterialDimensions,
        command.Notes);

    private static T Select<T>(OptionalField<T> field, T current) =>
        field.IsSpecified ? field.Value : current;
}

internal sealed class CaseNotFoundException : Exception
{
    internal CaseNotFoundException(string caseId)
        : base($"Case '{caseId}' was not found.")
    {
    }
}

internal sealed class CaseVersionConflictException : Exception
{
    internal CaseVersionConflictException(string caseId, int expectedVersion)
        : base($"Case '{caseId}' is no longer at version {expectedVersion}.")
    {
    }
}

internal sealed class CaseOperationNotFoundException : Exception
{
    internal CaseOperationNotFoundException(string caseId, string operationId)
        : base($"Case Operation '{operationId}' was not found under Case '{caseId}'.")
    {
    }
}

internal sealed class CaseOperationVersionConflictException : Exception
{
    internal CaseOperationVersionConflictException(string operationId, int expectedVersion)
        : base($"Case Operation '{operationId}' is no longer at version {expectedVersion}.")
    {
    }
}
