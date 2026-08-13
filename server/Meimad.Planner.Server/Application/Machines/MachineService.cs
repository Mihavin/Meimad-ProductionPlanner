using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Machines;

namespace Meimad.Planner.Server.Application.Machines;

internal sealed class MachineService
{
    private readonly IMachineRepository repository;
    private readonly TimeProvider timeProvider;

    public MachineService(IMachineRepository repository, TimeProvider timeProvider)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
    }

    internal async Task<Machine> CreateAsync(
        CreateMachineCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var values = MachineValidator.ValidateAndNormalize(ToValues(command));
        var now = timeProvider.GetUtcNow();
        var machine = new Machine(
            Guid.NewGuid().ToString("N"),
            values.Number,
            values.Name,
            values.ProcessType,
            values.AxisType,
            values.Capabilities,
            values.WorkingCalendarId,
            values.IsActive,
            values.DisplayEnabled,
            null,
            0,
            1,
            now,
            now,
            values.PicturePath,
            values.MachineTypeId);
        return await repository.CreateAsync(machine, editAuthority, cancellationToken);
    }

    internal Task<Machine?> GetByIdAsync(
        string machineId,
        CancellationToken cancellationToken = default) =>
        repository.GetByIdAsync(machineId, cancellationToken);

    internal Task<IReadOnlyList<Machine>> ListAsync(
        CancellationToken cancellationToken = default) =>
        repository.ListAsync(cancellationToken);

    internal async Task<Machine> UpdateAsync(
        string machineId,
        int expectedVersion,
        UpdateMachineCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var current = await repository.GetByIdAsync(machineId, cancellationToken)
            ?? throw new MachineNotFoundException(machineId);
        var values = MachineValidator.ValidateAndNormalize(new MachineValues(
            Select(command.Number, current.Number),
            Select(command.Name, current.Name),
            Select(command.ProcessType, current.ProcessType),
            Select(command.AxisType, current.AxisType),
            Select(command.Capabilities, current.Capabilities.Cast<string?>().ToArray()),
            Select(command.WorkingCalendarId, current.WorkingCalendarId),
            Select(command.IsActive, current.IsActive) ?? false,
            Select(command.DisplayEnabled, current.DisplayEnabled) ?? false,
            Select(command.PicturePath, current.PicturePath),
            Select(command.MachineTypeId, current.MachineTypeId)));
        var updated = current with
        {
            Number = values.Number,
            Name = values.Name,
            ProcessType = values.ProcessType,
            AxisType = values.AxisType,
            Capabilities = values.Capabilities,
            WorkingCalendarId = values.WorkingCalendarId,
            IsActive = values.IsActive,
            DisplayEnabled = values.DisplayEnabled,
            PicturePath = values.PicturePath,
            MachineTypeId = values.MachineTypeId,
            Version = expectedVersion + 1,
            UpdatedAt = timeProvider.GetUtcNow()
        };
        return await repository.UpdateAsync(
                updated,
                expectedVersion,
                editAuthority,
                cancellationToken)
            ?? throw new MachineVersionConflictException(machineId, expectedVersion);
    }

    private static MachineValues ToValues(CreateMachineCommand command) => new(
        command.Number,
        command.Name,
        command.ProcessType,
        command.AxisType,
        command.Capabilities,
        command.WorkingCalendarId,
        command.IsActive,
        command.DisplayEnabled,
        command.PicturePath,
        command.MachineTypeId);

    private static T Select<T>(MachineField<T> field, T current) =>
        field.IsSpecified ? field.Value : current;
}

internal sealed class MachineNotFoundException : Exception
{
    internal MachineNotFoundException(string machineId)
        : base($"Machine '{machineId}' was not found.")
    {
    }
}

internal sealed class WorkingCalendarNotFoundException : Exception
{
    internal WorkingCalendarNotFoundException(string calendarId)
        : base($"Working Calendar '{calendarId}' was not found.")
    {
    }
}

internal sealed class WorkingCalendarUsageException : Exception
{
    internal WorkingCalendarUsageException(string calendarId)
        : base($"Working Calendar '{calendarId}' is not enabled for Machine usage.")
    {
    }
}

internal sealed class MachineNumberConflictException : Exception
{
    internal MachineNumberConflictException(string number)
        : base($"Machine Number '{number}' already exists.")
    {
    }
}

internal sealed class MachineVersionConflictException : Exception
{
    internal MachineVersionConflictException(string machineId, int expectedVersion)
        : base($"Machine '{machineId}' is no longer at version {expectedVersion}.")
    {
    }
}

internal sealed class MachineBacklogCompatibilityException : Exception
{
    internal MachineBacklogCompatibilityException(string message)
        : base(message)
    {
    }
}

internal sealed class MachineTypeReferenceNotFoundException : Exception
{
    internal MachineTypeReferenceNotFoundException(string machineTypeId)
        : base($"Machine Type '{machineTypeId}' was not found.")
    {
    }
}
