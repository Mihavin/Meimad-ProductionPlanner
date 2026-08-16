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

    Task<WorkingCalendar?> GetByIdAsync(string workingCalendarId, CancellationToken cancellationToken);

    Task<WorkingCalendar?> UpdateAsync(
        WorkingCalendar calendar,
        int expectedVersion,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        string workingCalendarId,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<WorkingCalendar?> GetSetupCalendarAsync(CancellationToken cancellationToken);

    Task<WorkingCalendar> SetSetupCalendarAsync(
        string workingCalendarId,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task ClearSetupCalendarAsync(
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<WorkingCalendar?> GetMasterCalendarAsync(CancellationToken cancellationToken);
    Task<WorkingCalendar> SetMasterCalendarAsync(string workingCalendarId, EditAuthority editAuthority, CancellationToken cancellationToken);
    Task ClearMasterCalendarAsync(EditAuthority editAuthority, CancellationToken cancellationToken);
}
