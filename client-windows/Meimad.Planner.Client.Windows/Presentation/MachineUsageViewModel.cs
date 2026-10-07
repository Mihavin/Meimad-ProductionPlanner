using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>One choice of the history chart: every Machine together, or one Machine.</summary>
internal sealed record MachineUsageChartScope(string Label, string? MachineId)
{
    public override string ToString() => Label;
}

/// <summary>One choice of what counts as available time.</summary>
internal sealed record MachineUsageBasisOption(string Label, string Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// Reports → Machine usage: how each Machine's working time in a chosen period is used according to
/// the Timeline (production, setup, QC, part reload, reserved, hold, downtime, idle), the factory
/// totals, a daily history chart, a printable report and a CSV export. The Server calculates the
/// Timeline; everyone signed in may look and nothing is changed.
/// </summary>
internal sealed class MachineUsageViewModel : INotifyPropertyChanged
{
    internal const string AllMachines = "All machines";

    private readonly Action<string> openFile;
    private IPlannerApiClient? apiClient;
    private DateTime? from = DateTime.Today.AddDays(-6);
    private DateTime? to = DateTime.Today;
    private MachineUsageBasisOption basis;
    private bool isBusy;
    private string status = "Choose the period and press Calculate.";
    private MachineUsageReportInfo? report;
    private MachineUsageChartScope? chartScope;
    private MachineUsageMetricsInfo? totals;
    private IReadOnlyList<MachineUsageDayInfo> chartDays = [];

    internal MachineUsageViewModel(Action<string>? openFile = null)
    {
        this.openFile = openFile ?? (path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
        BasisOptions =
        [
            new MachineUsageBasisOption(MachineUsageText.Basis("schedule"), "schedule"),
            new MachineUsageBasisOption(MachineUsageText.Basis("fullDay"), "fullDay")
        ];
        basis = BasisOptions[0];
        CalculateCommand = new AsyncCommand(CalculateAsync, () => apiClient is not null && !IsBusy);
        PrintReportCommand = new AsyncCommand(PrintReportAsync, () => report is not null && !IsBusy);
        ExportCsvCommand = new AsyncCommand(ExportCsvAsync, () => report is not null && !IsBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MachineUsageRowInfo> Machines { get; } = [];

    /// <summary>The factory totals per day, one row per day of the period.</summary>
    public ObservableCollection<MachineUsageDayInfo> Days { get; } = [];

    public ObservableCollection<MachineUsageChartScope> ChartScopes { get; } = [];

    public IReadOnlyList<MachineUsageBasisOption> BasisOptions { get; }

    public AsyncCommand CalculateCommand { get; }
    public AsyncCommand PrintReportCommand { get; }
    public AsyncCommand ExportCsvCommand { get; }

    public DateTime? From
    {
        get => from;
        set => SetField(ref from, value);
    }

    public DateTime? To
    {
        get => to;
        set => SetField(ref to, value);
    }

    public MachineUsageBasisOption Basis
    {
        get => basis;
        set => SetField(ref basis, value ?? BasisOptions[0]);
    }

    public string Status
    {
        get => status;
        private set => SetField(ref status, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetField(ref isBusy, value)) return;
            CalculateCommand.RaiseCanExecuteChanged();
            PrintReportCommand.RaiseCanExecuteChanged();
            ExportCsvCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Overall performance: every Machine together over the period.</summary>
    public MachineUsageMetricsInfo? Totals
    {
        get => totals;
        private set
        {
            if (!SetField(ref totals, value)) return;
            OnPropertyChanged(nameof(HasReport));
        }
    }

    public bool HasReport => totals is not null;

    public MachineUsageChartScope? ChartScope
    {
        get => chartScope;
        set
        {
            if (!SetField(ref chartScope, value)) return;
            ChartDays = DaysOf(value);
        }
    }

    /// <summary>The days the history chart shows: the factory totals or one Machine's days.</summary>
    public IReadOnlyList<MachineUsageDayInfo> ChartDays
    {
        get => chartDays;
        private set => SetField(ref chartDays, value);
    }

    internal MachineUsageReportInfo? Report => report;

    /// <summary>The day the report was calculated; the chart marks it, and later days are forecast.</summary>
    public DateTime? Today => report?.CalculatedAt.ToLocalTime().Date;

    internal void AttachSession(IPlannerApiClient? client)
    {
        apiClient = client;
        CalculateCommand.RaiseCanExecuteChanged();
    }

    internal async Task CalculateAsync()
    {
        if (apiClient is null) return;
        if (From is not DateTime start || To is not DateTime end)
        {
            Status = "Choose the first and the last day of the period.";
            return;
        }
        if (end.Date < start.Date)
        {
            Status = "The last day is before the first day.";
            return;
        }

        IsBusy = true;
        Status = "Calculating the Timeline...";
        try
        {
            var result = await apiClient.GetMachineUsageAsync(
                DateOnly.FromDateTime(start), DateOnly.FromDateTime(end), Basis.Value);
            report = result;
            Replace(Machines, result.Machines);
            Replace(Days, result.Days);
            var selectedId = chartScope?.MachineId;
            Replace(ChartScopes, [new MachineUsageChartScope(AllMachines, null),
                .. result.Machines.Select(row => new MachineUsageChartScope(row.DisplayName, row.MachineId))]);
            Totals = result.Totals;
            OnPropertyChanged(nameof(Today));
            chartScope = null;
            ChartScope = ChartScopes.FirstOrDefault(scope => scope.MachineId == selectedId) ?? ChartScopes[0];
            var at = result.CalculatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            Status = $"{result.Machines.Count} machine(s); overall usage {result.Totals.UsageText} of {result.Totals.AvailableText}. According to the Timeline calculated at {at}: history before, forecast after.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            Status = Friendly(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Writes the report as a printable page and opens it (print it, or save it as PDF).</summary>
    internal Task PrintReportAsync()
    {
        if (report is null) return Task.CompletedTask;
        var localization = LocalizationService.Current;
        Write("html", MachineUsageReportDocument.Build(report, localization.Translate, localization.IsRightToLeft),
            "Report opened: {0}. Print it from the browser, or save it as PDF.");
        return Task.CompletedTask;
    }

    /// <summary>Writes every Machine and day as CSV rows (hours with a dot, invariant) and opens it.</summary>
    internal Task ExportCsvAsync()
    {
        if (report is null) return Task.CompletedTask;
        Write("csv", BuildCsv(report), "Exported: {0}.");
        return Task.CompletedTask;
    }

    internal static string BuildCsv(MachineUsageReportInfo report)
    {
        static string H(long seconds) => (seconds / 3600.0).ToString("0.00", CultureInfo.InvariantCulture);
        static string P(decimal? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty;
        static string Q(string value) => value.IndexOfAny([',', '"', '\n', '\r']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
        var csv = new StringBuilder();
        csv.AppendLine("Date,Machine number,Machine,Available h,Production h,Setup h,QC h,Part reload h,Reserved h,Hold h,Downtime h,Idle h,Outside schedule h,Used h,Usage %,Production %,Setup %,Idle %");
        void Row(string date, string number, string name, MachineUsageMetricsInfo m) =>
            csv.AppendLine(string.Join(",", Q(date), Q(number), Q(name),
                H(m.AvailableSeconds), H(m.ProductionSeconds), H(m.SetupSeconds), H(m.QcSeconds), H(m.PartReloadSeconds),
                H(m.ReservedSeconds), H(m.HoldSeconds), H(m.DowntimeSeconds), H(m.IdleSeconds),
                H(m.OutsideScheduleSeconds), H(m.UsedSeconds),
                P(m.UsagePercent), P(m.ProductionPercent), P(m.SetupPercent), P(m.IdlePercent)));
        var period = $"{report.From:yyyy-MM-dd}..{report.To:yyyy-MM-dd}";
        foreach (var machine in report.Machines)
        {
            foreach (var day in machine.Days)
                Row(day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), machine.Number, machine.Name, day.Metrics);
            Row(period, machine.Number, machine.Name, machine.Metrics);
        }
        foreach (var day in report.Days)
            Row(day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), string.Empty, AllMachines, day.Metrics);
        Row(period, string.Empty, AllMachines, report.Totals);
        return csv.ToString();
    }

    private void Write(string extension, string content, string message)
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "MeimadPlanner", "Reports");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"machine-usage-{report!.From:yyyy-MM-dd}-{report.To:yyyy-MM-dd}.{extension}");
            File.WriteAllText(path, content, new UTF8Encoding(true));
            openFile(path);
            Status = string.Format(CultureInfo.CurrentCulture, message, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Status = $"The report could not be opened: {exception.Message}";
        }
    }

    private IReadOnlyList<MachineUsageDayInfo> DaysOf(MachineUsageChartScope? scope) =>
        report is null ? []
        : scope?.MachineId is { } machineId
            ? report.Machines.FirstOrDefault(row => row.MachineId == machineId)?.Days ?? []
            : report.Days;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private static bool IsExpected(Exception exception) =>
        exception is PlannerApiException or PlannerProtocolException or HttpRequestException or TaskCanceledException;

    private static string Friendly(Exception exception) => exception switch
    {
        TaskCanceledException => "The Server did not respond before the client timeout.",
        HttpRequestException => "The configured Server could not be reached.",
        _ => exception.Message
    };

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
