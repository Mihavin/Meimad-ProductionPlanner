using Meimad.Planner.Server.Application.AdministrativeSetup;
using Meimad.Planner.Server.Domain.AdministrativeSetup;

namespace Meimad.Planner.Server.Application.Reports;

internal sealed class WeeklyMaterialReportService
{
    private readonly IWeeklyMaterialReportRepository repository;
    private readonly AdministrativeSetupService settingsService;
    private readonly IMaterialReportEmailSender emailSender;
    private readonly TimeProvider timeProvider;

    public WeeklyMaterialReportService(
        IWeeklyMaterialReportRepository repository,
        AdministrativeSetupService settingsService,
        IMaterialReportEmailSender emailSender,
        TimeProvider timeProvider)
    {
        this.repository = repository;
        this.settingsService = settingsService;
        this.emailSender = emailSender;
        this.timeProvider = timeProvider;
    }

    internal async Task<WeeklyMaterialOrderReport> GenerateAsync(CancellationToken token = default)
    {
        var settings = await settingsService.GetReportEmailSettingsAsync(token);
        var weekStart = UpcomingWeekStart(timeProvider.GetUtcNow(), settings.TimeZoneId);
        return new WeeklyMaterialOrderReport(
            await repository.ReadAsync(weekStart, weekStart.AddDays(7), token));
    }

    internal async Task<WeeklyMaterialOrderReport> SendNowAsync(CancellationToken token = default)
    {
        var settings = await settingsService.GetReportEmailSettingsAsync(token);
        EnsureDeliveryConfigured(settings);
        var report = await GenerateAsync(token);
        await emailSender.SendAsync(settings, report, token);
        return report;
    }

    internal async Task<bool> SendIfDueAsync(CancellationToken token = default)
    {
        var settings = await settingsService.GetReportEmailSettingsAsync(token);
        if (!settings.WeeklyMaterialReportEnabled)
        {
            return false;
        }

        EnsureDeliveryConfigured(settings);
        var zone = Zone(settings.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone);
        var day = localNow.DayOfWeek.ToString().ToLowerInvariant();
        if (day != settings.WeeklyMaterialReportSendDay
            || localNow.TimeOfDay < TimeOnly.ParseExact(settings.WeeklyMaterialReportTimeLocal, "HH:mm").ToTimeSpan())
        {
            return false;
        }

        var weekStart = UpcomingWeekStart(timeProvider.GetUtcNow(), settings.TimeZoneId);
        var periodKey = weekStart.ToString("yyyy-MM-dd");
        if (await repository.WasAutomaticallySentAsync(periodKey, token))
        {
            return false;
        }

        var report = new WeeklyMaterialOrderReport(
            await repository.ReadAsync(weekStart, weekStart.AddDays(7), token));
        await emailSender.SendAsync(settings, report, token);
        await repository.MarkAutomaticallySentAsync(
            periodKey, settings.Recipients.Count, timeProvider.GetUtcNow(), token);
        return true;
    }

    private static DateOnly UpcomingWeekStart(DateTimeOffset now, string? timeZoneId)
    {
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone(timeZoneId)).Date);
        var days = ((int)DayOfWeek.Sunday - (int)localDate.DayOfWeek + 7) % 7;
        return localDate.AddDays(days == 0 ? 7 : days);
    }

    private static TimeZoneInfo Zone(string? id) =>
        TimeZoneInfo.FindSystemTimeZoneById(id ?? "Asia/Jerusalem");

    private static void EnsureDeliveryConfigured(ReportEmailSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SenderAddress)
            || settings.Recipients.Count == 0
            || string.IsNullOrWhiteSpace(settings.SmtpHost)
            || settings.SmtpPort is null)
        {
            throw new WeeklyMaterialReportDeliveryException(
                "Sender, recipients, SMTP host, and SMTP port must be configured before sending.");
        }
    }
}

internal sealed class WeeklyMaterialReportDeliveryException(string message) : Exception(message);
