using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Localization;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>
/// Daily history of machine usage: one stacked bar of hours per day (production, setup, downtime,
/// idle, no data, in the functional-spec palette) with the usage percentage written above it. The
/// kinds are named in the legend beside the chart and in the tooltip of each bar, so the chart
/// reads without colour.
/// </summary>
internal sealed class MachineUsageHistoryChart : FrameworkElement
{
    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<MachineUsageDayInfo>), typeof(MachineUsageHistoryChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush NoDataBrush = HatchBrush();
    private const double Top = 20, Bottom = 22, Left = 44, Right = 8;
    private static readonly Brush GridBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)));
    private static readonly Pen OutlinePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 0.5));

    public MachineUsageHistoryChart()
    {
        ToolTipService.SetInitialShowDelay(this, 150);
        MinHeight = 160;
        Loaded += (_, _) => LocalizationService.Current.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => InvalidateVisual();

    public IReadOnlyList<MachineUsageDayInfo>? Days
    {
        get => (IReadOnlyList<MachineUsageDayInfo>?)GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    private static readonly Dictionary<string, Brush> KindBrushes = MachineUsageReportDocument.Kinds.ToDictionary(
        kind => kind.Name,
        kind => kind.Name == "No data"
            ? NoDataBrush
            : (Brush)Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(kind.Color))));

    internal static Brush KindBrush(string name) => KindBrushes[name];

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var days = Days;
        if (days is null || days.Count == 0 || ActualWidth <= Left + Right || ActualHeight <= Top + Bottom) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var plotHeight = ActualHeight - Top - Bottom;
        var slot = (ActualWidth - Left - Right) / days.Count;
        var maxHours = MaxHours(days);
        double Y(double hours) => Top + plotHeight * (1 - hours / maxHours);

        for (var tick = 0; tick <= 4; tick++)
        {
            var hours = maxHours * tick / 4;
            context.DrawLine(new Pen(GridBrush, 1), new Point(Left, Y(hours)), new Point(ActualWidth - Right, Y(hours)));
            var label = Text($"{hours:0} h", dpi);
            context.DrawText(label, new Point(Left - 4 - label.Width, Y(hours) - label.Height / 2));
        }

        var labelEvery = Math.Max(1, (int)Math.Ceiling(42 / slot));
        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index];
            var x = Left + slot * index + slot * 0.15;
            var barWidth = Math.Max(1, slot * 0.7);
            double stacked = 0;
            foreach (var (name, _, seconds) in MachineUsageReportDocument.Kinds)
            {
                var hours = seconds(day.Metrics) / 3600.0;
                if (hours <= 0) continue;
                context.DrawRectangle(KindBrush(name), OutlinePen,
                    new Rect(x, Y(stacked + hours), barWidth, Y(stacked) - Y(stacked + hours)));
                stacked += hours;
            }
            if (day.Metrics.AvailableSeconds > 0 && slot >= 30)
            {
                var usage = Text(day.Metrics.UsageText, dpi);
                context.DrawText(usage, new Point(x + barWidth / 2 - usage.Width / 2, Y(stacked) - usage.Height - 1));
            }
            if (index % labelEvery == 0)
            {
                var date = Text(day.Date.ToString("dd/MM", CultureInfo.CurrentCulture), dpi);
                context.DrawText(date, new Point(x + barWidth / 2 - date.Width / 2, ActualHeight - Bottom + 4));
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var days = Days;
        if (days is null || days.Count == 0) return;
        var slot = (ActualWidth - Left - Right) / days.Count;
        var index = (int)Math.Floor((e.GetPosition(this).X - Left) / slot);
        var text = index >= 0 && index < days.Count ? Describe(days[index]) : null;
        if (!Equals(ToolTip, text)) ToolTip = text;
    }

    /// <summary>The tooltip of one day: every kind in hours and percent, written out.</summary>
    internal static string Describe(MachineUsageDayInfo day)
    {
        string T(string value) => LocalizationService.Current.Translate(value);
        var m = day.Metrics;
        return string.Join(Environment.NewLine,
            $"{day.Date.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture)}",
            $"{T("Usage")}: {m.UsageText} ({m.UsedText} / {m.AvailableText}) - {T(m.UsageLevelText)}",
            $"{T("Production")}: {m.ProductionText}",
            $"{T("Machine setup")}: {m.SetupText}",
            $"{T("Downtime")}: {m.DowntimeText}",
            $"{T("Idle")}: {m.IdleText}",
            $"{T("No data")}: {m.NoDataText}",
            $"{T("Outside schedule")}: {m.OutsideScheduleText}");
    }

    private static double MaxHours(IReadOnlyList<MachineUsageDayInfo> days)
    {
        var maxSeconds = Math.Max(3600, days.Max(day => MachineUsageReportDocument.Kinds.Sum(kind => kind.Seconds(day.Metrics))));
        return Math.Ceiling(maxSeconds / 3600.0 / 4) * 4;
    }

    private static FormattedText Text(string value, double dpi) => new(
        value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), 10, TextBrush, dpi);

    private static Brush HatchBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)), null, new RectangleGeometry(new Rect(0, 0, 6, 6))));
        group.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)), 1.5),
            new LineGeometry(new Point(0, 6), new Point(6, 0))));
        return Frozen(new DrawingBrush(group)
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute
        });
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
