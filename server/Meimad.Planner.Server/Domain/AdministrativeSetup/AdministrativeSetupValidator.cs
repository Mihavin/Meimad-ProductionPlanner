using System.Globalization;
using System.Net.Mail;

namespace Meimad.Planner.Server.Domain.AdministrativeSetup;

internal static class AdministrativeSetupValidator
{
    internal static EmployeeResourceValues Validate(EmployeeResourceValues values)
    {
        var issues = new List<ValidationIssue>();
        var number = Required(values.EmployeeNumber, "employeeNumber", 50, issues);
        var firstName = Required(values.FirstName, "firstName", 100, issues);
        var lastName = Required(values.LastName, "lastName", 100, issues);
        var type = Required(values.ResourceType, "role", 100, issues);
        if (type is not null && !EmployeeResourceRole.All.Contains(type))
            issues.Add(new("role", "invalid_role", "role must be setup_worker, regular_worker, or qa_worker."));
        var calendarId = Required(values.AssignedCalendarId, "assignedCalendarId", 100, issues);
        var skills = NormalizeSkills(values.Skills, issues);
        var photoPath = Optional(values.PhotoPath, 2000, "photoPath", issues);
        var notes = Optional(values.Notes, 4000, "notes", issues);
        var email = Optional(values.Email, 320, "email", issues);
        if (email is not null && !IsEmail(email)) issues.Add(new("email", "invalid_email", "email must be a valid email address."));
        Throw(issues);
        return new(number!, firstName!, lastName!, type!, skills, calendarId!, photoPath, notes, email, values.IsActive);
    }

    internal static EmployeeCalendarExceptionValues Validate(EmployeeCalendarExceptionValues values)
    {
        var issues = new List<ValidationIssue>();
        if (!values.Date.HasValue) issues.Add(new("date", "required", "date is required."));
        var type = Required(values.ExceptionType, "exceptionType", 50, issues)?.ToLowerInvariant();
        if (type is not null && !EmployeeCalendarExceptionType.All.Contains(type))
            issues.Add(new("exceptionType", "invalid_exception_type", "exceptionType must be vacation, sick_day, personal_day, unavailable, or custom_note."));
        var note = Optional(values.Note, 1000, "note", issues);
        if (type == EmployeeCalendarExceptionType.CustomNote && note is null)
            issues.Add(new("note", "required", "note is required for custom_note exceptions."));

        string? startsAt = null;
        string? endsAt = null;
        if (!values.IsFullDay)
        {
            startsAt = LocalTime(values.StartsAtLocal, "startsAtLocal", false, issues);
            endsAt = LocalTime(values.EndsAtLocal, "endsAtLocal", true, issues);
            if (startsAt is not null && endsAt is not null && LocalMinutes(endsAt) <= LocalMinutes(startsAt))
                issues.Add(new("endsAtLocal", "invalid_time_range", "endsAtLocal must be later than startsAtLocal on the same day."));
        }
        else if (!string.IsNullOrWhiteSpace(values.StartsAtLocal) || !string.IsNullOrWhiteSpace(values.EndsAtLocal))
        {
            issues.Add(new("startsAtLocal", "full_day_has_times", "Full-day exceptions must not include start or end times."));
        }

        Throw(issues);
        return new(values.Date, type, values.IsFullDay, startsAt, endsAt, note);
    }

    internal static IsraeliHolidayValues Validate(IsraeliHolidayValues values)
    {
        var issues = new List<ValidationIssue>();
        if (!values.Date.HasValue) issues.Add(new("date", "required", "date is required."));
        var name = Required(values.Name, "name", 200, issues);
        var status = Required(values.Status, "status", 30, issues)?.ToLowerInvariant();
        if (status is not null && !IsraeliHolidayStatus.All.Contains(status))
            issues.Add(new("status", "invalid_holiday_status", "status must be non_working, working, or partial_working."));
        string? startsAt = null;
        string? endsAt = null;
        if (status == IsraeliHolidayStatus.PartialWorking)
        {
            startsAt = LocalTime(values.StartsAtLocal, "startsAtLocal", false, issues);
            endsAt = LocalTime(values.EndsAtLocal, "endsAtLocal", true, issues);
            if (startsAt is not null && endsAt is not null && LocalMinutes(endsAt) <= LocalMinutes(startsAt))
                issues.Add(new("endsAtLocal", "invalid_time_range", "endsAtLocal must be later than startsAtLocal on the same day."));
        }
        else if (!string.IsNullOrWhiteSpace(values.StartsAtLocal) || !string.IsNullOrWhiteSpace(values.EndsAtLocal))
            issues.Add(new("startsAtLocal", "holiday_times_not_allowed", "Only partial_working holidays may contain working times."));
        Throw(issues);
        return new(values.Date, name, status, startsAt, endsAt);
    }

    internal static ReportEmailSettingsValues Validate(ReportEmailSettingsValues values)
    {
        var issues = new List<ValidationIssue>();
        var sender = Optional(values.SenderAddress, 320, "senderAddress", issues);
        if (sender is not null && !IsEmail(sender)) issues.Add(new("senderAddress", "invalid_email", "senderAddress must be a valid email address."));
        var host = Optional(values.SmtpHost, 255, "smtpHost", issues);
        if (values.SmtpPort is < 1 or > 65535) issues.Add(new("smtpPort", "out_of_range", "smtpPort must be between 1 and 65535."));
        var recipients = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (value, index) in (values.Recipients ?? []).Select((value, index) => (value, index)))
        {
            var email = Optional(value, 320, $"recipients[{index}]", issues);
            if (email is null || !IsEmail(email)) issues.Add(new($"recipients[{index}]", "invalid_email", "Every recipient must be a valid email address."));
            else if (!seen.Add(email)) issues.Add(new($"recipients[{index}]", "duplicate_email", "Recipients must be unique ignoring case."));
            else recipients.Add(email);
        }
        if (recipients.Count > 50) issues.Add(new("recipients", "too_many", "At most 50 recipients are supported."));
        var reportTime = Optional(values.DailyReportTimeLocal, 5, "dailyReportTimeLocal", issues);
        if (reportTime is not null && !TimeOnly.TryParseExact(reportTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            issues.Add(new("dailyReportTimeLocal", "invalid_local_time", "dailyReportTimeLocal must use HH:mm."));
        var zone = Optional(values.TimeZoneId, 200, "timeZoneId", issues);
        var weeklyDay = Optional(values.WeeklyMaterialReportSendDay, 9, "weeklyMaterialReportSendDay", issues)
            ?? "thursday";
        var validDays = new HashSet<string>(
            ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"],
            StringComparer.Ordinal);
        if (!validDays.Contains(weeklyDay))
            issues.Add(new("weeklyMaterialReportSendDay", "invalid_weekday", "weeklyMaterialReportSendDay must be a lowercase weekday name."));
        var weeklyTime = Optional(values.WeeklyMaterialReportTimeLocal, 5, "weeklyMaterialReportTimeLocal", issues)
            ?? "08:00";
        if (!TimeOnly.TryParseExact(weeklyTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            issues.Add(new("weeklyMaterialReportTimeLocal", "invalid_local_time", "weeklyMaterialReportTimeLocal must use HH:mm."));
        var efficiencyDay = Optional(values.WeeklyEmployeeEfficiencySendDay, 9, "weeklyEmployeeEfficiencySendDay", issues)
            ?? "sunday";
        if (!validDays.Contains(efficiencyDay))
            issues.Add(new("weeklyEmployeeEfficiencySendDay", "invalid_weekday", "weeklyEmployeeEfficiencySendDay must be a lowercase weekday name."));
        var efficiencyTime = Optional(values.WeeklyEmployeeEfficiencyTimeLocal, 5, "weeklyEmployeeEfficiencyTimeLocal", issues)
            ?? "08:00";
        if (!TimeOnly.TryParseExact(efficiencyTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            issues.Add(new("weeklyEmployeeEfficiencyTimeLocal", "invalid_local_time", "weeklyEmployeeEfficiencyTimeLocal must use HH:mm."));
        if (zone is not null)
        {
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(zone); }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            { issues.Add(new("timeZoneId", "invalid_time_zone", "timeZoneId is not available on the Server.")); }
        }
        if (values.DailyReportEnabled)
        {
            if (sender is null) issues.Add(new("senderAddress", "required", "senderAddress is required when daily reports are enabled."));
            if (recipients.Count == 0) issues.Add(new("recipients", "required", "At least one recipient is required when daily reports are enabled."));
            if (host is null) issues.Add(new("smtpHost", "required", "smtpHost is required when daily reports are enabled."));
            if (!values.SmtpPort.HasValue) issues.Add(new("smtpPort", "required", "smtpPort is required when daily reports are enabled."));
            if (reportTime is null) issues.Add(new("dailyReportTimeLocal", "required", "dailyReportTimeLocal is required when daily reports are enabled."));
            if (zone is null) issues.Add(new("timeZoneId", "required", "timeZoneId is required when daily reports are enabled."));
        }
        if (values.WeeklyMaterialReportEnabled)
        {
            if (sender is null) issues.Add(new("senderAddress", "required", "senderAddress is required when the weekly material report is enabled."));
            if (recipients.Count == 0) issues.Add(new("recipients", "required", "At least one recipient is required when the weekly material report is enabled."));
            if (host is null) issues.Add(new("smtpHost", "required", "smtpHost is required when the weekly material report is enabled."));
            if (!values.SmtpPort.HasValue) issues.Add(new("smtpPort", "required", "smtpPort is required when the weekly material report is enabled."));
            if (zone is null) issues.Add(new("timeZoneId", "required", "timeZoneId is required when the weekly material report is enabled."));
        }
        if (values.WeeklyEmployeeEfficiencyEnabled)
        {
            if (sender is null) issues.Add(new("senderAddress", "required", "senderAddress is required when weekly employee efficiency is enabled."));
            if (recipients.Count == 0) issues.Add(new("recipients", "required", "At least one recipient is required when weekly employee efficiency is enabled."));
            if (host is null) issues.Add(new("smtpHost", "required", "smtpHost is required when weekly employee efficiency is enabled."));
            if (!values.SmtpPort.HasValue) issues.Add(new("smtpPort", "required", "smtpPort is required when weekly employee efficiency is enabled."));
            if (zone is null) issues.Add(new("timeZoneId", "required", "timeZoneId is required when weekly employee efficiency is enabled."));
        }
        Throw(issues);
        return new(sender, recipients, host, values.SmtpPort, values.UseSsl, values.DailyReportEnabled, reportTime, zone,
            values.WeeklyMaterialReportEnabled, weeklyDay, weeklyTime,
            values.WeeklyEmployeeEfficiencyEnabled, efficiencyDay, efficiencyTime);
    }

    private static string? Required(string? value, string field, int maximum, ICollection<ValidationIssue> issues)
    {
        var normalized = Optional(value, maximum, field, issues);
        if (normalized is null) issues.Add(new(field, "required", $"{field} is required."));
        return normalized;
    }

    private static string? Optional(string? value, int maximum, string field, ICollection<ValidationIssue> issues)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > maximum) issues.Add(new(field, "too_long", $"{field} must contain at most {maximum} characters."));
        return normalized;
    }

    private static bool IsEmail(string value)
    {
        try { return new MailAddress(value).Address == value; }
        catch (FormatException) { return false; }
    }

    private static string? LocalTime(string? value, string field, bool allowEndOfDay, ICollection<ValidationIssue> issues)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            issues.Add(new(field, "required", $"{field} is required for a partial-day exception."));
            return null;
        }
        if (allowEndOfDay && normalized == "24:00") return normalized;
        if (!TimeOnly.TryParseExact(normalized, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            issues.Add(new(field, "invalid_local_time", $"{field} must use HH:mm."));
            return null;
        }
        return parsed.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static int LocalMinutes(string value)
    {
        if (value == "24:00") return 1440;
        var parsed = TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture);
        return parsed.Hour * 60 + parsed.Minute;
    }

    private static IReadOnlyList<string> NormalizeSkills(IReadOnlyList<string?>? values, ICollection<ValidationIssue> issues)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (value, index) in (values ?? []).Select((value, index) => (value, index)))
        {
            var skill = Optional(value, 100, $"skills[{index}]", issues);
            if (skill is null) { issues.Add(new($"skills[{index}]", "required", "skill must not be blank.")); continue; }
            if (!seen.Add(skill)) issues.Add(new($"skills[{index}]", "duplicate_skill", "skills must be unique ignoring case."));
            else result.Add(skill);
        }
        if (result.Count > 100) issues.Add(new("skills", "too_many", "At most 100 skills are supported."));
        return result;
    }

    private static void Throw(IReadOnlyList<ValidationIssue> issues)
    {
        if (issues.Count > 0) throw new AdministrativeSetupValidationException(issues);
    }
}
