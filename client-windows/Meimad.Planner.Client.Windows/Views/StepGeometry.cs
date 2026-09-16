using OCCSharp;

namespace Meimad.Planner.Client.Windows.Views;

internal enum StepEntityKind
{
    Vertex,
    Edge,
    Face
}

/// <summary>Reference to a B-rep entity of one loaded model (0-based index within its kind).</summary>
internal readonly record struct StepEntityRef(StepEntityKind Kind, int Index);

internal enum StepCurveKind
{
    Line,
    Circle,
    Ellipse,
    Other
}

internal enum StepSurfaceKind
{
    Plane,
    Cylinder,
    Cone,
    Sphere,
    Torus,
    Other
}

internal sealed record StepCurveInfo(
    StepCurveKind Kind,
    double Length,
    StepPoint3 Start,
    StepPoint3 End,
    StepPoint3? Center,
    double? Radius,
    StepPoint3? Axis,
    StepPoint3? Direction);

internal sealed record StepSurfaceInfo(
    StepSurfaceKind Kind,
    double Area,
    StepPoint3 Centroid,
    StepPoint3? Origin,
    StepPoint3? Axis,
    double? Radius,
    double? MinorRadius);

internal sealed record StepMassProperties(double Volume, double SurfaceArea, StepPoint3 Centroid);

internal sealed record StepDistanceInfo(double Distance, StepPoint3 PointA, StepPoint3 PointB);

/// <summary>
/// Exact geometry behind a tessellated model. STEP models answer from the OpenCascade B-rep;
/// STL meshes answer from the triangles they were loaded from.
/// </summary>
internal interface IStepGeometry : IDisposable
{
    int VertexCount { get; }
    int EdgeCount { get; }
    int FaceCount { get; }
    StepPoint3 VertexPoint(int vertexIndex);
    StepCurveInfo EdgeInfo(int edgeIndex);
    StepSurfaceInfo FaceInfo(int faceIndex);
    StepMassProperties MassProperties();
    StepDistanceInfo? Distance(StepEntityRef first, StepEntityRef second);
}

internal sealed class OcctStepGeometry : IStepGeometry
{
    private readonly TopoDS_Shape shape;
    private readonly TopTools_IndexedMapOfShape vertices;
    private readonly TopTools_IndexedMapOfShape edges;
    private readonly TopTools_IndexedMapOfShape faces;
    private StepMassProperties? massProperties;
    private bool disposed;

    internal OcctStepGeometry(
        TopoDS_Shape shape,
        TopTools_IndexedMapOfShape vertices,
        TopTools_IndexedMapOfShape edges,
        TopTools_IndexedMapOfShape faces)
    {
        this.shape = shape;
        this.vertices = vertices;
        this.edges = edges;
        this.faces = faces;
    }

    public int VertexCount => vertices.Size();
    public int EdgeCount => edges.Size();
    public int FaceCount => faces.Size();

    public StepPoint3 VertexPoint(int vertexIndex)
    {
        ThrowIfDisposed();
        using var vertex = TopoDS.Vertex(vertices.FindKey(Checked(vertexIndex, VertexCount)));
        using var point = BRep_Tool.Pnt(vertex);
        return ToPoint(point);
    }

    public StepCurveInfo EdgeInfo(int edgeIndex)
    {
        ThrowIfDisposed();
        using var edge = TopoDS.Edge(edges.FindKey(Checked(edgeIndex, EdgeCount)));
        using var curve = new BRepAdaptor_Curve(edge);
        var first = curve.FirstParameter();
        var last = curve.LastParameter();
        var length = GCPnts_AbscissaPoint.Length(curve, first, last);
        using var startPoint = curve.Value(first);
        using var endPoint = curve.Value(last);
        var start = ToPoint(startPoint);
        var end = ToPoint(endPoint);
        switch (curve.GetType())
        {
            case GeomAbs_CurveType.GeomAbs_Line:
            {
                using var line = curve.Line();
                using var direction = line.Direction();
                return new StepCurveInfo(StepCurveKind.Line, length, start, end, null, null, null, ToPoint(direction));
            }
            case GeomAbs_CurveType.GeomAbs_Circle:
            {
                using var circle = curve.Circle();
                using var center = circle.Location();
                using var axis = circle.Axis();
                using var axisDirection = axis.Direction();
                return new StepCurveInfo(StepCurveKind.Circle, length, start, end, ToPoint(center), circle.Radius(), ToPoint(axisDirection), null);
            }
            case GeomAbs_CurveType.GeomAbs_Ellipse:
            {
                using var ellipse = curve.Ellipse();
                using var center = ellipse.Location();
                return new StepCurveInfo(StepCurveKind.Ellipse, length, start, end, ToPoint(center), ellipse.MajorRadius(), null, null);
            }
            default:
                return new StepCurveInfo(StepCurveKind.Other, length, start, end, null, null, null, null);
        }
    }

    public StepSurfaceInfo FaceInfo(int faceIndex)
    {
        ThrowIfDisposed();
        using var face = TopoDS.Face(faces.FindKey(Checked(faceIndex, FaceCount)));
        using var properties = new GProp_GProps();
        BRepGProp.SurfaceProperties(face, properties, false, false);
        var area = properties.Mass();
        using var centreOfMass = properties.CentreOfMass();
        var centroid = ToPoint(centreOfMass);
        using var surface = new BRepAdaptor_Surface(face, true);
        switch (surface.GetType())
        {
            case GeomAbs_SurfaceType.GeomAbs_Plane:
            {
                using var plane = surface.Plane();
                using var origin = plane.Location();
                using var axis = plane.Axis();
                using var normal = axis.Direction();
                var normalPoint = ToPoint(normal);
                // Report the outward normal so angles between faces match the shop-floor reading.
                if (face.Orientation() == TopAbs_Orientation.TopAbs_REVERSED)
                {
                    normalPoint = new StepPoint3(-normalPoint.X, -normalPoint.Y, -normalPoint.Z);
                }
                return new StepSurfaceInfo(StepSurfaceKind.Plane, area, centroid, ToPoint(origin), normalPoint, null, null);
            }
            case GeomAbs_SurfaceType.GeomAbs_Cylinder:
            {
                using var cylinder = surface.Cylinder();
                using var origin = cylinder.Location();
                using var axis = cylinder.Axis();
                using var direction = axis.Direction();
                return new StepSurfaceInfo(StepSurfaceKind.Cylinder, area, centroid, ToPoint(origin), ToPoint(direction), cylinder.Radius(), null);
            }
            case GeomAbs_SurfaceType.GeomAbs_Cone:
            {
                using var cone = surface.Cone();
                using var origin = cone.Location();
                using var axis = cone.Axis();
                using var direction = axis.Direction();
                return new StepSurfaceInfo(StepSurfaceKind.Cone, area, centroid, ToPoint(origin), ToPoint(direction), cone.RefRadius(), null);
            }
            case GeomAbs_SurfaceType.GeomAbs_Sphere:
            {
                using var sphere = surface.Sphere();
                using var origin = sphere.Location();
                return new StepSurfaceInfo(StepSurfaceKind.Sphere, area, centroid, ToPoint(origin), null, sphere.Radius(), null);
            }
            case GeomAbs_SurfaceType.GeomAbs_Torus:
            {
                using var torus = surface.Torus();
                using var origin = torus.Location();
                using var axis = torus.Axis();
                using var direction = axis.Direction();
                return new StepSurfaceInfo(StepSurfaceKind.Torus, area, centroid, ToPoint(origin), ToPoint(direction), torus.MajorRadius(), torus.MinorRadius());
            }
            default:
                return new StepSurfaceInfo(StepSurfaceKind.Other, area, centroid, null, null, null, null);
        }
    }

    public StepMassProperties MassProperties()
    {
        ThrowIfDisposed();
        if (massProperties is not null)
        {
            return massProperties;
        }

        using var volumeProperties = new GProp_GProps();
        BRepGProp.VolumeProperties(shape, volumeProperties, false, false, false);
        using var surfaceProperties = new GProp_GProps();
        BRepGProp.SurfaceProperties(shape, surfaceProperties, false, false);
        using var centre = volumeProperties.CentreOfMass();
        massProperties = new StepMassProperties(
            Math.Abs(volumeProperties.Mass()),
            surfaceProperties.Mass(),
            ToPoint(centre));
        return massProperties;
    }

    public StepDistanceInfo? Distance(StepEntityRef first, StepEntityRef second)
    {
        ThrowIfDisposed();
        using var firstShape = SubShape(first);
        using var secondShape = SubShape(second);
        using var progress = new Message_ProgressRange();
        using var extrema = new BRepExtrema_DistShapeShape();
        extrema.LoadS1(firstShape);
        extrema.LoadS2(secondShape);
        if (!extrema.Perform(progress) || !extrema.IsDone() || extrema.NbSolution() < 1)
        {
            return null;
        }

        using var pointA = extrema.PointOnShape1(1);
        using var pointB = extrema.PointOnShape2(1);
        return new StepDistanceInfo(extrema.Value(), ToPoint(pointA), ToPoint(pointB));
    }

    /// <summary>Minimum distance between entities of two different models (part vs. fixture clearance).</summary>
    internal static StepDistanceInfo? DistanceBetween(
        OcctStepGeometry first,
        StepEntityRef firstEntity,
        OcctStepGeometry second,
        StepEntityRef secondEntity)
    {
        first.ThrowIfDisposed();
        second.ThrowIfDisposed();
        using var firstShape = first.SubShape(firstEntity);
        using var secondShape = second.SubShape(secondEntity);
        using var progress = new Message_ProgressRange();
        using var extrema = new BRepExtrema_DistShapeShape();
        extrema.LoadS1(firstShape);
        extrema.LoadS2(secondShape);
        if (!extrema.Perform(progress) || !extrema.IsDone() || extrema.NbSolution() < 1)
        {
            return null;
        }

        using var pointA = extrema.PointOnShape1(1);
        using var pointB = extrema.PointOnShape2(1);
        return new StepDistanceInfo(extrema.Value(), ToPoint(pointA), ToPoint(pointB));
    }

    private TopoDS_Shape SubShape(StepEntityRef entity) => entity.Kind switch
    {
        StepEntityKind.Vertex => vertices.FindKey(Checked(entity.Index, VertexCount)),
        StepEntityKind.Edge => edges.FindKey(Checked(entity.Index, EdgeCount)),
        _ => faces.FindKey(Checked(entity.Index, FaceCount))
    };

    private static int Checked(int index, int count) => index >= 0 && index < count
        ? index + 1
        : throw new ArgumentOutOfRangeException(nameof(index), "The entity index is outside the loaded model.");

    private static StepPoint3 ToPoint(gp_Pnt point) => new(point.X(), point.Y(), point.Z());

    private static StepPoint3 ToPoint(gp_Dir direction) => new(direction.X(), direction.Y(), direction.Z());

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        // OCCSharp exposes no accessible destructor for TopTools_IndexedMapOfShape, so the three
        // index maps are left to the garbage collector; only the shape itself is released here.
        shape.Dispose();
    }
}

/// <summary>
/// Mesh-only geometry for STL files and STEP fallbacks: no B-rep entities, but volume, area and
/// centroid are still computed from the closed triangle mesh.
/// </summary>
internal sealed class MeshStepGeometry(IReadOnlyList<StepPoint3> points, IReadOnlyList<StepTriangle3> triangles) : IStepGeometry
{
    private StepMassProperties? massProperties;

    public int VertexCount => 0;
    public int EdgeCount => 0;
    public int FaceCount => 0;

    public StepPoint3 VertexPoint(int vertexIndex) =>
        throw new ArgumentOutOfRangeException(nameof(vertexIndex), "Mesh models have no B-rep vertices.");

    public StepCurveInfo EdgeInfo(int edgeIndex) =>
        throw new ArgumentOutOfRangeException(nameof(edgeIndex), "Mesh models have no B-rep edges.");

    public StepSurfaceInfo FaceInfo(int faceIndex) =>
        throw new ArgumentOutOfRangeException(nameof(faceIndex), "Mesh models have no B-rep faces.");

    public StepMassProperties MassProperties()
    {
        if (massProperties is not null)
        {
            return massProperties;
        }

        var volume6 = 0d;
        var area = 0d;
        var cx = 0d;
        var cy = 0d;
        var cz = 0d;
        foreach (var triangle in triangles)
        {
            var a = points[triangle.FirstIndex];
            var b = points[triangle.SecondIndex];
            var c = points[triangle.ThirdIndex];
            var v = a.X * (b.Y * c.Z - b.Z * c.Y) - a.Y * (b.X * c.Z - b.Z * c.X) + a.Z * (b.X * c.Y - b.Y * c.X);
            volume6 += v;
            cx += (a.X + b.X + c.X) * v;
            cy += (a.Y + b.Y + c.Y) * v;
            cz += (a.Z + b.Z + c.Z) * v;
            var ux = b.X - a.X; var uy = b.Y - a.Y; var uz = b.Z - a.Z;
            var wx = c.X - a.X; var wy = c.Y - a.Y; var wz = c.Z - a.Z;
            var nx = uy * wz - uz * wy; var ny = uz * wx - ux * wz; var nz = ux * wy - uy * wx;
            area += Math.Sqrt(nx * nx + ny * ny + nz * nz) / 2;
        }

        var centroid = Math.Abs(volume6) > 0.000000001
            ? new StepPoint3(cx / (4 * volume6), cy / (4 * volume6), cz / (4 * volume6))
            : default;
        massProperties = new StepMassProperties(Math.Abs(volume6) / 6, area, centroid);
        return massProperties;
    }

    public StepDistanceInfo? Distance(StepEntityRef first, StepEntityRef second) => null;

    public void Dispose()
    {
    }
}
