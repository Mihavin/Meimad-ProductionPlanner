using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Views;

public partial class TimelineView : UserControl
{
    private const double LabelWidth = 185;
    private const double HeaderHeight = 42;
    private const double DateHeaderRowHeight = 19;
    private static readonly TimeSpan LegacyDayStart = TimeSpan.FromHours(6);
    private static readonly TimeSpan LegacyDayEnd = TimeSpan.FromHours(18);
    internal const double CompactRowHeight = 38;
    internal const double LabelWidthForTests = LabelWidth;
    private const double RowHeight = CompactRowHeight;
    internal const double AssignmentLaneTop = 3;
    internal const double AssignmentLaneHeight = 22;
    internal const double CapacityLaneTop = 27;
    internal const double CapacityLaneHeight = 8;
    private TimelineViewModel? viewModel;
    private bool isLoaded;
    private readonly Func<DateTimeOffset> nowProvider;
    private DispatcherTimer? currentTimeTimer;
    private Canvas? currentTimeMarker;

    internal IReadOnlyList<string> RenderedMachineAssignmentIds => TimelineCanvas.Children
        .OfType<Border>()
        .Select(element => element.Tag)
        .OfType<TimelineInterval>()
        .Where(interval => !string.IsNullOrWhiteSpace(interval.MachineAssignmentId))
        .Select(interval => interval.MachineAssignmentId!)
        .ToArray();

    public TimelineView()
        : this(static () => DateTimeOffset.UtcNow)
    {
    }

    internal TimelineView(Func<DateTimeOffset> nowProvider)
    {
        this.nowProvider = nowProvider ?? throw new ArgumentNullException(nameof(nowProvider));
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        DetachViewModel();
        if (isLoaded)
        {
            AttachViewModel(args.NewValue as TimelineViewModel);
        }

        RenderTimeline();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        isLoaded = true;
        AttachViewModel(DataContext as TimelineViewModel);
        RenderTimeline();
        StartCurrentTimeTimer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        isLoaded = false;
        StopCurrentTimeTimer();
        DetachViewModel();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => RenderTimeline();

    private void AttachViewModel(TimelineViewModel? candidate)
    {
        if (ReferenceEquals(viewModel, candidate))
        {
            return;
        }

        DetachViewModel();
        viewModel = candidate;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.Machines.CollectionChanged += OnMachinesChanged;
        }
    }

    private void DetachViewModel()
    {
        if (viewModel is null)
        {
            return;
        }

        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.Machines.CollectionChanged -= OnMachinesChanged;
        viewModel = null;
    }

    private void OnMachinesChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
        RenderTimeline();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TimelineViewModel.HorizonStart)
            or nameof(TimelineViewModel.HorizonEnd)
            or nameof(TimelineViewModel.Machines)
            or nameof(TimelineViewModel.DisplayTimeZoneId)
            or nameof(TimelineViewModel.DayStartsAtLocal)
            or nameof(TimelineViewModel.DayEndsAtLocal)
            or nameof(TimelineViewModel.SelectedDependencies)
            or nameof(TimelineViewModel.SelectedBatch))
        {
            RenderTimeline();
        }
    }

    private void RenderTimeline()
    {
        var stopwatch = Stopwatch.StartNew();
        currentTimeMarker = null;
        TimelineCanvas.Children.Clear();
        if (viewModel is null
            || viewModel.HorizonEnd <= viewModel.HorizonStart
            || viewModel.Machines.Count == 0)
        {
            TimelineCanvas.Width = Math.Max(700, ActualWidth - 60);
            TimelineCanvas.Height = 100;
            AddText("No calculated Machine intervals in this range.", 12, 35, 15, Brushes.DimGray);
            Trace.WriteLine($"Timeline render completed in {stopwatch.Elapsed.TotalMilliseconds:F1} ms (empty projection).");
            return;
        }

        var duration = viewModel.HorizonEnd - viewModel.HorizonStart;
        var chartWidth = Math.Max(900, Math.Min(6000, duration.TotalHours * 22));
        TimelineCanvas.Width = LabelWidth + chartWidth + 18;
        TimelineCanvas.Height = HeaderHeight + viewModel.Machines.Count * RowHeight + 12;
        // Daylight/dark context belongs to the time ruler only. It is deliberately
        // rendered before the full-height grid lines and Machine rows so it cannot
        // change the meaning or status color of any planning interval.
        DrawTimeScaleBackgrounds(viewModel.HorizonStart, viewModel.HorizonEnd, chartWidth);
        // Calendar closures are a background layer. Draw them before dependency
        // grid lines, dependency arrows, and operation/capacity blocks so they
        // never obscure a planning fact.
        DrawCalendarBackgrounds(chartWidth, duration);
        DrawTimeGrid(viewModel.HorizonStart, viewModel.HorizonEnd, chartWidth);

        for (var row = 0; row < viewModel.Machines.Count; row++)
        {
            DrawMachineRow(viewModel.Machines[row], row, chartWidth, duration);
        }
        // Dependency arrows are the topmost layer so selected-batch links remain
        // legible over operation blocks and calendar context.
        DrawDependencyArrows(chartWidth, duration);
        UpdateCurrentTimeMarker(chartWidth);

        stopwatch.Stop();
        Trace.WriteLine(
            $"Timeline render completed in {stopwatch.Elapsed.TotalMilliseconds:F1} ms " +
            $"({viewModel.Machines.Count} machines, {viewModel.Machines.Sum(machine => machine.Intervals.Count)} intervals, " +
            $"{TimelineCanvas.Children.Count} visual elements).");
    }

    private void StartCurrentTimeTimer()
    {
        if (currentTimeTimer is not null)
        {
            return;
        }

        currentTimeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // A minute-level display is sufficient and avoids rerunning the
            // comparatively expensive timeline calculation just to move NOW.
            Interval = TimeSpan.FromSeconds(30)
        };
        currentTimeTimer.Tick += OnCurrentTimeTimerTick;
        currentTimeTimer.Start();
        UpdateCurrentTimeMarkerFromClockCore(requestForecastRefresh: false);
    }

    private void StopCurrentTimeTimer()
    {
        if (currentTimeTimer is null)
        {
            return;
        }

        currentTimeTimer.Stop();
        currentTimeTimer.Tick -= OnCurrentTimeTimerTick;
        currentTimeTimer = null;
        RemoveCurrentTimeMarker();
    }

    private void OnCurrentTimeTimerTick(object? sender, EventArgs args) =>
        UpdateCurrentTimeMarkerFromClockCore(requestForecastRefresh: true);

    // Kept as the marker-only entry point for rendering and focused WPF checks.
    // Automatic forecast requests are deliberately restricted to timer ticks.
    private void UpdateCurrentTimeMarkerFromClock() =>
        UpdateCurrentTimeMarkerFromClockCore(requestForecastRefresh: false);

    private void UpdateCurrentTimeMarkerFromClockCore(bool requestForecastRefresh)
    {
        if (!isLoaded || viewModel is null || viewModel.HorizonEnd <= viewModel.HorizonStart)
        {
            RemoveCurrentTimeMarker();
            return;
        }

        var duration = viewModel.HorizonEnd - viewModel.HorizonStart;
        var chartWidth = Math.Max(900, Math.Min(6000, duration.TotalHours * 22));
        var clientNow = nowProvider();
        var now = CurrentTimelineNow(viewModel, clientNow);
        UpdateCurrentTimeMarker(chartWidth, now);
        if (requestForecastRefresh)
        {
            _ = RequestAutomaticForecastRefreshSafelyAsync(clientNow);
        }
    }

    private async Task RequestAutomaticForecastRefreshSafelyAsync(DateTimeOffset now)
    {
        try
        {
            await viewModel!.RequestAutomaticForecastRefreshAsync(now);
        }
        catch (Exception exception)
        {
            // Expected request failures are handled in the view model.  Do not
            // let an unexpected background refresh failure tear down the WPF UI.
            Trace.WriteLine($"Timeline automatic forecast refresh failed: {exception}");
        }
    }

    private void UpdateCurrentTimeMarker(double chartWidth)
        => UpdateCurrentTimeMarker(chartWidth, CurrentTimelineNow(viewModel, nowProvider()));

    internal static DateTimeOffset CurrentTimelineNow(
        TimelineViewModel? timeline,
        DateTimeOffset clientNow) => timeline?.EstimatedServerNow(clientNow) ?? clientNow;

    private void UpdateCurrentTimeMarker(double chartWidth, DateTimeOffset now)
    {
        RemoveCurrentTimeMarker();
        if (viewModel is null || viewModel.Machines.Count == 0)
        {
            return;
        }

        if (!IsCurrentTimeWithinHorizon(now, viewModel.HorizonStart, viewModel.HorizonEnd))
        {
            return;
        }

        var x = CurrentTimeMarkerX(
            now,
            viewModel.HorizonStart,
            viewModel.HorizonEnd,
            LabelWidth,
            chartWidth);
        var displayZone = DisplayTimeZone();
        var marker = new Canvas
        {
            Width = TimelineCanvas.Width,
            Height = TimelineCanvas.Height,
            IsHitTestVisible = false,
            ToolTip = CurrentTimeMarkerLabel(now, displayZone),
            // This is a marker visual, never a TimelineInterval or assignment.
            Uid = "CurrentTimeMarker"
        };
        marker.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = x,
            X2 = x,
            Y1 = 0,
            Y2 = TimelineCanvas.Height,
            Stroke = CurrentTimeMarkerBrush,
            StrokeThickness = 1.5,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false
        });

        var label = new Border
        {
            Background = CurrentTimeMarkerBrush,
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(4, 1, 4, 1),
            Child = new TextBlock
            {
                Text = CurrentTimeMarkerLabel(now, displayZone),
                Foreground = Brushes.White,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold
            },
            IsHitTestVisible = false
        };
        marker.Children.Add(label);
        // Measure the real badge before clamping. The chart can be narrow, and
        // a fixed width estimate could let the label overlap the machine labels
        // or extend past the visible chart end.
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var badgeWidth = label.DesiredSize.Width;
        var chartStart = LabelWidth;
        var chartEnd = LabelWidth + chartWidth;
        var maximumBadgeLeft = Math.Max(chartStart, chartEnd - badgeWidth);
        var badgeLeft = Math.Clamp(x - badgeWidth / 2, chartStart, maximumBadgeLeft);
        Canvas.SetLeft(label, badgeLeft);
        Canvas.SetTop(label, 1);
        Canvas.SetLeft(marker, 0);
        Canvas.SetTop(marker, 0);
        TimelineCanvas.Children.Add(marker);
        currentTimeMarker = marker;
    }

    private void RemoveCurrentTimeMarker()
    {
        if (currentTimeMarker is null)
        {
            return;
        }

        TimelineCanvas.Children.Remove(currentTimeMarker);
        currentTimeMarker = null;
    }

    private void DrawCalendarBackgrounds(double chartWidth, TimeSpan duration)
    {
        if (viewModel is null || duration <= TimeSpan.Zero)
        {
            return;
        }

        for (var row = 0; row < viewModel.Machines.Count; row++)
        {
            var machine = viewModel.Machines[row];
            var y = HeaderHeight + row * RowHeight;
            foreach (var interval in machine.NonWorkingWindows ?? [])
            {
                var clippedStart = interval.StartsAt < viewModel.HorizonStart
                    ? viewModel.HorizonStart
                    : interval.StartsAt;
                var clippedEnd = interval.EndsAt > viewModel.HorizonEnd
                    ? viewModel.HorizonEnd
                    : interval.EndsAt;
                if (clippedEnd <= clippedStart)
                {
                    continue;
                }

                var x = LabelWidth
                    + chartWidth * (clippedStart - viewModel.HorizonStart).TotalSeconds
                        / duration.TotalSeconds;
                var width = Math.Max(
                    1,
                    chartWidth * (clippedEnd - clippedStart).TotalSeconds
                        / duration.TotalSeconds);
                var background = new Border
                {
                    // Deliberately no TimelineInterval Tag: this is calendar
                    // context, not a second operation/capacity block.
                    Background = CalendarBackgroundBrush,
                    BorderBrush = CalendarBackgroundEdgeBrush,
                    BorderThickness = new Thickness(0, 0, 1, 0),
                    Width = width,
                    Height = RowHeight,
                    ToolTip = CalendarBackgroundToolTip(interval),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(background, x);
                Canvas.SetTop(background, y);
                TimelineCanvas.Children.Add(background);
            }
        }
    }

    private void DrawTimeGrid(DateTimeOffset start, DateTimeOffset end, double chartWidth)
    {
        var totalHours = (end - start).TotalHours;
        var tickHours = SelectHourTickHours(totalHours, chartWidth);
        var displayZone = DisplayTimeZone();
        var (dayStart, dayEnd) = DisplayDayWindow();
        for (double hours = 0; hours <= totalHours; hours += tickHours)
        {
            var x = LabelWidth + chartWidth * hours / totalHours;
            var instant = start.AddHours(hours);
            AddLine(
                x,
                HeaderHeight - 5,
                x,
                HeaderHeight,
                Color.FromRgb(220, 224, 229),
                0.75);
            var foreground = IsDaylightHour(instant, displayZone, dayStart, dayEnd)
                ? Brushes.DimGray
                : Brushes.White;
            AddText(
                FormatTimeScaleHour(instant, displayZone, tickHours),
                x + 3,
                DateHeaderRowHeight,
                11,
                foreground,
                FontWeights.SemiBold);
        }

        var startDisplay = TimeZoneInfo.ConvertTime(start, displayZone);
        AddText(
            startDisplay.ToString("ddd dd MMM", CultureInfo.InvariantCulture),
            LabelWidth + 3,
            3,
            11,
            IsDaylightHour(start, displayZone, dayStart, dayEnd) ? Brushes.DimGray : Brushes.White,
            FontWeights.SemiBold);

        // A sampled tick can skip midnight (for example a 03:30 tick grid), so
        // add every local date boundary independently of the hour-label density.
        foreach (var boundary in LocalDateBoundaries(start, end, displayZone, chartWidth))
        {
            var x = LabelWidth + chartWidth * (boundary - start).TotalSeconds / (end - start).TotalSeconds;
            var displayBoundary = TimeZoneInfo.ConvertTime(boundary, displayZone);
            var foreground = IsDaylightHour(boundary, displayZone, dayStart, dayEnd)
                ? Brushes.DimGray
                : Brushes.White;
            AddLine(x, HeaderHeight - 5, x, TimelineCanvas.Height, Color.FromRgb(220, 224, 229), 1);
            AddText(
                displayBoundary.ToString("ddd dd MMM", CultureInfo.InvariantCulture),
                x + 3,
                3,
                11,
                foreground,
                FontWeights.SemiBold);
        }

        AddText(
            $"Factory time: {displayZone.Id} | DAY {dayStart:hh\\:mm}-{dayEnd:hh\\:mm} | DARK outside",
            4,
            3,
            9,
            Brushes.DimGray,
            FontWeights.SemiBold);
    }

    internal static IReadOnlyList<DateTimeOffset> LocalDateBoundaries(
        DateTimeOffset start,
        DateTimeOffset end,
        TimeZoneInfo zone,
        double chartWidth)
    {
        var first = TimeZoneInfo.ConvertTime(start, zone).Date.AddDays(1);
        var last = TimeZoneInfo.ConvertTime(end, zone).Date;
        var totalDays = Math.Max(1, (end - start).TotalDays);
        var pixelsPerDay = chartWidth / totalDays;
        var cadenceDays = Math.Max(1, (int)Math.Ceiling(80 / Math.Max(1, pixelsPerDay)));
        var boundaries = new List<DateTimeOffset>();
        var dayIndex = 0;
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            var boundary = LocalToUtc(date, zone);
            if (dayIndex % cadenceDays == 0 && boundary > start && boundary < end)
            {
                boundaries.Add(boundary);
            }

            dayIndex++;
        }

        return boundaries;
    }

    internal static double SelectHourTickHours(double totalHours, double chartWidth)
    {
        if (totalHours <= 0 || chartWidth <= 0)
        {
            return 1;
        }

        var pixelsPerHour = chartWidth / totalHours;
        foreach (var step in new[] { 1d, 2d, 3d, 6d, 12d, 24d, 48d, 72d, 168d, 336d, 720d, 1440d })
        {
            if (step * pixelsPerHour >= 24)
            {
                return step;
            }
        }

        return 1440;
    }

    private void DrawTimeScaleBackgrounds(DateTimeOffset start, DateTimeOffset end, double chartWidth)
    {
        var duration = end - start;
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var displayZone = DisplayTimeZone();
        var (dayStart, dayEnd) = DisplayDayWindow();
        var plan = BuildTimeScaleRenderPlan(start, end, displayZone, dayStart, dayEnd, 512, chartWidth);
        var drawing = new DrawingGroup();
        foreach (var span in plan)
        {
            var x = chartWidth * (span.Start - start).TotalSeconds / duration.TotalSeconds;
            var width = Math.Max(
                1,
                chartWidth * (span.End - span.Start).TotalSeconds / duration.TotalSeconds);
            drawing.Children.Add(new GeometryDrawing(
                span.IsMixed
                    ? TimeScaleMixedBrush
                    : span.Daylight ? TimeScaleDaylightBrush : TimeScaleDarkBrush,
                null,
                new RectangleGeometry(new Rect(x, 0, width, HeaderHeight))));
        }

        drawing.Freeze();
        var background = new Border
        {
            // This marker is time-scale context, never a timeline block.
            Tag = null,
            Background = new DrawingBrush(drawing)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                TileMode = TileMode.None
            },
            Width = chartWidth,
            Height = HeaderHeight,
            ToolTip = TimeScaleToolTip(displayZone, dayStart, dayEnd),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(background, LabelWidth);
        Canvas.SetTop(background, 0);
        TimelineCanvas.Children.Add(background);
    }

    private TimeZoneInfo DisplayTimeZone()
    {
        var id = viewModel?.DisplayTimeZoneId;
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Older Servers did not return display metadata. Keep the view
                // useful with the workstation/factory timezone as a fallback.
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Local;
    }

    private (TimeSpan Start, TimeSpan End) DisplayDayWindow()
    {
        var start = ParseLocalClock(viewModel?.DayStartsAtLocal, LegacyDayStart);
        var end = ParseLocalClock(viewModel?.DayEndsAtLocal, LegacyDayEnd);
        return start == end ? (LegacyDayStart, LegacyDayEnd) : (start, end);
    }

    private static TimeSpan ParseLocalClock(string? value, TimeSpan fallback) =>
        TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToTimeSpan()
            : fallback;

    internal static string FormatTimeScaleHour(
        DateTimeOffset instant,
        TimeZoneInfo displayZone,
        double tickHours)
    {
        var displayInstant = TimeZoneInfo.ConvertTime(instant, displayZone);
        var format = displayZone.IsAmbiguousTime(displayInstant.DateTime)
            ? "HH:mm zzz"
            : "HH";
        return displayInstant.ToString(format, CultureInfo.InvariantCulture);
    }

    internal static IReadOnlyList<TimeScaleSpan> BuildTimeScaleSpans(
        DateTimeOffset start,
        DateTimeOffset end,
        TimeZoneInfo zone,
        TimeSpan dayStart,
        TimeSpan dayEnd)
    {
        var localStart = TimeZoneInfo.ConvertTime(start, zone).Date.AddDays(-1);
        var localEnd = TimeZoneInfo.ConvertTime(end, zone).Date.AddDays(1);
        var spans = new List<TimeScaleSpan>();
        for (var date = localStart; date <= localEnd; date = date.AddDays(1))
        {
            var boundaries = new[]
            {
                LocalToUtc(date, zone),
                LocalToUtc(date + dayStart, zone),
                LocalToUtc(date + dayEnd, zone),
                LocalToUtc(date.AddDays(1), zone)
            }.OrderBy(value => value).Distinct().ToArray();
            for (var index = 0; index + 1 < boundaries.Length; index++)
            {
                var segmentStart = boundaries[index] < start ? start : boundaries[index];
                var segmentEnd = boundaries[index + 1] > end ? end : boundaries[index + 1];
                if (segmentEnd <= segmentStart)
                {
                    continue;
                }

                var midpoint = segmentStart + (segmentEnd - segmentStart) / 2;
                spans.Add(new TimeScaleSpan(
                    segmentStart,
                    segmentEnd,
                    IsDaylightHour(midpoint, zone, dayStart, dayEnd)));
            }
        }

        var merged = new List<TimeScaleSpan>();
        foreach (var span in spans.OrderBy(value => value.Start))
        {
            if (merged.Count > 0
                && merged[^1].Daylight == span.Daylight
                && merged[^1].End == span.Start)
            {
                merged[^1] = merged[^1] with { End = span.End };
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    internal static IReadOnlyList<TimeScaleSpan> BuildTimeScaleRenderPlan(
        DateTimeOffset start,
        DateTimeOffset end,
        TimeZoneInfo zone,
        TimeSpan dayStart,
        TimeSpan dayEnd,
        int maxSegments,
        double visibleChartWidth = 0)
    {
        var exact = BuildTimeScaleSpans(start, end, zone, dayStart, dayEnd);
        var resolutionCap = visibleChartWidth > 0
            ? (int)Math.Ceiling(visibleChartWidth * 2)
            : 0;
        var renderCap = Math.Max(maxSegments, resolutionCap);
        if (exact.Count <= renderCap || renderCap <= 0)
        {
            return exact;
        }

        var duration = end - start;
        var result = new List<TimeScaleSpan>(renderCap);
        var exactIndex = 0;
        for (var index = 0; index < renderCap; index++)
        {
            var segmentStart = start + SplitDuration(duration, index, renderCap);
            var segmentEnd = index == renderCap - 1
                ? end
                : start + SplitDuration(duration, index + 1, renderCap);
            while (exactIndex < exact.Count && exact[exactIndex].End <= segmentStart)
            {
                exactIndex++;
            }

            var scanIndex = exactIndex;
            long daylightTicks = 0;
            while (scanIndex < exact.Count && exact[scanIndex].Start < segmentEnd)
            {
                var span = exact[scanIndex];
                if (span.Daylight)
                {
                    daylightTicks += OverlapTicks(
                        span.Start, span.End, segmentStart, segmentEnd);
                }

                if (span.End <= segmentEnd)
                {
                    scanIndex++;
                }
                else
                {
                    break;
                }
            }

            exactIndex = scanIndex;
            var totalTicks = (segmentEnd - segmentStart).Ticks;
            var darkTicks = totalTicks - daylightTicks;
            var daylight = daylightTicks >= darkTicks;
            var mixed = daylightTicks > 0 && darkTicks > 0;
            if (result.Count > 0
                && result[^1].Daylight == daylight
                && result[^1].IsMixed == mixed)
            {
                result[^1] = result[^1] with { End = segmentEnd };
            }
            else
            {
                result.Add(new TimeScaleSpan(segmentStart, segmentEnd, daylight, mixed));
            }
        }

        return result;
    }

    private static TimeSpan SplitDuration(TimeSpan duration, int index, int count)
    {
        var wholeTicks = duration.Ticks / count;
        var remainderTicks = duration.Ticks % count;
        return TimeSpan.FromTicks(wholeTicks * index + remainderTicks * index / count);
    }

    private static long OverlapTicks(
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset binStart,
        DateTimeOffset binEnd)
    {
        var overlapStart = start > binStart ? start : binStart;
        var overlapEnd = end < binEnd ? end : binEnd;
        return overlapEnd > overlapStart ? (overlapEnd - overlapStart).Ticks : 0;
    }

    private static DateTimeOffset LocalToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }

    internal readonly record struct TimeScaleSpan(
        DateTimeOffset Start,
        DateTimeOffset End,
        bool Daylight,
        bool IsMixed = false);

    private void DrawMachineRow(
        TimelineMachine machine,
        int row,
        double chartWidth,
        TimeSpan duration)
    {
        var y = HeaderHeight + row * RowHeight;
        var machineLabel = MachineDisplayLabel(machine);
        var machineLabelBlock = AddText(
            machineLabel, 4, y + 7, 10, Brushes.Black, FontWeights.SemiBold);
        machineLabelBlock.Width = LabelWidth - 10;
        machineLabelBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        machineLabelBlock.ToolTip = machineLabel;
        AddLine(0, y + RowHeight, TimelineCanvas.Width, y + RowHeight, Color.FromRgb(220, 224, 229), 1);

        var visibleIntervals = machine.Intervals
            .Where(interval =>
                IsDefaultTimelineIntervalVisible(interval)
                && ((interval.EndsAt > viewModel!.HorizonStart
                        && interval.StartsAt < viewModel!.HorizonEnd)
                    || IsBoundaryBlockedMarker(
                        interval, viewModel!.HorizonStart, viewModel.HorizonEnd)))
            .ToArray();
        var laneAssignments = PartitionIntervals(visibleIntervals);
        var laneCounts = laneAssignments
            .GroupBy(value => value.PrimaryLane)
            .ToDictionary(group => group.Key, group => group.Max(value => value.Lane) + 1);
        for (var intervalIndex = 0; intervalIndex < visibleIntervals.Length; intervalIndex++)
        {
            var interval = visibleIntervals[intervalIndex];
            var clippedStart = interval.StartsAt < viewModel!.HorizonStart
                ? viewModel.HorizonStart
                : interval.StartsAt;
            var clippedEnd = interval.EndsAt > viewModel.HorizonEnd
                ? viewModel.HorizonEnd
                : interval.EndsAt;
            var boundaryBlockedMarker = IsBoundaryBlockedMarker(
                interval, viewModel.HorizonStart, viewModel.HorizonEnd);
            if (clippedEnd <= clippedStart && !boundaryBlockedMarker)
            {
                continue;
            }

            var x = LabelWidth + chartWidth * (clippedStart - viewModel.HorizonStart).TotalSeconds / duration.TotalSeconds;
            var width = boundaryBlockedMarker
                ? 8
                : Math.Max(8, chartWidth * (clippedEnd - clippedStart).TotalSeconds / duration.TotalSeconds);
            if (boundaryBlockedMarker)
            {
                x = Math.Clamp(x, LabelWidth, LabelWidth + chartWidth - width);
            }
            var usesPrimaryLane = UsesPrimaryLane(interval);
            var availableHeight = usesPrimaryLane ? AssignmentLaneHeight : CapacityLaneHeight;
            var laneTop = usesPrimaryLane ? AssignmentLaneTop : CapacityLaneTop;
            var lane = laneAssignments[intervalIndex].Lane;
            var laneHeight = availableHeight / Math.Max(1, laneCounts[usesPrimaryLane]);
            var label = TimelineBlockLabel(interval);
            var hasRenderablePhases = HasRenderablePhases(interval);
            var block = new Border
            {
                Tag = interval,
                Width = width,
                Height = laneHeight,
                Background = hasRenderablePhases
                    ? Brushes.Transparent
                    : IntervalBrush(interval),
                BorderBrush = hasRenderablePhases ? Brushes.Transparent : Brushes.White,
                BorderThickness = hasRenderablePhases ? new Thickness(0) : new Thickness(1),
                ToolTip = $"{label}\nLocal: {FormatLocal(interval.StartsAt, DisplayTimeZone())} → {FormatLocal(interval.EndsAt, DisplayTimeZone())}\n{interval.Detail}",
                Child = BuildIntervalContent(
                    interval, label, width, laneHeight, clippedStart, clippedEnd)
            };
            block.ToolTip = IntervalToolTip(interval, label, DisplayTimeZone());
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, y + laneTop + lane * laneHeight);
            TimelineCanvas.Children.Add(block);
        }
    }

    private void DrawDependencyArrows(double chartWidth, TimeSpan duration)
    {
        if (viewModel is null || duration <= TimeSpan.Zero)
        {
            return;
        }

        // Build endpoints once. Looking them up separately for every dependency used
        // to scan every machine and every interval twice per arrow.
        var endpoints = new Dictionary<string, (int Row, DateTimeOffset StartsAt, DateTimeOffset EndsAt)>(
            StringComparer.Ordinal);
        for (var row = 0; row < viewModel.Machines.Count; row++)
        {
            foreach (var interval in viewModel.Machines[row].Intervals.Where(IsOperationWorkInterval))
            {
                if (interval.OperationId is not { Length: > 0 } operationId)
                {
                    continue;
                }

                if (endpoints.TryGetValue(operationId, out var existing))
                {
                    endpoints[operationId] = (
                        existing.Row,
                        interval.StartsAt < existing.StartsAt ? interval.StartsAt : existing.StartsAt,
                        interval.EndsAt > existing.EndsAt ? interval.EndsAt : existing.EndsAt);
                }
                else
                {
                    endpoints.Add(operationId, (row, interval.StartsAt, interval.EndsAt));
                }
            }
        }

        foreach (var dependency in viewModel.SelectedDependencies)
        {
            if (!endpoints.TryGetValue(dependency.FromOperationId, out var from)
                || !endpoints.TryGetValue(dependency.ToOperationId, out var to))
            {
                continue;
            }

            var x1 = LabelWidth + chartWidth * (from.EndsAt - viewModel.HorizonStart).TotalSeconds / duration.TotalSeconds;
            var x2 = LabelWidth + chartWidth * (to.StartsAt - viewModel.HorizonStart).TotalSeconds / duration.TotalSeconds;
            var y1 = HeaderHeight + from.Row * RowHeight + AssignmentLaneTop + AssignmentLaneHeight / 2;
            var y2 = HeaderHeight + to.Row * RowHeight + AssignmentLaneTop + AssignmentLaneHeight / 2;
            var line = new System.Windows.Shapes.Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = new SolidColorBrush(Color.FromRgb(94, 53, 177)),
                StrokeThickness = 2,
                StrokeDashArray = [3, 2],
                ToolTip = dependency.Summary
            };
            TimelineCanvas.Children.Add(line);
            AddArrowHead(x1, y1, x2, y2, dependency.Summary);
        }
    }

    private void AddArrowHead(double x1, double y1, double x2, double y2, string tooltip)
    {
        var angle = Math.Atan2(y2 - y1, x2 - x1);
        const double length = 8;
        var left = new Point(
            x2 - length * Math.Cos(angle - Math.PI / 6),
            y2 - length * Math.Sin(angle - Math.PI / 6));
        var right = new Point(
            x2 - length * Math.Cos(angle + Math.PI / 6),
            y2 - length * Math.Sin(angle + Math.PI / 6));
        var head = new System.Windows.Shapes.Polygon
        {
            Points = [new Point(x2, y2), left, right],
            Fill = new SolidColorBrush(Color.FromRgb(94, 53, 177)),
            ToolTip = tooltip
        };
        TimelineCanvas.Children.Add(head);
    }

    internal static string IntervalLabel(TimelineInterval interval)
    {
        var type = interval.IsBlocked ? "BLOCKED" : interval.IsHold ? "HOLD" : IntervalTypeLabel(interval.Type);
        var ownsAssignment = !string.IsNullOrWhiteSpace(interval.MachineAssignmentId);
        if (interval.IsBlocked)
        {
            if (ownsAssignment && interval.OperationNumber.HasValue)
            {
                return $"{type} • {interval.PartNumber}/{interval.BatchNumber} OP{interval.OperationNumber} {interval.OperationName}"
                    + (string.IsNullOrWhiteSpace(interval.Detail) ? string.Empty : $" • {interval.Detail}");
            }

            return string.IsNullOrWhiteSpace(interval.Detail) ? type : $"{type} • {interval.Detail}";
        }

        // Capacity annotations are deliberately anonymous in the bar itself.
        // Their operation identity (when present) remains available in the
        // tooltip, but must not look like a second operation block.
        if (!ownsAssignment && !IsIdentifiedActualHistory(interval))
        {
            return string.IsNullOrWhiteSpace(interval.Detail) ? type : $"{type} • {interval.Detail}";
        }

        if (!interval.OperationNumber.HasValue)
        {
            return string.IsNullOrWhiteSpace(interval.Detail) ? type : $"{type} • {interval.Detail}";
        }

        var operationLabel =
            $"{type} • {interval.PartNumber}/{interval.BatchNumber} OP{interval.OperationNumber} {interval.OperationName}".TrimEnd();
        return ownsAssignment
            ? $"{operationLabel} • {interval.PlanningModeLabel}"
            : operationLabel;
    }

    internal static string TimelineBlockLabel(TimelineInterval interval) =>
        UsesPrimaryLane(interval)
            ? $"{interval.TimingLabel}: {IntervalLabel(interval)}"
            : IntervalLabel(interval);

    internal static string IntervalTypeLabel(string type) => type.Trim().ToLowerInvariant() switch
    {
        "actual_history" => "ACTUAL HISTORY",
        "assignment_annotation" => "CAPACITY",
        var normalized => normalized.Replace('_', ' ').ToUpperInvariant()
    };

    internal static bool IsIdentifiedActualHistory(TimelineInterval interval) =>
        string.Equals(interval.Type, "actual_history", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(interval.OperationId);

    internal static Brush CalendarBackgroundBrush => CreateFrozenBrush(
        Color.FromArgb(224, 224, 228, 232));

    internal static Brush CalendarBackgroundEdgeBrush => CreateFrozenBrush(
        Color.FromRgb(189, 198, 205));

    // The ruler uses muted, high-contrast fills so daylight/dark context remains
    // visible without competing with operation status colors in Machine rows.
    internal static Brush TimeScaleDaylightBrush => CreateFrozenBrush(
        Color.FromRgb(255, 243, 205));

    internal static Brush TimeScaleDarkBrush => CreateFrozenBrush(
        Color.FromRgb(55, 71, 90));

    internal static Brush TimeScaleMixedBrush => CreateFrozenBrush(
        Color.FromRgb(143, 151, 158));

    internal static Brush TimeScaleBoundaryBrush => CreateFrozenBrush(
        Color.FromArgb(80, 255, 255, 255));

    internal static Brush CurrentTimeMarkerBrush => CreateFrozenBrush(
        Color.FromRgb(198, 40, 40));

    internal static bool IsCurrentTimeWithinHorizon(
        DateTimeOffset now,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd) =>
        now >= horizonStart && now < horizonEnd;

    internal static double CurrentTimeMarkerX(
        DateTimeOffset now,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        double labelWidth,
        double chartWidth)
    {
        if (horizonEnd <= horizonStart)
        {
            return labelWidth;
        }

        var fraction = (now - horizonStart).TotalSeconds
            / (horizonEnd - horizonStart).TotalSeconds;
        return labelWidth + chartWidth * fraction;
    }

    internal static string CurrentTimeMarkerLabel(DateTimeOffset now, TimeZoneInfo displayZone)
    {
        var local = TimeZoneInfo.ConvertTime(now, displayZone);
        return $"NOW {local:yyyy-MM-dd HH:mm zzz}";
    }

    internal static bool IsDaylightHour(DateTimeOffset instant) =>
        IsDaylightHour(instant, TimeZoneInfo.Utc, LegacyDayStart, LegacyDayEnd);

    internal static bool IsDaylightHour(
        DateTimeOffset instant,
        TimeZoneInfo zone,
        TimeSpan dayStart,
        TimeSpan dayEnd)
    {
        var localTime = TimeZoneInfo.ConvertTime(instant, zone).TimeOfDay;
        return dayStart < dayEnd
            ? localTime >= dayStart && localTime < dayEnd
            : localTime >= dayStart || localTime < dayEnd;
    }

    internal static string TimeScaleToolTip(bool daylight) => daylight
        ? "DAYLIGHT HOURS (06:00-18:00)"
        : "DARK HOURS (18:00-06:00)";

    internal static string TimeScaleToolTip(
        bool daylight,
        TimeZoneInfo zone,
        TimeSpan dayStart,
        TimeSpan dayEnd) =>
        daylight
            ? $"DAYLIGHT HOURS ({dayStart:hh\\:mm}-{dayEnd:hh\\:mm} {zone.Id})"
            : $"DARK HOURS (outside configured DAY window {dayStart:hh\\:mm}-{dayEnd:hh\\:mm} {zone.Id})";

    internal static string TimeScaleToolTip(
        TimeZoneInfo zone,
        TimeSpan dayStart,
        TimeSpan dayEnd) =>
        $"DAYLIGHT HOURS ({dayStart:hh\\:mm}-{dayEnd:hh\\:mm} {zone.Id}); "
        + $"DARK HOURS outside configured DAY window ({dayStart:hh\\:mm}-{dayEnd:hh\\:mm})";

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    internal static string CalendarBackgroundToolTip(TimelineNonWorkingWindow interval)
    {
        var detail = string.IsNullOrWhiteSpace(interval.Detail)
            ? string.Empty
            : $"\n{interval.Detail}";
        return $"NON-WORKING CALENDAR\nMachine unavailable according to its configured working calendar."
            + detail;
    }

    internal static bool IsOperationWorkInterval(TimelineInterval interval) =>
        string.Equals(interval.Type, "operation", StringComparison.OrdinalIgnoreCase)
        || IsIdentifiedActualHistory(interval);

    internal static bool HasRenderablePhases(TimelineInterval interval) =>
        string.Equals(interval.Type, "operation", StringComparison.OrdinalIgnoreCase)
        && !interval.IsHold
        && interval.Phases is { Count: > 0 }
        && interval.Phases.Any(phase =>
            phase.EndsAt > phase.StartsAt && IsRenderablePhaseType(phase.Type));

    /// <summary>
    /// The default timeline is a plan view, not a capacity-debug view. Ordinary
    /// waiting/capacity intervals are therefore represented by the empty gap
    /// between real blocks. Assignment-owned blocked intervals remain visible so
    /// an explicit conflict or pause is not hidden from the planner.
    /// </summary>
    internal static bool IsDefaultTimelineIntervalVisible(TimelineInterval interval)
    {
        var type = interval.Type.Trim().ToLowerInvariant();
        if (type is "idle")
        {
            return false;
        }

        return type is not "waiting" || interval.IsBlocked || interval.IsHold;
    }

    private UIElement BuildIntervalContent(
        TimelineInterval interval,
        string label,
        double hostWidth,
        double hostHeight,
        DateTimeOffset clippedHostStart,
        DateTimeOffset clippedHostEnd)
    {
        var canvas = new Canvas
        {
            Width = hostWidth,
            Height = hostHeight,
            ClipToBounds = true
        };
        if (HasRenderablePhases(interval))
        {
            var hostDurationSeconds = (clippedHostEnd - clippedHostStart).TotalSeconds;
            foreach (var phase in interval.Phases!.Where(phase =>
                         phase.EndsAt > phase.StartsAt && IsRenderablePhaseType(phase.Type)))
            {
                var phaseStart = phase.StartsAt < clippedHostStart ? clippedHostStart : phase.StartsAt;
                var phaseEnd = phase.EndsAt > clippedHostEnd ? clippedHostEnd : phase.EndsAt;
                if (phaseEnd <= phaseStart)
                {
                    continue;
                }

                var phaseX = hostWidth * (phaseStart - clippedHostStart).TotalSeconds / hostDurationSeconds;
                var phaseWidth = Math.Max(
                    1,
                    hostWidth * (phaseEnd - phaseStart).TotalSeconds / hostDurationSeconds);
                var segment = new Border
                {
                    Width = phaseWidth,
                    Height = hostHeight,
                    Background = PhaseBrush(phase.Type),
                    ToolTip = string.IsNullOrWhiteSpace(phase.Detail)
                        ? PhaseLabel(phase.Type)
                        : $"{PhaseLabel(phase.Type)}: {phase.Detail}"
                };
                Canvas.SetLeft(segment, phaseX);
                Canvas.SetTop(segment, 0);
                canvas.Children.Add(segment);
            }
        }

        canvas.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = UsesPrimaryLane(interval) ? 9 : 7,
            FontWeight = FontWeights.SemiBold,
            Foreground = HasRenderablePhases(interval) ? Brushes.Black : Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(UsesPrimaryLane(interval) ? 5 : 2, 0, UsesPrimaryLane(interval) ? 5 : 2, 0)
        });
        return canvas;
    }

    internal static bool IsRenderablePhaseType(string type) => type.Trim().ToLowerInvariant() is
        "setup" or "qa" or "qc" or "qualitycontrol" or "quality_control"
        or "loadunload" or "load_unload" or "load/unload" or "partreload" or "part_reload"
        or "production" or "operation" or "reserved";

    internal static string PhaseLabel(string type) => type.Trim().ToLowerInvariant() switch
    {
        "setup" => "SETUP",
        "qa" or "qc" or "qualitycontrol" or "quality_control" => "QC",
        "loadunload" or "load_unload" or "load/unload" or "partreload" or "part_reload" => "PART RELOAD",
        "production" or "operation" => "PRODUCTION",
        "reserved" => "RESERVED",
        var normalized => normalized.Replace('_', ' ').ToUpperInvariant()
    };

    internal static Brush PhaseBrush(string type) => type.Trim().ToLowerInvariant() switch
    {
        "reserved" => CreateFrozenBrush(Color.FromRgb(245, 124, 0)),
        "setup" => CreateFrozenBrush(Color.FromRgb(251, 192, 45)),
        "qa" or "qc" or "qualitycontrol" or "quality_control"
            => CreateFrozenBrush(Color.FromRgb(67, 160, 71)),
        "loadunload" or "load_unload" or "load/unload" or "partreload" or "part_reload"
            => CreateFrozenBrush(Color.FromRgb(123, 31, 162)),
        "production" or "operation"
            => CreateFrozenBrush(Color.FromRgb(30, 136, 229)),
        _ => Brushes.Transparent
    };

    internal static bool IsBoundaryBlockedMarker(
        TimelineInterval interval,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd) =>
        interval.IsBlocked
        && interval.StartsAt == interval.EndsAt
        && interval.StartsAt >= horizonStart
        && interval.StartsAt <= horizonEnd;

    internal static bool UsesPrimaryLane(TimelineInterval interval) =>
        !interval.IsBlocked
        && (IsOperationWorkInterval(interval)
        || !string.IsNullOrWhiteSpace(interval.MachineAssignmentId));

    internal static IReadOnlyList<(bool PrimaryLane, int Lane)> PartitionIntervals(
        IReadOnlyList<TimelineInterval> intervals)
    {
        var result = new (bool PrimaryLane, int Lane)[intervals.Count];
        foreach (var group in intervals
                     .Select((interval, index) => (interval, index))
                     .GroupBy(value => UsesPrimaryLane(value.interval)))
        {
            var laneEnds = new List<(DateTimeOffset End, bool PointMarker)>();
            foreach (var item in group
                         .OrderBy(value => value.interval.StartsAt)
                         .ThenBy(value => value.interval.EndsAt)
                         .ThenBy(value => value.interval.OperationId, StringComparer.Ordinal)
                         .ThenBy(value => value.interval.Type, StringComparer.Ordinal)
                         .ThenBy(value => value.index))
            {
                var pointMarker = item.interval.IsBlocked
                    && item.interval.StartsAt == item.interval.EndsAt;
                var lane = 0;
                while (lane < laneEnds.Count
                    && (laneEnds[lane].End > item.interval.StartsAt
                        || (laneEnds[lane].End == item.interval.StartsAt
                            && (pointMarker || laneEnds[lane].PointMarker))))
                {
                    lane++;
                }

                if (lane == laneEnds.Count)
                {
                    laneEnds.Add((item.interval.EndsAt, pointMarker));
                }
                else
                {
                    laneEnds[lane] = (item.interval.EndsAt, pointMarker);
                }

                result[item.index] = (group.Key, lane);
            }
        }

        return result;
    }

    internal static Brush IntervalBrush(TimelineInterval interval) => interval.IsHold
        ? new SolidColorBrush(Color.FromRgb(126, 87, 194))
        : IntervalBrush(interval.Type);

    internal static Brush IntervalBrush(string type) => type.Trim().ToLowerInvariant() switch
    {
        "operation" => new SolidColorBrush(Color.FromRgb(30, 136, 229)),
        "waiting" => new SolidColorBrush(Color.FromRgb(126, 87, 194)),
        "downtime" => new SolidColorBrush(Color.FromRgb(198, 40, 40)),
        "reserved" => new SolidColorBrush(Color.FromRgb(245, 124, 0)),
        "actual_history" => new SolidColorBrush(Color.FromRgb(0, 137, 123)),
        _ => new SolidColorBrush(Color.FromRgb(158, 158, 158))
    };

    internal static string MachineDisplayLabel(TimelineMachine machine) =>
        $"{machine.Number} \u2014 {machine.Name}";

    private TextBlock AddText(
        string text,
        double left,
        double top,
        double fontSize,
        Brush foreground,
        FontWeight? weight = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Foreground = foreground,
            FontWeight = weight ?? FontWeights.Normal
        };
        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, top);
        TimelineCanvas.Children.Add(block);
        return block;
    }

    internal static string IntervalToolTip(TimelineInterval interval, string label) =>
        IntervalToolTip(interval, label, TimeZoneInfo.Local);

    internal static string IntervalToolTip(
        TimelineInterval interval,
        string label,
        TimeZoneInfo displayZone)
    {
        var forecast = interval.ForecastStart.HasValue || interval.ForecastEnd.HasValue
            ? $"\nForecast: {FormatLocal(interval.ForecastStart, displayZone)} → {FormatLocal(interval.ForecastEnd, displayZone)}"
            : string.Empty;
        var actual = interval.ActualStart.HasValue || interval.ActualEnd.HasValue
            ? $"\nActual: {FormatLocal(interval.ActualStart, displayZone)} → {FormatLocal(interval.ActualEnd, displayZone)}"
            : string.Empty;
        var detail = string.IsNullOrWhiteSpace(interval.Detail) ? string.Empty : $"\n{interval.Detail}";
        var operation = !string.IsNullOrWhiteSpace(interval.OperationId)
            && interval.OperationNumber.HasValue
            ? $"\nOperation: {interval.PartNumber}/{interval.BatchNumber} OP{interval.OperationNumber} {interval.OperationName}".TrimEnd()
            : string.Empty;
        var workFinishDate = interval.WorkFinishDate?.ToString("yyyy-MM-dd") ?? "—";
        if (string.IsNullOrWhiteSpace(interval.MachineAssignmentId))
        {
            return $"{label}{operation}\nLocal: {FormatLocal(interval.StartsAt, displayZone)} - {FormatLocal(interval.EndsAt, displayZone)}{detail}";
        }

        return $"{label}{operation}\nPlanning mode: {interval.PlanningModeLabel}\nWork Finish Date: {workFinishDate}\n{interval.TimingLabel}\nCalculated start: {FormatLocal(interval.StartsAt, displayZone)}\nCalculated finish: {FormatLocal(interval.EndsAt, displayZone)}{forecast}{actual}{detail}";
    }

    private static string FormatLocal(DateTimeOffset? value, TimeZoneInfo displayZone) => value.HasValue
        ? TimeZoneInfo.ConvertTime(value.Value, displayZone).ToString("yyyy-MM-dd HH:mm zzz")
        : "—";

    private void AddLine(double x1, double y1, double x2, double y2, Color color, double thickness)
    {
        var line = new System.Windows.Shapes.Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness
        };
        TimelineCanvas.Children.Add(line);
    }
}
