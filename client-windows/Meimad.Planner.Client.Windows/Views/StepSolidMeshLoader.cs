using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using OCCSharp;

namespace Meimad.Planner.Client.Windows.Views;

internal static class StepSolidMeshLoader
{
    private const int MaximumTriangles = 500_000;
    private const int MaximumVertices = 1_500_000;
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

        using var shape = reader.OneShape();
        if (shape.IsNull())
        {
            throw new InvalidDataException("The STEP model contains no transferable solid geometry.");
        }

        return Tessellate(shape, progress);
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

    internal static StepModelData Tessellate(TopoDS_Shape shape)
    {
        using var progress = new Message_ProgressRange();
        return Tessellate(shape, progress);
    }

    private static StepModelData Tessellate(TopoDS_Shape shape, Message_ProgressRange progress)
    {
        using var mesher = new BRepMesh_IncrementalMesh(shape, 0.08, false, 0.18, true);
        mesher.Perform(progress);

        var points = new List<StepPoint3>();
        var triangles = new List<StepTriangle3>();
        using var explorer = new TopExp_Explorer(
            shape,
            TopAbs_ShapeEnum.TopAbs_FACE,
            TopAbs_ShapeEnum.TopAbs_SHAPE);
        while (explorer.More())
        {
            using var face = TopoDS.Face(explorer.Current());
            using var location = new TopLoc_Location();
            using var triangulation = BRep_Tool.Triangulation(face, location, 0);
            if (triangulation is null || !triangulation.HasGeometry())
            {
                explorer.Next();
                continue;
            }

            var start = points.Count;
            var transformation = location.Transformation();
            for (var node = 1; node <= triangulation.NbNodes(); node++)
            {
                using var transformed = triangulation.Node(node).Transformed(transformation);
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
                if (triangles.Count > MaximumTriangles)
                {
                    throw new InvalidDataException("The STEP solid exceeds the 500,000-triangle viewer limit.");
                }
            }

            explorer.Next();
        }

        if (points.Count == 0 || triangles.Count == 0)
        {
            throw new InvalidDataException("The STEP model contains no tessellated faces.");
        }

        return new StepModelData(
            points,
            [],
            triangles,
            Enumerable.Range(0, points.Count).ToArray());
    }
}

internal sealed record StepModelData(
    IReadOnlyList<StepPoint3> Points,
    IReadOnlyList<StepSegment3> Segments,
    IReadOnlyList<StepTriangle3> Triangles,
    IReadOnlyList<int> SelectablePointIndices);

internal readonly record struct StepPoint3(double X, double Y, double Z);
internal readonly record struct StepSegment3(int StartIndex, int EndIndex);
internal readonly record struct StepTriangle3(int FirstIndex, int SecondIndex, int ThirdIndex);
