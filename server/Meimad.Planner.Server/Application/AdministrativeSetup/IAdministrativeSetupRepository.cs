using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.AdministrativeSetup;

namespace Meimad.Planner.Server.Application.AdministrativeSetup;

internal interface IAdministrativeSetupRepository
{
    Task<IReadOnlyList<EmployeeResource>> ListResourcesAsync(CancellationToken token);
    Task<EmployeeResource?> GetResourceAsync(string id, CancellationToken token);
    Task<EmployeeResource> CreateResourceAsync(EmployeeResource value, EditAuthority authority, CancellationToken token);
    Task<EmployeeResource?> UpdateResourceAsync(EmployeeResource value, int expectedVersion, EditAuthority authority, CancellationToken token);
    Task<bool> DeleteResourceAsync(string id, EditAuthority authority, CancellationToken token);
    Task<IReadOnlyList<EmployeeCalendarException>> ListEmployeeExceptionsAsync(string resourceId, DateOnly? from, DateOnly? to, CancellationToken token);
    Task<EmployeeCalendarException?> GetEmployeeExceptionAsync(string resourceId, string exceptionId, CancellationToken token);
    Task<EmployeeCalendarException> CreateEmployeeExceptionAsync(EmployeeCalendarException value, EditAuthority authority, CancellationToken token);
    Task<EmployeeCalendarException?> UpdateEmployeeExceptionAsync(EmployeeCalendarException value, int expectedVersion, EditAuthority authority, CancellationToken token);
    Task<bool> DeleteEmployeeExceptionAsync(string resourceId, string exceptionId, EditAuthority authority, CancellationToken token);
    Task<IReadOnlyList<IsraeliHoliday>> ListHolidaysAsync(CancellationToken token);
    Task<IsraeliHoliday?> GetHolidayAsync(string id, CancellationToken token);
    Task<IsraeliHoliday> CreateHolidayAsync(IsraeliHoliday value, EditAuthority authority, CancellationToken token);
    Task<IsraeliHoliday?> UpdateHolidayAsync(IsraeliHoliday value, int expectedVersion, EditAuthority authority, CancellationToken token);
    Task<bool> DeleteHolidayAsync(string id, EditAuthority authority, CancellationToken token);
    Task<IsraeliHolidaySyncResult> SynchronizeHolidaysAsync(
        IReadOnlyList<IsraeliHolidaySourceItem>? items, string provider, int fromYear, int toYear,
        DateTimeOffset attemptAt, string? error, EditAuthority authority, CancellationToken token);
    Task<ReportEmailSettings> GetReportEmailSettingsAsync(CancellationToken token);
    Task<ReportEmailSettings?> UpdateReportEmailSettingsAsync(ReportEmailSettings value, int expectedVersion, EditAuthority authority, CancellationToken token);
}
