using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
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

public enum StepProjectionMode
{
    Orthographic,
    Perspective
}

public enum StepMeasurementTool
{
    None,
    Point,
    Distance,
    MinimumDistance,
    EdgeLength,
    Radius,
    Angle,
    FaceArea,
    Volume
}

/// <summary>
/// Software CAD viewer: OpenCascade tessellation drawn with a painter's algorithm plus a depth buffer
/// for hidden-line removal, several model layers (part, stock, fixtures) in one scene, orthographic or
/// perspective projection, B-rep picking (vertices, edges, faces) and CAD-style measurement tools.
/// </summary>
public partial class StepViewerControl : UserControl
{
    private const long MaximumStepBytes = 64L * 1024 * 1024;
    private const int MaximumPoints = 50_000;
    private const int MaximumSegments = 75_000;
    private const double PickRadius = 12;
    private static readonly Regex EntityPattern = new(
        @"#(?<id>\d+)\s*=\s*(?<kind>[A-Z0-9_]+)\s*\((?<body>.*?)\)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex NumberPattern = new(
        @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[Ee][-+]?\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ReferencePattern = new(
        @"#(?<id>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Color DefaultPartColor = Color.FromRgb(45, 136, 196);
    private static readonly Color DefaultStockColor = Color.FromRgb(158, 158, 158);
    private static readonly Color DefaultFixtureColor = Color.FromRgb(96, 125, 139);
    private static readonly Color DefaultOtherColor = Color.FromRgb(121, 85, 72);

    // Combined scene arrays: every layer's points/triangles live here with offsets so one transform,
    // one depth buffer and one painter sort serve the whole scene.
    private readonly List<SceneLayer> layers = [];
    private readonly List<StepPoint3> points = [];
    private readonly List<StepSegment3> segments = [];
    private readonly List<int> segmentLayers = [];
    private readonly List<StepTriangle3> triangles = [];
    private readonly List<int> triangleLayers = [];
    private readonly List<int> triangleFaces = [];
    private readonly List<int> selectablePointIndices = [];
    private readonly List<StepBrepVertex> brepVertices = [];
    private readonly List<StepDisplayEdge> displayEdges = [];
    private readonly List<StepMeasurementAnnotation> annotations = [];
    private readonly List<StepPickedEntity> pickedEntities = [];
    private readonly Dictionary<(Color Color, byte Alpha), SolidColorBrush[]> paletteCache = [];
    private Point[] screenPoints = [];
    private StepPoint3[] transformedPoints = [];
    private double[] depthValues = [];
    private StepRenderedEdge[] renderedEdges = [];
    private Point dragStart;
    private bool isDragging;
    private bool isPanning;
    private bool isMeasuring;
    private int? measurementStartIndex;
    private int? measurementEndIndex;
    private GeometryModel3D? solidGeometryModel;
    private readonly PerspectiveCamera perspectiveCamera = new() { NearPlaneDistance = 0.01, FarPlaneDistance = 10000000 };
    private StepPoint3 modelCenter;
    private double yaw = -35 * Math.PI / 180;
    private double pitch = 25 * Math.PI / 180;
    private double cameraWidth = 1;
    private Vector panOffset;
    private bool hasAutoFitForCurrentModel;
    private bool autoFitScheduled;
    private StepDisplayMode displayMode = StepDisplayMode.Shaded;
    private StepProjectionMode projection = StepProjectionMode.Orthographic;
    private StepMeasurementTool activeTool = StepMeasurementTool.None;
    private StepReferenceFrame? customReference;
    private bool showBoundingBox;
    private bool isBuildingReference;
    private readonly List<int> referenceSelection = [];
    private bool isBuildingFaceReference;
    private int? referenceTriangleIndex;
    private DispatcherOperation? pendingViewportRender;
    private bool renderDeferredWhileHidden;
    private int layerSequence;

    public StepViewerControl()
    {
        InitializeComponent();
        IsVisibleChanged += Viewer_IsVisibleChanged;
    }

    public bool HasModel => points.Count > 0;

    public bool IsSolidModel => triangles.Count > 0;

    public int TriangleCount => triangles.Count;

    public StepDisplayMode DisplayMode => displayMode;

    public StepProjectionMode Projection => projection;

    public StepMeasurementTool ActiveTool => activeTool;

    public bool IsBoundingBoxVisible => showBoundingBox;

    internal int FitInvocationCount { get; private set; }

    internal double CameraWidth => cameraWidth;

    public bool IsSolidSurfaceVisible => SolidViewport.Visibility == Visibility.Visible;

    public int RenderedEdgeCount { get; private set; }

    public int RenderedSurfaceTriangleCount { get; private set; }

    internal int RenderInvocationCount { get; private set; }

    public string? LoadedPath => layers.Count > 0 ? layers[0].Path : null;

    public Point3D ModelCenter => new(modelCenter.X, modelCenter.Y, modelCenter.Z);

    public string MeasurementText { get; private set; } = "Select Distance, then click two model vertices.";

    public StepMeasurement? CurrentMeasurement { get; private set; }

    internal StepMeasurementResult? LastMeasurementResult { get; private set; }

    internal IReadOnlyList<StepMeasurementResult> MeasurementResults => annotations.Select(item => item.Result).ToArray();

    public StepBoundingBox? CurrentBoundingBox => HasModel ? CalculateBoundingBox(customReference) : null;

    public IReadOnlyList<StepViewerLayerInfo> Layers => layers.Select(layer => layer.ToInfo()).ToArray();

    public event EventHandler? ModelStateChanged;

    public event EventHandler? MeasurementChanged;

    public event EventHandler? ReferenceChanged;

    public event EventHandler? LayersChanged;

    // ---------------------------------------------------------------- loading and layers

    public void LoadStep(string path) => LoadFile(path);

    /// <summary>Replaces the whole scene with one model read from a STEP or STL file.</summary>
    public void LoadFile(string path)
    {
        var parsed = ReadFile(path, out var fallbackReason);
        ApplyModel(parsed, path, fallbackReason);
    }

    /// <summary>
    /// Guards every native OpenCascade call (through <see cref="ReadFile"/>) so at most one runs
    /// at a time, process-wide. OCCSharp/OpenCascade is not documented or verified thread-safe for
    /// concurrent use, and several StepViewerControl instances can genuinely be tessellating at
    /// once -- the embedded viewer in the Case workspace plus one or more detached "View in 3D"
    /// windows, which the app explicitly allows to be open simultaneously (see
    /// ModelViewerWindow.Open). Before LoadFileAsync/AddFileAsync existed, every load ran
    /// synchronously on the UI thread, which serialized them for free (only one could ever be
    /// running at a time); moving the work to background threads removed that accidental
    /// serialization; this restores it explicitly instead of leaving concurrent native calls to
    /// chance.
    /// </summary>
    private static readonly SemaphoreSlim NativeStepLoadGate = new(1, 1);

    /// <summary>
    /// Async counterpart of <see cref="LoadFile"/>: parses and tessellates the model on a
    /// background thread instead of the UI thread. A large or geometrically complex STEP file can
    /// take a long time to read and mesh through OpenCascade -- doing that synchronously used to
    /// freeze the entire application for however long it took, with no way to switch away or
    /// cancel. This keeps the app responsive while the file loads; call sites reached
    /// automatically (not from an explicit "open a file" user action) should always prefer this.
    /// </summary>
    public async Task LoadFileAsync(string path)
    {
        var (parsed, fallbackReason) = await LoadNativeAsync(path).ConfigureAwait(true);
        ApplyModel(parsed, path, fallbackReason);
    }

    /// <summary>
    /// Adds a STEP or STL file as an extra layer (rest material stock, fixture, ...) without
    /// disturbing the current camera. Returns the layer id.
    /// </summary>
    public string AddFile(string path, string label, string kind, Color? color = null, double opacity = 1)
    {
        var parsed = ReadFile(path, out _);
        return AddLayer(parsed, path, label, kind, color, opacity);
    }

    /// <summary>Async counterpart of <see cref="AddFile"/> -- see its remarks.</summary>
    public async Task<string> AddFileAsync(string path, string label, string kind, Color? color = null, double opacity = 1)
    {
        var (parsed, _) = await LoadNativeAsync(path).ConfigureAwait(true);
        return AddLayer(parsed, path, label, kind, color, opacity);
    }

    private static async Task<(StepModelData Model, string? FallbackReason)> LoadNativeAsync(string path)
    {
        await NativeStepLoadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            return await Task.Run(() => ReadFileWithFallback(path)).ConfigureAwait(true);
        }
        finally
        {
            NativeStepLoadGate.Release();
        }
    }

    private static (StepModelData Model, string? FallbackReason) ReadFileWithFallback(string path)
    {
        var model = ReadFile(path, out var fallbackReason);
        return (model, fallbackReason);
    }

    internal void LoadModel(StepModelData model, string displayPath) => ApplyModel(model, displayPath, null);

    internal string AddLayer(StepModelData model, string path, string label, string kind, Color? color = null, double opacity = 1)
    {
        var layer = new SceneLayer
        {
            Id = $"layer-{++layerSequence}",
            Label = label,
            Kind = kind,
            Path = path,
            Model = model,
            Color = color ?? DefaultColor(kind),
            Opacity = Math.Clamp(opacity, 0.05, 1),
            Units = model.Geometry is OcctStepGeometry ? "mm" : "units"
        };
        var hadModel = HasModel;
        layers.Add(layer);
        RebuildScene();
        if (!hadModel)
        {
            hasAutoFitForCurrentModel = false;
            AutoFitCurrentModelOnce();
        }
        RenderModel();
        UpdateStatus();
        ModelStateChanged?.Invoke(this, EventArgs.Empty);
        LayersChanged?.Invoke(this, EventArgs.Empty);
        return layer.Id;
    }

    public void RemoveLayer(string layerId)
    {
        var layer = layers.FirstOrDefault(item => item.Id == layerId);
        if (layer is null)
        {
            return;
        }
        layers.Remove(layer);
        layer.Model.Geometry?.Dispose();
        ClearMeasurementState();
        RebuildScene();
        RenderModel();
        UpdateStatus();
        ModelStateChanged?.Invoke(this, EventArgs.Empty);
        LayersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetLayerVisible(string layerId, bool visible)
    {
        var layer = layers.FirstOrDefault(item => item.Id == layerId);
        if (layer is null || layer.IsVisible == visible)
        {
            return;
        }
        layer.IsVisible = visible;
        RenderModel();
        LayersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetLayerStyle(string layerId, Color? color, double? opacity)
    {
        var layer = layers.FirstOrDefault(item => item.Id == layerId);
        if (layer is null)
        {
            return;
        }
        if (color.HasValue) layer.Color = color.Value;
        if (opacity.HasValue) layer.Opacity = Math.Clamp(opacity.Value, 0.05, 1);
        RenderModel();
        LayersChanged?.Invoke(this, EventArgs.Empty);
    }

    private static StepModelData ReadFile(string path, out string? fallbackReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        fallbackReason = null;
        var extension = Path.GetExtension(path);
        var isStl = string.Equals(extension, ".stl", StringComparison.OrdinalIgnoreCase);
        if (!isStl
            && !string.Equals(extension, ".stp", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".step", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Choose a .stp, .step, or .stl file.");
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The model file was not found.", path);
        }
        if (isStl)
        {
            return StlMeshLoader.Load(path);
        }
        if (info.Length > MaximumStepBytes)
        {
            throw new InvalidDataException("The STEP viewer supports files up to 64 MiB.");
        }

        try
        {
            return StepSolidMeshLoader.Load(path);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                          or TypeInitializationException
                                          or DllNotFoundException
                                          or EntryPointNotFoundException
                                          or System.ComponentModel.Win32Exception)
        {
            fallbackReason = InnermostMessage(exception);
            return Parse(File.ReadAllText(path));
        }
    }

    private static Color DefaultColor(string kind) => kind switch
    {
        "stock" => DefaultStockColor,
        "fixture" => DefaultFixtureColor,
        "other" => DefaultOtherColor,
        _ => DefaultPartColor
    };

    private void ApplyModel(StepModelData parsed, string path, string? solidFallbackReason)
    {
        DisposeLayers();
        layers.Add(new SceneLayer
        {
            Id = $"layer-{++layerSequence}",
            Label = Path.GetFileName(path),
            Kind = "part",
            Path = path,
            Model = parsed,
            Color = DefaultPartColor,
            Opacity = 1,
            Units = parsed.Geometry is OcctStepGeometry ? "mm" : "units",
            FallbackReason = solidFallbackReason
        });
        RebuildScene();
        yaw = -35 * Math.PI / 180;
        pitch = 25 * Math.PI / 180;
        cameraWidth = 1;
        panOffset = default;
        hasAutoFitForCurrentModel = false;
        autoFitScheduled = false;
        FitInvocationCount = 0;
        displayMode = StepDisplayMode.Shaded;
        showBoundingBox = false;
        ClearMeasurementState();
        activeTool = StepMeasurementTool.None;
        MeasurementText = "Select Distance, then click two model vertices.";
        customReference = null;
        UpdateStatus();
        AutoFitCurrentModelOnce();
        RenderModel();
        ModelStateChanged?.Invoke(this, EventArgs.Empty);
        MeasurementChanged?.Invoke(this, EventArgs.Empty);
        LayersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearModel()
    {
        DisposeLayers();
        RebuildScene();
        screenPoints = [];
        transformedPoints = [];
        depthValues = [];
        renderedEdges = [];
        modelCenter = default;
        cameraWidth = 1;
        panOffset = default;
        hasAutoFitForCurrentModel = false;
        autoFitScheduled = false;
        FitInvocationCount = 0;
        displayMode = StepDisplayMode.Shaded;
        showBoundingBox = false;
        customReference = null;
        activeTool = StepMeasurementTool.None;
        if (solidGeometryModel is not null)
        {
            SolidScene.Children.Remove(solidGeometryModel);
            solidGeometryModel = null;
        }
        ClearMeasurementState();
        MeasurementText = "Select Distance, then click two model vertices.";
        EdgeSurface.Clear();
        SurfaceLayer.Clear();
        RenderedEdgeCount = 0;
        RenderedSurfaceTriangleCount = 0;
        SolidViewport.Visibility = Visibility.Hidden;
        ModelCanvas.Children.Clear();
        StatusText.Text = "Open a STEP file to preview it.";
        ModelStateChanged?.Invoke(this, EventArgs.Empty);
        MeasurementChanged?.Invoke(this, EventArgs.Empty);
        LayersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeLayers()
    {
        foreach (var layer in layers)
        {
            layer.Model.Geometry?.Dispose();
        }
        layers.Clear();
    }

    /// <summary>Rebuilds the combined point/triangle/edge arrays from the layer list.</summary>
    private void RebuildScene()
    {
        points.Clear();
        segments.Clear();
        segmentLayers.Clear();
        triangles.Clear();
        triangleLayers.Clear();
        triangleFaces.Clear();
        selectablePointIndices.Clear();
        brepVertices.Clear();
        displayEdges.Clear();
        for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
        {
            var layer = layers[layerIndex];
            var model = layer.Model;
            layer.PointOffset = points.Count;
            layer.TriangleOffset = triangles.Count;
            points.AddRange(model.Points);
            foreach (var segment in model.Segments)
            {
                segments.Add(new StepSegment3(segment.StartIndex + layer.PointOffset, segment.EndIndex + layer.PointOffset));
                segmentLayers.Add(layerIndex);
            }
            for (var index = 0; index < model.Triangles.Count; index++)
            {
                var triangle = model.Triangles[index];
                triangles.Add(new StepTriangle3(
                    triangle.FirstIndex + layer.PointOffset,
                    triangle.SecondIndex + layer.PointOffset,
                    triangle.ThirdIndex + layer.PointOffset));
                triangleLayers.Add(layerIndex);
                triangleFaces.Add(model.TriangleFaceIndices is { } faces && index < faces.Count ? faces[index] : -1);
            }
            selectablePointIndices.AddRange(model.SelectablePointIndices.Select(index => index + layer.PointOffset));
            if (model.VertexPointIndices is { } vertices)
            {
                for (var vertexIndex = 0; vertexIndex < vertices.Count; vertexIndex++)
                {
                    brepVertices.Add(new StepBrepVertex(layerIndex, vertexIndex, vertices[vertexIndex] + layer.PointOffset));
                }
            }
            if (model.Edges is { Count: > 0 } edges)
            {
                foreach (var edge in edges)
                {
                    for (var index = 1; index < edge.PointIndices.Length; index++)
                    {
                        displayEdges.Add(new StepDisplayEdge(
                            edge.PointIndices[index - 1] + layer.PointOffset,
                            edge.PointIndices[index] + layer.PointOffset,
                            true,
                            [],
                            layerIndex,
                            edge.EdgeIndex));
                    }
                }
            }
            else
            {
                BuildCreaseEdges(layerIndex, layer);
            }
        }

        modelCenter = layers.Count == 0
            ? default
            : ModelCenterOfGravity(
                layers[0].Model.Points,
                layers[0].Model.Triangles,
                layers[0].Model.SelectablePointIndices);
        BuildSolidModel();
    }

    // ---------------------------------------------------------------- view control

    public void SetDisplayMode(StepDisplayMode mode)
    {
        displayMode = mode;
        UpdateStatus();
        RenderModel();
    }

    public void SetProjection(StepProjectionMode mode)
    {
        if (projection == mode)
        {
            return;
        }
        projection = mode;
        UpdateStatus();
        RenderModel();
        ModelStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetView(string view)
    {
        (yaw, pitch) = view switch
        {
            "front" => (0, 0),
            "back" => (Math.PI, 0),
            "top" => (0, Math.PI / 2),
            "bottom" => (0, -Math.PI / 2),
            "right" => (Math.PI / 2, 0),
            "left" => (-Math.PI / 2, 0),
            _ => (-35 * Math.PI / 180, 25 * Math.PI / 180)
        };
        RenderModel();
    }

    public void FitToWindow()
    {
        if (!HasModel || ViewerRoot.ActualWidth <= 1 || ViewerRoot.ActualHeight <= 1)
        {
            return;
        }

        panOffset = default;
        cameraWidth = CalculateFitCameraWidth();
        hasAutoFitForCurrentModel = true;
        FitInvocationCount++;
        RenderModel();
    }

    private void AutoFitCurrentModelOnce()
    {
        if (hasAutoFitForCurrentModel || !HasModel)
        {
            return;
        }

        if (ViewerRoot.ActualWidth > 1 && ViewerRoot.ActualHeight > 1)
        {
            FitToWindow();
            return;
        }

        if (autoFitScheduled)
        {
            return;
        }

        autoFitScheduled = true;
        Dispatcher.BeginInvoke(() =>
        {
            autoFitScheduled = false;
            AutoFitCurrentModelOnce();
        }, DispatcherPriority.Loaded);
    }

    private double CalculateFitCameraWidth()
    {
        var fitPoints = selectablePointIndices
            .Where(index => layers[LayerOfPoint(index)].IsVisible)
            .Select(index => Transform(points[index]))
            .ToArray();
        if (fitPoints.Length == 0)
        {
            fitPoints = selectablePointIndices.Select(index => Transform(points[index])).ToArray();
        }
        var halfSpanX = Math.Max(0.0000005, fitPoints.Max(point => Math.Abs(point.X)));
        var halfSpanY = Math.Max(0.0000005, fitPoints.Max(point => Math.Abs(point.Y)));
        var availableWidth = Math.Max(1, ViewerRoot.ActualWidth - 40);
        var availableHeight = Math.Max(1, ViewerRoot.ActualHeight - 70);
        var pixelsPerModelUnit = Math.Min(
            availableWidth / (2 * halfSpanX),
            availableHeight / (2 * halfSpanY));
        var width = Math.Max(0.000001, ViewerRoot.ActualWidth / pixelsPerModelUnit);
        // Perspective foreshortening keeps the far side inside the frame with a little slack.
        return projection == StepProjectionMode.Perspective ? width * 1.15 : width;
    }

    private int LayerOfPoint(int pointIndex)
    {
        for (var index = layers.Count - 1; index >= 0; index--)
        {
            if (pointIndex >= layers[index].PointOffset)
            {
                return index;
            }
        }
        return 0;
    }

    private void UpdateStatus()
    {
        if (layers.Count == 0)
        {
            StatusText.Text = "Open a STEP file to preview it.";
            return;
        }

        var primary = layers[0];
        var name = Path.GetFileName(primary.Path);
        if (!IsSolidModel)
        {
            StatusText.Text = segments.Count > 0
                ? $"{name} · solid faces unavailable · {segments.Count:N0} fallback edges · {primary.FallbackReason}"
                : $"{name} · solid faces unavailable · {points.Count:N0} fallback points · {primary.FallbackReason}";
            return;
        }

        var modeLabel = displayMode switch
        {
            StepDisplayMode.VisibleEdges => "shaded + edges",
            StepDisplayMode.Wireframe => "wireframe",
            StepDisplayMode.HiddenLine => "hidden line",
            StepDisplayMode.Transparent => "transparent",
            _ => "shaded"
        };
        var projectionLabel = projection == StepProjectionMode.Perspective ? "perspective" : "orthographic";
        var extra = layers.Count > 1 ? $" · {layers.Count} models" : string.Empty;
        StatusText.Text = $"{name} · {modeLabel} · {projectionLabel} · {points.Count:N0} vertices · {triangles.Count:N0} triangles{extra}";
    }

    // ---------------------------------------------------------------- bounding box and reference frame

    public void ShowBoundingBox(bool visible)
    {
        showBoundingBox = visible;
        RenderModel();
        ReferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearCustomReference()
    {
        customReference = null;
        RenderModel();
        ReferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void BeginCustomReferenceByPoints()
    {
        if (!HasModel) return;
        CancelTool();
        referenceSelection.Clear();
        isBuildingReference = true;
        MeasurementText = "Reference: select 3 base-plane points, then 2 direction points.";
        RenderModel();
        ReferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void BeginCustomReferenceByFaceAndEdge()
    {
        if (!IsSolidModel) throw new InvalidOperationException("Face selection requires a tessellated solid STEP model.");
        CancelTool();
        isBuildingFaceReference = true;
        referenceTriangleIndex = null;
        MeasurementText = "Reference: click a planar face, then click a visible edge for X direction.";
        RenderModel();
        ReferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCustomReferenceByPoints(int baseA, int baseB, int baseC, int directionA, int directionB)
    {
        foreach (var index in new[] { baseA, baseB, baseC, directionA, directionB })
        {
            if (index < 0 || index >= points.Count) throw new ArgumentOutOfRangeException(nameof(baseA));
        }
        customReference = BuildReference(points[baseA], points[baseB], points[baseC], points[directionA], points[directionB]);
        RenderModel();
        ReferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCustomReferenceByFaceAndEdge(int triangleIndex, int edgeIndex)
    {
        if (triangleIndex < 0 || triangleIndex >= triangles.Count) throw new ArgumentOutOfRangeException(nameof(triangleIndex));
        if (edgeIndex < 0 || edgeIndex >= displayEdges.Count) throw new ArgumentOutOfRangeException(nameof(edgeIndex));
        var face = triangles[triangleIndex];
        var edge = displayEdges[edgeIndex];
        customReference = BuildReference(points[face.FirstIndex], points[face.SecondIndex], points[face.ThirdIndex],
            points[edge.FirstPointIndex], points[edge.SecondPointIndex]);
        RenderModel();
        ReferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void FlipReferenceAxis(string axis)
    {
        if (customReference is null) return;
        customReference = axis.Trim().ToUpperInvariant() switch
        {
            "X" => customReference with { X = Scale(customReference.X, -1), Y = Scale(customReference.Y, -1) },
            "Y" => customReference with { Y = Scale(customReference.Y, -1), Z = Scale(customReference.Z, -1) },
            "Z" => customReference with { Z = Scale(customReference.Z, -1), Y = Scale(customReference.Y, -1) },
            _ => throw new ArgumentException("Axis must be X, Y, or Z.", nameof(axis))
        };
        RenderModel();
        ReferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    private StepBoundingBox CalculateBoundingBox(StepReferenceFrame? reference)
    {
        var frame = reference ?? StepReferenceFrame.Model;
        var candidates = selectablePointIndices.Where(index => layers[LayerOfPoint(index)].IsVisible).ToArray();
        if (candidates.Length == 0)
        {
            candidates = selectablePointIndices.ToArray();
        }
        var local = candidates.Select(index => Relative(points[index], frame.Origin))
            .Select(value => new StepPoint3(Dot(value, frame.X), Dot(value, frame.Y), Dot(value, frame.Z))).ToArray();
        return new StepBoundingBox(local.Min(p => p.X), local.Max(p => p.X), local.Min(p => p.Y), local.Max(p => p.Y), local.Min(p => p.Z), local.Max(p => p.Z), reference is not null);
    }

    private static StepReferenceFrame BuildReference(StepPoint3 a, StepPoint3 b, StepPoint3 c, StepPoint3 directionA, StepPoint3 directionB)
    {
        var z = Normalize(Cross(Relative(b, a), Relative(c, a)), "The three base points must not be collinear.");
        var direction = Relative(directionB, directionA);
        var x = Normalize(Add(direction, Scale(z, -Dot(direction, z))), "The direction must lie in the base plane.");
        var y = Normalize(Cross(z, x), "The reference direction is invalid.");
        return new StepReferenceFrame(a, x, y, z);
    }

    private static StepPoint3 Normalize(StepPoint3 value, string message)
    {
        var length = Math.Sqrt(Dot(value, value));
        if (length <= 0.000000001) throw new InvalidOperationException(message);
        return Scale(value, 1 / length);
    }
    private static double Dot(StepPoint3 a, StepPoint3 b) => a.X*b.X + a.Y*b.Y + a.Z*b.Z;
    private static StepPoint3 Cross(StepPoint3 a, StepPoint3 b) => new(a.Y*b.Z-a.Z*b.Y, a.Z*b.X-a.X*b.Z, a.X*b.Y-a.Y*b.X);
    private static StepPoint3 Scale(StepPoint3 a, double scale) => new(a.X*scale, a.Y*scale, a.Z*scale);
    private static StepPoint3 Add(StepPoint3 a, StepPoint3 b) => new(a.X+b.X, a.Y+b.Y, a.Z+b.Z);
    private static double Length(StepPoint3 a) => Math.Sqrt(Dot(a, a));

    // ---------------------------------------------------------------- measurement tools

    public void BeginDistanceMeasurement() => BeginMeasurement(StepMeasurementTool.Distance);

    /// <summary>Starts a measurement tool; the next clicks pick the entities it needs.</summary>
    public void BeginMeasurement(StepMeasurementTool tool)
    {
        if (!HasModel)
        {
            return;
        }

        isBuildingReference = false;
        isBuildingFaceReference = false;
        referenceSelection.Clear();
        activeTool = tool;
        pickedEntities.Clear();
        isMeasuring = tool != StepMeasurementTool.None;
        measurementStartIndex = null;
        measurementEndIndex = null;
        CurrentMeasurement = null;
        if (tool == StepMeasurementTool.Volume)
        {
            CompleteVolumeMeasurement();
            return;
        }
        MeasurementText = ToolPrompt(tool);
        RenderModel();
        MeasurementChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cancels the active tool and clears every measurement annotation.</summary>
    public void ClearMeasurement()
    {
        ClearMeasurementState();
        MeasurementText = "Select Distance, then click two model vertices.";
        RenderModel();
        MeasurementChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cancels the active tool but keeps completed annotations on screen.</summary>
    public void CancelTool()
    {
        activeTool = StepMeasurementTool.None;
        isMeasuring = false;
        pickedEntities.Clear();
        measurementStartIndex = null;
        measurementEndIndex = null;
    }

    private void ClearMeasurementState()
    {
        CancelTool();
        annotations.Clear();
        CurrentMeasurement = null;
        LastMeasurementResult = null;
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
        var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);
        CurrentMeasurement = new StepMeasurement(distance, deltaX, deltaY, deltaZ);
        var units = UnitsOfPoint(firstPointIndex);
        var text = FormattableString.Invariant(
            $"Distance {distance:0.####} {units}\nΔX {deltaX:0.####}   ΔY {deltaY:0.####}   ΔZ {deltaZ:0.####}");
        if (customReference is { } frame)
        {
            var local = ToFrame(Relative(second, first), frame);
            text += FormattableString.Invariant($"\nReference ΔX {local.X:0.####}   ΔY {local.Y:0.####}   ΔZ {local.Z:0.####}");
        }
        CompleteMeasurement(new StepMeasurementResult(
            StepMeasurementTool.Distance,
            FormattableString.Invariant($"{distance:0.###} {units}"),
            text,
            distance,
            [first, second]));
        isMeasuring = activeTool == StepMeasurementTool.Distance;
        if (isMeasuring)
        {
            // Keep the tool armed so the next two clicks start a fresh distance.
            measurementStartIndex = null;
            measurementEndIndex = null;
        }
    }

    private static string ToolPrompt(StepMeasurementTool tool) => tool switch
    {
        StepMeasurementTool.Point => "Point: click a model vertex to read its coordinates.",
        StepMeasurementTool.Distance => "Distance: click the first model vertex.",
        StepMeasurementTool.MinimumDistance => "Minimum distance: click the first vertex, edge, or face.",
        StepMeasurementTool.EdgeLength => "Edge length: click an edge.",
        StepMeasurementTool.Radius => "Radius: click a circular edge or a cylindrical, spherical, or toroidal face.",
        StepMeasurementTool.Angle => "Angle: click the first straight edge or planar face.",
        StepMeasurementTool.FaceArea => "Area: click a face.",
        StepMeasurementTool.Volume => "Volume: computed for the visible models.",
        _ => "Select a measurement tool."
    };

    private void CompleteMeasurement(StepMeasurementResult result)
    {
        annotations.Add(new StepMeasurementAnnotation(result));
        LastMeasurementResult = result;
        MeasurementText = result.Details;
        pickedEntities.Clear();
        RenderModel();
        MeasurementChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteVolumeMeasurement()
    {
        var lines = new List<string>();
        double? total = null;
        var anchors = new List<StepPoint3>();
        foreach (var layer in layers.Where(item => item.IsVisible && item.Model.Geometry is not null))
        {
            var mass = layer.Model.Geometry!.MassProperties();
            total = (total ?? 0) + mass.Volume;
            anchors.Add(mass.Centroid);
            var cubic = layer.Units == "mm" ? "mm³" : "units³";
            var square = layer.Units == "mm" ? "mm²" : "units²";
            lines.Add(FormattableString.Invariant(
                $"{layer.Label}: volume {mass.Volume:0.###} {cubic}, surface {mass.SurfaceArea:0.###} {square}, centroid ({mass.Centroid.X:0.###}, {mass.Centroid.Y:0.###}, {mass.Centroid.Z:0.###})"));
        }
        var box = CalculateBoundingBox(customReference);
        lines.Add(FormattableString.Invariant($"Bounding box {box.X:0.###} × {box.Y:0.###} × {box.Z:0.###}{(box.UsesCustomReference ? " (custom reference)" : string.Empty)}"));
        if (total is null)
        {
            MeasurementText = "Volume is only available for tessellated solids.";
            activeTool = StepMeasurementTool.None;
            isMeasuring = false;
            MeasurementChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        activeTool = StepMeasurementTool.None;
        isMeasuring = false;
        CompleteMeasurement(new StepMeasurementResult(
            StepMeasurementTool.Volume,
            FormattableString.Invariant($"V {total.Value:0.###}"),
            string.Join('\n', lines),
            total.Value,
            anchors.Count > 0 ? [anchors[0]] : []));
    }

    private void HandleToolClick(Point pointer)
    {
        switch (activeTool)
        {
            case StepMeasurementTool.Point:
                if (PickVertex(pointer) is { } vertex)
                {
                    var world = points[vertex.PointIndex];
                    var units = layers[vertex.LayerIndex].Units;
                    var text = FormattableString.Invariant($"Point X {world.X:0.####}   Y {world.Y:0.####}   Z {world.Z:0.####} {units}");
                    if (customReference is { } frame)
                    {
                        var local = ToFrame(Relative(world, frame.Origin), frame);
                        text += FormattableString.Invariant($"\nReference X {local.X:0.####}   Y {local.Y:0.####}   Z {local.Z:0.####}");
                    }
                    CompleteMeasurement(new StepMeasurementResult(
                        StepMeasurementTool.Point,
                        FormattableString.Invariant($"({world.X:0.##}, {world.Y:0.##}, {world.Z:0.##})"),
                        text, 0, [world]));
                }
                else
                {
                    Prompt("No vertex at that position. Click closer to a model corner.");
                }
                return;

            case StepMeasurementTool.Distance:
                SelectMeasurementPoint(pointer);
                return;

            case StepMeasurementTool.MinimumDistance:
                if (PickEntity(pointer) is { } picked)
                {
                    pickedEntities.Add(picked);
                    if (pickedEntities.Count < 2)
                    {
                        Prompt($"{Describe(picked)} selected. Click the second vertex, edge, or face.");
                        return;
                    }
                    CompleteMinimumDistance(pickedEntities[0], pickedEntities[1]);
                }
                else
                {
                    Prompt("Nothing selectable at that position.");
                }
                return;

            case StepMeasurementTool.EdgeLength:
                if (PickEdge(pointer) is { } edge && EdgeInfo(edge) is { } curve)
                {
                    var units = layers[edge.LayerIndex].Units;
                    var kind = curve.Kind switch
                    {
                        StepCurveKind.Line => "straight edge",
                        StepCurveKind.Circle => "circular arc",
                        StepCurveKind.Ellipse => "elliptical arc",
                        _ => "curved edge"
                    };
                    var text = FormattableString.Invariant($"Edge length {curve.Length:0.####} {units} ({kind})");
                    if (curve.Kind == StepCurveKind.Circle && curve.Radius is { } radius)
                    {
                        text += FormattableString.Invariant($"\nRadius {radius:0.####}   Diameter {2 * radius:0.####}");
                    }
                    var deltas = Relative(curve.End, curve.Start);
                    text += FormattableString.Invariant($"\nChord ΔX {deltas.X:0.####}   ΔY {deltas.Y:0.####}   ΔZ {deltas.Z:0.####}");
                    CompleteMeasurement(new StepMeasurementResult(
                        StepMeasurementTool.EdgeLength,
                        FormattableString.Invariant($"L {curve.Length:0.###} {units}"),
                        text, curve.Length, [curve.Start, curve.End], edge));
                }
                else
                {
                    Prompt("Click a B-rep edge of a STEP model (STL meshes have no exact edges).");
                }
                return;

            case StepMeasurementTool.Radius:
                CompleteRadius(pointer);
                return;

            case StepMeasurementTool.Angle:
                if (PickEntity(pointer, allowVertices: false) is { } angular)
                {
                    pickedEntities.Add(angular);
                    if (pickedEntities.Count < 2)
                    {
                        Prompt($"{Describe(angular)} selected. Click the second straight edge or planar face.");
                        return;
                    }
                    CompleteAngle(pickedEntities[0], pickedEntities[1]);
                }
                else
                {
                    Prompt("Click a straight edge or a planar face.");
                }
                return;

            case StepMeasurementTool.FaceArea:
                if (PickFace(pointer) is { } face && FaceInfo(face) is { } surface)
                {
                    var units = layers[face.LayerIndex].Units;
                    var square = units == "mm" ? "mm²" : "units²";
                    var text = FormattableString.Invariant($"Face area {surface.Area:0.####} {square} ({SurfaceLabel(surface.Kind)})");
                    if (surface.Radius is { } radius)
                    {
                        text += FormattableString.Invariant($"\nRadius {radius:0.####}   Diameter {2 * radius:0.####}");
                    }
                    if (surface.Axis is { } axis)
                    {
                        text += FormattableString.Invariant($"\n{(surface.Kind == StepSurfaceKind.Plane ? "Normal" : "Axis")} ({axis.X:0.####}, {axis.Y:0.####}, {axis.Z:0.####})");
                    }
                    CompleteMeasurement(new StepMeasurementResult(
                        StepMeasurementTool.FaceArea,
                        FormattableString.Invariant($"A {surface.Area:0.###} {square}"),
                        text, surface.Area, [surface.Centroid], face));
                }
                else
                {
                    Prompt("Click a face of a STEP model (STL meshes have no exact faces).");
                }
                return;
        }
    }

    private void Prompt(string text)
    {
        MeasurementText = text;
        RenderModel();
        MeasurementChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteRadius(Point pointer)
    {
        if (PickEdge(pointer) is { } edge && EdgeInfo(edge) is { Kind: StepCurveKind.Circle, Radius: { } radius, Center: { } center } curve)
        {
            var units = layers[edge.LayerIndex].Units;
            var text = FormattableString.Invariant(
                $"Radius {radius:0.####} {units}   Diameter {2 * radius:0.####} {units}\nCenter ({center.X:0.####}, {center.Y:0.####}, {center.Z:0.####})\nArc length {curve.Length:0.####}");
            if (curve.Axis is { } axis)
            {
                text += FormattableString.Invariant($"\nAxis ({axis.X:0.####}, {axis.Y:0.####}, {axis.Z:0.####})");
            }
            CompleteMeasurement(new StepMeasurementResult(
                StepMeasurementTool.Radius,
                FormattableString.Invariant($"R {radius:0.###} / Ø {2 * radius:0.###}"),
                text, radius, [center, curve.Start], edge));
            return;
        }

        if (PickFace(pointer) is { } face && FaceInfo(face) is { Radius: { } faceRadius } surface)
        {
            var units = layers[face.LayerIndex].Units;
            var text = FormattableString.Invariant(
                $"{SurfaceLabel(surface.Kind)} radius {faceRadius:0.####} {units}   Diameter {2 * faceRadius:0.####} {units}");
            if (surface.MinorRadius is { } minor)
            {
                text += FormattableString.Invariant($"\nMinor radius {minor:0.####}");
            }
            if (surface.Origin is { } origin)
            {
                text += FormattableString.Invariant($"\nAxis point ({origin.X:0.####}, {origin.Y:0.####}, {origin.Z:0.####})");
            }
            if (surface.Axis is { } axis)
            {
                text += FormattableString.Invariant($"\nAxis ({axis.X:0.####}, {axis.Y:0.####}, {axis.Z:0.####})");
            }
            CompleteMeasurement(new StepMeasurementResult(
                StepMeasurementTool.Radius,
                FormattableString.Invariant($"R {faceRadius:0.###} / Ø {2 * faceRadius:0.###}"),
                text, faceRadius, [surface.Centroid], face));
            return;
        }

        Prompt("Click a circular edge or a cylindrical, conical, spherical, or toroidal face.");
    }

    private void CompleteMinimumDistance(StepPickedEntity first, StepPickedEntity second)
    {
        StepDistanceInfo? result = null;
        var firstGeometry = layers[first.LayerIndex].Model.Geometry as OcctStepGeometry;
        var secondGeometry = layers[second.LayerIndex].Model.Geometry as OcctStepGeometry;
        if (first.Entity is { } firstEntity && second.Entity is { } secondEntity
            && firstGeometry is not null && secondGeometry is not null)
        {
            result = first.LayerIndex == second.LayerIndex
                ? firstGeometry.Distance(firstEntity, secondEntity)
                : OcctStepGeometry.DistanceBetween(firstGeometry, firstEntity, secondGeometry, secondEntity);
        }
        if (result is null && first.Entity?.Kind is null or StepEntityKind.Vertex && second.Entity?.Kind is null or StepEntityKind.Vertex)
        {
            // Mesh vertices (STL or fallback models) only support point-to-point distance.
            var a = points[first.PointIndex];
            var b = points[second.PointIndex];
            result = new StepDistanceInfo(Length(Relative(b, a)), a, b);
        }
        if (result is null)
        {
            Prompt("Minimum distance between these entities needs STEP geometry on both sides.");
            pickedEntities.Clear();
            return;
        }

        var units = layers[first.LayerIndex].Units;
        var delta = Relative(result.PointB, result.PointA);
        var text = FormattableString.Invariant(
            $"Minimum distance {result.Distance:0.####} {units} ({Describe(first)} → {Describe(second)})\nΔX {delta.X:0.####}   ΔY {delta.Y:0.####}   ΔZ {delta.Z:0.####}");
        CurrentMeasurement = new StepMeasurement(result.Distance, delta.X, delta.Y, delta.Z);
        CompleteMeasurement(new StepMeasurementResult(
            StepMeasurementTool.MinimumDistance,
            FormattableString.Invariant($"{result.Distance:0.###} {units}"),
            text, result.Distance, [result.PointA, result.PointB], first, second));
    }

    private void CompleteAngle(StepPickedEntity first, StepPickedEntity second)
    {
        var firstDirection = DirectionOf(first, out var firstLabel, out var firstAnchor);
        var secondDirection = DirectionOf(second, out var secondLabel, out var secondAnchor);
        if (firstDirection is null || secondDirection is null)
        {
            Prompt("Angles need straight edges or planar faces of a STEP model.");
            pickedEntities.Clear();
            return;
        }

        var cosine = Math.Clamp(Dot(firstDirection.Value, secondDirection.Value), -1, 1);
        var angle = Math.Acos(cosine) * 180 / Math.PI;
        // Edge/plane pairs measure the angle between the edge and the plane, not its normal.
        var mixed = first.Entity?.Kind != second.Entity?.Kind;
        if (mixed)
        {
            angle = 90 - Math.Acos(Math.Abs(cosine)) * 180 / Math.PI;
        }
        var text = mixed
            ? FormattableString.Invariant($"Angle between {firstLabel} and {secondLabel}: {angle:0.###}°")
            : FormattableString.Invariant($"Angle between {firstLabel} and {secondLabel}: {angle:0.###}°   (supplement {180 - angle:0.###}°)");
        CompleteMeasurement(new StepMeasurementResult(
            StepMeasurementTool.Angle,
            FormattableString.Invariant($"{angle:0.##}°"),
            text, angle, [firstAnchor, secondAnchor], first, second));
    }

    private StepPoint3? DirectionOf(StepPickedEntity picked, out string label, out StepPoint3 anchor)
    {
        label = Describe(picked);
        anchor = points[picked.PointIndex];
        if (picked.Entity is not { } entity)
        {
            return null;
        }
        if (entity.Kind == StepEntityKind.Edge && EdgeInfo(picked) is { Kind: StepCurveKind.Line, Direction: { } direction } curve)
        {
            anchor = Scale(Add(curve.Start, curve.End), 0.5);
            return direction;
        }
        if (entity.Kind == StepEntityKind.Face && FaceInfo(picked) is { Kind: StepSurfaceKind.Plane, Axis: { } normal } surface)
        {
            anchor = surface.Centroid;
            return normal;
        }
        return null;
    }

    private static string SurfaceLabel(StepSurfaceKind kind) => kind switch
    {
        StepSurfaceKind.Plane => "planar face",
        StepSurfaceKind.Cylinder => "cylindrical face",
        StepSurfaceKind.Cone => "conical face",
        StepSurfaceKind.Sphere => "spherical face",
        StepSurfaceKind.Torus => "toroidal face",
        _ => "free-form face"
    };

    private string Describe(StepPickedEntity picked) => picked.Entity switch
    {
        { Kind: StepEntityKind.Vertex, Index: var index } => $"vertex {index + 1}",
        { Kind: StepEntityKind.Edge, Index: var index } => $"edge {index + 1}",
        { Kind: StepEntityKind.Face, Index: var index } => $"face {index + 1}",
        _ => "point"
    };

    private StepCurveInfo? EdgeInfo(StepPickedEntity picked) =>
        picked.Entity is { Kind: StepEntityKind.Edge } entity && layers[picked.LayerIndex].Model.Geometry is { EdgeCount: > 0 } geometry
            ? geometry.EdgeInfo(entity.Index)
            : null;

    private StepSurfaceInfo? FaceInfo(StepPickedEntity picked) =>
        picked.Entity is { Kind: StepEntityKind.Face } entity && layers[picked.LayerIndex].Model.Geometry is { FaceCount: > 0 } geometry
            ? geometry.FaceInfo(entity.Index)
            : null;

    private string UnitsOfPoint(int pointIndex) => layers.Count == 0 ? "units" : layers[LayerOfPoint(pointIndex)].Units;

    private static StepPoint3 ToFrame(StepPoint3 vector, StepReferenceFrame frame) =>
        new(Dot(vector, frame.X), Dot(vector, frame.Y), Dot(vector, frame.Z));

    // ---------------------------------------------------------------- picking

    /// <summary>Nearest B-rep vertex under the pointer, else the nearest selectable mesh point.</summary>
    private StepPickedEntity? PickVertex(Point pointer)
    {
        if (screenPoints.Length != points.Count)
        {
            return null;
        }

        StepPickedEntity? best = null;
        var bestDistance = PickRadius;
        foreach (var vertex in brepVertices)
        {
            if (!layers[vertex.LayerIndex].IsVisible) continue;
            var distance = (screenPoints[vertex.PointIndex] - pointer).Length;
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = new StepPickedEntity(vertex.LayerIndex, new StepEntityRef(StepEntityKind.Vertex, vertex.VertexIndex), vertex.PointIndex, -1);
            }
        }
        if (best is not null)
        {
            return best;
        }

        var nearest = selectablePointIndices
            .Where(index => layers[LayerOfPoint(index)].IsVisible)
            .Select(index => (Index: index, Distance: (screenPoints[index] - pointer).Length))
            .Where(candidate => candidate.Distance <= PickRadius)
            .OrderBy(candidate => candidate.Distance)
            .FirstOrDefault((Index: -1, Distance: double.MaxValue));
        return nearest.Index < 0
            ? null
            : new StepPickedEntity(LayerOfPoint(nearest.Index), null, nearest.Index, -1);
    }

    /// <summary>Nearest rendered edge segment under the pointer that belongs to a B-rep edge.</summary>
    private StepPickedEntity? PickEdge(Point pointer)
    {
        StepPickedEntity? best = null;
        var bestDistance = PickRadius * 0.75;
        foreach (var rendered in renderedEdges)
        {
            var edge = displayEdges[rendered.DisplayEdgeIndex];
            if (edge.EdgeIndex < 0) continue;
            var distance = DistanceToSegment(pointer, rendered.Start, rendered.End);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = new StepPickedEntity(edge.LayerIndex, new StepEntityRef(StepEntityKind.Edge, edge.EdgeIndex), edge.FirstPointIndex, -1);
            }
        }
        return best;
    }

    /// <summary>Front-most visible triangle under the pointer, mapped to its B-rep face when known.</summary>
    private StepPickedEntity? PickFace(Point pointer)
    {
        if (screenPoints.Length != points.Count)
        {
            return null;
        }

        var bestTriangle = -1;
        var bestDepth = double.NegativeInfinity;
        for (var index = 0; index < triangles.Count; index++)
        {
            if (!layers[triangleLayers[index]].IsVisible) continue;
            var triangle = triangles[index];
            if (!IsFrontFacing(index) || !PointInTriangle(pointer, triangle)) continue;
            var depth = DepthAt(pointer, triangle);
            if (depth > bestDepth)
            {
                bestDepth = depth;
                bestTriangle = index;
            }
        }
        if (bestTriangle < 0)
        {
            return null;
        }
        var faceIndex = triangleFaces[bestTriangle];
        return new StepPickedEntity(
            triangleLayers[bestTriangle],
            faceIndex >= 0 ? new StepEntityRef(StepEntityKind.Face, faceIndex) : null,
            triangles[bestTriangle].FirstIndex,
            bestTriangle);
    }

    private StepPickedEntity? PickEntity(Point pointer, bool allowVertices = true)
    {
        if (allowVertices && PickVertex(pointer) is { } vertex && vertex.Entity is not null)
        {
            return vertex;
        }
        if (PickEdge(pointer) is { } edge)
        {
            return edge;
        }
        if (PickFace(pointer) is { } face)
        {
            return face;
        }
        return allowVertices ? PickVertex(pointer) : null;
    }

    private double DepthAt(Point pointer, StepTriangle3 triangle)
    {
        var a = screenPoints[triangle.FirstIndex];
        var b = screenPoints[triangle.SecondIndex];
        var c = screenPoints[triangle.ThirdIndex];
        var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (Math.Abs(area) < 0.000000001)
        {
            return double.NegativeInfinity;
        }
        var wa = ((b.X - pointer.X) * (c.Y - pointer.Y) - (b.Y - pointer.Y) * (c.X - pointer.X)) / area;
        var wb = ((c.X - pointer.X) * (a.Y - pointer.Y) - (c.Y - pointer.Y) * (a.X - pointer.X)) / area;
        var wc = 1 - wa - wb;
        return wa * depthValues[triangle.FirstIndex] + wb * depthValues[triangle.SecondIndex] + wc * depthValues[triangle.ThirdIndex];
    }

    // ---------------------------------------------------------------- snapshot

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

    // ---------------------------------------------------------------- rendering

    private void RenderModel()
    {
        if (pendingViewportRender is { Status: DispatcherOperationStatus.Pending })
        {
            pendingViewportRender.Abort();
            pendingViewportRender = null;
        }
        RenderInvocationCount++;
        ModelCanvas.Children.Clear();
        EdgeSurface.Clear();
        SurfaceLayer.Clear();
        RenderedEdgeCount = 0;
        RenderedSurfaceTriangleCount = 0;
        renderedEdges = [];
        if (!HasModel || ViewerRoot.ActualWidth <= 1 || ViewerRoot.ActualHeight <= 1)
        {
            return;
        }

        var viewDistance = ViewDistance();
        transformedPoints = points.Select(Transform).ToArray();
        depthValues = new double[transformedPoints.Length];
        var projected = new Point[transformedPoints.Length];
        for (var index = 0; index < transformedPoints.Length; index++)
        {
            var view = transformedPoints[index];
            if (projection == StepProjectionMode.Perspective)
            {
                var denominator = Math.Max(0.000001, viewDistance - view.Z);
                var factor = viewDistance / denominator;
                projected[index] = new Point(view.X * factor, view.Y * factor);
                depthValues[index] = 1 / denominator;
            }
            else
            {
                projected[index] = new Point(view.X, view.Y);
                depthValues[index] = view.Z;
            }
        }
        var scale = ViewerRoot.ActualWidth / Math.Max(0.000001, cameraWidth);
        Point Screen(Point point) => new(
            ViewerRoot.ActualWidth / 2 + point.X * scale + panOffset.X,
            ViewerRoot.ActualHeight / 2 - point.Y * scale + panOffset.Y);
        screenPoints = projected.Select(Screen).ToArray();

        var fills = displayMode is StepDisplayMode.Shaded or StepDisplayMode.VisibleEdges or StepDisplayMode.Transparent;
        var showsAllEdges = displayMode is StepDisplayMode.Wireframe or StepDisplayMode.Transparent;
        var showsVisibleEdges = displayMode is StepDisplayMode.VisibleEdges or StepDisplayMode.HiddenLine;

        if (triangles.Count > 0)
        {
            SolidViewport.Visibility = fills ? Visibility.Visible : Visibility.Hidden;
            UpdateSolidCamera(cameraWidth, viewDistance);
            var visibleTriangleIndices = Enumerable.Range(0, triangles.Count)
                .Where(index => layers[triangleLayers[index]].IsVisible)
                .ToArray();
            if (fills)
            {
                var renderedTriangles = visibleTriangleIndices
                    .Select(index =>
                    {
                        var triangle = triangles[index];
                        var first = transformedPoints[triangle.FirstIndex];
                        var second = transformedPoints[triangle.SecondIndex];
                        var third = transformedPoints[triangle.ThirdIndex];
                        var normal = TriangleNormal(first, second, third);
                        var light = Math.Clamp(0.58 + Math.Abs(normal.X * -0.3 + normal.Y * 0.45 + normal.Z * 0.84) * 0.42, 0, 1);
                        return new StepScreenTriangle(
                            screenPoints[triangle.FirstIndex],
                            screenPoints[triangle.SecondIndex],
                            screenPoints[triangle.ThirdIndex],
                            (depthValues[triangle.FirstIndex] + depthValues[triangle.SecondIndex] + depthValues[triangle.ThirdIndex]) / 3,
                            (byte)Math.Clamp((int)Math.Round(light * 15), 0, 15),
                            index,
                            triangleLayers[index]);
                    })
                    .OrderBy(triangle => triangle.Depth)
                    .ThenBy(triangle => triangle.SourceIndex)
                    .ToArray();
                SurfaceLayer.Draw(renderedTriangles, layerIndex => Palette(layers[layerIndex], displayMode == StepDisplayMode.Transparent));
                RenderedSurfaceTriangleCount = renderedTriangles.Length;
            }
            if (showsAllEdges)
            {
                var all = new List<StepRenderedEdge>();
                for (var index = 0; index < displayEdges.Count; index++)
                {
                    var edge = displayEdges[index];
                    if (!layers[edge.LayerIndex].IsVisible) continue;
                    all.Add(new StepRenderedEdge(screenPoints[edge.FirstPointIndex], screenPoints[edge.SecondPointIndex], index));
                }
                renderedEdges = all.ToArray();
            }
            else if (showsVisibleEdges)
            {
                renderedEdges = BuildVisibleEdgeSegments(visibleTriangleIndices);
            }
            if (renderedEdges.Length > 0)
            {
                EdgeSurface.Draw(renderedEdges.Select(edge => new StepScreenEdge(edge.Start, edge.End)).ToArray(), displayMode);
                RenderedEdgeCount = renderedEdges.Length;
            }
        }
        else if (segments.Count > 0)
        {
            SolidViewport.Visibility = Visibility.Hidden;
            for (var index = 0; index < segments.Count; index++)
            {
                if (!layers[segmentLayers[index]].IsVisible) continue;
                var start = screenPoints[segments[index].StartIndex];
                var end = screenPoints[segments[index].EndIndex];
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

        if (showBoundingBox)
        {
            RenderBoundingBox(Screen);
        }
        for (var index = 0; index < referenceSelection.Count && screenPoints.Length == points.Count; index++)
        {
            AddMeasurementMarker(screenPoints[referenceSelection[index]], $"R{index + 1}");
        }
        RenderSelection();
        RenderAnnotations(Screen);
        RenderMeasurement();
    }

    private double ViewDistance()
    {
        var extent = points.Select(point => Length(Relative(point, modelCenter))).DefaultIfEmpty(1).Max();
        return Math.Max(1, extent * 4);
    }

    private SolidColorBrush[] Palette(SceneLayer layer, bool transparentMode)
    {
        var alpha = (byte)Math.Round(255 * (transparentMode ? Math.Min(layer.Opacity, 0.4) : layer.Opacity));
        var key = (layer.Color, alpha);
        if (!paletteCache.TryGetValue(key, out var palette))
        {
            palette = Enumerable.Range(0, 16)
                .Select(index =>
                {
                    var factor = 0.58 + index / 15d * 0.42;
                    var brush = new SolidColorBrush(Color.FromArgb(
                        alpha,
                        (byte)Math.Round(layer.Color.R * factor),
                        (byte)Math.Round(layer.Color.G * factor),
                        (byte)Math.Round(layer.Color.B * factor)));
                    brush.Freeze();
                    return brush;
                })
                .ToArray();
            paletteCache[key] = palette;
        }
        return palette;
    }

    private void RenderBoundingBox(Func<Point, Point> screen)
    {
        var box = CalculateBoundingBox(customReference);
        var frame = customReference ?? StepReferenceFrame.Model;
        var corners = new StepPoint3[8];
        for (var i = 0; i < 8; i++)
        {
            var lx = (i & 1) == 0 ? box.MinX : box.MaxX;
            var ly = (i & 2) == 0 ? box.MinY : box.MaxY;
            var lz = (i & 4) == 0 ? box.MinZ : box.MaxZ;
            corners[i] = Add(frame.Origin, Add(Scale(frame.X, lx), Add(Scale(frame.Y, ly), Scale(frame.Z, lz))));
        }
        var projected = corners.Select(point => screen(Project(point))).ToArray();
        foreach (var (a, b) in new[] { (0,1),(2,3),(4,5),(6,7),(0,2),(1,3),(4,6),(5,7),(0,4),(1,5),(2,6),(3,7) })
        {
            ModelCanvas.Children.Add(new Line { X1 = projected[a].X, Y1 = projected[a].Y, X2 = projected[b].X, Y2 = projected[b].Y,
                Stroke = Brushes.DarkOrange, StrokeThickness = 1.25, StrokeDashArray = new DoubleCollection([5,3]) });
        }
    }

    /// <summary>Projects a world point with the current camera (used for non-vertex anchors).</summary>
    private Point Project(StepPoint3 point)
    {
        var view = Transform(point);
        if (projection != StepProjectionMode.Perspective)
        {
            return new Point(view.X, view.Y);
        }
        var factor = ViewDistance() / Math.Max(0.000001, ViewDistance() - view.Z);
        return new Point(view.X * factor, view.Y * factor);
    }

    private Point ScreenOf(StepPoint3 point)
    {
        var scale = ViewerRoot.ActualWidth / Math.Max(0.000001, cameraWidth);
        var projected = Project(point);
        return new Point(
            ViewerRoot.ActualWidth / 2 + projected.X * scale + panOffset.X,
            ViewerRoot.ActualHeight / 2 - projected.Y * scale + panOffset.Y);
    }

    private void RenderSelection()
    {
        foreach (var picked in pickedEntities)
        {
            DrawEntityHighlight(picked, Brushes.OrangeRed, 2.5);
        }
    }

    private void DrawEntityHighlight(StepPickedEntity picked, Brush brush, double thickness)
    {
        if (screenPoints.Length != points.Count)
        {
            return;
        }
        switch (picked.Entity?.Kind)
        {
            case StepEntityKind.Edge:
                for (var index = 0; index < displayEdges.Count; index++)
                {
                    var edge = displayEdges[index];
                    if (edge.LayerIndex != picked.LayerIndex || edge.EdgeIndex != picked.Entity.Value.Index) continue;
                    var start = screenPoints[edge.FirstPointIndex];
                    var end = screenPoints[edge.SecondPointIndex];
                    ModelCanvas.Children.Add(new Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Stroke = brush, StrokeThickness = thickness });
                }
                break;
            case StepEntityKind.Face:
            {
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    for (var index = 0; index < triangles.Count; index++)
                    {
                        if (triangleLayers[index] != picked.LayerIndex || triangleFaces[index] != picked.Entity.Value.Index || !IsFrontFacing(index)) continue;
                        var triangle = triangles[index];
                        context.BeginFigure(screenPoints[triangle.FirstIndex], true, true);
                        context.LineTo(screenPoints[triangle.SecondIndex], false, false);
                        context.LineTo(screenPoints[triangle.ThirdIndex], false, false);
                    }
                }
                geometry.Freeze();
                var fill = new SolidColorBrush(Color.FromArgb(90, 255, 140, 0));
                fill.Freeze();
                ModelCanvas.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Fill = fill, Stroke = brush, StrokeThickness = 1 });
                break;
            }
            default:
                AddMeasurementMarker(screenPoints[picked.PointIndex], string.Empty);
                break;
        }
    }

    private void RenderAnnotations(Func<Point, Point> screen)
    {
        foreach (var annotation in annotations)
        {
            var result = annotation.Result;
            var anchors = result.Anchors.Select(anchor => screen(Project(anchor))).ToArray();
            if (anchors.Length == 0)
            {
                continue;
            }
            if (result.FirstEntity is { } first) DrawEntityHighlight(first, Brushes.Crimson, 2);
            if (result.SecondEntity is { } second) DrawEntityHighlight(second, Brushes.Crimson, 2);
            if (anchors.Length >= 2 && result.Tool is StepMeasurementTool.Distance or StepMeasurementTool.MinimumDistance or StepMeasurementTool.Angle or StepMeasurementTool.EdgeLength)
            {
                ModelCanvas.Children.Add(new Line
                {
                    X1 = anchors[0].X, Y1 = anchors[0].Y, X2 = anchors[1].X, Y2 = anchors[1].Y,
                    Stroke = Brushes.Crimson, StrokeThickness = 2, StrokeDashArray = new DoubleCollection([4, 3])
                });
            }
            foreach (var anchor in anchors.Take(2))
            {
                AddMeasurementMarker(anchor, string.Empty);
            }
            var labelPosition = anchors.Length >= 2
                ? new Point((anchors[0].X + anchors[1].X) / 2, (anchors[0].Y + anchors[1].Y) / 2)
                : anchors[0];
            AddAnnotationLabel(labelPosition, result.Label);
        }
    }

    private void AddAnnotationLabel(Point position, string text)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
            BorderBrush = Brushes.Crimson,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 2, 5, 2),
            Child = new TextBlock { Text = text, Foreground = Brushes.Crimson, FontWeight = FontWeights.SemiBold, FontSize = 12 }
        };
        Canvas.SetLeft(border, position.X + 8);
        Canvas.SetTop(border, position.Y - 22);
        ModelCanvas.Children.Add(border);
    }

    private void BuildCreaseEdges(int layerIndex, SceneLayer layer)
    {
        var model = layer.Model;
        if (model.Triangles.Count == 0 || model.Points.Count == 0)
        {
            return;
        }

        var offset = layer.PointOffset;
        var triangleOffset = layer.TriangleOffset;
        var minX = model.Points.Min(point => point.X);
        var maxX = model.Points.Max(point => point.X);
        var minY = model.Points.Min(point => point.Y);
        var maxY = model.Points.Max(point => point.Y);
        var minZ = model.Points.Min(point => point.Z);
        var maxZ = model.Points.Max(point => point.Z);
        var tolerance = Math.Max(0.0000001, Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)) * 0.00000001);
        var canonicalByCoordinate = new Dictionary<StepVertexKey, (int Id, int PointIndex)>();
        var canonicalIds = new int[model.Points.Count];
        for (var pointIndex = 0; pointIndex < model.Points.Count; pointIndex++)
        {
            var point = model.Points[pointIndex];
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
        for (var triangleIndex = 0; triangleIndex < model.Triangles.Count; triangleIndex++)
        {
            var triangle = model.Triangles[triangleIndex];
            AddEdge(triangle.FirstIndex, triangle.SecondIndex, triangleIndex);
            AddEdge(triangle.SecondIndex, triangle.ThirdIndex, triangleIndex);
            AddEdge(triangle.ThirdIndex, triangle.FirstIndex, triangleIndex);
        }

        foreach (var edge in edgeBuilders.Values)
        {
            var isBoundaryOrCrease = edge.TriangleIndices.Count != 2;
            if (!isBoundaryOrCrease)
            {
                var firstNormal = TriangleNormal(model, model.Triangles[edge.TriangleIndices[0]]);
                var secondNormal = TriangleNormal(model, model.Triangles[edge.TriangleIndices[1]]);
                var dot = firstNormal.X * secondNormal.X + firstNormal.Y * secondNormal.Y + firstNormal.Z * secondNormal.Z;
                isBoundaryOrCrease = dot < Math.Cos(25 * Math.PI / 180);
            }
            displayEdges.Add(new StepDisplayEdge(
                edge.FirstPointIndex + offset,
                edge.SecondPointIndex + offset,
                isBoundaryOrCrease,
                edge.TriangleIndices.Select(index => index + triangleOffset).ToArray(),
                layerIndex,
                -1));
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

    private static StepPoint3 TriangleNormal(StepModelData model, StepTriangle3 triangle) =>
        TriangleNormal(model.Points[triangle.FirstIndex], model.Points[triangle.SecondIndex], model.Points[triangle.ThirdIndex]);

    private bool IsVisibleFeatureEdge(StepDisplayEdge edge)
    {
        if (edge.TriangleIndices.Length == 0)
        {
            // B-rep edges are always feature edges; the depth buffer decides what is hidden.
            return true;
        }
        if (edge.IsBoundaryOrCrease || edge.TriangleIndices.Length != 2)
        {
            return edge.TriangleIndices.Any(IsFrontFacing);
        }

        var firstFacing = ProjectedTriangleArea(triangles[edge.TriangleIndices[0]]);
        var secondFacing = ProjectedTriangleArea(triangles[edge.TriangleIndices[1]]);
        return Math.Abs(firstFacing) > 0.000001
               && Math.Abs(secondFacing) > 0.000001
               && Math.Sign(firstFacing) != Math.Sign(secondFacing);
    }

    private StepRenderedEdge[] BuildVisibleEdgeSegments(int[] visibleTriangleIndices)
    {
        var depthBuffer = StepDepthBuffer.Build(
            ViewerRoot.ActualWidth,
            ViewerRoot.ActualHeight,
            screenPoints,
            depthValues,
            visibleTriangleIndices.Select(index => triangles[index]).ToArray());
        var result = new List<StepRenderedEdge>();
        for (var edgeIndex = 0; edgeIndex < displayEdges.Count; edgeIndex++)
        {
            var edge = displayEdges[edgeIndex];
            if (!layers[edge.LayerIndex].IsVisible || !IsVisibleFeatureEdge(edge))
            {
                continue;
            }
            var start = screenPoints[edge.FirstPointIndex];
            var end = screenPoints[edge.SecondPointIndex];
            var startDepth = depthValues[edge.FirstPointIndex];
            var endDepth = depthValues[edge.SecondPointIndex];
            var bufferStart = depthBuffer.ToBuffer(start);
            var bufferEnd = depthBuffer.ToBuffer(end);
            var length = (bufferEnd - bufferStart).Length;
            var steps = Math.Clamp((int)Math.Ceiling(length * 1.5), 1, 8192);
            int? visibleStart = null;
            for (var interval = 0; interval < steps; interval++)
            {
                var midpoint = (interval + 0.5) / steps;
                var sample = new Point(
                    start.X + (end.X - start.X) * midpoint,
                    start.Y + (end.Y - start.Y) * midpoint);
                var sampleDepth = startDepth + (endDepth - startDepth) * midpoint;
                var visible = depthBuffer.IsVisible(sample, sampleDepth);
                if (visible && visibleStart is null)
                {
                    visibleStart = interval;
                }
                if ((!visible || interval == steps - 1) && visibleStart is not null)
                {
                    var exclusiveEnd = visible && interval == steps - 1 ? interval + 1 : interval;
                    if (exclusiveEnd > visibleStart.Value)
                    {
                        result.Add(new StepRenderedEdge(
                            Interpolate(start, end, visibleStart.Value / (double)steps),
                            Interpolate(start, end, exclusiveEnd / (double)steps),
                            edgeIndex));
                    }
                    visibleStart = null;
                }
            }
        }
        return result.ToArray();
    }

    private static Point Interpolate(Point start, Point end, double value) => new(
        start.X + (end.X - start.X) * value,
        start.Y + (end.Y - start.Y) * value);

    private bool IsFrontFacing(int triangleIndex)
    {
        var triangle = triangles[triangleIndex];
        var normal = TriangleNormal(points[triangle.FirstIndex], points[triangle.SecondIndex], points[triangle.ThirdIndex]);
        if (projection == StepProjectionMode.Perspective)
        {
            var (depth, _) = ViewAxes();
            var camera = Add(modelCenter, Scale(depth, ViewDistance()));
            var centroid = Scale(Add(Add(points[triangle.FirstIndex], points[triangle.SecondIndex]), points[triangle.ThirdIndex]), 1d / 3);
            return Dot(normal, Relative(camera, centroid)) > 0.0000001;
        }
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

    private static StepPoint3 TriangleNormal(StepPoint3 first, StepPoint3 second, StepPoint3 third)
    {
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
        var diffuse = new DiffuseMaterial(new SolidColorBrush(DefaultPartColor));
        var specular = new SpecularMaterial(new SolidColorBrush(Color.FromRgb(210, 228, 240)), 28);
        var material = new MaterialGroup();
        material.Children.Add(diffuse);
        material.Children.Add(specular);
        material.Freeze();
        solidGeometryModel = new GeometryModel3D(mesh, material) { BackMaterial = material };
        SolidScene.Children.Add(solidGeometryModel);
    }

    private (StepPoint3 Depth, StepPoint3 Up) ViewAxes()
    {
        var sinYaw = Math.Sin(yaw);
        var cosYaw = Math.Cos(yaw);
        var sinPitch = Math.Sin(pitch);
        var cosPitch = Math.Cos(pitch);
        return (
            new StepPoint3(cosPitch * sinYaw, sinPitch, cosPitch * cosYaw),
            new StepPoint3(-sinPitch * sinYaw, cosPitch, -sinPitch * cosYaw));
    }

    private void UpdateSolidCamera(double width, double distance)
    {
        var (depth, up) = ViewAxes();
        var position = new Point3D(
            modelCenter.X + depth.X * distance,
            modelCenter.Y + depth.Y * distance,
            modelCenter.Z + depth.Z * distance);
        var look = new Vector3D(-depth.X * distance, -depth.Y * distance, -depth.Z * distance);
        var upVector = new Vector3D(up.X, up.Y, up.Z);
        var extent = distance / 4;
        if (projection == StepProjectionMode.Perspective)
        {
            perspectiveCamera.Position = position;
            perspectiveCamera.LookDirection = look;
            perspectiveCamera.UpDirection = upVector;
            perspectiveCamera.FieldOfView = 2 * Math.Atan((Math.Max(0.000001, width) / 2) / distance) * 180 / Math.PI;
            perspectiveCamera.NearPlaneDistance = Math.Max(0.0001, distance - extent * 2);
            perspectiveCamera.FarPlaneDistance = Math.Max(perspectiveCamera.NearPlaneDistance + 1, distance + extent * 2);
            if (!ReferenceEquals(SolidViewport.Camera, perspectiveCamera))
            {
                SolidViewport.Camera = perspectiveCamera;
            }
        }
        else
        {
            SolidCamera.Position = position;
            SolidCamera.LookDirection = look;
            SolidCamera.UpDirection = upVector;
            SolidCamera.Width = Math.Max(0.000001, width);
            SolidCamera.NearPlaneDistance = Math.Max(0.0001, distance - extent * 2);
            SolidCamera.FarPlaneDistance = Math.Max(SolidCamera.NearPlaneDistance + 1, distance + extent * 2);
            if (!ReferenceEquals(SolidViewport.Camera, SolidCamera))
            {
                SolidViewport.Camera = SolidCamera;
            }
        }
        SolidViewport.RenderTransform = new TranslateTransform(panOffset.X, panOffset.Y);
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

    // ---------------------------------------------------------------- input

    private void Viewer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        var pointer = e.GetPosition(ViewerRoot);
        if (HasModel && activeTool != StepMeasurementTool.None)
        {
            HandleToolClick(pointer);
            e.Handled = true;
            return;
        }
        if (isMeasuring && HasModel)
        {
            SelectMeasurementPoint(pointer);
            e.Handled = true;
            return;
        }
        if (isBuildingReference && HasModel)
        {
            SelectReferencePoint(pointer);
            e.Handled = true;
            return;
        }
        if (isBuildingFaceReference && HasModel)
        {
            SelectReferenceFaceOrEdge(pointer);
            e.Handled = true;
            return;
        }
        dragStart = pointer;
        isDragging = true;
        ViewerRoot.CaptureMouse();
    }

    private void Viewer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        isDragging = false;
        if (!isPanning)
        {
            ViewerRoot.ReleaseMouseCapture();
        }
    }

    private void Viewer_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasModel)
        {
            return;
        }
        dragStart = e.GetPosition(ViewerRoot);
        isPanning = true;
        ViewerRoot.CaptureMouse();
        e.Handled = true;
    }

    private void Viewer_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        isPanning = false;
        if (!isDragging)
        {
            ViewerRoot.ReleaseMouseCapture();
        }
    }

    private void Viewer_MouseMove(object sender, MouseEventArgs e)
    {
        if (!HasModel || (!isDragging && !isPanning))
        {
            return;
        }

        var current = e.GetPosition(ViewerRoot);
        if (isPanning)
        {
            panOffset += current - dragStart;
        }
        else
        {
            yaw += (current.X - dragStart.X) * 0.01;
            pitch = Math.Clamp(pitch + (current.Y - dragStart.Y) * 0.01, -Math.PI / 2, Math.PI / 2);
        }
        dragStart = current;
        RenderModel();
    }

    private void Viewer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!HasModel)
        {
            return;
        }
        cameraWidth = Math.Clamp(
            cameraWidth * (e.Delta > 0 ? 1 / 1.12 : 1 / 0.89),
            0.000001,
            1_000_000_000);
        RenderModel();
        e.Handled = true;
    }

    private void Viewer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (activeTool != StepMeasurementTool.None || isBuildingReference || isBuildingFaceReference))
        {
            CancelTool();
            isBuildingReference = false;
            isBuildingFaceReference = false;
            referenceSelection.Clear();
            MeasurementText = "Tool cancelled.";
            RenderModel();
            MeasurementChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void Viewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!HasModel)
        {
            return;
        }

        if (!hasAutoFitForCurrentModel)
        {
            AutoFitCurrentModelOnce();
            return;
        }

        if (!IsVisible)
        {
            renderDeferredWhileHidden = true;
            return;
        }

        RequestViewportRender();
    }

    private void Viewer_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && renderDeferredWhileHidden)
        {
            renderDeferredWhileHidden = false;
            RequestViewportRender();
        }
    }

    private void RequestViewportRender()
    {
        if (!HasModel
            || !IsVisible
            || ViewerRoot.ActualWidth <= 1
            || ViewerRoot.ActualHeight <= 1
            || pendingViewportRender is { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        pendingViewportRender = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() =>
            {
                pendingViewportRender = null;
                if (IsVisible && HasModel)
                {
                    RenderModel();
                }
            }));
    }

    internal void RotateForTest(double yawDelta, double pitchDelta)
    {
        yaw += yawDelta;
        pitch = Math.Clamp(pitch + pitchDelta, -Math.PI / 2, Math.PI / 2);
        RenderModel();
    }

    internal Point EdgeProjectionForTest(int pointIndex) => screenPoints[pointIndex];

    internal Point SolidCameraProjectionForTest(int pointIndex)
    {
        var point = new Point3D(points[pointIndex].X, points[pointIndex].Y, points[pointIndex].Z);
        var target = SolidCamera.Position + SolidCamera.LookDirection;
        var relative = point - target;
        var up = SolidCamera.UpDirection;
        up.Normalize();
        var right = Vector3D.CrossProduct(SolidCamera.LookDirection, up);
        right.Normalize();
        var scale = ViewerRoot.ActualWidth / SolidCamera.Width;
        return new Point(
            ViewerRoot.ActualWidth / 2 + Vector3D.DotProduct(relative, right) * scale + panOffset.X,
            ViewerRoot.ActualHeight / 2 - Vector3D.DotProduct(relative, up) * scale + panOffset.Y);
    }

    private void SelectMeasurementPoint(Point pointer)
    {
        if (screenPoints.Length != points.Count)
        {
            return;
        }

        if (PickVertex(pointer) is not { } nearest)
        {
            MeasurementText = "No vertex at that position. Click closer to a model corner or edge endpoint.";
            MeasurementChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (measurementStartIndex is null || measurementEndIndex is not null)
        {
            measurementStartIndex = nearest.PointIndex;
            measurementEndIndex = null;
            CurrentMeasurement = null;
            MeasurementText = "First vertex selected. Click the second vertex.";
            RenderModel();
            MeasurementChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        MeasureBetweenVertices(measurementStartIndex.Value, nearest.PointIndex);
    }

    private void SelectReferencePoint(Point pointer)
    {
        if (PickVertex(pointer) is not { } nearest)
        {
            MeasurementText = "No model vertex at that position.";
            ReferenceChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        referenceSelection.Add(nearest.PointIndex);
        if (referenceSelection.Count == 5)
        {
            try
            {
                SetCustomReferenceByPoints(referenceSelection[0], referenceSelection[1], referenceSelection[2], referenceSelection[3], referenceSelection[4]);
                MeasurementText = "Custom reference active.";
                isBuildingReference = false;
            }
            catch (InvalidOperationException exception)
            {
                MeasurementText = exception.Message;
                referenceSelection.Clear();
            }
        }
        else
        {
            MeasurementText = $"Reference point {referenceSelection.Count}/5 selected.";
        }
        RenderModel();
        ReferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SelectReferenceFaceOrEdge(Point pointer)
    {
        if (referenceTriangleIndex is null)
        {
            if (PickFace(pointer) is not { } match) MeasurementText = "No visible face at that position.";
            else { referenceTriangleIndex = match.TriangleIndex; MeasurementText = "Base face selected. Click a visible edge for direction."; }
        }
        else
        {
            var edge = renderedEdges
                .Select(value => (value, distance: DistanceToSegment(pointer, value.Start, value.End)))
                .Where(value => value.distance <= 14)
                .OrderBy(value => value.distance)
                .Select(value => (int?)value.value.DisplayEdgeIndex)
                .FirstOrDefault();
            if (edge is null) MeasurementText = "No visible edge at that position.";
            else
            {
                SetCustomReferenceByFaceAndEdge(referenceTriangleIndex.Value, edge.Value);
                MeasurementText = "Custom face/edge reference active.";
                isBuildingFaceReference = false;
                referenceTriangleIndex = null;
            }
        }
        RenderModel();
        ReferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool PointInTriangle(Point point, StepTriangle3 triangle)
    {
        var a = screenPoints[triangle.FirstIndex]; var b = screenPoints[triangle.SecondIndex]; var c = screenPoints[triangle.ThirdIndex];
        static double Sign(Point p1, Point p2, Point p3) => (p1.X-p3.X)*(p2.Y-p3.Y)-(p2.X-p3.X)*(p1.Y-p3.Y);
        var d1=Sign(point,a,b); var d2=Sign(point,b,c); var d3=Sign(point,c,a);
        return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var dx=b.X-a.X; var dy=b.Y-a.Y; var length=dx*dx+dy*dy;
        if (length <= 0) return (p-a).Length;
        var t=Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/length,0,1);
        return (p-new Point(a.X+t*dx,a.Y+t*dy)).Length;
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
        if (label.Length == 0)
        {
            return;
        }
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

    private sealed class SceneLayer
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public required string Kind { get; init; }
        public required string Path { get; init; }
        public required StepModelData Model { get; init; }
        public Color Color { get; set; }
        public double Opacity { get; set; } = 1;
        public bool IsVisible { get; set; } = true;
        public string Units { get; init; } = "units";
        public string? FallbackReason { get; init; }
        public int PointOffset { get; set; }
        public int TriangleOffset { get; set; }

        public StepViewerLayerInfo ToInfo() => new(
            Id, Label, Kind, Path, IsVisible, Color, Opacity, Model.Triangles.Count, Model.HasTopology, Units);
    }
}

public enum StepDisplayMode
{
    Shaded,
    VisibleEdges,
    Wireframe,
    HiddenLine,
    Transparent
}

public sealed record StepViewerLayerInfo(
    string Id,
    string Label,
    string Kind,
    string Path,
    bool IsVisible,
    Color Color,
    double Opacity,
    int TriangleCount,
    bool HasTopology,
    string Units);

/// <summary>A completed measurement: short on-canvas label, full details, and its world anchors.</summary>
internal sealed record StepMeasurementResult(
    StepMeasurementTool Tool,
    string Label,
    string Details,
    double Value,
    IReadOnlyList<StepPoint3> Anchors,
    StepPickedEntity? FirstEntity = null,
    StepPickedEntity? SecondEntity = null);

internal readonly record struct StepPickedEntity(int LayerIndex, StepEntityRef? Entity, int PointIndex, int TriangleIndex);

internal sealed record StepMeasurementAnnotation(StepMeasurementResult Result);

internal readonly record struct StepBrepVertex(int LayerIndex, int VertexIndex, int PointIndex);

internal readonly record struct StepRenderedEdge(Point Start, Point End, int DisplayEdgeIndex);

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
        var brush = new SolidColorBrush(mode is StepDisplayMode.Wireframe or StepDisplayMode.Transparent
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

public sealed class StepSurfaceDrawingHost : FrameworkElement
{
    private readonly DrawingVisual visual = new();

    public StepSurfaceDrawingHost()
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

    internal void Draw(IReadOnlyList<StepScreenTriangle> triangles, Func<int, SolidColorBrush[]> paletteForLayer)
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
            context.DrawGeometry(paletteForLayer(triangle.LayerIndex)[triangle.Shade], null, geometry);
        }
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
    int[] TriangleIndices,
    int LayerIndex = 0,
    int EdgeIndex = -1);

internal readonly record struct StepScreenEdge(Point Start, Point End);

internal sealed class StepDepthBuffer
{
    private const int MaximumDimension = 2048;
    private const double BarycentricTolerance = 0.000000001;
    private readonly double[] depths;
    private readonly int[] owners;
    private readonly List<StepDepthPlane> planes = [];
    private readonly double scaleX;
    private readonly double scaleY;
    private readonly double depthTolerance;

    private StepDepthBuffer(
        int width,
        int height,
        double scaleX,
        double scaleY,
        double depthTolerance)
    {
        Width = width;
        Height = height;
        this.scaleX = scaleX;
        this.scaleY = scaleY;
        this.depthTolerance = depthTolerance;
        depths = new double[width * height];
        Array.Fill(depths, double.NegativeInfinity);
        owners = new int[width * height];
        Array.Fill(owners, -1);
    }

    private int Width { get; }
    private int Height { get; }

    internal static StepDepthBuffer Build(
        double viewportWidth,
        double viewportHeight,
        IReadOnlyList<Point> screenPoints,
        IReadOnlyList<double> pointDepths,
        IReadOnlyList<StepTriangle3> triangles)
    {
        var safeWidth = Math.Max(1, viewportWidth);
        var safeHeight = Math.Max(1, viewportHeight);
        var width = Math.Clamp((int)Math.Ceiling(safeWidth), 1, MaximumDimension);
        var height = Math.Clamp((int)Math.Ceiling(safeHeight), 1, MaximumDimension);
        var minimumDepth = pointDepths.Count == 0 ? 0 : pointDepths.Min();
        var maximumDepth = pointDepths.Count == 0 ? 0 : pointDepths.Max();
        var depthSpan = Math.Abs(maximumDepth - minimumDepth);
        var buffer = new StepDepthBuffer(
            width,
            height,
            width / safeWidth,
            height / safeHeight,
            Math.Max(0.00000001, depthSpan * 0.0000001));

        for (var triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
        {
            var triangle = triangles[triangleIndex];
            buffer.Rasterize(
                screenPoints[triangle.FirstIndex],
                screenPoints[triangle.SecondIndex],
                screenPoints[triangle.ThirdIndex],
                pointDepths[triangle.FirstIndex],
                pointDepths[triangle.SecondIndex],
                pointDepths[triangle.ThirdIndex]);
        }

        return buffer;
    }

    internal Point ToBuffer(Point point) => new(point.X * scaleX, point.Y * scaleY);

    internal bool IsVisible(Point point, double depth)
    {
        var bufferPoint = ToBuffer(point);
        var centerX = (int)Math.Floor(bufferPoint.X);
        var centerY = (int)Math.Floor(bufferPoint.Y);
        if (centerX < 0 || centerX >= Width || centerY < 0 || centerY >= Height)
        {
            return true;
        }

        var owner = owners[centerY * Width + centerX];
        var frontDepth = owner < 0
            ? double.NegativeInfinity
            : planes[owner].DepthAt(bufferPoint);
        if (owner < 0)
        {
            for (var y = Math.Max(0, centerY - 1); y <= Math.Min(Height - 1, centerY + 1); y++)
            {
                for (var x = Math.Max(0, centerX - 1); x <= Math.Min(Width - 1, centerX + 1); x++)
                {
                    owner = owners[y * Width + x];
                    if (owner >= 0)
                    {
                        frontDepth = Math.Max(frontDepth, planes[owner].DepthAt(bufferPoint));
                    }
                }
            }
        }

        return double.IsNegativeInfinity(frontDepth) || depth >= frontDepth - depthTolerance;
    }

    private void Rasterize(
        Point first,
        Point second,
        Point third,
        double firstDepth,
        double secondDepth,
        double thirdDepth)
    {
        first = ToBuffer(first);
        second = ToBuffer(second);
        third = ToBuffer(third);
        var area = Cross(second - first, third - first);
        if (Math.Abs(area) <= BarycentricTolerance)
        {
            return;
        }

        var planeIndex = planes.Count;
        planes.Add(StepDepthPlane.Create(
            first,
            second,
            third,
            firstDepth,
            secondDepth,
            thirdDepth,
            area));

        var minX = Math.Max(0, (int)Math.Floor(Math.Min(first.X, Math.Min(second.X, third.X))));
        var maxX = Math.Min(Width - 1, (int)Math.Ceiling(Math.Max(first.X, Math.Max(second.X, third.X))));
        var minY = Math.Max(0, (int)Math.Floor(Math.Min(first.Y, Math.Min(second.Y, third.Y))));
        var maxY = Math.Min(Height - 1, (int)Math.Ceiling(Math.Max(first.Y, Math.Max(second.Y, third.Y))));
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var sample = new Point(x + 0.5, y + 0.5);
                var firstWeight = Cross(second - sample, third - sample) / area;
                var secondWeight = Cross(third - sample, first - sample) / area;
                var thirdWeight = 1 - firstWeight - secondWeight;
                if (firstWeight < -BarycentricTolerance
                    || secondWeight < -BarycentricTolerance
                    || thirdWeight < -BarycentricTolerance)
                {
                    continue;
                }

                var depth = firstWeight * firstDepth
                            + secondWeight * secondDepth
                            + thirdWeight * thirdDepth;
                var index = y * Width + x;
                if (depth > depths[index])
                {
                    depths[index] = depth;
                    owners[index] = planeIndex;
                }
            }
        }
    }

    private static double Cross(Vector first, Vector second) => first.X * second.Y - first.Y * second.X;
}

internal readonly record struct StepDepthPlane(double DepthX, double DepthY, double Constant)
{
    internal static StepDepthPlane Create(
        Point first,
        Point second,
        Point third,
        double firstDepth,
        double secondDepth,
        double thirdDepth,
        double area)
    {
        var depthX = ((secondDepth - firstDepth) * (third.Y - first.Y)
                      - (thirdDepth - firstDepth) * (second.Y - first.Y)) / area;
        var depthY = ((second.X - first.X) * (thirdDepth - firstDepth)
                      - (third.X - first.X) * (secondDepth - firstDepth)) / area;
        return new StepDepthPlane(
            depthX,
            depthY,
            firstDepth - depthX * first.X - depthY * first.Y);
    }

    internal double DepthAt(Point point) => DepthX * point.X + DepthY * point.Y + Constant;
}

internal readonly record struct StepScreenTriangle(
    Point First,
    Point Second,
    Point Third,
    double Depth,
    byte Shade,
    int SourceIndex,
    int LayerIndex = 0);

internal readonly record struct StepVertexKey(long X, long Y, long Z);

public sealed record StepMeasurement(double Distance, double DeltaX, double DeltaY, double DeltaZ);

public sealed record StepBoundingBox(double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ, bool UsesCustomReference)
{
    public double X => MaxX - MinX;
    public double Y => MaxY - MinY;
    public double Z => MaxZ - MinZ;
}

internal sealed record StepReferenceFrame(StepPoint3 Origin, StepPoint3 X, StepPoint3 Y, StepPoint3 Z)
{
    internal static StepReferenceFrame Model { get; } = new(default, new(1,0,0), new(0,1,0), new(0,0,1));
}
