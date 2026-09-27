using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Meimad.Planner.Client.Windows.Localization;
using Meimad.Planner.Client.Windows.Presentation.ToolPreparation;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>
/// A flat schematic of one prepared tool: the holder at the top on the spindle gauge line, the
/// components below it and the cutter at the bottom, with the measured length and diameter as
/// dimension lines. Unspecified dimensions are drawn dashed. The drawing is always left-to-right.
/// </summary>
internal sealed class ToolShapePreview : FrameworkElement
{
    public static readonly DependencyProperty GeometryProperty = DependencyProperty.Register(
        nameof(Geometry), typeof(ToolShapeGeometry), typeof(ToolShapePreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush HolderBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x82, 0x8C));
    private static readonly Brush BodyBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC6));
    private static readonly Brush CutterBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x8B, 0xD1));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));
    private static readonly Brush DimensionBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly Pen OutlinePen = new(new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x3E)), 1);
    private static readonly Pen DefaultPen = new(new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x3E)), 1) { DashStyle = DashStyles.Dash };
    private static readonly Pen GaugePen = new(new SolidColorBrush(Color.FromRgb(0x5F, 0x66, 0x70)), 1) { DashStyle = DashStyles.DashDot };
    private static readonly Pen DimensionPen = new(DimensionBrush, 1);
    private static readonly Typeface Typeface = new("Segoe UI");

    static ToolShapePreview()
    {
        foreach (var pen in new[] { OutlinePen, DefaultPen, GaugePen, DimensionPen }) pen.Freeze();
        foreach (var brush in new[] { HolderBrush, BodyBrush, CutterBrush, TextBrush, DimensionBrush }) brush.Freeze();
    }

    public ToolShapePreview()
    {
        FlowDirection = FlowDirection.LeftToRight;
        MinHeight = 220;
    }

    public ToolShapeGeometry? Geometry
    {
        get => (ToolShapeGeometry?)GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var geometry = Geometry;
        if (geometry is null || geometry.Segments.Count == 0 || ActualWidth < 60 || ActualHeight < 60) return;

        const double margin = 18;
        const double labelColumn = 96;
        const double dimensionColumn = 64;
        var drawingTop = margin + 14;
        var availableHeight = ActualHeight - drawingTop - margin;
        var availableWidth = ActualWidth - margin * 2 - labelColumn - dimensionColumn;
        var scale = Math.Min(availableHeight / Math.Max(geometry.TotalLength, 1), availableWidth / Math.Max(geometry.MaximumDiameter, 1));
        var centerX = margin + labelColumn + availableWidth / 2;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Spindle gauge line.
        context.DrawLine(GaugePen, new Point(margin, drawingTop), new Point(ActualWidth - margin, drawingTop));
        DrawText(context, Localize("Gauge line"), new Point(margin, drawingTop - 14), 10, TextBrush, dpi);

        foreach (var segment in geometry.Segments)
        {
            var top = drawingTop + segment.Top * scale;
            var height = Math.Max(segment.Height * scale, 1);
            var width = Math.Max(segment.Diameter * scale, 2);
            var left = centerX - width / 2;
            var pen = segment.IsDefault ? DefaultPen : OutlinePen;
            var brush = segment.Kind switch
            {
                "HOLDER" => HolderBrush,
                "CUTTER" or "BALL" or "POINT" or "CONE" or "DISC" or "INSERT" or "SPHERE" or "BLADE" or "DOVETAIL" => CutterBrush,
                _ => BodyBrush
            };
            switch (segment.Kind)
            {
                case "DOVETAIL":
                {
                    // Narrow at the neck, full diameter at the tip.
                    var neckWidth = Math.Max(width * 0.45, 2);
                    var dovetail = new StreamGeometry();
                    using (var figure = dovetail.Open())
                    {
                        figure.BeginFigure(new Point(centerX - neckWidth / 2, top), true, true);
                        figure.LineTo(new Point(centerX + neckWidth / 2, top), true, false);
                        figure.LineTo(new Point(left + width, top + height), true, false);
                        figure.LineTo(new Point(left, top + height), true, false);
                    }
                    dovetail.Freeze();
                    context.DrawGeometry(brush, pen, dovetail);
                    break;
                }
                case "BLADE":
                {
                    // A grooving or parting blade hangs from one side of the holder.
                    var bladeWidth = Math.Max(width, 2);
                    var bladeLeft = centerX + Math.Max(2, geometry.MaximumDiameter * scale * 0.5) - bladeWidth;
                    context.DrawRectangle(brush, pen, new Rect(bladeLeft, top, bladeWidth, height));
                    break;
                }
                case "GAP":
                    // The undescribed part of the assembly: only the spindle axis down to the cutter.
                    context.DrawLine(DefaultPen, new Point(centerX, top), new Point(centerX, top + height));
                    continue;
                case "HOLDER":
                {
                    // Flange at the gauge line, taper below it.
                    var flange = Math.Min(height * 0.3, 10 * scale);
                    context.DrawRectangle(brush, pen, new Rect(left, top, width, flange));
                    var taper = new StreamGeometry();
                    using (var figure = taper.Open())
                    {
                        figure.BeginFigure(new Point(left + width * 0.1, top + flange), true, true);
                        figure.LineTo(new Point(left + width * 0.9, top + flange), true, false);
                        figure.LineTo(new Point(left + width * 0.7, top + height), true, false);
                        figure.LineTo(new Point(left + width * 0.3, top + height), true, false);
                    }
                    taper.Freeze();
                    context.DrawGeometry(brush, pen, taper);
                    break;
                }
                case "POINT":
                case "CONE":
                {
                    var tip = new StreamGeometry();
                    using (var figure = tip.Open())
                    {
                        figure.BeginFigure(new Point(left, top), true, true);
                        figure.LineTo(new Point(left + width, top), true, false);
                        if (segment.Kind == "CONE")
                        {
                            var tipWidth = Math.Max(width * 0.15, 2);
                            figure.LineTo(new Point(centerX + tipWidth / 2, top + height), true, false);
                            figure.LineTo(new Point(centerX - tipWidth / 2, top + height), true, false);
                        }
                        else
                        {
                            figure.LineTo(new Point(centerX, top + height), true, false);
                        }
                    }
                    tip.Freeze();
                    context.DrawGeometry(brush, pen, tip);
                    break;
                }
                case "BALL":
                case "SPHERE":
                {
                    var radius = width / 2;
                    var ball = new StreamGeometry();
                    using (var figure = ball.Open())
                    {
                        figure.BeginFigure(new Point(left, top), true, true);
                        figure.LineTo(new Point(left + width, top), true, false);
                        figure.ArcTo(new Point(left, top), new Size(radius, Math.Max(height, 1)), 0, false, SweepDirection.Clockwise, true, false);
                    }
                    ball.Freeze();
                    context.DrawGeometry(brush, pen, ball);
                    break;
                }
                case "CUTTER":
                {
                    var radius = segment.CornerRadius * scale;
                    context.DrawRoundedRectangle(brush, pen, new Rect(left, top, width, height), 0, 0);
                    if (radius > 0)
                    {
                        context.DrawRoundedRectangle(brush, pen, new Rect(left, top + height - radius * 2, width, radius * 2), radius, radius);
                    }
                    // Flute hint lines.
                    var flutes = Math.Max(1, (int)(height / Math.Max(8, 4 * scale)));
                    for (var index = 1; index <= flutes; index++)
                    {
                        var y = top + height * index / (flutes + 1);
                        context.DrawLine(OutlinePen, new Point(left + width * 0.2, y), new Point(left + width * 0.8, y - Math.Min(6, height * 0.2)));
                    }
                    break;
                }
                case "INSERT":
                {
                    var insert = new StreamGeometry();
                    using (var figure = insert.Open())
                    {
                        figure.BeginFigure(new Point(left, top), true, true);
                        figure.LineTo(new Point(left + width, top), true, false);
                        figure.LineTo(new Point(left + width, top + height), true, false);
                        figure.LineTo(new Point(left + width * 0.35, top + height), true, false);
                    }
                    insert.Freeze();
                    context.DrawGeometry(brush, pen, insert);
                    break;
                }
                default:
                    context.DrawRectangle(brush, pen, new Rect(left, top, width, height));
                    break;
            }

            var label = $"{Localize(segment.Label)} Ø{Format(segment.Diameter)} × {Format(segment.Height)}";
            DrawText(context, label, new Point(margin, top + Math.Max(0, height / 2 - 7)), 10, TextBrush, dpi, labelColumn - 4);
        }

        var tipY = drawingTop + geometry.TotalLength * scale;
        var dimensionX = ActualWidth - margin - dimensionColumn + 10;
        if (geometry.MeasuredLength is { } length)
        {
            context.DrawLine(DimensionPen, new Point(dimensionX, drawingTop), new Point(dimensionX, tipY));
            context.DrawLine(DimensionPen, new Point(dimensionX - 4, drawingTop), new Point(dimensionX + 4, drawingTop));
            context.DrawLine(DimensionPen, new Point(dimensionX - 4, tipY), new Point(dimensionX + 4, tipY));
            DrawText(context, $"L {Format(length)}", new Point(dimensionX + 6, (drawingTop + tipY) / 2 - 7), 11, DimensionBrush, dpi);
        }
        if (geometry.MeasuredDiameter is { } diameter)
        {
            var width = Math.Max(diameter * scale, 2);
            var y = Math.Min(tipY + 10, ActualHeight - margin);
            context.DrawLine(DimensionPen, new Point(centerX - width / 2, y), new Point(centerX + width / 2, y));
            DrawText(context, $"Ø {Format(diameter)}", new Point(centerX + width / 2 + 4, y - 7), 11, DimensionBrush, dpi);
        }
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    // Drawn text bypasses the XAML localization behavior, so the labels go through the catalog here.
    private static string Localize(string text) => LocalizationService.Current.Translate(text);

    private static void DrawText(DrawingContext context, string text, Point origin, double size, Brush brush, double dpi, double maxWidth = 0)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, size, brush, dpi);
        if (maxWidth > 0)
        {
            formatted.MaxTextWidth = maxWidth;
            formatted.MaxLineCount = 2;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
        }
        context.DrawText(formatted, origin);
    }
}
