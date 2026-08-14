using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class TimelineViewModel : INotifyPropertyChanged
{
    // Forecast positions for not-started operations are calculated by the Server
    // relative to its current clock.  Viewports may ask for a fresh projection,
    // but the shared view model limits those read-only requests.
    internal static readonly TimeSpan AutomaticForecastRefreshInterval = TimeSpan.FromSeconds(30);
    private IPlannerApiClient? apiClient;
    private readonly Func<DateTimeOffset> clientNowProvider;
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
    private string? displayTimeZoneId;
    private string? dayStartsAtLocal;
    private string? dayEndsAtLocal;
    private long invalidationVersion;
    private DateTimeOffset? lastAutomaticForecastRefreshAt;
    private DateTimeOffset? serverReadAt;
    private DateTimeOffset? serverReadObservedAt;

    internal TimelineViewModel()
        : this(static () => DateTimeOffset.UtcNow)
    {
    }

    internal TimelineViewModel(Func<DateTimeOffset> clientNowProvider)
    {
        this.clientNowProvider = clientNowProvider ?? throw new ArgumentNullException(nameof(clientNowProvider));
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

    public string? DisplayTimeZoneId
    {
        get => displayTimeZoneId;
        private set => SetField(ref displayTimeZoneId, value);
    }

    public string? DayStartsAtLocal
    {
        get => dayStartsAtLocal;
        private set => SetField(ref dayStartsAtLocal, value);
    }

    public string? DayEndsAtLocal
    {
        get => dayEndsAtLocal;
        private set => SetField(ref dayEndsAtLocal, value);
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
        lastAutomaticForecastRefreshAt = null;
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

    /// <summary>
    /// Requests a server-authoritative refresh of the already-loaded forecast.
    /// This never changes assignments, backlog order, status, or timeline times
    /// in the client.  Multiple Timeline windows share this gate through their
    /// common view model.
    /// </summary>
    internal async Task RequestAutomaticForecastRefreshAsync(DateTimeOffset now)
    {
        var serverNow = EstimatedServerNow(now);
        if (!hasLoaded
            || apiClient is null
            || IsBusy
            || HorizonEnd <= HorizonStart
            || serverNow < HorizonStart
            || serverNow >= HorizonEnd
            || !HasFloatingNotStartedAssignment()
            || lastAutomaticForecastRefreshAt is { } lastRefresh
                && serverNow - lastRefresh < AutomaticForecastRefreshInterval)
        {
            return;
        }

        // Claim the shared throttle before the first await.  This coalesces
        // simultaneous timer ticks from embedded and separate Timeline windows.
        lastAutomaticForecastRefreshAt = serverNow;
        Trace.WriteLine(
            $"Timeline automatic forecast refresh requested at server time {serverNow:O}; " +
            "requesting the Server's current projection.");
        await RefreshAsync();
    }

    /// <summary>
    /// Estimates the Server's current instant from its most recent Timeline
    /// snapshot. Until a snapshot is loaded, the supplied client instant is
    /// retained as the deterministic fallback.
    /// </summary>
    internal DateTimeOffset EstimatedServerNow(DateTimeOffset clientNow) =>
        serverReadAt is { } readAt && serverReadObservedAt is { } observedAt
            ? readAt + (clientNow - observedAt)
            : clientNow;

    private bool HasFloatingNotStartedAssignment() => Machines
        .SelectMany(machine => machine.Intervals)
        .Any(interval => !string.IsNullOrWhiteSpace(interval.MachineAssignmentId)
            && string.Equals(interval.OperationStatus, "not_started", StringComparison.OrdinalIgnoreCase)
            && (interval.IsForecast || interval.IsBlocked));

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

        var from = new DateTimeOffset(DateTime.SpecifyKind(FromDate.Value.Date, DateTimeKind.Utc));
        var to = new DateTimeOffset(DateTime.SpecifyKind(ToDate.Value.Date, DateTimeKind.Utc));
        while (apiClient is not null)
        {
            var requestedVersion = invalidationVersion;
            IsBusy = true;
            try
            {
                var requestStopwatch = Stopwatch.StartNew();
                var snapshot = await apiClient.GetTimelineAsync(from, to);
                requestStopwatch.Stop();
                if (requestedVersion != invalidationVersion)
                {
                    hasLoaded = false;
                    StatusMessage = "The plan changed during calculation. Recalculating from the Server.";
                    continue;
                }

                var applyStopwatch = Stopwatch.StartNew();
                Apply(snapshot);
                applyStopwatch.Stop();
                Trace.WriteLine(
                    $"Timeline refresh: API {requestStopwatch.Elapsed.TotalMilliseconds:F1} ms, " +
                    $"view-model apply {applyStopwatch.Elapsed.TotalMilliseconds:F1} ms " +
                    $"({snapshot.Machines.Count} machines, " +
                    $"{snapshot.Machines.Sum(machine => machine.Intervals.Count)} intervals, " +
                    $"{snapshot.Conflicts.Count} conflicts).");
                hasLoaded = true;
                var hasInsufficientHorizon = snapshot.Conflicts.Any(conflict =>
                    string.Equals(conflict.Code, "insufficient_availability", StringComparison.Ordinal));
                var planningWarningCount = snapshot.Conflicts.Count(conflict =>
                    conflict.Code.StartsWith("backward_", StringComparison.Ordinal));
                StatusMessage = planningWarningCount > 0
                    ? $"Server calculation loaded with {planningWarningCount} assignment planning warning(s)."
                    : hasInsufficientHorizon
                        ? "Some operations do not fit in the selected date range. Extend the To date to see their sequential forecast."
                        : $"Server calculation loaded at {snapshot.ReadAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}. Planning modes are applied per operation assignment.";
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
        serverReadAt = snapshot.ReadAt;
        serverReadObservedAt = clientNowProvider();
        DisplayTimeZoneId = snapshot.DisplayTimeZoneId;
        DayStartsAtLocal = snapshot.DayStartsAtLocal;
        DayEndsAtLocal = snapshot.DayEndsAtLocal;
        Replace(Batches, snapshot.Batches);
        var workFinishDates = snapshot.Batches.ToDictionary(
            batch => batch.BatchId,
            batch => batch.WorkFinishDate,
            StringComparer.Ordinal);
        Replace(
            Machines,
            snapshot.Machines.Select(machine => machine with
            {
                Intervals = machine.Intervals.Select(interval =>
                    interval.WorkFinishDate.HasValue
                        ? interval
                        : interval with
                        {
                            WorkFinishDate = interval.BatchId is { } batchId
                                && workFinishDates.TryGetValue(batchId, out var workFinishDate)
                                    ? workFinishDate
                                    : null
                        }).ToArray()
            }));
        TraceDuplicateBlocks(Machines);
        Replace(Conflicts, snapshot.Conflicts);
        allDependencies = snapshot.Dependencies;
        SelectedBatch = Batches.FirstOrDefault(batch => batch.BatchId == selectedId)
            ?? Batches.FirstOrDefault();
        ApplyDependencyFilter();
        OnPropertyChanged(nameof(Machines));
    }

    private static void TraceDuplicateBlocks(IEnumerable<TimelineMachine> machines)
    {
        var duplicateIds = DuplicateMachineAssignmentIds(machines);
        Trace.WriteLine(
            $"Timeline duplicate assignment-block detection: {duplicateIds.Count} duplicate assignment ID(s).");
        foreach (var duplicateId in duplicateIds)
        {
            Trace.WriteLine(
                $"DUPLICATE_TIMELINE_BLOCK operationAssignmentId={duplicateId}");
        }
    }

    internal static IReadOnlyList<string> DuplicateMachineAssignmentIds(
        IEnumerable<TimelineMachine> machines) => machines
        .SelectMany(machine => machine.Intervals)
        .Where(interval => !string.IsNullOrWhiteSpace(interval.MachineAssignmentId))
        .GroupBy(interval => interval.MachineAssignmentId!, StringComparer.Ordinal)
        .Where(group => group.Count() > 1)
        .Select(group => group.Key)
        .Order(StringComparer.Ordinal)
        .ToArray();

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
        serverReadAt = null;
        serverReadObservedAt = null;
        DisplayTimeZoneId = null;
        DayStartsAtLocal = null;
        DayEndsAtLocal = null;
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
