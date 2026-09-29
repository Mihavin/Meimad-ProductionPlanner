using System.Globalization;
using System.Windows;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>
/// A Case Operation's collected times (owner request 2026-09-29): the Operation's own times, the NC
/// time and the real median per Machine, the measurements behind the medians and the history of the
/// Operation's times. The NC cycle and the real times can be applied to the Case Operation.
/// </summary>
public partial class OperationTimeStatisticsWindow : Window
{
    private readonly IPlannerApiClient api;
    private readonly string clientId;
    private readonly string caseId;
    private readonly string caseOperationId;
    private PlannerOperationTimeStatistics? statistics;

    internal OperationTimeStatisticsWindow(IPlannerApiClient api, string clientId, string caseId, string caseOperationId)
    {
        this.api = api;
        this.clientId = clientId;
        this.caseId = caseId;
        this.caseOperationId = caseOperationId;
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Whether a time was applied to the Case Operation.</summary>
    internal bool Changed { get; private set; }

    /// <summary>Shows the statistics over <paramref name="owner"/>; true when a time was applied.</summary>
    internal static bool Open(Window owner, IPlannerApiClient api, string clientId, string caseId, string caseOperationId)
    {
        var window = new OperationTimeStatisticsWindow(api, clientId, caseId, caseOperationId) { Owner = owner };
        window.ShowDialog();
        return window.Changed;
    }

    private async Task LoadAsync()
    {
        try
        {
            Display(await api.GetOperationTimeStatisticsAsync(caseId, caseOperationId));
            StatusText.Text = string.Empty;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void Display(PlannerOperationTimeStatistics value)
    {
        statistics = value;
        HeaderText.Text = $"OP {value.OperationNumber} {value.Name}";
        OperationTimesText.Text = string.Join("  ·  ",
            $"{T("Setup")}: {Duration(value.SetupSeconds)}",
            $"{T("Cycle / part")}: {Duration(value.CycleSeconds)}",
            $"{T("QC after setup")}: {Duration(value.QaSeconds)}",
            $"{T("Load/unload")}: {Duration(value.LoadUnloadSeconds)}");
        TimesGrid.ItemsSource = value.Machines.SelectMany(machine => new[]
        {
            TimeRow.Create(value, machine, "cycle"), TimeRow.Create(value, machine, "setup"),
            TimeRow.Create(value, machine, "qa"), TimeRow.Create(value, machine, "load_unload")
        }).ToArray();
        var machines = value.Machines.ToDictionary(machine => machine.MachineId, MachineText);
        SamplesGrid.ItemsSource = value.Samples.Select(sample => new SampleRow(
            Local(sample.MeasuredAt), machines.GetValueOrDefault(sample.MachineId, sample.MachineId), KindText(sample.Kind),
            Duration(sample.Seconds), T(sample.Source == "MANUAL" ? "Manual report" : "Machine events"),
            sample.BatchNumber ?? string.Empty, sample.InMedian ? "✓" : string.Empty)).ToArray();
        HistoryGrid.ItemsSource = value.History.Select(change => new HistoryRow(
            Local(change.ChangedAt), KindText(change.Kind), SourceText(change), Duration(change.PreviousSeconds),
            Duration(change.NewSeconds), change.ChangedBy)).ToArray();
        if (value.Machines.Count == 0)
            StatusText.Text = T("No NC time and no measurement on any Machine yet.");
    }

    private async void ApplyNc_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TimeRow row } && row.Machine.NcCycleSeconds is { } seconds)
            await ApplyAsync(row, "NC", Math.Round(seconds, MidpointRounding.AwayFromZero));
    }

    private async void ApplyReal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TimeRow row } && row.Measured is { } measured)
            await ApplyAsync(row, "MEASURED", row.Kind == "setup"
                ? row.Machine.SetupApplySeconds ?? 0 : Math.Round(measured.MedianSeconds, MidpointRounding.AwayFromZero));
    }

    private async Task ApplyAsync(TimeRow row, string source, double seconds)
    {
        if (statistics is not { } current) return;
        var question = string.Format(CultureInfo.CurrentCulture,
            T("Set the Operation's {0} to {1} ({2}, Machine {3})? Pending Work Orders take it now; released ones on Refresh from Case. The change is kept in the history."),
            row.KindText, Duration(seconds), T(source == "NC" ? "NC time" : "real median"), row.Machine.MachineNumber);
        if (LocalizedMessageBox.Show(this, question, "Apply time on operation", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        try
        {
            Display(await api.ApplyOperationTimeAsync(caseId, caseOperationId, row.Kind, source, row.Machine.MachineId, current.Version, clientId));
            Changed = true;
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, T("The Operation's {0} is now {1}."), row.KindText, Duration(seconds));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusText.Text = exception.Message;
            await LoadAsync();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string T(string value) => LocalizationService.Current.Translate(value);

    private static string MachineText(PlannerOperationMachineTimes machine) => $"{machine.MachineNumber} {machine.MachineName}";

    private static string KindText(string kind) => T(kind switch
    {
        "cycle" => "Cycle / part",
        "setup" => "Setup",
        "qa" => "QC after setup",
        _ => "Load/unload"
    });

    private static string SourceText(PlannerOperationTimeChange change) => change.Source switch
    {
        "NC" => $"{T("NC time")} · {change.MachineNumber}",
        "MEASURED" => $"{T("Real median")} · {change.MachineNumber} · {change.SampleCount} {T("samples")}",
        _ => T("Manual edit")
    };

    private static string Local(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    private static string Duration(double? seconds) => seconds is { } value && double.IsFinite(value) && value >= 0
        ? Formatting.DurationText.Format(checked((long)Math.Round(value, MidpointRounding.AwayFromZero)))
        : "—";

    /// <summary>One time kind on one Machine.</summary>
    internal sealed record TimeRow(
        string Kind, PlannerOperationMachineTimes Machine, PlannerMeasuredTime? Measured, string MachineText, string KindText,
        string OperationText, string NcText, string RealText, string UsedText, bool CanApplyNc, bool CanApplyReal, string ApplyRealToolTip)
    {
        internal static TimeRow Create(PlannerOperationTimeStatistics operation, PlannerOperationMachineTimes machine, string kind)
        {
            var measured = kind switch
            {
                "cycle" => machine.MeasuredCycle,
                "setup" => machine.MeasuredSetup,
                "qa" => machine.MeasuredQa,
                _ => machine.MeasuredLoadUnload
            };
            var usableMeasured = measured is not null && (kind != "load_unload" || machine.LoadingIsPerPart);
            double? nc = kind switch { "cycle" => machine.NcCycleSeconds, "setup" => machine.NcSetupSeconds, _ => null };
            double? own = kind switch
            {
                "cycle" => operation.CycleSeconds,
                "setup" => operation.SetupSeconds,
                "qa" => operation.QaSeconds,
                _ => operation.LoadUnloadSeconds
            };
            var used = usableMeasured ? T("Real median")
                : nc is not null ? T(kind == "setup" ? "Setup estimate" : "NC time")
                : T("Operation");
            var real = measured is null ? "—"
                : $"{Duration(measured.MedianSeconds)} · {measured.SampleCount} {T("samples")}";
            var toolTip = kind switch
            {
                "setup" when machine.SetupApplySeconds is { } fixture => string.Format(CultureInfo.CurrentCulture,
                    T("Writes {0} as the Operation's setup time: the real setup without the tool loading and the first piece, which the setup estimate adds."),
                    Duration(fixture)),
                "load_unload" when !machine.LoadingIsPerPart =>
                    T("The Operation loads automatically or every N parts, so the gap between cycles is not its load/unload time."),
                _ => T("Write the real median of this Operation on this Machine to the Case Operation")
            };
            return new TimeRow(kind, machine, measured, OperationTimeStatisticsWindow.MachineText(machine),
                OperationTimeStatisticsWindow.KindText(kind), Duration(own),
                nc is null ? "—" : kind == "setup" ? $"{Duration(nc)} ({T("estimate")})" : Duration(nc),
                real, used, kind == "cycle" && machine.NcCycleSeconds is not null, usableMeasured, toolTip);
        }
    }

    internal sealed record SampleRow(
        string MeasuredAtText, string MachineText, string KindText, string DurationText, string SourceText, string BatchNumber, string InMedianText);

    internal sealed record HistoryRow(
        string ChangedAtText, string KindText, string SourceText, string FromText, string ToText, string ChangedBy);
}
