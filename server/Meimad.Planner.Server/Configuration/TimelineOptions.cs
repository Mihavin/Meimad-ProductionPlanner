using System.Globalization;

namespace Meimad.Planner.Server.Configuration;

public sealed class TimelineOptions
{
    public const string SectionName = "Timeline";
    public string DayShiftStartsAtLocal { get; init; } = "06:00";
    public string DayShiftEndsAtLocal { get; init; } = "18:00";
    public string TimeZoneId { get; init; } = "Asia/Jerusalem";
    public int MaximumInteractiveDays { get; init; } = 180;
    public int MaximumReportDays { get; init; } = 730;

    public static TimelineOptions FromConfiguration(IConfiguration configuration)
    {
        var options = configuration.GetSection(SectionName).Get<TimelineOptions>() ?? new();
        if (options.MaximumInteractiveDays is < 1 or > 3650 || options.MaximumReportDays < options.MaximumInteractiveDays
            || options.MaximumReportDays > 3650)
            throw new InvalidOperationException("Timeline horizons must satisfy 1 <= MaximumInteractiveDays <= MaximumReportDays <= 3650.");
        if (!TimeOnly.TryParseExact(options.DayShiftStartsAtLocal, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(options.DayShiftEndsAtLocal, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end)
            || end <= start)
        {
            throw new InvalidOperationException(
                "Timeline day-shift values must be HH:mm and define one same-day window with end after start.");
        }
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new InvalidOperationException(
                $"Timeline:TimeZoneId '{options.TimeZoneId}' is not installed on this Server.", exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new InvalidOperationException(
                $"Timeline:TimeZoneId '{options.TimeZoneId}' is invalid on this Server.", exception);
        }
        return options;
    }
}
