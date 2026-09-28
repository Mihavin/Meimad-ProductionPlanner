using System.Globalization;
using System.Net;
using System.Text;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// The Employee workload report as one self-contained, printable HTML page: a summary per
/// Employee, the load per day and the work booked on each Employee. Every load level is written as
/// text next to its colour, and the page prints in black and white as well.
/// </summary>
internal static class EmployeeWorkloadReportDocument
{
    internal static string Build(EmployeeWorkloadReportInfo report, Func<string, string> translate, bool rightToLeft)
    {
        string T(string value) => WebUtility.HtmlEncode(translate(value));
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var culture = CultureInfo.CurrentCulture;
        var html = new StringBuilder();
        html.Append($$"""
            <!doctype html>
            <html lang="{{(rightToLeft ? "he" : culture.TwoLetterISOLanguageName)}}" dir="{{(rightToLeft ? "rtl" : "ltr")}}">
            <head>
            <meta charset="utf-8">
            <title>{{T("Employee workload")}} {{report.From:yyyy-MM-dd}} - {{report.To:yyyy-MM-dd}}</title>
            <style>
              body { font-family: "Segoe UI", Arial, sans-serif; color: #111; margin: 24px; font-size: 12px; }
              h1 { font-size: 20px; margin: 0 0 4px; }
              h2 { font-size: 15px; margin: 22px 0 6px; border-bottom: 1px solid #999; padding-bottom: 2px; }
              h3 { font-size: 13px; margin: 14px 0 4px; }
              p.meta { color: #444; margin: 0 0 12px; }
              table { border-collapse: collapse; width: 100%; margin-bottom: 8px; }
              th, td { border: 1px solid #bbb; padding: 3px 6px; text-align: start; vertical-align: top; }
              th { background: #eef1f4; }
              td.number { text-align: end; white-space: nowrap; }
              .level { font-weight: 600; white-space: nowrap; }
              .over, .full { background: #fde2e1; }
              .high { background: #fff3cd; }
              .normal { background: #e3f4e6; }
              .low, .none { background: #f2f2f2; }
              .employee { page-break-inside: avoid; }
              @media print { body { margin: 10mm; } h2 { page-break-after: avoid; } }
            </style>
            </head>
            <body>
            <h1>{{T("Employee workload")}}</h1>
            <p class="meta">{{T("Period")}}: {{report.From.ToString("d", culture)}} - {{report.To.ToString("d", culture)}}.
            {{T("Planned load from the Timeline, counted from")}} {{E(report.CountedFrom.ToLocalTime().ToString("g", culture))}}.
            {{T("Calculated at")}} {{E(report.CalculatedAt.ToLocalTime().ToString("g", culture))}}.</p>
            <p class="meta">{{T("Booked time is the setup, QA and load/unload the Timeline assigns to each employee and the station steps placed on them; working time is what the Timeline may book after calendars, breaks, holidays and absences.")}}</p>
            <h2>{{T("Summary")}}</h2>
            <table>
            <tr><th>{{T("Employee #")}}</th><th>{{T("Name")}}</th><th>{{T("Role")}}</th><th>{{T("Working time")}}</th><th>{{T("Setup time")}}</th><th>{{T("QA")}}</th><th>{{T("Load/unload")}}</th><th>{{T("Station steps")}}</th><th>{{T("Booked")}}</th><th>{{T("Load")}}</th><th>{{T("Level")}}</th></tr>
            """);
        foreach (var row in report.Employees)
        {
            html.Append($"""
                <tr><td>{E(row.EmployeeNumber)}</td><td>{E(row.Name)}</td><td>{T(row.RoleText)}</td>
                <td class="number">{E(row.AvailableText)}</td><td class="number">{E(row.SetupText)}</td><td class="number">{E(row.QaText)}</td>
                <td class="number">{E(row.LoadUnloadText)}</td><td class="number">{E(row.StationStepText)}</td><td class="number">{E(row.BookedText)}</td>
                <td class="number {row.LoadLevel}">{E(row.LoadText)}</td><td class="level {row.LoadLevel}">{T(row.LevelText)}</td></tr>
                """);
        }
        html.Append("</table>");

        var dates = report.Employees.SelectMany(row => row.Days.Select(day => day.Date)).Distinct().Order().ToArray();
        if (dates.Length > 0)
        {
            html.Append($"<h2>{T("Load per day")}</h2><table><tr><th>{T("Name")}</th>");
            foreach (var date in dates) html.Append($"<th>{E(date.ToString("ddd dd/MM", culture))}</th>");
            html.Append("</tr>");
            foreach (var row in report.Employees)
            {
                html.Append($"<tr><td>{E(row.Name)}</td>");
                foreach (var date in dates)
                {
                    var day = row.Days.FirstOrDefault(value => value.Date == date);
                    var level = day is null || day.AvailableSeconds == 0
                        ? day is { BookedSeconds: > 0 } ? "over" : "none"
                        : day.LoadPercent switch { > 100 => "over", >= 100 => "full", >= 85 => "high", >= 30 => "normal", _ => "low" };
                    html.Append(day is null
                        ? "<td></td>"
                        : $"""<td class="number {level}">{E(day.LoadText)}<br>{E(day.BookedText)} / {E(day.AvailableText)}</td>""");
                }
                html.Append("</tr>");
            }
            html.Append("</table>");
        }

        html.Append($"<h2>{T("Work per employee")}</h2>");
        foreach (var row in report.Employees.Where(value => value.Work.Count > 0))
        {
            html.Append($"""
                <div class="employee"><h3>{E(row.Name)} ({E(row.EmployeeNumber)}) - {E(row.BookedText)}, {E(row.LoadText)} <span class="level {row.LoadLevel}">{T(row.LevelText)}</span></h3>
                <table><tr><th>{T("Kind")}</th><th>{T("Work")}</th><th>{T("Hours")}</th><th>{T("When")}</th></tr>
                """);
            foreach (var item in row.Work)
            {
                html.Append($"""<tr><td>{T(item.KindText)}</td><td>{E(item.WorkText)}</td><td class="number">{E(item.HoursText)}</td><td>{E(item.WhenText)}</td></tr>""");
            }
            html.Append("</table></div>");
        }
        html.Append("</body></html>");
        return html.ToString();
    }
}
