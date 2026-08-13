using System.Collections.Specialized;
using System.ComponentModel;
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
    internal const double CompactRowHeight = 30;
    private const double RowHeight = CompactRowHeight;
    private TimelineViewModel? viewModel;
    private bool isLoaded;

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
        TimelineCanvas.Children.Clear();
        if (viewModel is null
            || viewModel.HorizonEnd <= viewModel.HorizonStart
            || viewModel.Machines.Count == 0)
        {
            TimelineCanvas.Width = Math.Max(700, ActualWidth - 60);
            TimelineCanvas.Height = 100;
            AddText("No calculated Machine intervals in this range.", 12, 35, 15, Brushes.DimGray);
            return;
        }

        var duration = viewModel.HorizonEnd - viewModel.HorizonStart;
        var chartWidth = Math.Max(900, Math.Min(6000, duration.TotalHours * 22));
        TimelineCanvas.Width = LabelWidth + chartWidth + 18;
        TimelineCanvas.Height = HeaderHeight + viewModel.Machines.Count * RowHeight + 12;
        DrawTimeGrid(viewModel.HorizonStart, viewModel.HorizonEnd, chartWidth);
        DrawDependencyArrows(chartWidth, duration);

        for (var row = 0; row < viewModel.Machines.Count; row++)
        {
            DrawMachineRow(viewModel.Machines[row], row, chartWidth, duration);
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
        var machineLabel = $"{machine.Number} — {machine.Name}";
        var machineLabelBlock = AddText(
            machineLabel = MachineDisplayLabel(machine), 4, y + 7, 10, Brushes.Black, FontWeights.SemiBold);
        machineLabelBlock.Width = LabelWidth - 10;
        machineLabelBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        machineLabelBlock.ToolTip = machineLabel;
        AddLine(0, y + RowHeight, TimelineCanvas.Width, y + RowHeight, Color.FromRgb(220, 224, 229), 1);

        var visibleIntervals = machine.Intervals
            .Where(interval => interval.EndsAt > viewModel!.HorizonStart
                && interval.StartsAt < viewModel.HorizonEnd)
            .ToArray();
        foreach (var interval in visibleIntervals)
        {
            var clippedStart = interval.StartsAt < viewModel!.HorizonStart
                ? viewModel.HorizonStart
                : interval.StartsAt;
            var clippedEnd = interval.EndsAt > viewModel.HorizonEnd
                ? viewModel.HorizonEnd
                : interval.EndsAt;
            if (clippedEnd <= clippedStart)
            {
                continue;
            }

            var x = LabelWidth + chartWidth * (clippedStart - viewModel.HorizonStart).TotalSeconds / duration.TotalSeconds;
            var width = Math.Max(8, chartWidth * (clippedEnd - clippedStart).TotalSeconds / duration.TotalSeconds);
            var exactOverlaps = visibleIntervals.Where(candidate =>
                    candidate.StartsAt == interval.StartsAt
                    && candidate.EndsAt == interval.EndsAt)
                .OrderBy(candidate => candidate.OperationNumber)
                .ThenBy(candidate => candidate.OperationId, StringComparer.Ordinal)
                .ToArray();
            var lane = Array.IndexOf(exactOverlaps, interval);
            var laneHeight = (RowHeight - 6) / Math.Max(1, exactOverlaps.Length);
            var label = IntervalLabel(interval);
            var block = new Border
            {
                Width = width,
                Height = laneHeight,
                Background = IntervalBrush(interval.Type),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                ToolTip = $"{label}\nLocal: {interval.StartsAt.ToLocalTime():yyyy-MM-dd HH:mm} → {interval.EndsAt.ToLocalTime():yyyy-MM-dd HH:mm}\n{interval.Detail}",
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = interval.Type == "setup" ? Brushes.Black : Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 5, 0)
                }
            };
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, y + 3 + lane * laneHeight);
            TimelineCanvas.Children.Add(block);
        }
    }

    private void DrawDependencyArrows(double chartWidth, TimeSpan duration)
    {
        if (viewModel is null || duration <= TimeSpan.Zero)
        {
            return;
        }

        foreach (var dependency in viewModel.SelectedDependencies)
        {
            var from = FindEndpoint(dependency.FromOperationId, isFinish: true);
            var to = FindEndpoint(dependency.ToOperationId, isFinish: false);
            if (from is null || to is null)
            {
                continue;
            }

            var (fromRow, fromAt) = from.Value;
            var (toRow, toAt) = to.Value;
            var x1 = LabelWidth + chartWidth * (fromAt - viewModel.HorizonStart).TotalSeconds / duration.TotalSeconds;
            var x2 = LabelWidth + chartWidth * (toAt - viewModel.HorizonStart).TotalSeconds / duration.TotalSeconds;
            var y1 = HeaderHeight + fromRow * RowHeight + RowHeight / 2;
            var y2 = HeaderHeight + toRow * RowHeight + RowHeight / 2;
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

        (int Row, DateTimeOffset At)? FindEndpoint(string operationId, bool isFinish)
        {
            for (var row = 0; row < viewModel.Machines.Count; row++)
            {
                var matching = viewModel.Machines[row].Intervals
                    .Where(interval => interval.OperationId == operationId)
                    .ToArray();
                if (matching.Length > 0)
                {
                    return (row, isFinish
                        ? matching.Max(interval => interval.EndsAt)
                        : matching.Min(interval => interval.StartsAt));
                }
            }

            return null;
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

    private static string IntervalLabel(TimelineInterval interval)
    {
        var type = interval.Type.ToUpperInvariant();
        return interval.OperationNumber.HasValue
            ? $"{type} • {interval.PartNumber}/{interval.BatchNumber} OP{interval.OperationNumber} {interval.OperationName}".TrimEnd()
            : string.IsNullOrWhiteSpace(interval.Detail) ? type : $"{type} • {interval.Detail}";
    }

    private static Brush IntervalBrush(string type) => type switch
    {
        "setup" => new SolidColorBrush(Color.FromRgb(251, 192, 45)),
        "production" => new SolidColorBrush(Color.FromRgb(30, 136, 229)),
        "waiting" => new SolidColorBrush(Color.FromRgb(126, 87, 194)),
        "downtime" => new SolidColorBrush(Color.FromRgb(198, 40, 40)),
        "reserved" => new SolidColorBrush(Color.FromRgb(245, 124, 0)),
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
