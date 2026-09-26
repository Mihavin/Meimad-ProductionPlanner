using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// Read-only list of the Kitaron material purchase-order lines with their reported delivery
/// status. Kitaron owns the data; the page only filters what the Server imported.
/// </summary>
internal sealed class MaterialOrdersViewModel : INotifyPropertyChanged
{
    internal const string AllStatuses = "All statuses";
    internal const string OpenStatuses = "Not yet received";

    private IPlannerApiClient? api;
    private IReadOnlyList<KitaronMaterialOrder> all = [];
    private bool isBusy;
    private string searchText = string.Empty;
    private string statusFilter = OpenStatuses;
    private string status = "Connect to view the Kitaron material orders.";

    internal MaterialOrdersViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => api is not null && !isBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<KitaronMaterialOrder> Items { get; } = [];

    public AsyncCommand RefreshCommand { get; }

    public IReadOnlyList<string> StatusFilters { get; } =
    [
        OpenStatuses, AllStatuses, "⚠ Late", "○ Open", "● Supplier confirmed", "◐ Partially received",
        "✓ Received", "■ Closed"
    ];

    public string SearchText
    {
        get => searchText;
        set { if (Set(ref searchText, value)) ApplyFilter(); }
    }

    public string StatusFilter
    {
        get => statusFilter;
        set { if (Set(ref statusFilter, value)) ApplyFilter(); }
    }

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    /// <summary>Called on every connection status change; the ~9,000-line list is loaded again
    /// only for a new Server connection, otherwise on Refresh.</summary>
    internal void AttachSession(IPlannerApiClient? client)
    {
        var changed = !ReferenceEquals(api, client);
        api = client;
        RefreshCommand.RaiseCanExecuteChanged();
        if (api is not null && (changed || all.Count == 0)) _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (api is null || isBusy) return;
        isBusy = true;
        RefreshCommand.RaiseCanExecuteChanged();
        try
        {
            all = await api.ListKitaronMaterialOrdersAsync();
            ApplyFilter();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            isBusy = false;
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    private void ApplyFilter()
    {
        var search = searchText.Trim();
        var shown = all.Where(item => MatchesStatus(item) && MatchesSearch(item, search)).ToArray();
        Items.Clear();
        foreach (var item in shown) Items.Add(item);
        var open = all.Count(item => item.IsOpen);
        var late = all.Count(item => item.DeliveryStatus == "late");
        Status = all.Count == 0
            ? "The Server has no Kitaron material orders yet. They arrive with the Kitaron synchronization when the material-order fields are mapped."
            : $"{shown.Length} of {all.Count} Kitaron material order line(s) shown; {open} not yet received, {late} late.";
    }

    private bool MatchesStatus(KitaronMaterialOrder item) => statusFilter switch
    {
        AllStatuses => true,
        OpenStatuses => item.IsOpen,
        _ => item.DeliveryStatusText == statusFilter
    };

    private static bool MatchesSearch(KitaronMaterialOrder item, string search) =>
        search.Length == 0
        || Contains(item.PurchaseOrderText, search)
        || Contains(item.MaterialNumber, search)
        || Contains(item.Description, search)
        || Contains(item.Supplier, search)
        || Contains(item.KitaronStatus, search);

    private static bool Contains(string? value, string search) =>
        value is not null && value.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
