using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// Setup → Employees → Workload (owner decision 2026-09-28): the planned load of every active
/// Employee in a chosen period, calculated by the Server from the Timeline, and a printable report.
/// Everyone signed in may look; nothing is changed.
/// </summary>
internal sealed class EmployeeWorkloadViewModel : INotifyPropertyChanged
{
    private readonly Action<string> openReport;
    private IPlannerApiClient? apiClient;
    private DateTime? from = DateTime.Today;
    private DateTime? to = DateTime.Today.AddDays(13);
    private bool isBusy;
    private string status = "Choose the period and press Calculate.";
    private EmployeeWorkloadReportInfo? report;
    private EmployeeWorkloadRowInfo? selectedEmployee;

    internal EmployeeWorkloadViewModel(Action<string>? openReport = null)
    {
        this.openReport = openReport ?? (path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
        CalculateCommand = new AsyncCommand(CalculateAsync, () => apiClient is not null && !IsBusy);
        PrintReportCommand = new AsyncCommand(PrintReportAsync, () => report is not null && !IsBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<EmployeeWorkloadRowInfo> Employees { get; } = [];

    /// <summary>Per-day load of the selected Employee.</summary>
    public ObservableCollection<EmployeeWorkloadDayInfo> SelectedDays { get; } = [];

    /// <summary>The work booked on the selected Employee in the period.</summary>
    public ObservableCollection<EmployeeWorkloadItemInfo> SelectedWork { get; } = [];

    public AsyncCommand CalculateCommand { get; }
    public AsyncCommand PrintReportCommand { get; }

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
        }
    }

    public EmployeeWorkloadRowInfo? SelectedEmployee
    {
        get => selectedEmployee;
        set
        {
            // Rows are records: a recalculated row equal by value is still a new instance to show.
            if (ReferenceEquals(selectedEmployee, value)) return;
            selectedEmployee = value;
            OnPropertyChanged();
            Replace(SelectedDays, value?.Days ?? []);
            Replace(SelectedWork, value?.Work ?? []);
        }
    }

    internal EmployeeWorkloadReportInfo? Report => report;

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
            var result = await apiClient.GetEmployeeWorkloadAsync(
                DateOnly.FromDateTime(start), DateOnly.FromDateTime(end));
            report = result;
            var selectedId = selectedEmployee?.EmployeeId;
            Replace(Employees, result.Employees);
            SelectedEmployee = Employees.FirstOrDefault(row => row.EmployeeId == selectedId) ?? Employees.FirstOrDefault();
            var count = result.Employees.Count;
            var busy = result.Employees.Count(row => row.LoadLevel is "over" or "full" or "high");
            var counted = result.CountedFrom.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            var at = result.CalculatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
            Status = $"{count} employee(s), {busy} highly loaded. Counted from {counted}; calculated at {at}.";
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
        try
        {
            var localization = LocalizationService.Current;
            var html = EmployeeWorkloadReportDocument.Build(report, localization.Translate, localization.IsRightToLeft);
            var folder = Path.Combine(Path.GetTempPath(), "MeimadPlanner", "Reports");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"employee-workload-{report.From:yyyy-MM-dd}-{report.To:yyyy-MM-dd}.html");
            File.WriteAllText(path, html, new System.Text.UTF8Encoding(true));
            openReport(path);
            Status = $"Report opened: {path}. Print it from the browser, or save it as PDF.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Status = $"The report could not be opened: {exception.Message}";
        }
        return Task.CompletedTask;
    }

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
