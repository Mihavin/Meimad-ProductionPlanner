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
/// Daily history of machine usage according to the Timeline: one stacked bar of hours per day in
/// the Timeline legend colours, with the usage percentage written above it and a dashed "Now" line
/// at today, after which the days are forecast. The kinds are named in the legend beside the chart
/// and in the tooltip of each bar, so the chart reads without colour.
/// </summary>
internal sealed class MachineUsageHistoryChart : FrameworkElement
{
    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<MachineUsageDayInfo>), typeof(MachineUsageHistoryChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TodayProperty = DependencyProperty.Register(
        nameof(Today), typeof(DateTime?), typeof(MachineUsageHistoryChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Top = 34, Bottom = 22, Left = 44, Right = 8;
    private static readonly Brush GridBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)));
    private static readonly Brush NowBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)));
    private static readonly Pen OutlinePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 0.5));
    private static readonly Pen NowPen = Frozen(new Pen(NowBrush, 1.5) { DashStyle = DashStyles.Dash });

    private static readonly Dictionary<string, Brush> KindBrushes = MachineUsageReportDocument.Kinds.ToDictionary(
        kind => kind.Name,
        kind => (Brush)Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(kind.Color))));

    public MachineUsageHistoryChart()
    {
        ToolTipService.SetInitialShowDelay(this, 150);
        MinHeight = 160;
        Loaded += (_, _) => LocalizationService.Current.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
    }

    public IReadOnlyList<MachineUsageDayInfo>? Days
    {
        get => (IReadOnlyList<MachineUsageDayInfo>?)GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    /// <summary>The day of the report's calculation; later days are Timeline forecast.</summary>
    public DateTime? Today
    {
        get => (DateTime?)GetValue(TodayProperty);
        set => SetValue(TodayProperty, value);
    }

    internal static Brush KindBrush(string name) => KindBrushes[name];

    private void OnLanguageChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext context)
    {
        // Technical plots retain the same paper/ink contrast as their printable report.
        context.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
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
            var label = Text($"{hours:0} h", dpi, TextBrush);
            context.DrawText(label, new Point(Left - 4 - label.Width, Y(hours) - label.Height / 2));
        }

        var today = Today is { } value ? DateOnly.FromDateTime(value) : (DateOnly?)null;
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
                var usage = Text(day.Metrics.UsageText, dpi, TextBrush);
                context.DrawText(usage, new Point(x + barWidth / 2 - usage.Width / 2, Y(stacked) - usage.Height - 1));
            }
            if (index % labelEvery == 0)
            {
                var date = Text(day.Date.ToString("dd/MM", CultureInfo.CurrentCulture), dpi, TextBrush);
                context.DrawText(date, new Point(x + barWidth / 2 - date.Width / 2, ActualHeight - Bottom + 4));
            }
            if (day.Date == today)
            {
                var nowX = Left + slot * index;
                context.DrawLine(NowPen, new Point(nowX, 14), new Point(nowX, Top + plotHeight));
                var now = Text(LocalizationService.Current.Translate("Today") + " →", dpi, NowBrush);
                context.DrawText(now, new Point(nowX + 3, 0));
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
        var today = Today is { } value ? DateOnly.FromDateTime(value) : (DateOnly?)null;
        var text = index >= 0 && index < days.Count ? Describe(days[index], today) : null;
        if (!Equals(ToolTip, text)) ToolTip = text;
    }

    /// <summary>The tooltip of one day: whether it is history or forecast, and every kind written out.</summary>
    internal static string Describe(MachineUsageDayInfo day, DateOnly? today = null)
    {
        string T(string value) => LocalizationService.Current.Translate(value);
        var m = day.Metrics;
        var timing = today is not { } current ? null
            : day.Date < current ? T("History")
            : day.Date == current ? T("Today: history until now, then forecast")
            : T("Forecast");
        return string.Join(Environment.NewLine, new[]
        {
            day.Date.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture) + (timing is null ? string.Empty : $" ({timing})"),
            $"{T("Usage")}: {m.UsageText} ({m.UsedText} / {m.AvailableText}) - {T(m.UsageLevelText)}",
            $"{T("Production")}: {m.ProductionText}",
            $"{T("Machine setup")}: {m.SetupText}",
            $"{T("QC")}: {m.QcText}",
            $"{T("Part reload")}: {m.PartReloadText}",
            $"{T("Reserved")}: {m.ReservedText}",
            $"{T("Hold")}: {m.HoldText}",
            $"{T("Downtime")}: {m.DowntimeText}",
            $"{T("Idle")}: {m.IdleText}",
            $"{T("Outside schedule")}: {m.OutsideScheduleText}"
        });
    }

    private static double MaxHours(IReadOnlyList<MachineUsageDayInfo> days)
    {
        var maxSeconds = Math.Max(3600, days.Max(day => MachineUsageReportDocument.Kinds.Sum(kind => kind.Seconds(day.Metrics))));
        return Math.Ceiling(maxSeconds / 3600.0 / 4) * 4;
    }

    private static FormattedText Text(string value, double dpi, Brush brush) => new(
        value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), 10, brush, dpi);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
