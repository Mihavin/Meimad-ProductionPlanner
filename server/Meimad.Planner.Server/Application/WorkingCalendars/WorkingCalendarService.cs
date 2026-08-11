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
            command.ShiftEndsAtLocal));
        var now = timeProvider.GetUtcNow();
        return await repository.CreateAsync(new WorkingCalendar(
            Guid.NewGuid().ToString("N"),
            values.Name,
            values.TimeZoneId,
            values.Workdays,
            values.ShiftStartsAtLocal,
            values.ShiftEndsAtLocal,
            "weekly",
            1,
            now,
            now), editAuthority, cancellationToken);
    }

    internal Task<IReadOnlyList<WorkingCalendar>> ListAsync(
        CancellationToken cancellationToken = default) => repository.ListAsync(cancellationToken);
}

internal sealed class WorkingCalendarNameConflictException : Exception
{
    internal WorkingCalendarNameConflictException(string name)
        : base($"Working Calendar name '{name}' already exists.")
    {
    }
}
