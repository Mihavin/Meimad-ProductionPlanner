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
    /// <summary>The kinds of the history chart, bottom to top, with the functional-spec palette.</summary>
    internal static readonly IReadOnlyList<(string Name, string Color, Func<MachineUsageMetricsInfo, long> Seconds)> Kinds =
    [
        ("Production", "#1E88E5", metrics => metrics.ProductionSeconds),
        ("Machine setup", "#FBC02D", metrics => metrics.SetupSeconds),
        ("Downtime", "#C62828", metrics => metrics.DowntimeSeconds),
        ("Idle", "#9E9E9E", metrics => metrics.IdleSeconds),
        ("No data", "#E0E0E0", metrics => metrics.NoDataSeconds)
    ];

    internal static string Build(MachineUsageReportInfo report, Func<string, string> translate, bool rightToLeft)
    {
        string T(string value) => WebUtility.HtmlEncode(translate(value));
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var culture = CultureInfo.CurrentCulture;
        var totals = report.Totals;
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
            {{T("Counted until")}} {{E(report.CountedUntil.ToLocalTime().ToString("g", culture))}}.
            {{T("Calculated at")}} {{E(report.CalculatedAt.ToLocalTime().ToString("g", culture))}}.</p>
            <p class="meta">{{T("Usage is production plus setup over the available time. Production is the CNC running state on monitored machines and the reported production sessions on the others; setup runs from the Offset Loader or reported setup run to Send to QC; no data is time a monitored machine sent no state; idle is the rest. Work outside the available time is shown apart and does not raise the percentages.")}}</p>
            <h2>{{T("Overall performance")}}</h2>
            <div class="kpis">
              <div class="kpi">{{T("Usage")}}<b>{{E(totals.UsageText)}}</b>{{E(totals.UsedText)}} / {{E(totals.AvailableText)}}</div>
              <div class="kpi">{{T("Production")}}<b>{{E(MachineUsageText.Hours(totals.ProductionSeconds))}}</b>{{E(MachineUsageText.Percent(totals.ProductionPercent))}}</div>
              <div class="kpi">{{T("Total setup time")}}<b>{{E(MachineUsageText.Hours(totals.SetupSeconds))}}</b>{{E(MachineUsageText.Percent(totals.SetupPercent))}}</div>
              <div class="kpi">{{T("Total idle time")}}<b>{{E(MachineUsageText.Hours(totals.IdleSeconds))}}</b>{{E(MachineUsageText.Percent(totals.IdlePercent))}}</div>
              <div class="kpi">{{T("Downtime")}}<b>{{E(MachineUsageText.Hours(totals.DowntimeSeconds))}}</b>{{E(MachineUsageText.Percent(totals.DowntimePercent))}}</div>
              <div class="kpi">{{T("No data")}}<b>{{E(MachineUsageText.Hours(totals.NoDataSeconds))}}</b>{{E(MachineUsageText.Percent(totals.NoDataPercent))}}</div>
              <div class="kpi">{{T("Outside schedule")}}<b>{{E(totals.OutsideScheduleText)}}</b></div>
            </div>
            <h2>{{T("Usage per machine")}}</h2>
            """);
        AppendMetricsHeader(html, T, T("Machine"), T("Data source"));
        foreach (var row in report.Machines)
            AppendMetricsRow(html, T, E(row.DisplayName), T(row.DataSourceText), row.Metrics, total: false);
        AppendMetricsRow(html, T, T("All machines"), string.Empty, totals, total: true);
        html.Append("</table>");

        html.Append($"<h2>{T("Daily history")}</h2>");
        html.Append(Chart(report.Days, T));
        AppendMetricsHeader(html, T, T("Day"), null);
        foreach (var day in report.Days)
            AppendMetricsRow(html, T, E(day.DateText), null, day.Metrics, total: false);
        html.Append("</table>");

        if (report.Days.Count > 1)
        {
            html.Append($"<h2>{T("Daily history per machine")}</h2>");
            foreach (var row in report.Machines)
            {
                html.Append($"""<div class="machine"><h3>{E(row.DisplayName)} - {E(row.Metrics.UsageText)} <span class="level {row.Metrics.UsageLevel}">{T(row.Metrics.UsageLevelText)}</span></h3>""");
                html.Append(Chart(row.Days, T));
                html.Append("</div>");
            }
        }
        html.Append("</body></html>");
        return html.ToString();
    }

    private static void AppendMetricsHeader(StringBuilder html, Func<string, string> t, string first, string? second)
    {
        html.Append($"<table><tr><th>{first}</th>");
        if (second is not null) html.Append($"<th>{second}</th>");
        html.Append($"<th>{t("Available time")}</th><th>{t("Production")}</th><th>{t("Machine setup")}</th><th>{t("Downtime")}</th><th>{t("No data")}</th><th>{t("Idle")}</th><th>{t("Outside schedule")}</th><th>{t("Used")}</th><th>{t("Usage")}</th><th>{t("Level")}</th></tr>");
    }

    private static void AppendMetricsRow(
        StringBuilder html, Func<string, string> t, string first, string? second, MachineUsageMetricsInfo m, bool total)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        html.Append(total ? "<tr class=\"total\">" : "<tr>");
        html.Append($"<td>{first}</td>");
        if (second is not null) html.Append($"<td>{second}</td>");
        html.Append($"""
            <td class="number">{E(m.AvailableText)}</td><td class="number">{E(m.ProductionText)}</td><td class="number">{E(m.SetupText)}</td>
            <td class="number">{E(m.DowntimeText)}</td><td class="number">{E(m.NoDataText)}</td><td class="number">{E(m.IdleText)}</td>
            <td class="number">{E(m.OutsideScheduleText)}</td><td class="number">{E(m.UsedText)}</td>
            <td class="number {m.UsageLevel}">{E(m.UsageText)}</td><td class="level {m.UsageLevel}">{t(m.UsageLevelText)}</td></tr>
            """);
    }

    /// <summary>Stacked hours per day with the usage percentage written above each bar.</summary>
    internal static string Chart(IReadOnlyList<MachineUsageDayInfo> days, Func<string, string> t)
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
        svg.Append("""<defs><pattern id="nodata" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="6" height="6" fill="#E0E0E0"/><line x1="0" y1="0" x2="0" y2="6" stroke="#9E9E9E" stroke-width="2"/></pattern></defs>""");
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
                var fill = name == "No data" ? "url(#nodata)" : color;
                svg.Append(CultureInfo.InvariantCulture,
                    $"""<rect x="{x:0.#}" y="{Y(stacked + hours):0.#}" width="{barWidth:0.#}" height="{Y(stacked) - Y(stacked + hours):0.#}" fill="{fill}" stroke="#555" stroke-width="0.5"><title>{WebUtility.HtmlEncode(day.DateText)}: {t(name)} {hours:0.0} h</title></rect>""");
                stacked += hours;
            }
            svg.Append(CultureInfo.InvariantCulture,
                $"""<text x="{x + barWidth / 2:0.#}" y="{Y(stacked) - 4:0.#}" font-size="10" text-anchor="middle">{WebUtility.HtmlEncode(day.Metrics.UsageText)}</text>""");
            svg.Append(CultureInfo.InvariantCulture,
                $"""<text x="{x + barWidth / 2:0.#}" y="{top + height + 14:0.#}" font-size="10" text-anchor="middle">{WebUtility.HtmlEncode(day.Date.ToString("dd/MM", CultureInfo.CurrentCulture))}</text>""");
        }
        svg.Append("</svg>");
        var legend = string.Concat(Kinds.Select(kind =>
            $"""<span><i style="background:{(kind.Name == "No data" ? "repeating-linear-gradient(45deg,#E0E0E0 0 3px,#9E9E9E 3px 5px)" : kind.Color)}"></i>{t(kind.Name)}</span>"""));
        return $"""<div class="legend">{legend}<span>{t("Usage % above each bar")}</span></div>{svg}""";
    }
}
