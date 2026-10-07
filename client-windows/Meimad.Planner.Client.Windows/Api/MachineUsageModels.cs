using System.Globalization;

namespace Meimad.Planner.Client.Windows.Api;

/// <summary>
/// Recorded Machine usage from the Server (GET /api/v1/reports/machine-usage) for whole factory
/// days; only time up to <see cref="CountedUntil"/> counts.
/// </summary>
internal sealed record MachineUsageReportInfo(
    DateOnly From,
    DateOnly To,
    string Basis,
    string TimeZoneId,
    DateTimeOffset CalculatedAt,
    DateTimeOffset CountedUntil,
    MachineUsageMetricsInfo Totals,
    IReadOnlyList<MachineUsageDayInfo> Days,
    IReadOnlyList<MachineUsageRowInfo> Machines);

/// <summary>
/// Exclusive kinds of the available time: production + setup + downtime + no data + idle =
/// available. Work outside the available time is counted apart and never raises a percentage.
/// </summary>
internal sealed record MachineUsageMetricsInfo(
    long AvailableSeconds,
    long ProductionSeconds,
    long SetupSeconds,
    long DowntimeSeconds,
    long NoDataSeconds,
    long IdleSeconds,
    long OutsideScheduleSeconds,
    long UsedSeconds,
    decimal? UsagePercent,
    decimal? ProductionPercent,
    decimal? SetupPercent,
    decimal? DowntimePercent,
    decimal? NoDataPercent,
    decimal? IdlePercent)
{
    public string AvailableText => MachineUsageText.Hours(AvailableSeconds);
    public string ProductionText => MachineUsageText.HoursAndPercent(ProductionSeconds, ProductionPercent);
    public string SetupText => MachineUsageText.HoursAndPercent(SetupSeconds, SetupPercent);
    public string DowntimeText => MachineUsageText.HoursAndPercent(DowntimeSeconds, DowntimePercent);
    public string NoDataText => MachineUsageText.HoursAndPercent(NoDataSeconds, NoDataPercent);
    public string IdleText => MachineUsageText.HoursAndPercent(IdleSeconds, IdlePercent);
    public string OutsideScheduleText => MachineUsageText.Hours(OutsideScheduleSeconds);
    public string UsedText => MachineUsageText.Hours(UsedSeconds);
    public string UsageText => MachineUsageText.Percent(UsagePercent);
    public string UsageLevel => MachineUsageText.Level(UsagePercent, AvailableSeconds);
    public string UsageLevelText => MachineUsageText.LevelText(UsageLevel);
}

internal sealed record MachineUsageDayInfo(DateOnly Date, MachineUsageMetricsInfo Metrics)
{
    public string DateText => Date.ToString("ddd dd/MM", CultureInfo.CurrentCulture);
}

internal sealed record MachineUsageRowInfo(
    string MachineId,
    string Number,
    string Name,
    string DataSource,
    MachineUsageMetricsInfo Metrics,
    IReadOnlyList<MachineUsageDayInfo> Days)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Number) || Number == Name ? Name : $"{Number} - {Name}";
    public string DataSourceText => MachineUsageText.DataSource(DataSource);
}

/// <summary>Display text for usage values; every usage level is spelled out, not only coloured.</summary>
internal static class MachineUsageText
{
    internal static string Hours(long seconds) =>
        (seconds / 3600.0).ToString("0.0", CultureInfo.CurrentCulture) + " h";

    internal static string Percent(decimal? value) =>
        value is decimal percent ? percent.ToString("0.0", CultureInfo.CurrentCulture) + " %" : "-";

    internal static string HoursAndPercent(long seconds, decimal? percent) =>
        percent is null ? Hours(seconds) : $"{Hours(seconds)} ({Percent(percent)})";

    /// <summary><c>none</c> (no available time), <c>low</c> (under 40 %), <c>normal</c> or <c>high</c> (75 % or more).</summary>
    internal static string Level(decimal? usagePercent, long availableSeconds) => availableSeconds == 0
        ? "none"
        : usagePercent switch { >= 75 => "high", >= 40 => "normal", _ => "low" };

    internal static string LevelText(string level) => level switch
    {
        "high" => "High use",
        "normal" => "Normal use",
        "low" => "Low use",
        _ => "No available time"
    };

    internal static string DataSource(string source) => source switch
    {
        "cnc" => "CNC monitoring",
        "manual" => "Manual reports",
        _ => source
    };

    internal static string Basis(string basis) => basis switch
    {
        "fullDay" => "Whole day (24 h)",
        _ => "Working calendar"
    };
}
