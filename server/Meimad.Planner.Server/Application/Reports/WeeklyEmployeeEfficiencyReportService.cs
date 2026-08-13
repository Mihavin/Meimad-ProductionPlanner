using Meimad.Planner.Server.Application.AdministrativeSetup;
using Meimad.Planner.Server.Domain.AdministrativeSetup;

namespace Meimad.Planner.Server.Application.Reports;

internal sealed class WeeklyEmployeeEfficiencyReportService
{
    private readonly IWeeklyEmployeeEfficiencyRepository repository;
    private readonly AdministrativeSetupService administrativeSetup;
    private readonly IEmployeeEfficiencyEmailSender emailSender;
    private readonly TimeProvider timeProvider;

    public WeeklyEmployeeEfficiencyReportService(
        IWeeklyEmployeeEfficiencyRepository repository,
        AdministrativeSetupService administrativeSetup,
        IEmployeeEfficiencyEmailSender emailSender,
        TimeProvider timeProvider)
    {
        this.repository = repository;
        this.administrativeSetup = administrativeSetup;
        this.emailSender = emailSender;
        this.timeProvider = timeProvider;
    }

    internal async Task<EmployeeWorkMeasurement> RecordAsync(
        EmployeeWorkMeasurementValues values, string recordedBy, CancellationToken token = default)
    {
        var employee = values.EmployeeResourceId?.Trim();
        if (string.IsNullOrEmpty(employee))
            throw new EmployeeWorkMeasurementValidationException("employeeResourceId", "employeeResourceId is required.");
        if (!values.WorkDate.HasValue)
            throw new EmployeeWorkMeasurementValidationException("workDate", "workDate is required.");
        if (values.PlannedSeconds < 0 || values.ActualSeconds < 0)
            throw new EmployeeWorkMeasurementValidationException("time", "Planned and actual seconds must not be negative.");
        if (values.PlannedSeconds == 0 && values.ActualSeconds == 0)
            throw new EmployeeWorkMeasurementValidationException("time", "At least one planned or actual second is required.");
        if (values.SourceReference?.Trim().Length > 200 || values.Notes?.Trim().Length > 1000)
            throw new EmployeeWorkMeasurementValidationException("text", "Source reference is limited to 200 characters and notes to 1000.");
        return await repository.CreateMeasurementAsync(new(
            Guid.NewGuid().ToString("N"), employee, values.WorkDate.Value,
            values.PlannedSeconds, values.ActualSeconds, Clean(values.SourceReference), Clean(values.Notes),
            recordedBy, timeProvider.GetUtcNow()), token);
    }

    internal async Task<WeeklyEmployeeEfficiencyReport> GenerateAsync(CancellationToken token = default)
    {
        var settings = await administrativeSetup.GetReportEmailSettingsAsync(token);
        var (start, end, fromInstant, toInstant) = PreviousWeek(timeProvider.GetUtcNow(), settings.TimeZoneId);
        var aggregates = await repository.ReadAsync(start, end, token);
        var items = new List<WeeklyEmployeeEfficiencyItem>(aggregates.Count);
        foreach (var aggregate in aggregates)
        {
            var availability = await administrativeSetup.GetEmployeeAvailabilityAsync(
                aggregate.EmployeeResourceId, fromInstant, toInstant, token);
            var capacity = availability.Windows.Sum(window =>
                Math.Max(0L, (long)(window.EndsAt - window.StartsAt).TotalSeconds));
            var difference = aggregate.ActualSeconds - aggregate.PlannedSeconds;
            items.Add(new(
                aggregate.EmployeeResourceId, aggregate.EmployeeNumber, aggregate.FirstName,
                aggregate.LastName, aggregate.Role, aggregate.PlannedSeconds, aggregate.ActualSeconds,
                difference, Percent(difference, aggregate.PlannedSeconds), capacity,
                Percent(aggregate.PlannedSeconds, capacity), Percent(aggregate.ActualSeconds, capacity)));
        }
        return new(start, end.AddDays(-1), items);
    }

    internal async Task<WeeklyEmployeeEfficiencyReport> SendNowAsync(CancellationToken token = default)
    {
        var settings = await administrativeSetup.GetReportEmailSettingsAsync(token);
        EnsureDeliveryConfigured(settings);
        var report = await GenerateAsync(token);
        await emailSender.SendAsync(settings, report, token);
        return report;
    }

    internal async Task<bool> SendIfDueAsync(CancellationToken token = default)
    {
        var settings = await administrativeSetup.GetReportEmailSettingsAsync(token);
        if (!settings.WeeklyEmployeeEfficiencyEnabled) return false;
        EnsureDeliveryConfigured(settings);
        var zone = Zone(settings.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone);
        if (localNow.DayOfWeek.ToString().ToLowerInvariant() != settings.WeeklyEmployeeEfficiencySendDay
            || localNow.TimeOfDay < TimeOnly.ParseExact(settings.WeeklyEmployeeEfficiencyTimeLocal, "HH:mm").ToTimeSpan())
            return false;
        var report = await GenerateAsync(token);
        var key = report.WeekStart.ToString("yyyy-MM-dd");
        if (await repository.WasAutomaticallySentAsync(key, token)) return false;
        await emailSender.SendAsync(settings, report, token);
        await repository.MarkAutomaticallySentAsync(key, settings.Recipients.Count, timeProvider.GetUtcNow(), token);
        return true;
    }

    private static (DateOnly Start, DateOnly End, DateTimeOffset From, DateTimeOffset To) PreviousWeek(DateTimeOffset now, string? zoneId)
    {
        var zone = Zone(zoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).Date);
        var daysSinceSunday = (int)today.DayOfWeek;
        var end = today.AddDays(-daysSinceSunday);
        var start = end.AddDays(-7);
        return (start, end, AtStart(start, zone), AtStart(end, zone));
    }

    private static DateTimeOffset AtStart(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static decimal? Percent(long numerator, long denominator) =>
        denominator == 0 ? null : Math.Round(numerator * 100m / denominator, 2, MidpointRounding.AwayFromZero);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static TimeZoneInfo Zone(string? id) => TimeZoneInfo.FindSystemTimeZoneById(id ?? "Asia/Jerusalem");

    private static void EnsureDeliveryConfigured(ReportEmailSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SenderAddress) || settings.Recipients.Count == 0
            || string.IsNullOrWhiteSpace(settings.SmtpHost) || settings.SmtpPort is null)
            throw new EmployeeEfficiencyReportDeliveryException(
                "Sender, recipients, SMTP host, and SMTP port must be configured before sending.");
    }
}

internal sealed class EmployeeEfficiencyReportDeliveryException(string message) : Exception(message);
