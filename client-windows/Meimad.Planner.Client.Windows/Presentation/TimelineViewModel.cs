using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class TimelineViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? apiClient;
    private IReadOnlyList<TimelineDependency> allDependencies = [];
    private bool hasLoaded;
    private bool isBusy;
    private DateTime? fromDate = DateTime.UtcNow.Date;
    // Long CNC batches routinely span multiple working days. A 30-day default
    // lets the read-only Timeline show dependency chains instead of reporting
    // the first long predecessor as outside a one-week calculation horizon.
    private DateTime? toDate = DateTime.UtcNow.Date.AddDays(30);
    private TimelineBatch? selectedBatch;
    private string statusMessage = "Connect to the Server to calculate the Timeline.";
    private DateTimeOffset horizonStart;
    private DateTimeOffset horizonEnd;
    private long invalidationVersion;

    internal TimelineViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, CanRefresh);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TimelineBatch> Batches { get; } = [];

    public ObservableCollection<TimelineMachine> Machines { get; } = [];

    public ObservableCollection<TimelineConflict> Conflicts { get; } = [];

    public ObservableCollection<TimelineDependency> SelectedDependencies { get; } = [];

    public AsyncCommand RefreshCommand { get; }

    public DateTime? FromDate
    {
        get => fromDate;
        set
        {
            if (SetField(ref fromDate, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public DateTime? ToDate
    {
        get => toDate;
        set
        {
            if (SetField(ref toDate, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public TimelineBatch? SelectedBatch
    {
        get => selectedBatch;
        set
        {
            if (SetField(ref selectedBatch, value))
            {
                ApplyDependencyFilter();
            }
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetField(ref statusMessage, value);
    }

    public DateTimeOffset HorizonStart
    {
        get => horizonStart;
        private set => SetField(ref horizonStart, value);
    }

    public DateTimeOffset HorizonEnd
    {
        get => horizonEnd;
        private set => SetField(ref horizonEnd, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetField(ref isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    internal void AttachSession(IPlannerApiClient? newApiClient)
    {
        if (ReferenceEquals(apiClient, newApiClient))
        {
            return;
        }

        apiClient = newApiClient;
        invalidationVersion++;
        hasLoaded = false;
        ClearProjection();
        RefreshCommand.RaiseCanExecuteChanged();
    }

    internal async Task EnsureLoadedAsync()
    {
        if (!hasLoaded && apiClient is not null)
        {
            await RefreshAsync();
        }
    }

    internal void Invalidate()
    {
        invalidationVersion++;
        hasLoaded = false;
        StatusMessage = "The plan changed. The Timeline will be recalculated from the Server.";
    }

    internal async Task RefreshAsync()
    {
        if (apiClient is null || !FromDate.HasValue || !ToDate.HasValue
            || ToDate.Value.Date <= FromDate.Value.Date)
        {
            StatusMessage = "Choose a valid UTC date range with the end after the start.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        var from = new DateTimeOffset(DateTime.SpecifyKind(FromDate!.Value.Date, DateTimeKind.Utc));
        var to = new DateTimeOffset(DateTime.SpecifyKind(ToDate!.Value.Date, DateTimeKind.Utc));
        while (apiClient is not null)
        {
            var requestedVersion = invalidationVersion;
            IsBusy = true;
            try
            {
                var snapshot = await apiClient.GetTimelineAsync(from, to);
                Apply(snapshot);
                hasLoaded = requestedVersion == invalidationVersion;
                var hasInsufficientHorizon = snapshot.Conflicts.Any(conflict =>
                    string.Equals(conflict.Code, "insufficient_availability", StringComparison.Ordinal));
                StatusMessage = hasLoaded
                    ? hasInsufficientHorizon
                        ? "Some operations do not fit in the selected date range. Extend the To date to see their sequential forecast."
                        : $"Server calculation loaded at {snapshot.ReadAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}."
                    : "The plan changed during calculation. Recalculating from the Server.";
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                StatusMessage = FriendlyMessage(exception);
                return;
            }
            finally
            {
                IsBusy = false;
            }

            if (hasLoaded)
            {
                return;
            }
        }
    }

    private void Apply(TimelineSnapshot snapshot)
    {
        var selectedId = SelectedBatch?.BatchId;
        HorizonStart = snapshot.HorizonStart;
        HorizonEnd = snapshot.HorizonEnd;
        Replace(Batches, snapshot.Batches);
        Replace(Machines, snapshot.Machines);
        Replace(Conflicts, snapshot.Conflicts);
        allDependencies = snapshot.Dependencies;
        SelectedBatch = Batches.FirstOrDefault(batch => batch.BatchId == selectedId)
            ?? Batches.FirstOrDefault();
        ApplyDependencyFilter();
        OnPropertyChanged(nameof(Machines));
    }

    private void ApplyDependencyFilter()
    {
        Replace(
            SelectedDependencies,
            SelectedBatch is null
                ? []
                : allDependencies.Where(dependency =>
                    dependency.BatchId == SelectedBatch.BatchId).ToArray());
    }

    private void ClearProjection()
    {
        Batches.Clear();
        Machines.Clear();
        Conflicts.Clear();
        SelectedDependencies.Clear();
        allDependencies = [];
        selectedBatch = null;
        HorizonStart = default;
        HorizonEnd = default;
    }

    private bool CanRefresh() => apiClient is not null
        && !IsBusy
        && FromDate.HasValue
        && ToDate.HasValue
        && ToDate.Value.Date > FromDate.Value.Date;

    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        destination.Clear();
        foreach (var value in source)
        {
            destination.Add(value);
        }
    }

    private static bool IsExpected(Exception exception) => exception is
        PlannerApiException or PlannerProtocolException or HttpRequestException or TaskCanceledException;

    private static string FriendlyMessage(Exception exception) => exception switch
    {
        TaskCanceledException => "The Server did not return the Timeline before the client timeout.",
        HttpRequestException => "The configured Server could not be reached.",
        PlannerApiException apiException => $"{apiException.Message} ({apiException.Code})",
        _ => exception.Message
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
