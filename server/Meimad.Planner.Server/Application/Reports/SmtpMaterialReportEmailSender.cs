using System.Net.Mail;
using System.Text;
using Meimad.Planner.Server.Domain.AdministrativeSetup;

namespace Meimad.Planner.Server.Application.Reports;

internal sealed class SmtpMaterialReportEmailSender(ReportEmailSmtp smtp) : IMaterialReportEmailSender
{
    public async Task SendAsync(
        ReportEmailSettings settings,
        WeeklyMaterialOrderReport report,
        CancellationToken token)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(settings.SenderAddress!),
            Subject = "Weekly Material Order Report",
            Body = Body(report),
            IsBodyHtml = false
        };
        foreach (var recipient in settings.Recipients)
        {
            message.To.Add(recipient);
        }

        using var client = smtp.CreateClient(settings);
        await client.SendMailAsync(message, token);
    }

    internal static string Body(WeeklyMaterialOrderReport report)
    {
        var builder = new StringBuilder()
            .Append("Case / Part Number\tRequired Material Piece Quantity\r\n");
        foreach (var item in report.Items)
        {
            builder.Append(item.PartNumber).Append('\t')
                .Append(item.RequiredMaterialPieceQuantity).Append("\r\n");
        }
        return builder.ToString();
    }
}
