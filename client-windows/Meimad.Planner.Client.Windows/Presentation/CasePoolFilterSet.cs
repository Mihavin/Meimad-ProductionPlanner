using System.ComponentModel;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// Jira-style Case pool filters: one row of chips, each a single choice, all combined with AND.
/// "Any" leaves a chip out. Date chips offer presets relative to today or a custom range.
/// </summary>
internal sealed class CasePoolFilterSet : INotifyPropertyChanged
{
    internal const string Any = "Any";
    internal const string CustomRange = "Custom range";

    private string workOrders = Any;
    private string release = Any;
    private string orders = Any;
    private string operations = Any;
    private string materialOrders = Any;
    private string supplyDate = Any;
    private string startDate = Any;
    private DateTime? supplyFrom;
    private DateTime? supplyTo;
    private DateTime? startFrom;
    private DateTime? startTo;
    private bool suppress;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised once per user change of any filter.</summary>
    internal event EventHandler? FiltersChanged;

    public IReadOnlyList<string> WorkOrderOptions { get; } = [Any, "With Work Orders", "Without Work Orders"];
    public IReadOnlyList<string> ReleaseOptions { get; } = [Any, "Pending", "Released"];
    public IReadOnlyList<string> OrderOptions { get; } = [Any, "With active Orders", "Without active Orders"];
    public IReadOnlyList<string> OperationOptions { get; } = [Any, "With operations", "Without operations"];
    public IReadOnlyList<string> MaterialOrderOptions { get; } = [Any, "Verified", "To verify"];
    public IReadOnlyList<string> SupplyDateOptions { get; } = [Any, "Overdue", "Next 7 days", "Next 30 days", "Next 90 days", CustomRange];
    public IReadOnlyList<string> StartDateOptions { get; } = [Any, "Already started", "Next 7 days", "Next 30 days", "Next 90 days", CustomRange];

    public string WorkOrders { get => workOrders; set => Set(ref workOrders, value); }
    public string Release { get => release; set => Set(ref release, value); }
    public string Orders { get => orders; set => Set(ref orders, value); }
    public string Operations { get => operations; set => Set(ref operations, value); }
    public string MaterialOrders { get => materialOrders; set => Set(ref materialOrders, value); }

    public string SupplyDate
    {
        get => supplyDate;
        set { if (Set(ref supplyDate, value)) OnPropertyChanged(nameof(IsSupplyCustom)); }
    }

    public string StartDate
    {
        get => startDate;
        set { if (Set(ref startDate, value)) OnPropertyChanged(nameof(IsStartCustom)); }
    }

    public DateTime? SupplyFrom { get => supplyFrom; set => Set(ref supplyFrom, value); }
    public DateTime? SupplyTo { get => supplyTo; set => Set(ref supplyTo, value); }
    public DateTime? StartFrom { get => startFrom; set => Set(ref startFrom, value); }
    public DateTime? StartTo { get => startTo; set => Set(ref startTo, value); }

    public bool IsSupplyCustom => supplyDate == CustomRange;
    public bool IsStartCustom => startDate == CustomRange;

    internal void Reset()
    {
        suppress = true;
        WorkOrders = Release = Orders = Operations = MaterialOrders = SupplyDate = StartDate = Any;
        SupplyFrom = SupplyTo = StartFrom = StartTo = null;
        suppress = false;
    }

    /// <summary>Adds the chip values to the Server query; date presets are resolved against today.</summary>
    internal CaseQuery ApplyTo(CaseQuery query, DateOnly today)
    {
        var (supplyStart, supplyEnd) = Range(supplyDate, supplyFrom, supplyTo, today, pastPreset: "Overdue");
        var (startStart, startEnd) = Range(startDate, startFrom, startTo, today, pastPreset: "Already started");
        return query with
        {
            WorkOrders = Token(workOrders, "With Work Orders", "with", "Without Work Orders", "without"),
            Release = Token(release, "Pending", "pending", "Released", "released"),
            Orders = Token(orders, "With active Orders", "active", "Without active Orders", "none"),
            Operations = Token(operations, "With operations", "with", "Without operations", "without"),
            MaterialOrders = Token(materialOrders, "Verified", "verified", "To verify", "toVerify"),
            SupplyFrom = supplyStart,
            SupplyTo = supplyEnd,
            StartFrom = startStart,
            StartTo = startEnd
        };
    }

    /// <summary>The filter as one readable condition, e.g. "Work Orders = With AND Release = Pending".</summary>
    internal string Describe(IEnumerable<string> extra)
    {
        var parts = extra.ToList();
        void Add(string label, string value)
        {
            if (value != Any) parts.Add($"{label} = {value}");
        }
        Add("Work Orders", workOrders);
        Add("Release", release);
        Add("Orders", orders);
        Add("Operations", operations);
        Add("Material order", materialOrders);
        Add("Supply date", supplyDate == CustomRange ? $"{Day(supplyFrom)} – {Day(supplyTo)}" : supplyDate);
        Add("Production start", startDate == CustomRange ? $"{Day(startFrom)} – {Day(startTo)}" : startDate);
        return parts.Count == 0 ? "No filters" : string.Join(" AND ", parts);
    }

    private static string Day(DateTime? value) => value?.ToString("yyyy-MM-dd") ?? "…";

    private static string? Token(string value, string first, string firstToken, string second, string secondToken) =>
        value == first ? firstToken : value == second ? secondToken : null;

    private static (DateOnly? From, DateOnly? To) Range(
        string preset, DateTime? from, DateTime? to, DateOnly today, string pastPreset) => preset switch
    {
        "Next 7 days" => (today, today.AddDays(7)),
        "Next 30 days" => (today, today.AddDays(30)),
        "Next 90 days" => (today, today.AddDays(90)),
        CustomRange => (from is null ? null : DateOnly.FromDateTime(from.Value), to is null ? null : DateOnly.FromDateTime(to.Value)),
        _ when preset == pastPreset => (null, today.AddDays(-1)),
        _ => (null, null)
    };

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        if (!suppress) FiltersChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
