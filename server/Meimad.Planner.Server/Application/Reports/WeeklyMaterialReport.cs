using Meimad.Planner.Server.Domain.AdministrativeSetup;

namespace Meimad.Planner.Server.Application.Reports;

internal sealed record WeeklyMaterialReportItem(
    string CaseId,
    string PartNumber,
    long RequiredMaterialPieceQuantity);

internal sealed record WeeklyMaterialOrderReport(IReadOnlyList<WeeklyMaterialReportItem> Items);

internal interface IWeeklyMaterialReportRepository
{
    Task<IReadOnlyList<WeeklyMaterialReportItem>> ReadAsync(
        DateOnly weekStart,
        DateOnly weekEndExclusive,
        CancellationToken token);
    Task<bool> WasAutomaticallySentAsync(string periodKey, CancellationToken token);
    Task MarkAutomaticallySentAsync(string periodKey, int recipientCount, DateTimeOffset sentAt, CancellationToken token);
}

internal interface IMaterialReportEmailSender
{
    Task SendAsync(ReportEmailSettings settings, WeeklyMaterialOrderReport report, CancellationToken token);
}
