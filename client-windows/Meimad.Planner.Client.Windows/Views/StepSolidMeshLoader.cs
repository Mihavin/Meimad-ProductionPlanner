using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using OCCSharp;

namespace Meimad.Planner.Client.Windows.Views;

internal static class StepSolidMeshLoader
{
    private const int MaximumTriangles = 500_000;
    private const int MaximumVertices = 1_500_000;
    private const int FreeEdgeSamples = 32;
    private static readonly object NativeRuntimeLock = new();
    private static readonly List<IntPtr> NativeRuntimeHandles = [];
    private static bool nativeRuntimeConfigured;
    private static readonly string[] RequiredWrapperLibraries =
    [
        "OCCSTKernel.dll",
        "OCCSTKMath.dll",
        "OCCSTKG2d.dll",
        "OCCSTKG3d.dll",
        "OCCSTKGeomBase.dll",
        "OCCSTKGeomAlgo.dll",
        "OCCSTKBRep.dll",
        "OCCSTKTopAlgo.dll",
        "OCCSTKShHealing.dll",
        "OCCSTKMesh.dll",
        "OCCSTKPrim.dll",
        "OCCSTKCDF.dll",
        "OCCSTKLCAF.dll",
        "OCCSTKCAF.dll",
        "OCCSTKXSBase.dll",
        "OCCSTKDE.dll",
        "OCCSTKDESTEP.dll"
    ];

    public static StepModelData Load(string path)
    {
        EnsureNativeRuntime();
        using var stagedFile = StageForOpenCascade(path);
        using var progress = new Message_ProgressRange();
        using var reader = new STEPControl_Reader();
        var status = reader.ReadFile(stagedFile.Path);
        if (status != IFSelect_ReturnStatus.IFSelect_RetDone)
        {
            throw new InvalidDataException($"OpenCascade could not read this STEP model ({status}).");
        }

        var roots = reader.NbRootsForTransfer();
        for (var root = 1; root <= roots; root++)
        {
            reader.TransferRoot(root, progress);
        }

        var shape = reader.OneShape();
        if (shape.IsNull())
        {
            shape.Dispose();
            throw new InvalidDataException("The STEP model contains no transferable solid geometry.");
        }

        try
        {
            return Tessellate(shape, progress, keepGeometry: true);
        }
        catch
        {
            shape.Dispose();
            throw;
        }
    }

    private static StagedStepFile StageForOpenCascade(string sourcePath)
    {
        // OCCSharp's native STEP reader receives a narrow Windows path. Always stage through an
        // ASCII-only local name so Unicode factory folders, UNC shares, and long Case paths do not
        // turn a valid solid into the legacy wire fallback.
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlannerStep");
        Directory.CreateDirectory(directory);
        var stagedPath = Path.Combine(directory, $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath).ToLowerInvariant()}");
        File.Copy(sourcePath, stagedPath, overwrite: false);
        return new StagedStepFile(stagedPath);
    }

    private static void EnsureNativeRuntime()
    {
        if (!OperatingSystem.IsWindows() || nativeRuntimeConfigured)
        {
            return;
        }

        lock (NativeRuntimeLock)
        {
            if (nativeRuntimeConfigured)
            {
                return;
            }
            var nativeDirectory = File.Exists(Path.Combine(AppContext.BaseDirectory, RequiredWrapperLibraries[0]))
                ? AppContext.BaseDirectory
                : Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
            if (!SetDllDirectory(nativeDirectory))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The local OpenCascade runtime directory could not be activated.");
            }
            foreach (var library in RequiredWrapperLibraries)
            {
                var packagedPath = Path.Combine(nativeDirectory, library);
                if (!File.Exists(packagedPath))
                {
                    throw new DllNotFoundException($"The packaged OpenCascade library '{library}' is missing.");
                }
                NativeRuntimeHandles.Add(NativeLibrary.Load(packagedPath));
            }
            nativeRuntimeConfigured = true;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string pathName);

    private sealed class StagedStepFile(string path) : IDisposable
    {
        internal string Path { get; } = path;
        public void Dispose()
        {
            try { File.Delete(Path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Tessellates a shape the caller owns; the result carries no exact geometry.</summary>
    internal static StepModelData Tessellate(TopoDS_Shape shape)
    {
        using var progress = new Message_ProgressRange();
        return Tessellate(shape, progress, keepGeometry: false);
    }

    /// <summary>
    /// Tessellates a shape and, when requested, keeps it alive behind an <see cref="OcctStepGeometry"/>
    /// so measurements can use the exact B-rep. Ownership of <paramref name="shape"/> passes to the
    /// geometry in that case.
    /// </summary>
    internal static StepModelData Tessellate(TopoDS_Shape shape, bool keepGeometry)
    {
        using var progress = new Message_ProgressRange();
        return Tessellate(shape, progress, keepGeometry);
    }

    private static StepModelData Tessellate(TopoDS_Shape shape, Message_ProgressRange progress, bool keepGeometry)
    {
        using var mesher = new BRepMesh_IncrementalMesh(shape, 0.08, false, 0.18, true);
        mesher.Perform(progress);

        var vertexMap = new TopTools_IndexedMapOfShape();
        var edgeMap = new TopTools_IndexedMapOfShape();
        var faceMap = new TopTools_IndexedMapOfShape();
        // OCCSharp's wrapper for these maps has no accessible C++ destructor: both Dispose and the
        // SWIG finalizer throw MethodAccessException, and a throwing finalizer kills the process.
        // Suppress finalization and accept the small native leak per loaded model.
        GC.SuppressFinalize(vertexMap);
        GC.SuppressFinalize(edgeMap);
        GC.SuppressFinalize(faceMap);
        TopExp.MapShapes(shape, TopAbs_ShapeEnum.TopAbs_VERTEX, vertexMap);
        TopExp.MapShapes(shape, TopAbs_ShapeEnum.TopAbs_EDGE, edgeMap);
        TopExp.MapShapes(shape, TopAbs_ShapeEnum.TopAbs_FACE, faceMap);

        var points = new List<StepPoint3>();
        var triangles = new List<StepTriangle3>();
        var triangleFaces = new List<int>();
        var edgePolylines = new StepEdgePolyline?[edgeMap.Size()];
        try
        {
            for (var faceNumber = 1; faceNumber <= faceMap.Size(); faceNumber++)
            {
                using var face = TopoDS.Face(faceMap.FindKey(faceNumber));
                using var location = new TopLoc_Location();
                using var triangulation = BRep_Tool.Triangulation(face, location, 0);
                if (triangulation is null || triangulation.NbNodes() == 0)
                {
                    continue;
                }

                var start = points.Count;
                using var transformation = location.Transformation();
                for (var node = 1; node <= triangulation.NbNodes(); node++)
                {
                    using var original = triangulation.Node(node);
                    using var transformed = original.Transformed(transformation);
                    points.Add(new StepPoint3(transformed.X(), transformed.Y(), transformed.Z()));
                    if (points.Count > MaximumVertices)
                    {
                        throw new InvalidDataException("The STEP solid exceeds the 1,500,000-vertex viewer limit.");
                    }
                }

                var reversed = face.Orientation() == TopAbs_Orientation.TopAbs_REVERSED;
                for (var triangleIndex = 1; triangleIndex <= triangulation.NbTriangles(); triangleIndex++)
                {
                    using var triangle = triangulation.Triangle(triangleIndex);
                    var first = 0;
                    var second = 0;
                    var third = 0;
                    triangle.Get(ref first, ref second, ref third);
                    triangles.Add(reversed
                        ? new StepTriangle3(start + first - 1, start + third - 1, start + second - 1)
                        : new StepTriangle3(start + first - 1, start + second - 1, start + third - 1));
                    triangleFaces.Add(faceNumber - 1);
                    if (triangles.Count > MaximumTriangles)
                    {
                        throw new InvalidDataException("The STEP solid exceeds the 500,000-triangle viewer limit.");
                    }
                }

                // True B-rep edges: the mesher records which triangulation nodes each edge runs
                // along, which draws far cleaner outlines than crease detection between triangles.
                using var edgeExplorer = new TopExp_Explorer(face, TopAbs_ShapeEnum.TopAbs_EDGE, TopAbs_ShapeEnum.TopAbs_SHAPE);
                while (edgeExplorer.More())
                {
                    using var current = edgeExplorer.Current();
                    var edgeNumber = edgeMap.FindIndex(current);
                    if (edgeNumber > 0 && edgePolylines[edgeNumber - 1] is null)
                    {
                        using var edge = TopoDS.Edge(current);
                        using var polygon = BRep_Tool.PolygonOnTriangulation(edge, triangulation, location);
                        if (polygon is not null && polygon.NbNodes() >= 2)
                        {
                            var indices = new int[polygon.NbNodes()];
                            for (var node = 1; node <= indices.Length; node++)
                            {
                                indices[node - 1] = start + polygon.Node(node) - 1;
                            }
                            edgePolylines[edgeNumber - 1] = new StepEdgePolyline(edgeNumber - 1, indices, indices[0] == indices[^1]);
                        }
                    }
                    edgeExplorer.Next();
                }
            }

            if (points.Count == 0 || triangles.Count == 0)
            {
                throw new InvalidDataException("The STEP model contains no tessellated faces.");
            }

            // Edges not shared with any triangulated face (free wires) are sampled from their curve.
            for (var edgeNumber = 1; edgeNumber <= edgeMap.Size(); edgeNumber++)
            {
                if (edgePolylines[edgeNumber - 1] is not null)
                {
                    continue;
                }
                using var edge = TopoDS.Edge(edgeMap.FindKey(edgeNumber));
                if (BRep_Tool.Degenerated(edge))
                {
                    continue;
                }
                using var curve = new BRepAdaptor_Curve(edge);
                var first = curve.FirstParameter();
                var last = curve.LastParameter();
                if (double.IsNaN(first) || double.IsNaN(last) || double.IsInfinity(first) || double.IsInfinity(last))
                {
                    continue;
                }
                var indices = new int[FreeEdgeSamples + 1];
                for (var sample = 0; sample <= FreeEdgeSamples; sample++)
                {
                    using var point = curve.Value(first + (last - first) * sample / FreeEdgeSamples);
                    indices[sample] = points.Count;
                    points.Add(new StepPoint3(point.X(), point.Y(), point.Z()));
                }
                edgePolylines[edgeNumber - 1] = new StepEdgePolyline(edgeNumber - 1, indices, false);
            }

            // B-rep vertices become dedicated selectable points so picking snaps to real corners.
            var vertexPointIndices = new int[vertexMap.Size()];
            for (var vertexNumber = 1; vertexNumber <= vertexMap.Size(); vertexNumber++)
            {
                using var vertex = TopoDS.Vertex(vertexMap.FindKey(vertexNumber));
                using var point = BRep_Tool.Pnt(vertex);
                vertexPointIndices[vertexNumber - 1] = points.Count;
                points.Add(new StepPoint3(point.X(), point.Y(), point.Z()));
            }

            var edges = edgePolylines.Where(edge => edge is not null).Select(edge => edge!).ToArray();
            // The index maps have no accessible destructor in OCCSharp; they are small and are
            // left to the garbage collector whether or not the geometry keeps them.
            IStepGeometry? geometry = keepGeometry
                ? new OcctStepGeometry(shape, vertexMap, edgeMap, faceMap)
                : null;

            return new StepModelData(
                points,
                [],
                triangles,
                Enumerable.Range(0, points.Count).ToArray(),
                triangleFaces,
                edges,
                vertexPointIndices,
                geometry,
                faceMap.Size());
        }
        catch (InvalidDataException)
        {
            throw;
        }
    }
}

internal sealed record StepModelData(
    IReadOnlyList<StepPoint3> Points,
    IReadOnlyList<StepSegment3> Segments,
    IReadOnlyList<StepTriangle3> Triangles,
    IReadOnlyList<int> SelectablePointIndices,
    IReadOnlyList<int>? TriangleFaceIndices = null,
    IReadOnlyList<StepEdgePolyline>? Edges = null,
    IReadOnlyList<int>? VertexPointIndices = null,
    IStepGeometry? Geometry = null,
    int FaceCount = 0)
{
    /// <summary>True when the model carries B-rep topology (faces, edges, vertices) for picking.</summary>
    public bool HasTopology => Edges is { Count: > 0 } || VertexPointIndices is { Count: > 0 };
}

/// <summary>One B-rep edge drawn as a polyline through model points.</summary>
internal sealed record StepEdgePolyline(int EdgeIndex, int[] PointIndices, bool IsClosed);

internal readonly record struct StepPoint3(double X, double Y, double Z);
internal readonly record struct StepSegment3(int StartIndex, int EndIndex);
internal readonly record struct StepTriangle3(int FirstIndex, int SecondIndex, int ThirdIndex);
