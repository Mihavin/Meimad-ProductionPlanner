using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace Meimad.Planner.Client.Windows.Views;

public partial class StepViewerControl : UserControl
{
    private const long MaximumStepBytes = 64L * 1024 * 1024;
    private const int MaximumPoints = 50_000;
    private const int MaximumSegments = 75_000;
    private static readonly Regex EntityPattern = new(
        @"#(?<id>\d+)\s*=\s*(?<kind>[A-Z0-9_]+)\s*\((?<body>.*?)\)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex NumberPattern = new(
        @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[Ee][-+]?\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ReferencePattern = new(
        @"#(?<id>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<StepPoint3> points = [];
    private readonly List<StepSegment3> segments = [];
    private readonly List<StepTriangle3> triangles = [];
    private readonly List<int> selectablePointIndices = [];
    private Point[] screenPoints = [];
    private Point dragStart;
    private bool isDragging;
    private bool isMeasuring;
    private int? measurementStartIndex;
    private int? measurementEndIndex;
    private StepPoint3 modelCenter;
    private double yaw = -35 * Math.PI / 180;
    private double pitch = 25 * Math.PI / 180;
    private double zoom = 1;

    public StepViewerControl()
    {
        InitializeComponent();
    }

    public bool HasModel => points.Count > 0;

    public bool IsSolidModel => triangles.Count > 0;

    public int TriangleCount => triangles.Count;

    public string? LoadedPath { get; private set; }

    public Point3D ModelCenter => new(modelCenter.X, modelCenter.Y, modelCenter.Z);

    public string MeasurementText { get; private set; } = "Select Distance, then click two model vertices.";

    public StepMeasurement? CurrentMeasurement { get; private set; }

    public event EventHandler? ModelStateChanged;

    public event EventHandler? MeasurementChanged;

    public void LoadStep(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = Path.GetExtension(path);
        if (!string.Equals(extension, ".stp", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".step", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Choose a .stp or .step file.");
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The STEP file was not found.", path);
        }
        if (info.Length > MaximumStepBytes)
        {
            throw new InvalidDataException("The simple STEP viewer supports files up to 64 MiB.");
        }

        StepModelData parsed;
        string? solidFallbackReason = null;
        try
        {
            parsed = StepSolidMeshLoader.Load(path);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                          or TypeInitializationException
                                          or DllNotFoundException
                                          or EntryPointNotFoundException)
        {
            solidFallbackReason = exception.Message;
            parsed = Parse(File.ReadAllText(path));
        }
        points.Clear();
        points.AddRange(parsed.Points);
        segments.Clear();
        segments.AddRange(parsed.Segments);
        triangles.Clear();
        triangles.AddRange(parsed.Triangles);
        selectablePointIndices.Clear();
        selectablePointIndices.AddRange(parsed.SelectablePointIndices);
        modelCenter = GeometryCentroid(points, selectablePointIndices);
        LoadedPath = path;
        yaw = -35 * Math.PI / 180;
        pitch = 25 * Math.PI / 180;
        zoom = 1;
        ClearMeasurement();
        StatusText.Text = segments.Count > 0
            ? $"{Path.GetFileName(path)} · {points.Count:N0} vertices · {segments.Count:N0} edges"
            : $"{Path.GetFileName(path)} · {points.Count:N0} points · no explicit STEP edges found";
        StatusText.Text = IsSolidModel
            ? $"{Path.GetFileName(path)} · shaded solid · {points.Count:N0} vertices · {triangles.Count:N0} triangles"
            : segments.Count > 0
                ? $"{Path.GetFileName(path)} · solid faces unavailable · {segments.Count:N0} fallback edges · {solidFallbackReason}"
                : $"{Path.GetFileName(path)} · solid faces unavailable · {points.Count:N0} fallback points · {solidFallbackReason}";
        FitToWindow();
        Dispatcher.BeginInvoke(FitToWindow, DispatcherPriority.Loaded);
        ModelStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearModel()
    {
        points.Clear();
        segments.Clear();
        triangles.Clear();
        selectablePointIndices.Clear();
        screenPoints = [];
        modelCenter = default;
        LoadedPath = null;
        ClearMeasurement();
        ModelCanvas.Children.Clear();
        SolidSurface.Clear();
        StatusText.Text = "Open a STEP file to preview it.";
        ModelStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetView(string view)
    {
        (yaw, pitch) = view switch
        {
            "front" => (0, 0),
            "top" => (0, Math.PI / 2),
            "right" => (Math.PI / 2, 0),
            _ => (-35 * Math.PI / 180, 25 * Math.PI / 180)
        };
        zoom = 1;
        RenderModel();
    }

    public void FitToWindow()
    {
        zoom = 1;
        RenderModel();
    }

    public void BeginDistanceMeasurement()
    {
        if (!HasModel)
        {
            return;
        }

        isMeasuring = true;
        measurementStartIndex = null;
        measurementEndIndex = null;
        CurrentMeasurement = null;
        MeasurementText = "Click the first model vertex.";
        RenderModel();
        MeasurementChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearMeasurement()
    {
        isMeasuring = false;
        measurementStartIndex = null;
        measurementEndIndex = null;
        CurrentMeasurement = null;
        MeasurementText = "Select Distance, then click two model vertices.";
        RenderModel();
        MeasurementChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MeasureBetweenVertices(int firstPointIndex, int secondPointIndex)
    {
        if (firstPointIndex < 0 || firstPointIndex >= points.Count
            || secondPointIndex < 0 || secondPointIndex >= points.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(firstPointIndex), "Measurement point index is outside the loaded STEP model.");
        }

        measurementStartIndex = firstPointIndex;
        measurementEndIndex = secondPointIndex;
        var first = points[firstPointIndex];
        var second = points[secondPointIndex];
        var deltaX = second.X - first.X;
        var deltaY = second.Y - first.Y;
        var deltaZ = second.Z - first.Z;
        CurrentMeasurement = new StepMeasurement(
            Math.Sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ),
            deltaX,
            deltaY,
            deltaZ);
        MeasurementText = FormattableString.Invariant(
            $"Distance {CurrentMeasurement.Distance:0.####} model units\nΔX {deltaX:0.####}   ΔY {deltaY:0.####}   ΔZ {deltaZ:0.####}");
        isMeasuring = true;
        RenderModel();
        MeasurementChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SaveSnapshot(string path)
    {
        if (!HasModel)
        {
            throw new InvalidOperationException("Open a STEP model before taking a snapshot.");
        }

        ViewerRoot.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(ViewerRoot.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(ViewerRoot.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(ViewerRoot);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private void RenderModel()
    {
        ModelCanvas.Children.Clear();
        SolidSurface.Clear();
        if (!HasModel || ViewerRoot.ActualWidth <= 1 || ViewerRoot.ActualHeight <= 1)
        {
            return;
        }

        var transformed = points.Select(Transform).ToArray();
        var projected = transformed.Select(point => new Point(point.X, point.Y)).ToArray();
        var fittedGeometry = selectablePointIndices.Select(index => projected[index]).ToArray();
        var minX = fittedGeometry.Min(point => point.X);
        var maxX = fittedGeometry.Max(point => point.X);
        var minY = fittedGeometry.Min(point => point.Y);
        var maxY = fittedGeometry.Max(point => point.Y);
        var spanX = Math.Max(0.000001, maxX - minX);
        var spanY = Math.Max(0.000001, maxY - minY);
        var availableWidth = Math.Max(1, ViewerRoot.ActualWidth - 40);
        var availableHeight = Math.Max(1, ViewerRoot.ActualHeight - 70);
        var scale = Math.Min(availableWidth / spanX, availableHeight / spanY) * zoom;
        var centerX = (minX + maxX) / 2;
        var centerY = (minY + maxY) / 2;
        Point Screen(Point point) => new(
            ViewerRoot.ActualWidth / 2 + (point.X - centerX) * scale,
            ViewerRoot.ActualHeight / 2 - (point.Y - centerY) * scale);
        screenPoints = projected.Select(Screen).ToArray();

        if (triangles.Count > 0)
        {
            var renderedTriangles = triangles.Select(triangle =>
            {
                var first = transformed[triangle.FirstIndex];
                var second = transformed[triangle.SecondIndex];
                var third = transformed[triangle.ThirdIndex];
                var ab = new StepPoint3(second.X - first.X, second.Y - first.Y, second.Z - first.Z);
                var ac = new StepPoint3(third.X - first.X, third.Y - first.Y, third.Z - first.Z);
                var nx = ab.Y * ac.Z - ab.Z * ac.Y;
                var ny = ab.Z * ac.X - ab.X * ac.Z;
                var nz = ab.X * ac.Y - ab.Y * ac.X;
                var length = Math.Max(0.000001, Math.Sqrt(nx * nx + ny * ny + nz * nz));
                var light = Math.Abs((nx * -0.32 + ny * 0.42 + nz * 0.85) / length);
                return new StepScreenTriangle(
                    screenPoints[triangle.FirstIndex],
                    screenPoints[triangle.SecondIndex],
                    screenPoints[triangle.ThirdIndex],
                    (first.Z + second.Z + third.Z) / 3,
                    Math.Clamp(0.3 + light * 0.7, 0.3, 1));
            }).OrderBy(triangle => triangle.Depth).ToArray();
            SolidSurface.Draw(renderedTriangles);
        }
        else if (segments.Count > 0)
        {
            foreach (var segment in segments)
            {
                var start = screenPoints[segment.StartIndex];
                var end = screenPoints[segment.EndIndex];
                ModelCanvas.Children.Add(new Line
                {
                    X1 = start.X,
                    Y1 = start.Y,
                    X2 = end.X,
                    Y2 = end.Y,
                    Stroke = new SolidColorBrush(Color.FromRgb(17, 70, 112)),
                    StrokeThickness = 1.15,
                    SnapsToDevicePixels = true
                });
            }
        }
        else
        {
            for (var index = 0; index < Math.Min(projected.Length, 10_000); index++)
            {
                var screen = screenPoints[index];
                var dot = new Ellipse
                {
                    Width = 2.5,
                    Height = 2.5,
                    Fill = new SolidColorBrush(Color.FromRgb(17, 70, 112))
                };
                Canvas.SetLeft(dot, screen.X - 1.25);
                Canvas.SetTop(dot, screen.Y - 1.25);
                ModelCanvas.Children.Add(dot);
            }
        }

        RenderMeasurement();
    }

    private StepPoint3 Transform(StepPoint3 point)
    {
        var cosYaw = Math.Cos(yaw);
        var sinYaw = Math.Sin(yaw);
        var centeredX = point.X - modelCenter.X;
        var centeredY = point.Y - modelCenter.Y;
        var centeredZ = point.Z - modelCenter.Z;
        var x1 = centeredX * cosYaw - centeredZ * sinYaw;
        var z1 = centeredX * sinYaw + centeredZ * cosYaw;
        var cosPitch = Math.Cos(pitch);
        var sinPitch = Math.Sin(pitch);
        return new StepPoint3(
            x1,
            centeredY * cosPitch - z1 * sinPitch,
            centeredY * sinPitch + z1 * cosPitch);
    }

    private void Viewer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (isMeasuring && HasModel)
        {
            SelectMeasurementPoint(e.GetPosition(ViewerRoot));
            e.Handled = true;
            return;
        }
        dragStart = e.GetPosition(ViewerRoot);
        isDragging = true;
        ViewerRoot.CaptureMouse();
    }

    private void Viewer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        isDragging = false;
        ViewerRoot.ReleaseMouseCapture();
    }

    private void Viewer_MouseMove(object sender, MouseEventArgs e)
    {
        if (!isDragging || !HasModel)
        {
            return;
        }

        var current = e.GetPosition(ViewerRoot);
        yaw += (current.X - dragStart.X) * 0.01;
        pitch = Math.Clamp(pitch + (current.Y - dragStart.Y) * 0.01, -Math.PI / 2, Math.PI / 2);
        dragStart = current;
        RenderModel();
    }

    private void Viewer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!HasModel)
        {
            return;
        }
        zoom = Math.Clamp(zoom * (e.Delta > 0 ? 1.12 : 0.89), 0.2, 8);
        RenderModel();
    }

    private void Viewer_SizeChanged(object sender, SizeChangedEventArgs e) => RenderModel();

    private void SelectMeasurementPoint(Point pointer)
    {
        if (screenPoints.Length != points.Count)
        {
            return;
        }

        var nearest = selectablePointIndices
            .Select(index => (Index: index, Distance: (screenPoints[index] - pointer).Length))
            .Where(candidate => candidate.Distance <= 14)
            .OrderBy(candidate => candidate.Distance)
            .FirstOrDefault((Index: -1, Distance: double.MaxValue));
        if (nearest.Index < 0)
        {
            MeasurementText = "No vertex at that position. Click closer to a model corner or edge endpoint.";
            MeasurementChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (measurementStartIndex is null || measurementEndIndex is not null)
        {
            measurementStartIndex = nearest.Index;
            measurementEndIndex = null;
            CurrentMeasurement = null;
            MeasurementText = "First vertex selected. Click the second vertex.";
            RenderModel();
            MeasurementChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        MeasureBetweenVertices(measurementStartIndex.Value, nearest.Index);
    }

    private void RenderMeasurement()
    {
        if (measurementStartIndex is not int firstIndex || screenPoints.Length != points.Count)
        {
            return;
        }

        AddMeasurementMarker(screenPoints[firstIndex], "A");
        if (measurementEndIndex is not int secondIndex)
        {
            return;
        }

        var first = screenPoints[firstIndex];
        var second = screenPoints[secondIndex];
        ModelCanvas.Children.Add(new Line
        {
            X1 = first.X,
            Y1 = first.Y,
            X2 = second.X,
            Y2 = second.Y,
            Stroke = Brushes.Crimson,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection([4, 3])
        });
        AddMeasurementMarker(second, "B");
    }

    private void AddMeasurementMarker(Point point, string label)
    {
        var marker = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = Brushes.White,
            Stroke = Brushes.Crimson,
            StrokeThickness = 2
        };
        Canvas.SetLeft(marker, point.X - 5);
        Canvas.SetTop(marker, point.Y - 5);
        ModelCanvas.Children.Add(marker);
        var caption = new TextBlock { Text = label, Foreground = Brushes.Crimson, FontWeight = FontWeights.Bold };
        Canvas.SetLeft(caption, point.X + 7);
        Canvas.SetTop(caption, point.Y - 10);
        ModelCanvas.Children.Add(caption);
    }

    private static StepPoint3 GeometryCentroid(IReadOnlyList<StepPoint3> modelPoints, IReadOnlyList<int> geometryPointIndices)
    {
        var x = 0d;
        var y = 0d;
        var z = 0d;
        foreach (var index in geometryPointIndices)
        {
            x += modelPoints[index].X;
            y += modelPoints[index].Y;
            z += modelPoints[index].Z;
        }

        return new StepPoint3(x / geometryPointIndices.Count, y / geometryPointIndices.Count, z / geometryPointIndices.Count);
    }

    private static StepModelData Parse(string text)
    {
        var pointByEntity = new Dictionary<int, StepPoint3>();
        var vertexToPoint = new Dictionary<int, int>();
        var edgeReferences = new List<(int StartVertex, int EndVertex)>();
        var polylineReferences = new List<int[]>();

        foreach (Match match in EntityPattern.Matches(text))
        {
            var id = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
            var kind = match.Groups["kind"].Value;
            var body = match.Groups["body"].Value;
            if (kind == "CARTESIAN_POINT" && pointByEntity.Count < MaximumPoints)
            {
                var numbers = NumberPattern.Matches(body).Select(value =>
                    double.Parse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                if (numbers.Length >= 3)
                {
                    pointByEntity[id] = new StepPoint3(numbers[^3], numbers[^2], numbers[^1]);
                }
            }
            else if (kind == "VERTEX_POINT")
            {
                var references = References(body);
                if (references.Length > 0)
                {
                    vertexToPoint[id] = references[^1];
                }
            }
            else if (kind == "EDGE_CURVE" && edgeReferences.Count < MaximumSegments)
            {
                var references = References(body);
                if (references.Length >= 2)
                {
                    edgeReferences.Add((references[0], references[1]));
                }
            }
            else if (kind == "POLYLINE" && polylineReferences.Count < MaximumSegments)
            {
                var references = References(body);
                if (references.Length >= 2)
                {
                    polylineReferences.Add(references);
                }
            }
        }

        var orderedEntities = pointByEntity.Keys.OrderBy(id => id).ToArray();
        var pointIndex = orderedEntities.Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index);
        var resultPoints = orderedEntities.Select(id => pointByEntity[id]).ToArray();
        var resultSegments = new List<StepSegment3>();
        var selectableIndices = new HashSet<int>();
        foreach (var pointEntity in vertexToPoint.Values)
        {
            if (pointIndex.TryGetValue(pointEntity, out var vertexIndex))
            {
                selectableIndices.Add(vertexIndex);
            }
        }
        foreach (var edge in edgeReferences)
        {
            if (vertexToPoint.TryGetValue(edge.StartVertex, out var startPoint)
                && vertexToPoint.TryGetValue(edge.EndVertex, out var endPoint)
                && pointIndex.TryGetValue(startPoint, out var startIndex)
                && pointIndex.TryGetValue(endPoint, out var endIndex)
                && startIndex != endIndex)
            {
                resultSegments.Add(new StepSegment3(startIndex, endIndex));
                selectableIndices.Add(startIndex);
                selectableIndices.Add(endIndex);
            }
        }
        foreach (var polyline in polylineReferences)
        {
            for (var index = 1; index < polyline.Length && resultSegments.Count < MaximumSegments; index++)
            {
                if (pointIndex.TryGetValue(polyline[index - 1], out var startIndex)
                    && pointIndex.TryGetValue(polyline[index], out var endIndex)
                    && startIndex != endIndex)
                {
                    resultSegments.Add(new StepSegment3(startIndex, endIndex));
                    selectableIndices.Add(startIndex);
                    selectableIndices.Add(endIndex);
                }
            }
        }

        if (resultPoints.Length == 0)
        {
            throw new InvalidDataException("No Cartesian points were found in this STEP file.");
        }
        if (selectableIndices.Count == 0)
        {
            selectableIndices.UnionWith(Enumerable.Range(0, resultPoints.Length));
        }
        return new StepModelData(
            resultPoints,
            resultSegments.Distinct().Take(MaximumSegments).ToArray(),
            [],
            selectableIndices.OrderBy(index => index).ToArray());
    }

    private static int[] References(string value) => ReferencePattern.Matches(value)
        .Select(match => int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture))
        .ToArray();

}

public sealed record StepMeasurement(double Distance, double DeltaX, double DeltaY, double DeltaZ);

internal readonly record struct StepScreenTriangle(
    Point First,
    Point Second,
    Point Third,
    double Depth,
    double Shade);

public sealed class StepSolidDrawingHost : FrameworkElement
{
    private readonly DrawingVisual visual = new();
    private static readonly SolidColorBrush[] ShadedBrushes = Enumerable.Range(0, 24).Select(index =>
    {
        var shade = 0.3 + index / 23d * 0.7;
        var brush = new SolidColorBrush(Color.FromRgb(
            (byte)(45 * shade),
            (byte)(136 * shade),
            (byte)(196 * shade)));
        brush.Freeze();
        return brush;
    }).ToArray();

    public StepSolidDrawingHost() => AddVisualChild(visual);

    public void Clear()
    {
        using var context = visual.RenderOpen();
    }

    internal void Draw(IReadOnlyList<StepScreenTriangle> triangles)
    {
        using var context = visual.RenderOpen();
        foreach (var triangle in triangles)
        {
            var geometry = new StreamGeometry();
            using (var geometryContext = geometry.Open())
            {
                geometryContext.BeginFigure(triangle.First, true, true);
                geometryContext.LineTo(triangle.Second, true, false);
                geometryContext.LineTo(triangle.Third, true, false);
            }
            geometry.Freeze();
            var shadeIndex = Math.Clamp(
                (int)Math.Round(triangle.Shade * (ShadedBrushes.Length - 1)),
                0,
                ShadedBrushes.Length - 1);
            context.DrawGeometry(ShadedBrushes[shadeIndex], null, geometry);
        }
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => index == 0
        ? visual
        : throw new ArgumentOutOfRangeException(nameof(index));
}
