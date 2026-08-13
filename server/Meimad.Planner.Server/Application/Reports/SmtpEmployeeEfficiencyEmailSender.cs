using System.Globalization;
using System.Net.Mail;
using System.Text;
using Meimad.Planner.Server.Domain.AdministrativeSetup;

namespace Meimad.Planner.Server.Application.Reports;

internal sealed class SmtpEmployeeEfficiencyEmailSender : IEmployeeEfficiencyEmailSender
{
    public async Task SendAsync(ReportEmailSettings settings, WeeklyEmployeeEfficiencyReport report, CancellationToken token)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(settings.SenderAddress!),
            Subject = $"Weekly Employee Efficiency Report — {report.WeekStart:yyyy-MM-dd}",
            Body = Body(report), IsBodyHtml = false
        };
        foreach (var recipient in settings.Recipients) message.To.Add(recipient);
        using var client = new SmtpClient(settings.SmtpHost!, settings.SmtpPort!.Value)
        { EnableSsl = settings.UseSsl, UseDefaultCredentials = false };
        await client.SendMailAsync(message, token);
    }

    internal static string Body(WeeklyEmployeeEfficiencyReport report)
    {
        var b = new StringBuilder()
            .Append("Employee\tRole\tPlanned\tActual\tDifference\tDifference %\tAvailable capacity\tPlanned capacity %\tActual capacity %\r\n");
        foreach (var item in report.Employees)
            b.Append(item.FirstName).Append(' ').Append(item.LastName).Append('\t')
             .Append(Role(item.Role)).Append('\t').Append(Duration(item.PlannedSeconds)).Append('\t')
             .Append(Duration(item.ActualSeconds)).Append('\t').Append(Duration(item.DifferenceSeconds)).Append('\t')
             .Append(Percent(item.PercentageDifference)).Append('\t').Append(Duration(item.AvailableCapacitySeconds)).Append('\t')
             .Append(Percent(item.PlannedCapacityPercent)).Append('\t').Append(Percent(item.ActualCapacityPercent)).Append("\r\n");
        return b.ToString();
    }

    private static string Role(string value) => value switch
    { "setup_worker" => "Setup worker", "qa_worker" => "QA worker", "regular_worker" => "Regular worker", _ => value };
    private static string Duration(long seconds)
    {
        var absolute = Math.Abs(seconds);
        return string.Create(CultureInfo.InvariantCulture,
            $"{(seconds < 0 ? "-" : string.Empty)}{absolute / 3600:00}:{absolute % 3600 / 60:00}:{absolute % 60:00}");
    }
    private static string Percent(decimal? value) => value.HasValue ? $"{value.Value:0.00}%" : "n/a";
}
