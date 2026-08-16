using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.WorkingCalendars;

namespace Meimad.Planner.Server.Application.WorkingCalendars;

internal sealed class WorkingCalendarService
{
    private readonly IWorkingCalendarRepository repository;
    private readonly TimeProvider timeProvider;

    public WorkingCalendarService(IWorkingCalendarRepository repository, TimeProvider timeProvider)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
    }

    internal async Task<WorkingCalendar> CreateAsync(
        CreateWorkingCalendarCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var values = WorkingCalendarValidator.ValidateAndNormalize(new WorkingCalendarValues(
            command.Name,
            command.TimeZoneId,
            command.Workdays,
            command.ShiftStartsAtLocal,
            command.ShiftEndsAtLocal,
            command.Windows,
            command.BreakWindows,
            command.Exceptions,
            command.Usages));
        var now = timeProvider.GetUtcNow();
        return await repository.CreateAsync(new WorkingCalendar(
            Guid.NewGuid().ToString("N"),
            values.Name,
            values.TimeZoneId,
            values.Workdays,
            values.Windows.Count == 1 ? values.Windows[0].StartsAtLocal : null,
            values.Windows.Count == 1 ? values.Windows[0].EndsAtLocal : null,
            values.Windows,
            values.BreakWindows,
            values.Exceptions,
            values.Usages,
            "weekly",
            1,
            now,
            now,
            command.UseIsraeliHolidays), editAuthority, cancellationToken);
    }

    internal Task<IReadOnlyList<WorkingCalendar>> ListAsync(
        CancellationToken cancellationToken = default) => repository.ListAsync(cancellationToken);

    internal Task<WorkingCalendar?> GetByIdAsync(
        string workingCalendarId,
        CancellationToken cancellationToken = default) =>
        repository.GetByIdAsync(workingCalendarId, cancellationToken);

    internal async Task<WorkingCalendar> UpdateAsync(
        string workingCalendarId,
        int expectedVersion,
        UpdateWorkingCalendarCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var current = await repository.GetByIdAsync(workingCalendarId, cancellationToken)
            ?? throw new WorkingCalendarNotFoundException(workingCalendarId);
        var windowsWerePatched = command.Windows.IsSpecified;
        var legacyShiftWasPatched = command.ShiftStartsAtLocal.IsSpecified || command.ShiftEndsAtLocal.IsSpecified;
        var values = WorkingCalendarValidator.ValidateAndNormalize(new WorkingCalendarValues(
            Select(command.Name, current.Name),
            Select(command.TimeZoneId, current.TimeZoneId),
            Select(command.Workdays, current.Workdays.Cast<string?>().ToArray()),
            windowsWerePatched ? null : Select(command.ShiftStartsAtLocal, legacyShiftWasPatched ? current.ShiftStartsAtLocal : null),
            windowsWerePatched ? null : Select(command.ShiftEndsAtLocal, legacyShiftWasPatched ? current.ShiftEndsAtLocal : null),
            legacyShiftWasPatched ? null : Select(command.Windows, current.Windows.Cast<WorkingCalendarWindow?>().ToArray()),
            Select(command.BreakWindows, current.BreakWindows.Cast<WorkingCalendarWindow?>().ToArray()),
            Select(command.Exceptions, current.Exceptions.Cast<WorkingCalendarException?>().ToArray()),
            Select(command.Usages, current.Usages.Cast<string?>().ToArray())));
        var updated = current with
        {
            Name = values.Name,
            TimeZoneId = values.TimeZoneId,
            Workdays = values.Workdays,
            ShiftStartsAtLocal = values.Windows.Count == 1 ? values.Windows[0].StartsAtLocal : null,
            ShiftEndsAtLocal = values.Windows.Count == 1 ? values.Windows[0].EndsAtLocal : null,
            Windows = values.Windows,
            BreakWindows = values.BreakWindows,
            Exceptions = values.Exceptions,
            Usages = values.Usages,
            UseIsraeliHolidays = Select(command.UseIsraeliHolidays, current.UseIsraeliHolidays) ?? false,
            ScheduleKind = "weekly",
            Version = expectedVersion + 1,
            UpdatedAt = timeProvider.GetUtcNow()
        };
        return await repository.UpdateAsync(updated, expectedVersion, editAuthority, cancellationToken)
            ?? throw new WorkingCalendarVersionConflictException(workingCalendarId, expectedVersion);
    }

    internal Task<bool> DeleteAsync(
        string workingCalendarId,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default) =>
        repository.DeleteAsync(workingCalendarId, editAuthority, cancellationToken);

    internal Task<WorkingCalendar?> GetSetupCalendarAsync(CancellationToken cancellationToken = default) =>
        repository.GetSetupCalendarAsync(cancellationToken);

    internal Task<WorkingCalendar> SetSetupCalendarAsync(
        string workingCalendarId,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default) =>
        repository.SetSetupCalendarAsync(
            workingCalendarId,
            timeProvider.GetUtcNow(),
            editAuthority,
            cancellationToken);

    internal Task ClearSetupCalendarAsync(
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default) =>
        repository.ClearSetupCalendarAsync(
            timeProvider.GetUtcNow(),
            editAuthority,
            cancellationToken);

    internal Task<WorkingCalendar?> GetMasterCalendarAsync(CancellationToken cancellationToken = default) =>
        repository.GetMasterCalendarAsync(cancellationToken);

    internal Task<WorkingCalendar> SetMasterCalendarAsync(string workingCalendarId, EditAuthority editAuthority,
        CancellationToken cancellationToken = default) =>
        repository.SetMasterCalendarAsync(workingCalendarId, editAuthority, cancellationToken);

    internal Task ClearMasterCalendarAsync(EditAuthority editAuthority, CancellationToken cancellationToken = default) =>
        repository.ClearMasterCalendarAsync(editAuthority, cancellationToken);

    private static T Select<T>(WorkingCalendarField<T> field, T current) =>
        field.IsSpecified ? field.Value : current;
}

internal sealed class WorkingCalendarNameConflictException : Exception
{
    internal WorkingCalendarNameConflictException(string name)
        : base($"Working Calendar name '{name}' already exists.")
    {
    }
}

internal sealed class WorkingCalendarNotFoundException(string id)
    : Exception($"Working Calendar '{id}' was not found.");

internal sealed class WorkingCalendarVersionConflictException(string id, int version)
    : Exception($"Working Calendar '{id}' is no longer at version {version}.");

internal sealed class WorkingCalendarInUseException(string id)
    : Exception($"Working Calendar '{id}' is assigned to a Machine, Employee Resource, or selected as the Setup Calendar.");

internal sealed class WorkingCalendarUsageInUseException(string id, string message)
    : Exception($"Working Calendar '{id}': {message}");
