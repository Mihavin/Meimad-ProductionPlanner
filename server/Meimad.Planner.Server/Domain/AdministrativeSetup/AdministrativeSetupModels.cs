namespace Meimad.Planner.Server.Domain.AdministrativeSetup;

internal sealed record EmployeeResource(
    string ResourceId, string EmployeeNumber, string Name, string ResourceType, string? Email,
    string FirstName, string LastName, IReadOnlyList<string> Skills, string AssignedCalendarId,
    string? PhotoPath, string? Notes, bool IsActive, int Version, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool RespectMasterCalendar = true)
{
    internal bool IsAvailableForFuturePlanning => IsActive && !string.IsNullOrWhiteSpace(AssignedCalendarId);
}

internal static class EmployeeResourceRole
{
    internal const string SetupWorker = "setup_worker";
    internal const string RegularWorker = "regular_worker";
    internal const string QaWorker = "qa_worker";
    internal static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    { SetupWorker, RegularWorker, QaWorker };

    internal static string CalendarUsage(string role) => role switch
    {
        SetupWorker => "setup_worker",
        RegularWorker => "regular_worker",
        QaWorker => "qa_worker",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
}

internal sealed record EmployeeResourceValues(
    string? EmployeeNumber, string? FirstName, string? LastName, string? ResourceType,
    IReadOnlyList<string?>? Skills, string? AssignedCalendarId, string? PhotoPath, string? Notes,
    string? Email, bool IsActive)
{
    internal string Name => $"{FirstName} {LastName}".Trim();
}

internal sealed record EmployeeCalendarException(
    string ExceptionId,
    string ResourceId,
    DateOnly Date,
    string ExceptionType,
    bool IsFullDay,
    string? StartsAtLocal,
    string? EndsAtLocal,
    string? Note,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record EmployeeCalendarExceptionValues(
    DateOnly? Date,
    string? ExceptionType,
    bool IsFullDay,
    string? StartsAtLocal,
    string? EndsAtLocal,
    string? Note);

internal static class EmployeeCalendarExceptionType
{
    internal const string Vacation = "vacation";
    internal const string SickDay = "sick_day";
    internal const string PersonalDay = "personal_day";
    internal const string Unavailable = "unavailable";
    internal const string CustomNote = "custom_note";

    internal static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    { Vacation, SickDay, PersonalDay, Unavailable, CustomNote };
}

internal sealed record EmployeeAvailabilityWindow(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

internal sealed record EmployeeAvailability(
    string ResourceId,
    bool IsActive,
    string? AssignedCalendarId,
    string? TimeZoneId,
    IReadOnlyList<EmployeeAvailabilityWindow> Windows,
    IReadOnlyList<EmployeeCalendarException> Exceptions);

internal sealed record IsraeliHoliday(
    string IsraeliHolidayId, DateOnly Date, string Name, string Status,
    string? StartsAtLocal, string? EndsAtLocal, string Source, string? ExternalId,
    bool IsManualOverride, int Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

internal sealed record IsraeliHolidayValues(
    DateOnly? Date, string? Name, string? Status, string? StartsAtLocal, string? EndsAtLocal);

internal static class IsraeliHolidayStatus
{
    internal const string NonWorking = "non_working";
    internal const string Working = "working";
    internal const string PartialWorking = "partial_working";
    internal static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    { NonWorking, Working, PartialWorking };
}

internal sealed record IsraeliHolidaySourceItem(
    string ExternalId, DateOnly Date, string Name, string Status);

internal sealed record IsraeliHolidaySyncResult(
    string Provider, int FromYear, int ToYear, int Created, int Updated, int PreservedManual,
    DateTimeOffset LastAttemptAt, DateTimeOffset? LastSuccessAt, string? Error)
{
    internal bool Succeeded => Error is null;
}

internal sealed record ReportEmailSettings(
    string? SenderAddress, IReadOnlyList<string> Recipients, string? SmtpHost, int? SmtpPort,
    bool UseSsl, bool DailyReportEnabled, string? DailyReportTimeLocal, string? TimeZoneId,
    int Version, DateTimeOffset UpdatedAt,
    bool WeeklyMaterialReportEnabled = false,
    string WeeklyMaterialReportSendDay = "thursday",
    string WeeklyMaterialReportTimeLocal = "08:00",
    bool WeeklyEmployeeEfficiencyEnabled = false,
    string WeeklyEmployeeEfficiencySendDay = "sunday",
    string WeeklyEmployeeEfficiencyTimeLocal = "08:00");

internal sealed record ReportEmailSettingsValues(
    string? SenderAddress, IReadOnlyList<string?>? Recipients, string? SmtpHost, int? SmtpPort,
    bool UseSsl, bool DailyReportEnabled, string? DailyReportTimeLocal, string? TimeZoneId,
    bool WeeklyMaterialReportEnabled = false,
    string? WeeklyMaterialReportSendDay = null,
    string? WeeklyMaterialReportTimeLocal = null,
    bool WeeklyEmployeeEfficiencyEnabled = false,
    string? WeeklyEmployeeEfficiencySendDay = null,
    string? WeeklyEmployeeEfficiencyTimeLocal = null);

internal sealed record ValidationIssue(string Field, string Code, string Message);

internal sealed class AdministrativeSetupValidationException(IReadOnlyList<ValidationIssue> issues)
    : Exception("Administrative Setup validation failed.")
{
    internal IReadOnlyList<ValidationIssue> Issues { get; } = issues;
}
