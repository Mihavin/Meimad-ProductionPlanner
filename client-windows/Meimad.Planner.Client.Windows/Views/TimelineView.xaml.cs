using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Views;

public partial class TimelineView : UserControl
{
    private const double LabelWidth = 185;
    private const double HeaderHeight = 24;
    internal const double CompactRowHeight = 38;
    private const double RowHeight = CompactRowHeight;
    internal const double AssignmentLaneTop = 3;
    internal const double AssignmentLaneHeight = 22;
    internal const double CapacityLaneTop = 27;
    internal const double CapacityLaneHeight = 8;
    private TimelineViewModel? viewModel;
    private bool isLoaded;

    internal IReadOnlyList<string> RenderedMachineAssignmentIds => TimelineCanvas.Children
        .OfType<Border>()
        .Select(element => element.Tag)
        .OfType<TimelineInterval>()
        .Where(interval => !string.IsNullOrWhiteSpace(interval.MachineAssignmentId))
        .Select(interval => interval.MachineAssignmentId!)
        .ToArray();

    public TimelineView()
    {
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
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        isLoaded = false;
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
            or nameof(TimelineViewModel.SelectedDependencies)
            or nameof(TimelineViewModel.SelectedBatch))
        {
            RenderTimeline();
        }
    }

    private void RenderTimeline()
    {
        var stopwatch = Stopwatch.StartNew();
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

        stopwatch.Stop();
        Trace.WriteLine(
            $"Timeline render completed in {stopwatch.Elapsed.TotalMilliseconds:F1} ms " +
            $"({viewModel.Machines.Count} machines, {viewModel.Machines.Sum(machine => machine.Intervals.Count)} intervals, " +
            $"{TimelineCanvas.Children.Count} visual elements).");
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
        var tickHours = totalHours <= 48 ? 6 : totalHours <= 168 ? 24 : 48;
        for (double hours = 0; hours <= totalHours; hours += tickHours)
        {
            var x = LabelWidth + chartWidth * hours / totalHours;
            AddLine(x, HeaderHeight - 5, x, TimelineCanvas.Height, Color.FromRgb(220, 224, 229), 1);
            var instant = start.AddHours(hours);
            AddText(
                tickHours < 24 ? instant.ToString("ddd HH:mm") : instant.ToString("ddd dd MMM"),
                x + 3,
                3,
                11,
                Brushes.DimGray);
        }
    }

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
                !string.Equals(interval.Type, "idle", StringComparison.OrdinalIgnoreCase)
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
                ToolTip = $"{label}\nLocal: {interval.StartsAt.ToLocalTime():yyyy-MM-dd HH:mm} → {interval.EndsAt.ToLocalTime():yyyy-MM-dd HH:mm}\n{interval.Detail}",
                Child = BuildIntervalContent(
                    interval, label, width, laneHeight, clippedStart, clippedEnd)
            };
            block.ToolTip = IntervalToolTip(interval, label);
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
                    ToolTip = phase.Detail
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
        "setup" or "qa" or "loadunload" or "load_unload" or "load/unload"
        or "production" or "operation" or "reserved";

    internal static Brush PhaseBrush(string type) => type.Trim().ToLowerInvariant() switch
    {
        "reserved" => new SolidColorBrush(Color.FromRgb(245, 124, 0)),
        "setup" or "qa" or "loadunload" or "load_unload" or "load/unload"
            or "production" or "operation"
            => new SolidColorBrush(Color.FromRgb(30, 136, 229)),
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

    internal static string IntervalToolTip(TimelineInterval interval, string label)
    {
        var forecast = interval.ForecastStart.HasValue || interval.ForecastEnd.HasValue
            ? $"\nForecast: {FormatLocal(interval.ForecastStart)} → {FormatLocal(interval.ForecastEnd)}"
            : string.Empty;
        var actual = interval.ActualStart.HasValue || interval.ActualEnd.HasValue
            ? $"\nActual: {FormatLocal(interval.ActualStart)} → {FormatLocal(interval.ActualEnd)}"
            : string.Empty;
        var detail = string.IsNullOrWhiteSpace(interval.Detail) ? string.Empty : $"\n{interval.Detail}";
        var operation = !string.IsNullOrWhiteSpace(interval.OperationId)
            && interval.OperationNumber.HasValue
            ? $"\nOperation: {interval.PartNumber}/{interval.BatchNumber} OP{interval.OperationNumber} {interval.OperationName}".TrimEnd()
            : string.Empty;
        var workFinishDate = interval.WorkFinishDate?.ToString("yyyy-MM-dd") ?? "—";
        if (string.IsNullOrWhiteSpace(interval.MachineAssignmentId))
        {
            return $"{label}{operation}\nLocal: {interval.StartsAt.ToLocalTime():yyyy-MM-dd HH:mm} - {interval.EndsAt.ToLocalTime():yyyy-MM-dd HH:mm}{detail}";
        }

        return $"{label}{operation}\nPlanning mode: {interval.PlanningModeLabel}\nWork Finish Date: {workFinishDate}\n{interval.TimingLabel}\nCalculated start: {interval.StartsAt.ToLocalTime():yyyy-MM-dd HH:mm}\nCalculated finish: {interval.EndsAt.ToLocalTime():yyyy-MM-dd HH:mm}{forecast}{actual}{detail}";
    }

    private static string FormatLocal(DateTimeOffset? value) => value.HasValue
        ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
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
