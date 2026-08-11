using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.WorkingCalendars;

namespace Meimad.Planner.Server.Application.WorkingCalendars;

internal interface IWorkingCalendarRepository
{
    Task<WorkingCalendar> CreateAsync(
        WorkingCalendar calendar,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkingCalendar>> ListAsync(CancellationToken cancellationToken);
}
