using System.Globalization;

namespace Meimad.Planner.Client.Windows.Api;

/// <summary>
/// Planned Employee load from the Server's Timeline (GET /api/v1/resources/workload) for whole
/// factory days; only time from <see cref="CountedFrom"/> on counts.
/// </summary>
internal sealed record EmployeeWorkloadReportInfo(
    DateOnly From,
    DateOnly To,
    DateTimeOffset CalculatedAt,
    DateTimeOffset CountedFrom,
    string TimeZoneId,
    IReadOnlyList<EmployeeWorkloadRowInfo> Employees);

internal sealed record EmployeeWorkloadRowInfo(
    string EmployeeId,
    string EmployeeNumber,
    string Name,
    string Role,
    long AvailableSeconds,
    long SetupSeconds,
    long QaSeconds,
    long LoadUnloadSeconds,
    long StationStepSeconds,
    long BookedSeconds,
    decimal? LoadPercent,
    string LoadLevel,
    IReadOnlyList<EmployeeWorkloadDayInfo> Days,
    IReadOnlyList<EmployeeWorkloadItemInfo> Work)
{
    public string RoleText => EmployeeWorkloadText.Role(Role);
    public string AvailableText => EmployeeWorkloadText.Hours(AvailableSeconds);
    public string SetupText => EmployeeWorkloadText.Hours(SetupSeconds);
    public string QaText => EmployeeWorkloadText.Hours(QaSeconds);
    public string LoadUnloadText => EmployeeWorkloadText.Hours(LoadUnloadSeconds);
    public string StationStepText => EmployeeWorkloadText.Hours(StationStepSeconds);
    public string BookedText => EmployeeWorkloadText.Hours(BookedSeconds);
    public string LoadText => EmployeeWorkloadText.Percent(LoadPercent);
    public string LevelText => EmployeeWorkloadText.Level(LoadLevel);
}

internal sealed record EmployeeWorkloadDayInfo(DateOnly Date, long AvailableSeconds, long BookedSeconds, decimal? LoadPercent)
{
    public string DateText => Date.ToString("ddd dd/MM", CultureInfo.CurrentCulture);
    public string AvailableText => EmployeeWorkloadText.Hours(AvailableSeconds);
    public string BookedText => EmployeeWorkloadText.Hours(BookedSeconds);
    public string LoadText => EmployeeWorkloadText.Percent(LoadPercent);
}

internal sealed record EmployeeWorkloadItemInfo(
    string Kind,
    string? BatchNumber,
    string? PartNumber,
    int? OperationNumber,
    string? Name,
    long Seconds,
    DateTimeOffset FirstStart,
    DateTimeOffset LastEnd)
{
    public string KindText => EmployeeWorkloadText.Kind(Kind);
    public string WorkText => string.Join(" ", new[]
    {
        BatchNumber is null ? null : $"WO {BatchNumber}",
        PartNumber,
        OperationNumber is int number ? $"OP{number:00}" : null,
        Name
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string HoursText => EmployeeWorkloadText.Hours(Seconds);
    public string WhenText =>
        $"{FirstStart.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} - {LastEnd.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";
}

/// <summary>Display text for workload values; every load level is spelled out, not only coloured.</summary>
internal static class EmployeeWorkloadText
{
    internal static string Hours(long seconds) =>
        (seconds / 3600.0).ToString("0.0", CultureInfo.CurrentCulture) + " h";

    internal static string Percent(decimal? value) =>
        value is decimal percent ? percent.ToString("0.0", CultureInfo.CurrentCulture) + " %" : "-";

    internal static string Level(string level) => level switch
    {
        "over" => "Overbooked",
        "full" => "Fully booked",
        "high" => "High",
        "normal" => "Normal",
        "low" => "Low",
        _ => "No working time"
    };

    internal static string Role(string role) => role switch
    {
        "setup_worker" => "Setup worker",
        "qa_worker" => "QA worker",
        "regular_worker" => "Regular worker",
        _ => role
    };

    internal static string Kind(string kind) => kind switch
    {
        "setup" => "Machine setup",
        "qa" => "QA",
        "load_unload" => "Load/unload",
        "station_step" => "Station step",
        _ => kind
    };
}
