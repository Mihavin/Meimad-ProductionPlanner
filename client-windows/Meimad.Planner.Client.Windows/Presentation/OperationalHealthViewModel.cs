using System.Collections.ObjectModel;
using System.ComponentModel;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class OperationalHealthViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? api;
    private int generation;
    private bool busy;
    private string? nextCursor;
    public ObservableCollection<OperationalHealthItem> Items { get; } = [];
    public string Status { get; private set; } = "Sign in to inspect operational health.";
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand MoreCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public OperationalHealthViewModel()
    {
        RefreshCommand = new(() => ReadAsync(false), () => api is not null && !busy);
        MoreCommand = new(() => ReadAsync(true), () => api is not null && !busy && nextCursor is not null);
    }

    internal void AttachSession(IPlannerApiClient? client)
    {
        generation++;
        api = client;
        busy = false;
        Items.Clear();
        nextCursor = null;
        Status = client is null ? "Sign in to inspect operational health." : "Refresh to inspect operational health.";
        Changed();
    }

    internal async Task ReadAsync(bool more)
    {
        if (busy || api is not { } client || more && nextCursor is null) return;
        var current = generation;
        busy = true;
        Changed();
        try
        {
            var page = await client.GetOperationalHealthAsync(more ? nextCursor : null);
            if (current != generation) return;
            if (!more) Items.Clear();
            foreach (var item in page.Items) Items.Add(item);
            nextCursor = page.NextCursor;
            Status = $"Sampled {page.ReadAt:u}. {Items.Count} entries loaded. Pages are separate observations; unknown values remain unknown.";
        }
        catch (Exception exception)
        {
            if (current == generation) Status = $"Health refresh failed. Displayed entries are previous observations. {exception.Message}";
        }
        finally { if (current == generation) { busy = false; Changed(); } }
    }

    private void Changed()
    {
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        RefreshCommand.RaiseCanExecuteChanged(); MoreCommand.RaiseCanExecuteChanged();
    }
}
