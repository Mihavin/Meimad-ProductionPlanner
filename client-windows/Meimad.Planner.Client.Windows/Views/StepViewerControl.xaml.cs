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
    private readonly List<StepDisplayEdge> displayEdges = [];
    private Point[] screenPoints = [];
    private Point dragStart;
    private bool isDragging;
    private bool isMeasuring;
    private int? measurementStartIndex;
    private int? measurementEndIndex;
    private GeometryModel3D? solidGeometryModel;
    private StepPoint3 modelCenter;
    private double yaw = -35 * Math.PI / 180;
    private double pitch = 25 * Math.PI / 180;
    private double zoom = 1;
    private StepDisplayMode displayMode = StepDisplayMode.Shaded;

    public StepViewerControl()
    {
        InitializeComponent();
    }

    public bool HasModel => points.Count > 0;

    public bool IsSolidModel => triangles.Count > 0;

    public int TriangleCount => triangles.Count;

    public StepDisplayMode DisplayMode => displayMode;

    public bool IsSolidSurfaceVisible => SolidViewport.Visibility == Visibility.Visible;

    public int RenderedEdgeCount { get; private set; }

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
                                          or EntryPointNotFoundException
                                          or System.ComponentModel.Win32Exception)
        {
            solidFallbackReason = InnermostMessage(exception);
            parsed = Parse(File.ReadAllText(path));
        }
        ApplyModel(parsed, path, solidFallbackReason);
    }

    internal void LoadModel(StepModelData model, string displayPath) => ApplyModel(model, displayPath, null);

    public void SetDisplayMode(StepDisplayMode mode)
    {
        displayMode = mode;
        if (IsSolidModel && LoadedPath is not null)
        {
            UpdateSolidStatus();
        }
        RenderModel();
    }

    private void ApplyModel(StepModelData parsed, string path, string? solidFallbackReason)
    {
        points.Clear();
        points.AddRange(parsed.Points);
        segments.Clear();
        segments.AddRange(parsed.Segments);
        triangles.Clear();
        triangles.AddRange(parsed.Triangles);
        selectablePointIndices.Clear();
        selectablePointIndices.AddRange(parsed.SelectablePointIndices);
        modelCenter = ModelCenterOfGravity(points, triangles, selectablePointIndices);
        BuildDisplayEdges();
        BuildSolidModel();
        LoadedPath = path;
        yaw = -35 * Math.PI / 180;
        pitch = 25 * Math.PI / 180;
        zoom = 1;
        ClearMeasurement();
        StatusText.Text = IsSolidModel
            ? $"{Path.GetFileName(path)} · shaded solid · {points.Count:N0} vertices · {triangles.Count:N0} triangles"
            : segments.Count > 0
                ? $"{Path.GetFileName(path)} · solid faces unavailable · {segments.Count:N0} fallback edges · {solidFallbackReason}"
                : $"{Path.GetFileName(path)} · solid faces unavailable · {points.Count:N0} fallback points · {solidFallbackReason}";
        if (IsSolidModel)
        {
            UpdateSolidStatus();
        }
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
        displayEdges.Clear();
        screenPoints = [];
        modelCenter = default;
        LoadedPath = null;
        if (solidGeometryModel is not null)
        {
            SolidScene.Children.Remove(solidGeometryModel);
            solidGeometryModel = null;
        }
        ClearMeasurement();
        EdgeSurface.Clear();
        RenderedEdgeCount = 0;
        SolidViewport.Visibility = Visibility.Hidden;
        ModelCanvas.Children.Clear();
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

    private void UpdateSolidStatus()
    {
        var modeLabel = displayMode switch
        {
            StepDisplayMode.VisibleEdges => "visible edges",
            StepDisplayMode.Wireframe => "wireframe",
            _ => "shaded"
        };
        StatusText.Text = $"{Path.GetFileName(LoadedPath)} · {modeLabel} · {points.Count:N0} vertices · {triangles.Count:N0} triangles";
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
        EdgeSurface.Clear();
        RenderedEdgeCount = 0;
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
            SolidViewport.Visibility = displayMode == StepDisplayMode.Wireframe
                ? Visibility.Hidden
                : Visibility.Visible;
            UpdateSolidCamera(scale);
            if (displayMode != StepDisplayMode.Shaded)
            {
                var renderedEdges = displayEdges
                    .Where(edge => displayMode == StepDisplayMode.Wireframe || IsVisibleFeatureEdge(edge))
                    .Select(edge => new StepScreenEdge(screenPoints[edge.FirstPointIndex], screenPoints[edge.SecondPointIndex]))
                    .ToArray();
                EdgeSurface.Draw(renderedEdges, displayMode);
                RenderedEdgeCount = renderedEdges.Length;
            }
        }
        else if (segments.Count > 0)
        {
            SolidViewport.Visibility = Visibility.Hidden;
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
            SolidViewport.Visibility = Visibility.Hidden;
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

    private void BuildDisplayEdges()
    {
        displayEdges.Clear();
        if (triangles.Count == 0 || points.Count == 0)
        {
            return;
        }

        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);
        var minZ = points.Min(point => point.Z);
        var maxZ = points.Max(point => point.Z);
        var tolerance = Math.Max(0.0000001, Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)) * 0.00000001);
        var canonicalByCoordinate = new Dictionary<StepVertexKey, (int Id, int PointIndex)>();
        var canonicalIds = new int[points.Count];
        for (var pointIndex = 0; pointIndex < points.Count; pointIndex++)
        {
            var point = points[pointIndex];
            var key = new StepVertexKey(
                (long)Math.Round((point.X - minX) / tolerance),
                (long)Math.Round((point.Y - minY) / tolerance),
                (long)Math.Round((point.Z - minZ) / tolerance));
            if (!canonicalByCoordinate.TryGetValue(key, out var canonical))
            {
                canonical = (canonicalByCoordinate.Count, pointIndex);
                canonicalByCoordinate.Add(key, canonical);
            }
            canonicalIds[pointIndex] = canonical.Id;
        }

        var representativePointById = canonicalByCoordinate.Values.ToDictionary(value => value.Id, value => value.PointIndex);
        var edgeBuilders = new Dictionary<(int First, int Second), StepDisplayEdgeBuilder>();
        for (var triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
        {
            var triangle = triangles[triangleIndex];
            AddEdge(triangle.FirstIndex, triangle.SecondIndex, triangleIndex);
            AddEdge(triangle.SecondIndex, triangle.ThirdIndex, triangleIndex);
            AddEdge(triangle.ThirdIndex, triangle.FirstIndex, triangleIndex);
        }

        foreach (var edge in edgeBuilders.Values)
        {
            var isBoundaryOrCrease = edge.TriangleIndices.Count != 2;
            if (!isBoundaryOrCrease)
            {
                var firstNormal = TriangleNormal(triangles[edge.TriangleIndices[0]]);
                var secondNormal = TriangleNormal(triangles[edge.TriangleIndices[1]]);
                var dot = firstNormal.X * secondNormal.X + firstNormal.Y * secondNormal.Y + firstNormal.Z * secondNormal.Z;
                isBoundaryOrCrease = dot < Math.Cos(25 * Math.PI / 180);
            }
            displayEdges.Add(new StepDisplayEdge(
                edge.FirstPointIndex,
                edge.SecondPointIndex,
                isBoundaryOrCrease,
                edge.TriangleIndices.ToArray()));
        }
        return;

        void AddEdge(int firstPointIndex, int secondPointIndex, int triangleIndex)
        {
            var firstId = canonicalIds[firstPointIndex];
            var secondId = canonicalIds[secondPointIndex];
            if (firstId == secondId)
            {
                return;
            }
            var key = firstId < secondId ? (firstId, secondId) : (secondId, firstId);
            if (!edgeBuilders.TryGetValue(key, out var builder))
            {
                builder = new StepDisplayEdgeBuilder(
                    representativePointById[key.Item1],
                    representativePointById[key.Item2]);
                edgeBuilders.Add(key, builder);
            }
            builder.TriangleIndices.Add(triangleIndex);
        }
    }

    private bool IsVisibleFeatureEdge(StepDisplayEdge edge)
    {
        if (edge.IsBoundaryOrCrease || edge.TriangleIndices.Length != 2)
        {
            return edge.TriangleIndices.Any(triangleIndex => IsFrontFacing(triangles[triangleIndex]));
        }

        var firstFacing = ProjectedTriangleArea(triangles[edge.TriangleIndices[0]]);
        var secondFacing = ProjectedTriangleArea(triangles[edge.TriangleIndices[1]]);
        return Math.Abs(firstFacing) > 0.000001
               && Math.Abs(secondFacing) > 0.000001
               && Math.Sign(firstFacing) != Math.Sign(secondFacing);
    }

    private bool IsFrontFacing(StepTriangle3 triangle)
    {
        var normal = TriangleNormal(triangle);
        var depthX = Math.Cos(pitch) * Math.Sin(yaw);
        var depthY = Math.Sin(pitch);
        var depthZ = Math.Cos(pitch) * Math.Cos(yaw);
        return normal.X * depthX + normal.Y * depthY + normal.Z * depthZ > 0.0000001;
    }

    private double ProjectedTriangleArea(StepTriangle3 triangle)
    {
        var first = screenPoints[triangle.FirstIndex];
        var second = screenPoints[triangle.SecondIndex];
        var third = screenPoints[triangle.ThirdIndex];
        return (second.X - first.X) * (third.Y - first.Y)
               - (second.Y - first.Y) * (third.X - first.X);
    }

    private StepPoint3 TriangleNormal(StepTriangle3 triangle)
    {
        var first = points[triangle.FirstIndex];
        var second = points[triangle.SecondIndex];
        var third = points[triangle.ThirdIndex];
        var ux = second.X - first.X;
        var uy = second.Y - first.Y;
        var uz = second.Z - first.Z;
        var vx = third.X - first.X;
        var vy = third.Y - first.Y;
        var vz = third.Z - first.Z;
        var x = uy * vz - uz * vy;
        var y = uz * vx - ux * vz;
        var z = ux * vy - uy * vx;
        var length = Math.Sqrt(x * x + y * y + z * z);
        return length <= 0.000000001 ? default : new StepPoint3(x / length, y / length, z / length);
    }

    private void BuildSolidModel()
    {
        if (solidGeometryModel is not null)
        {
            SolidScene.Children.Remove(solidGeometryModel);
            solidGeometryModel = null;
        }
        if (triangles.Count == 0)
        {
            return;
        }

        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection(points.Select(point => new Point3D(point.X, point.Y, point.Z))),
            TriangleIndices = new Int32Collection(triangles.SelectMany(triangle => new[]
            {
                triangle.FirstIndex,
                triangle.SecondIndex,
                triangle.ThirdIndex
            }))
        };
        mesh.Freeze();
        var diffuse = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(45, 136, 196)));
        var specular = new SpecularMaterial(new SolidColorBrush(Color.FromRgb(210, 228, 240)), 28);
        var material = new MaterialGroup();
        material.Children.Add(diffuse);
        material.Children.Add(specular);
        material.Freeze();
        solidGeometryModel = new GeometryModel3D(mesh, material) { BackMaterial = material };
        SolidScene.Children.Add(solidGeometryModel);
    }

    private void UpdateSolidCamera(double scale)
    {
        var sinYaw = Math.Sin(yaw);
        var cosYaw = Math.Cos(yaw);
        var sinPitch = Math.Sin(pitch);
        var cosPitch = Math.Cos(pitch);
        var depth = new Vector3D(cosPitch * sinYaw, sinPitch, cosPitch * cosYaw);
        var up = new Vector3D(-sinPitch * sinYaw, cosPitch, -sinPitch * cosYaw);
        var extent = points.Select(point =>
        {
            var dx = point.X - modelCenter.X;
            var dy = point.Y - modelCenter.Y;
            var dz = point.Z - modelCenter.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }).DefaultIfEmpty(1).Max();
        var distance = Math.Max(1, extent * 4);
        SolidCamera.Position = new Point3D(
            modelCenter.X + depth.X * distance,
            modelCenter.Y + depth.Y * distance,
            modelCenter.Z + depth.Z * distance);
        SolidCamera.LookDirection = -depth * distance;
        SolidCamera.UpDirection = up;
        SolidCamera.Width = Math.Max(0.000001, ViewerRoot.ActualWidth / scale);
        SolidCamera.NearPlaneDistance = Math.Max(0.0001, distance - extent * 2);
        SolidCamera.FarPlaneDistance = Math.Max(SolidCamera.NearPlaneDistance + 1, distance + extent * 2);
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

    internal static StepPoint3 ModelCenterOfGravity(
        IReadOnlyList<StepPoint3> modelPoints,
        IReadOnlyList<StepTriangle3> modelTriangles,
        IReadOnlyList<int> geometryPointIndices)
    {
        if (modelTriangles.Count > 0)
        {
            var reference = modelPoints[modelTriangles[0].FirstIndex];
            var weightedX = 0d;
            var weightedY = 0d;
            var weightedZ = 0d;
            var signedVolume6 = 0d;
            foreach (var triangle in modelTriangles)
            {
                var first = Relative(modelPoints[triangle.FirstIndex], reference);
                var second = Relative(modelPoints[triangle.SecondIndex], reference);
                var third = Relative(modelPoints[triangle.ThirdIndex], reference);
                var volume6 = first.X * (second.Y * third.Z - second.Z * third.Y)
                              - first.Y * (second.X * third.Z - second.Z * third.X)
                              + first.Z * (second.X * third.Y - second.Y * third.X);
                signedVolume6 += volume6;
                weightedX += (first.X + second.X + third.X) * volume6;
                weightedY += (first.Y + second.Y + third.Y) * volume6;
                weightedZ += (first.Z + second.Z + third.Z) * volume6;
            }

            if (Math.Abs(signedVolume6) > 0.000000001)
            {
                return new StepPoint3(
                    reference.X + weightedX / (4 * signedVolume6),
                    reference.Y + weightedY / (4 * signedVolume6),
                    reference.Z + weightedZ / (4 * signedVolume6));
            }
        }

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

    private static StepPoint3 Relative(StepPoint3 point, StepPoint3 origin) => new(
        point.X - origin.X,
        point.Y - origin.Y,
        point.Z - origin.Z);

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

    private static string InnermostMessage(Exception exception)
    {
        while (exception.InnerException is not null)
        {
            exception = exception.InnerException;
        }
        return exception.Message;
    }

}

public enum StepDisplayMode
{
    Shaded,
    VisibleEdges,
    Wireframe
}

public sealed class StepEdgeDrawingHost : FrameworkElement
{
    private readonly DrawingVisual visual = new();

    public StepEdgeDrawingHost()
    {
        AddVisualChild(visual);
        AddLogicalChild(visual);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => index == 0
        ? visual
        : throw new ArgumentOutOfRangeException(nameof(index));

    internal void Clear()
    {
        using var context = visual.RenderOpen();
    }

    internal void Draw(IReadOnlyList<StepScreenEdge> edges, StepDisplayMode mode)
    {
        var brush = new SolidColorBrush(mode == StepDisplayMode.Wireframe
            ? Color.FromRgb(38, 77, 105)
            : Color.FromRgb(17, 42, 61));
        brush.Freeze();
        var pen = new Pen(brush, mode == StepDisplayMode.Wireframe ? 0.8 : 1.15);
        pen.Freeze();
        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            foreach (var edge in edges)
            {
                geometryContext.BeginFigure(edge.Start, false, false);
                geometryContext.LineTo(edge.End, true, false);
            }
        }
        geometry.Freeze();
        using var context = visual.RenderOpen();
        context.DrawGeometry(null, pen, geometry);
    }
}

internal sealed class StepDisplayEdgeBuilder(int firstPointIndex, int secondPointIndex)
{
    public int FirstPointIndex { get; } = firstPointIndex;
    public int SecondPointIndex { get; } = secondPointIndex;
    public List<int> TriangleIndices { get; } = [];
}

internal sealed record StepDisplayEdge(
    int FirstPointIndex,
    int SecondPointIndex,
    bool IsBoundaryOrCrease,
    int[] TriangleIndices);

internal readonly record struct StepScreenEdge(Point Start, Point End);

internal readonly record struct StepVertexKey(long X, long Y, long Z);

public sealed record StepMeasurement(double Distance, double DeltaX, double DeltaY, double DeltaZ);
