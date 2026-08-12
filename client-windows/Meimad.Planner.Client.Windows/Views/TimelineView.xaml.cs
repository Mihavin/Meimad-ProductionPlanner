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
    private const double LabelWidth = 155;
    private const double HeaderHeight = 34;
    private const double RowHeight = 54;
    private TimelineViewModel? viewModel;

    public TimelineView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => RenderTimeline();
        SizeChanged += (_, _) => RenderTimeline();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.Machines.CollectionChanged -= OnMachinesChanged;
        }

        viewModel = args.NewValue as TimelineViewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.Machines.CollectionChanged += OnMachinesChanged;
        }

        RenderTimeline();
    }

    private void OnMachinesChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
        RenderTimeline();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TimelineViewModel.HorizonStart)
            or nameof(TimelineViewModel.HorizonEnd)
            or nameof(TimelineViewModel.Machines))
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
        AddText($"{machine.Number}\n{machine.Name}", 4, y + 7, 12, Brushes.Black, FontWeights.SemiBold);
        AddLine(0, y + RowHeight, TimelineCanvas.Width, y + RowHeight, Color.FromRgb(220, 224, 229), 1);

        foreach (var interval in machine.Intervals)
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
            var label = IntervalLabel(interval);
            var block = new Border
            {
                Width = width,
                Height = RowHeight - 12,
                Background = IntervalBrush(interval.Type),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                ToolTip = $"{label}\nLocal: {interval.StartsAt.ToLocalTime():yyyy-MM-dd HH:mm} → {interval.EndsAt.ToLocalTime():yyyy-MM-dd HH:mm}\n{interval.Detail}",
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = interval.Type == "setup" ? Brushes.Black : Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 5, 0)
                }
            };
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, y + 6);
            TimelineCanvas.Children.Add(block);
        }
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
        "downtime" => new SolidColorBrush(Color.FromRgb(198, 40, 40)),
        "reserved" => new SolidColorBrush(Color.FromRgb(245, 124, 0)),
        _ => new SolidColorBrush(Color.FromRgb(158, 158, 158))
    };

    private void AddText(
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
