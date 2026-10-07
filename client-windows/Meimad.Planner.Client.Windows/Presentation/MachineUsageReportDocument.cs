using System.Globalization;
using System.Net;
using System.Text;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// The machine usage report as one self-contained, printable HTML page: overall performance, usage
/// per Machine, a daily history chart with its table, and the days of each Machine. Every colour
/// has its kind written next to it, and the page prints in black and white as well.
/// </summary>
internal static class MachineUsageReportDocument
{
    /// <summary>The kinds of the history chart, bottom to top, in the Timeline legend colours.</summary>
    internal static readonly IReadOnlyList<(string Name, string Color, Func<MachineUsageMetricsInfo, long> Seconds)> Kinds =
    [
        ("Production", "#1E88E5", metrics => metrics.ProductionSeconds),
        ("Machine setup", "#FBC02D", metrics => metrics.SetupSeconds),
        ("QC", "#43A047", metrics => metrics.QcSeconds),
        ("Part reload", "#7B1FA2", metrics => metrics.PartReloadSeconds),
        ("Reserved", "#F57C00", metrics => metrics.ReservedSeconds),
        ("Hold", "#7E57C2", metrics => metrics.HoldSeconds),
        ("Downtime", "#C62828", metrics => metrics.DowntimeSeconds),
        ("Idle", "#E0E0E0", metrics => metrics.IdleSeconds)
    ];

    internal static string Build(MachineUsageReportInfo report, Func<string, string> translate, bool rightToLeft)
    {
        string T(string value) => WebUtility.HtmlEncode(translate(value));
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var culture = CultureInfo.CurrentCulture;
        var totals = report.Totals;
        var today = DateOnly.FromDateTime(report.CalculatedAt.ToLocalTime().DateTime);
        var html = new StringBuilder();
        html.Append($$"""
            <!doctype html>
            <html lang="{{(rightToLeft ? "he" : culture.TwoLetterISOLanguageName)}}" dir="{{(rightToLeft ? "rtl" : "ltr")}}">
            <head>
            <meta charset="utf-8">
            <title>{{T("Machine usage")}} {{report.From:yyyy-MM-dd}} - {{report.To:yyyy-MM-dd}}</title>
            <style>
              body { font-family: "Segoe UI", Arial, sans-serif; color: #111; margin: 24px; font-size: 12px; }
              h1 { font-size: 20px; margin: 0 0 4px; }
              h2 { font-size: 15px; margin: 22px 0 6px; border-bottom: 1px solid #999; padding-bottom: 2px; }
              h3 { font-size: 13px; margin: 14px 0 4px; }
              p.meta { color: #444; margin: 0 0 8px; }
              table { border-collapse: collapse; width: 100%; margin-bottom: 8px; }
              th, td { border: 1px solid #bbb; padding: 3px 6px; text-align: start; vertical-align: top; }
              th { background: #eef1f4; }
              td.number { text-align: end; white-space: nowrap; }
              tr.total td { font-weight: 600; background: #f6f6f6; }
              .kpis { display: flex; flex-wrap: wrap; gap: 8px; margin: 6px 0 4px; }
              .kpi { border: 1px solid #bbb; padding: 6px 10px; min-width: 120px; }
              .kpi b { display: block; font-size: 16px; }
              .level { font-weight: 600; white-space: nowrap; }
              .high { background: #e3f4e6; }
              .normal { background: #fff3cd; }
              .low { background: #fde2e1; }
              .none { background: #f2f2f2; }
              .legend span { display: inline-block; margin-inline-end: 14px; }
              .legend i { display: inline-block; width: 12px; height: 12px; border: 1px solid #555; margin-inline-end: 4px; vertical-align: middle; }
              .machine { page-break-inside: avoid; }
              svg text { font-family: "Segoe UI", Arial, sans-serif; }
              @media print { body { margin: 10mm; } h2 { page-break-after: avoid; } }
            </style>
            </head>
            <body>
            <h1>{{T("Machine usage")}}</h1>
            <p class="meta">{{T("Period")}}: {{report.From.ToString("d", culture)}} - {{report.To.ToString("d", culture)}}.
            {{T("Available time")}}: {{T(MachineUsageText.Basis(report.Basis))}}.
            {{T("Calculated at")}} {{E(report.CalculatedAt.ToLocalTime().ToString("g", culture))}}.</p>
            <p class="meta">{{T("Usage follows the Timeline: the time its bars occupy each machine (production, setup, QC, part reload and reserved) over the machine's working time. Before now the Timeline shows the recorded actual work as production; after now it shows the forecast. Hold, downtime and idle time are not usage. Work outside the working time is shown apart and does not raise the percentages.")}}</p>
            <h2>{{T("Overall performance")}}</h2>
            <div class="kpis">
              <div class="kpi">{{T("Usage")}}<b>{{E(totals.UsageText)}}</b>{{E(totals.UsedText)}} / {{E(totals.AvailableText)}}</div>
              <div class="kpi">{{T("Production")}}<b>{{E(MachineUsageText.Hours(totals.ProductionSeconds))}}</b>{{E(MachineUsageText.Percent(totals.ProductionPercent))}}</div>
              <div class="kpi">{{T("Total setup time")}}<b>{{E(MachineUsageText.Hours(totals.SetupSeconds))}}</b>{{E(MachineUsageText.Percent(totals.SetupPercent))}}</div>
              <div class="kpi">{{T("Total idle time")}}<b>{{E(MachineUsageText.Hours(totals.IdleSeconds))}}</b>{{E(MachineUsageText.Percent(totals.IdlePercent))}}</div>
              <div class="kpi">{{T("QC")}}<b>{{E(MachineUsageText.Hours(totals.QcSeconds))}}</b>{{E(MachineUsageText.Percent(totals.QcPercent))}}</div>
              <div class="kpi">{{T("Part reload")}}<b>{{E(MachineUsageText.Hours(totals.PartReloadSeconds))}}</b>{{E(MachineUsageText.Percent(totals.PartReloadPercent))}}</div>
              <div class="kpi">{{T("Downtime")}}<b>{{E(MachineUsageText.Hours(totals.DowntimeSeconds))}}</b>{{E(MachineUsageText.Percent(totals.DowntimePercent))}}</div>
              <div class="kpi">{{T("Hold")}}<b>{{E(MachineUsageText.Hours(totals.HoldSeconds))}}</b>{{E(MachineUsageText.Percent(totals.HoldPercent))}}</div>
              <div class="kpi">{{T("Outside schedule")}}<b>{{E(totals.OutsideScheduleText)}}</b></div>
            </div>
            <h2>{{T("Usage per machine")}}</h2>
            """);
        AppendMetricsHeader(html, T, T("Machine"));
        foreach (var row in report.Machines)
            AppendMetricsRow(html, T, E(row.DisplayName), row.Metrics, total: false);
        AppendMetricsRow(html, T, T("All machines"), totals, total: true);
        html.Append("</table>");

        html.Append($"<h2>{T("Daily history")}</h2>");
        html.Append(Chart(report.Days, today, T));
        AppendMetricsHeader(html, T, T("Day"));
        foreach (var day in report.Days)
            AppendMetricsRow(html, T, E(day.DateText), day.Metrics, total: false);
        html.Append("</table>");

        if (report.Days.Count > 1)
        {
            html.Append($"<h2>{T("Daily history per machine")}</h2>");
            foreach (var row in report.Machines)
            {
                html.Append($"""<div class="machine"><h3>{E(row.DisplayName)} - {E(row.Metrics.UsageText)} <span class="level {row.Metrics.UsageLevel}">{T(row.Metrics.UsageLevelText)}</span></h3>""");
                html.Append(Chart(row.Days, today, T));
                html.Append("</div>");
            }
        }
        html.Append("</body></html>");
        return html.ToString();
    }

    private static void AppendMetricsHeader(StringBuilder html, Func<string, string> t, string first) =>
        html.Append($"<table><tr><th>{first}</th><th>{t("Available time")}</th><th>{t("Production")}</th><th>{t("Machine setup")}</th><th>{t("QC")}</th><th>{t("Part reload")}</th><th>{t("Reserved")}</th><th>{t("Hold")}</th><th>{t("Downtime")}</th><th>{t("Idle")}</th><th>{t("Outside schedule")}</th><th>{t("Used")}</th><th>{t("Usage")}</th><th>{t("Level")}</th></tr>");

    private static void AppendMetricsRow(
        StringBuilder html, Func<string, string> t, string first, MachineUsageMetricsInfo m, bool total)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        html.Append(total ? "<tr class=\"total\">" : "<tr>");
        html.Append($"""
            <td>{first}</td><td class="number">{E(m.AvailableText)}</td><td class="number">{E(m.ProductionText)}</td>
            <td class="number">{E(m.SetupText)}</td><td class="number">{E(m.QcText)}</td><td class="number">{E(m.PartReloadText)}</td>
            <td class="number">{E(m.ReservedText)}</td><td class="number">{E(m.HoldText)}</td><td class="number">{E(m.DowntimeText)}</td>
            <td class="number">{E(m.IdleText)}</td><td class="number">{E(m.OutsideScheduleText)}</td><td class="number">{E(m.UsedText)}</td>
            <td class="number {m.UsageLevel}">{E(m.UsageText)}</td><td class="level {m.UsageLevel}">{t(m.UsageLevelText)}</td></tr>
            """);
    }

    /// <summary>
    /// Stacked hours per day with the usage percentage written above each bar and a "Now" line
    /// between the history and the forecast days.
    /// </summary>
    internal static string Chart(IReadOnlyList<MachineUsageDayInfo> days, DateOnly today, Func<string, string> t)
    {
        if (days.Count == 0) return string.Empty;
        const double height = 180, top = 22, bottom = 34, left = 44, right = 8;
        var slot = Math.Clamp(640.0 / days.Count, 14, 60);
        var width = left + right + slot * days.Count;
        var maxSeconds = Math.Max(3600, days.Max(day => Kinds.Sum(kind => kind.Seconds(day.Metrics))));
        var maxHours = Math.Ceiling(maxSeconds / 3600.0 / 4) * 4;
        double Y(double hours) => top + height * (1 - hours / maxHours);
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"""<svg xmlns="http://www.w3.org/2000/svg" width="{width:0}" height="{top + height + bottom:0}" role="img" aria-label="{t("Daily history")}">""");
        for (var tick = 0; tick <= 4; tick++)
        {
            var hours = maxHours * tick / 4;
            svg.Append(CultureInfo.InvariantCulture,
                $"""<line x1="{left}" x2="{width - right}" y1="{Y(hours):0.#}" y2="{Y(hours):0.#}" stroke="#ddd"/><text x="{left - 4}" y="{Y(hours) + 4:0.#}" font-size="10" text-anchor="end">{hours:0} h</text>""");
        }
        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index];
            var x = left + slot * index + slot * 0.15;
            var barWidth = slot * 0.7;
            double stacked = 0;
            foreach (var (name, color, seconds) in Kinds)
            {
                var hours = seconds(day.Metrics) / 3600.0;
                if (hours <= 0) continue;
                svg.Append(CultureInfo.InvariantCulture,
                    $"""<rect x="{x:0.#}" y="{Y(stacked + hours):0.#}" width="{barWidth:0.#}" height="{Y(stacked) - Y(stacked + hours):0.#}" fill="{color}" stroke="#555" stroke-width="0.5"><title>{WebUtility.HtmlEncode(day.DateText)}: {t(name)} {hours:0.0} h</title></rect>""");
                stacked += hours;
            }
            svg.Append(CultureInfo.InvariantCulture,
                $"""<text x="{x + barWidth / 2:0.#}" y="{Y(stacked) - 4:0.#}" font-size="10" text-anchor="middle">{WebUtility.HtmlEncode(day.Metrics.UsageText)}</text>""");
            svg.Append(CultureInfo.InvariantCulture,
                $"""<text x="{x + barWidth / 2:0.#}" y="{top + height + 14:0.#}" font-size="10" text-anchor="middle">{WebUtility.HtmlEncode(day.Date.ToString("dd/MM", CultureInfo.CurrentCulture))}</text>""");
            if (day.Date == today)
            {
                var nowX = left + slot * index;
                svg.Append(CultureInfo.InvariantCulture,
                    $"""<line x1="{nowX:0.#}" x2="{nowX:0.#}" y1="{top - 6:0.#}" y2="{top + height:0.#}" stroke="#C62828" stroke-dasharray="4 3"/><text x="{nowX + 3:0.#}" y="{top + height + 28:0.#}" font-size="10" fill="#C62828">{t("Today")} - {t("forecast after now")}</text>""");
            }
        }
        svg.Append("</svg>");
        var legend = string.Concat(Kinds.Select(kind => $"""<span><i style="background:{kind.Color}"></i>{t(kind.Name)}</span>"""));
        return $"""<div class="legend">{legend}<span>{t("Usage % above each bar")}</span></div>{svg}""";
    }
}
