using System.IO;
using OCCSharp;

namespace Meimad.Planner.Client.Windows.Views;

internal static class StepSolidMeshLoader
{
    private const int MaximumTriangles = 500_000;
    private const int MaximumVertices = 1_500_000;

    public static StepModelData Load(string path)
    {
        using var progress = new Message_ProgressRange();
        using var reader = new STEPControl_Reader();
        if (reader.ReadFile(path) != IFSelect_ReturnStatus.IFSelect_RetDone)
        {
            throw new InvalidDataException("OpenCascade could not read this STEP model.");
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

        using var mesher = new BRepMesh_IncrementalMesh(shape, 0.08, false, 0.18, true);
        mesher.Perform(progress);
        if (mesher.GetStatusFlags() != 0 && !mesher.IsModified())
        {
            throw new InvalidDataException("OpenCascade could not tessellate this STEP model.");
        }

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
