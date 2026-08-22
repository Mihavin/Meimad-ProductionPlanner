using Meimad.Planner.Server.Domain.AdministrativeSetup;

namespace Meimad.Planner.Server.Application.AdministrativeSetup;

internal readonly record struct AdminField<T>(bool IsSpecified, T Value)
{
    internal static AdminField<T> Unspecified => new(false, default!);
    internal static AdminField<T> Specified(T value) => new(true, value);
}

internal sealed record CreateEmployeeResourceCommand(
    string? EmployeeNumber, string? FirstName, string? LastName, string? ResourceType,
    IReadOnlyList<string?>? Skills, string? AssignedCalendarId, string? PhotoPath, string? Notes,
    string? Email, bool IsActive, bool RespectMasterCalendar = true,
    double ToolLoadSecondsPerTool = 60, double? FixtureAssemblySeconds = null,
    double FirstPartRunningSpeedPercent = 66.6666666667);

internal sealed record UpdateEmployeeResourceCommand(
    AdminField<string?> EmployeeNumber, AdminField<string?> FirstName, AdminField<string?> LastName,
    AdminField<string?> ResourceType, AdminField<IReadOnlyList<string?>?> Skills,
    AdminField<string?> AssignedCalendarId, AdminField<string?> PhotoPath, AdminField<string?> Notes,
    AdminField<string?> Email, AdminField<bool?> IsActive,
    AdminField<bool?> RespectMasterCalendar = default,
    AdminField<double?> ToolLoadSecondsPerTool = default,
    AdminField<double?> FixtureAssemblySeconds = default,
    AdminField<double?> FirstPartRunningSpeedPercent = default);

internal sealed record CreateEmployeeCalendarExceptionCommand(
    DateOnly? Date, string? ExceptionType, bool IsFullDay,
    string? StartsAtLocal, string? EndsAtLocal, string? Note);

internal sealed record UpdateEmployeeCalendarExceptionCommand(
    AdminField<DateOnly?> Date, AdminField<string?> ExceptionType, AdminField<bool?> IsFullDay,
    AdminField<string?> StartsAtLocal, AdminField<string?> EndsAtLocal, AdminField<string?> Note);

internal sealed record CreateIsraeliHolidayCommand(
    DateOnly? Date, string? Name, string? Status, string? StartsAtLocal, string? EndsAtLocal);
internal sealed record UpdateIsraeliHolidayCommand(
    AdminField<DateOnly?> Date, AdminField<string?> Name, AdminField<string?> Status,
    AdminField<string?> StartsAtLocal, AdminField<string?> EndsAtLocal);

internal sealed record SyncIsraeliHolidaysCommand(int FromYear, int ToYear);

internal sealed record UpdateReportEmailSettingsCommand(ReportEmailSettingsValues Values);
