using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.ToolPreparation;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// The tools the planned operations of a period need: the Server reads the operations the Timeline
/// places in the period, their released tool tables and part material, and returns one line per tool
/// and material group with the copies needed and how they move between Machines. Read-only.
/// </summary>
internal sealed class ToolRequirementsViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? api;
    private IReadOnlyList<ToolRequirementRow> all = [];
    private bool isBusy;
    private DateTime fromDate = DateTime.Today;
    private DateTime toDate = DateTime.Today.AddDays(6);
    private string searchText = string.Empty;
    private ToolRequirementRow? selectedItem;
    private string selectedRoute = string.Empty;
    private string status = "Pick a period and press Calculate.";
    private int withoutToolTableCount;

    internal ToolRequirementsViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => api is not null && !isBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ToolRequirementRow> Items { get; } = [];

    public ObservableCollection<ToolUseRow> SelectedUses { get; } = [];

    public ObservableCollection<OperationWithoutToolsRow> OperationsWithoutToolTable { get; } = [];

    public AsyncCommand RefreshCommand { get; }

    public bool IsConnected => api is not null;

    public bool IsBusy => isBusy;

    public DateTime FromDate
    {
        get => fromDate;
        set => Set(ref fromDate, value.Date);
    }

    /// <summary>The last day of the period; the period ends at the end of this day.</summary>
    public DateTime ToDate
    {
        get => toDate;
        set => Set(ref toDate, value.Date);
    }

    public string SearchText
    {
        get => searchText;
        set { if (Set(ref searchText, value)) ApplyFilter(); }
    }

    public ToolRequirementRow? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (!Set(ref selectedItem, value)) return;
            SelectedUses.Clear();
            foreach (var use in value?.Source.Uses ?? []) SelectedUses.Add(new ToolUseRow(use));
            SelectedRoute = value?.RouteText ?? string.Empty;
        }
    }

    public string SelectedRoute
    {
        get => selectedRoute;
        private set => Set(ref selectedRoute, value);
    }

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public int WithoutToolTableCount
    {
        get => withoutToolTableCount;
        private set => Set(ref withoutToolTableCount, value);
    }

    internal (DateTimeOffset From, DateTimeOffset To) Period =>
        (new DateTimeOffset(fromDate.Date), new DateTimeOffset(toDate.Date.AddDays(1)));

    internal void AttachSession(IPlannerApiClient? client)
    {
        api = client;
        OnPropertyChanged(nameof(IsConnected));
        RefreshCommand.RaiseCanExecuteChanged();
    }

    internal async Task RefreshAsync()
    {
        if (api is not { } client || isBusy) return;
        if (toDate < fromDate)
        {
            Status = "The last day must not be before the first day.";
            return;
        }

        SetBusy(true);
        Status = "Calculating the tools of the planned operations...";
        try
        {
            var (from, to) = Period;
            var report = await client.GetToolRequirementsAsync(from, to);
            all = report.Tools.Select(tool => new ToolRequirementRow(tool)).ToArray();
            OperationsWithoutToolTable.Clear();
            foreach (var operation in report.OperationsWithoutToolTable)
                OperationsWithoutToolTable.Add(new OperationWithoutToolsRow(operation));
            WithoutToolTableCount = report.OperationsWithoutToolTable.Count;
            ApplyFilter();
            Status = $"{report.Tools.Count} tool(s) for {report.PlannedOperationCount} planned operation(s); {report.OperationsWithoutToolTable.Count} operation(s) have no released tool table.";
        }
        catch (Exception exception) when (exception is PlannerApiException or HttpRequestException or TaskCanceledException)
        {
            Status = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>The period's tool requirements as an Excel workbook, or null when it could not be made.</summary>
    internal async Task<byte[]?> ExportAsync()
    {
        if (api is not { } client || isBusy) return null;
        SetBusy(true);
        try
        {
            var (from, to) = Period;
            return await client.ExportToolRequirementsAsync(from, to);
        }
        catch (Exception exception) when (exception is PlannerApiException or HttpRequestException or TaskCanceledException)
        {
            Status = exception.Message;
            return null;
        }
        finally
        {
            SetBusy(false);
        }
    }

    internal void ReportSaved(string path) => Status = $"Tool requirements saved to {path}.";

    private void ApplyFilter()
    {
        var search = searchText.Trim();
        var keep = selectedItem;
        Items.Clear();
        foreach (var row in all.Where(row => row.Matches(search))) Items.Add(row);
        SelectedItem = Items.FirstOrDefault(row => ReferenceEquals(row, keep));
    }

    private void SetBusy(bool value)
    {
        isBusy = value;
        OnPropertyChanged(nameof(IsBusy));
        RefreshCommand.RaiseCanExecuteChanged();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    internal static string MaterialLabel(string group) => group switch
    {
        "ALUMINUM" => "Aluminum",
        "TITANIUM" => "Titanium",
        "STAINLESS" => "Stainless steel",
        "NICKEL" => "Nickel alloy",
        "STEEL" => "Steel",
        "COPPER" => "Copper alloy",
        "PLASTIC" => "Plastic",
        _ => "Unknown material"
    };

    internal static string Time(DateTimeOffset value) =>
        value.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>One tool line: material group, type, size and name, with the copies the plan needs.</summary>
internal sealed class ToolRequirementRow(PlannerToolRequirement source)
{
    public PlannerToolRequirement Source { get; } = source;

    public string MaterialText => ToolRequirementsViewModel.MaterialLabel(Source.MaterialGroup);

    public string TypeText => ToolPreparationCatalog.Shape(Source.ToolType).Name;

    public string TypeSourceText => Source.ToolTypeSource == "catalog" ? "Tool Catalog" : "Tool name";

    public string DiameterText => Source.Diameter?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-";

    public string ToolName => Source.ToolName;

    public int CopiesNeeded => Source.CopiesNeeded;

    public string MachinesText => string.Join(", ", Source.Machines);

    public int UseCount => Source.Uses.Count;

    public int MachineChanges => Source.MachineChanges;

    public string SharingText => Source.Machines.Count <= 1
        ? "One machine"
        : Source.CopiesNeeded < Source.Machines.Count ? "Moves between machines" : "A copy on each machine";

    public string FirstNeedText => ToolRequirementsViewModel.Time(Source.FirstNeed);

    public string LastNeedText => ToolRequirementsViewModel.Time(Source.LastNeed);

    public string RouteText => string.Join(Environment.NewLine, Source.Routes.Select(route =>
        $"#{route.CopyNumber}: " + string.Join("  >  ", route.Stops.Select(stop =>
            $"{stop.MachineLabel} {ToolRequirementsViewModel.Time(stop.From)}-{ToolRequirementsViewModel.Time(stop.To)}"))));

    internal bool Matches(string search) =>
        search.Length == 0
        || Contains(Source.ToolName, search) || Contains(TypeText, search) || Contains(MaterialText, search)
        || Contains(MachinesText, search) || Contains(DiameterText, search)
        || Source.Uses.Any(use => Contains(use.WorkOrderNumber, search) || Contains(use.PartNumber, search));

    private static bool Contains(string? value, string search) =>
        value is not null && value.Contains(search, StringComparison.CurrentCultureIgnoreCase);
}

internal sealed class ToolUseRow(PlannerToolUse source)
{
    public string WorkOrderNumber => source.WorkOrderNumber;

    public string PartNumber => source.PartNumber;

    public string OperationText => $"OP{source.OperationNumber.ToString(CultureInfo.InvariantCulture)} {source.OperationName}";

    public string MachineLabel => source.MachineLabel;

    public string ToolNumbersText => string.Join(", ", source.ToolNumbers);

    public string Holder => source.Holder ?? string.Empty;

    public string LengthText => source.Length?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    public string Material => source.Material ?? string.Empty;

    public string FromText => ToolRequirementsViewModel.Time(source.StartsAt);

    public string ToText => ToolRequirementsViewModel.Time(source.EndsAt);
}

internal sealed class OperationWithoutToolsRow(PlannerOperationWithoutTools source)
{
    public string WorkOrderNumber => source.WorkOrderNumber;

    public string PartNumber => source.PartNumber;

    public string OperationText => $"OP{source.OperationNumber.ToString(CultureInfo.InvariantCulture)} {source.OperationName}";

    public string MachineLabel => source.MachineLabel;

    public string MaterialText => ToolRequirementsViewModel.MaterialLabel(source.MaterialGroup);

    public string FromText => ToolRequirementsViewModel.Time(source.StartsAt);

    public string ToText => ToolRequirementsViewModel.Time(source.EndsAt);
}
