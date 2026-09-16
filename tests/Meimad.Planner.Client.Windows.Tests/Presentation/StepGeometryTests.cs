using System.Text;
using Meimad.Planner.Client.Windows.Views;
using OCCSharp;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class StepGeometryTests
{
    [Fact]
    public void Tessellation_keeps_brep_topology_and_exact_geometry_for_a_box()
    {
        using var box = new BRepPrimAPI_MakeBox(new gp_Pnt(100, 200, 300), 10, 20, 30);
        var shape = box.Shape();
        var model = StepSolidMeshLoader.Tessellate(shape, keepGeometry: true);
        using var geometry = Assert.IsType<OcctStepGeometry>(model.Geometry);

        Assert.Equal(6, model.FaceCount);
        Assert.Equal(6, geometry.FaceCount);
        Assert.Equal(12, geometry.EdgeCount);
        Assert.Equal(8, geometry.VertexCount);
        Assert.NotNull(model.Edges);
        Assert.Equal(12, model.Edges!.Count);
        Assert.All(model.Edges, edge => Assert.True(edge.PointIndices.Length >= 2));
        Assert.NotNull(model.VertexPointIndices);
        Assert.Equal(8, model.VertexPointIndices!.Count);
        Assert.NotNull(model.TriangleFaceIndices);
        Assert.Equal(model.Triangles.Count, model.TriangleFaceIndices!.Count);
        Assert.Equal(6, model.TriangleFaceIndices.Distinct().Count());

        // B-rep vertices are real model points; the edge polylines end on model points too.
        var vertexPoints = model.VertexPointIndices.Select(index => model.Points[index]).ToArray();
        Assert.Contains(vertexPoints, point => Math.Abs(point.X - 100) < 0.001 && Math.Abs(point.Y - 200) < 0.001 && Math.Abs(point.Z - 300) < 0.001);
        Assert.Contains(vertexPoints, point => Math.Abs(point.X - 110) < 0.001 && Math.Abs(point.Y - 220) < 0.001 && Math.Abs(point.Z - 330) < 0.001);

        var lengths = Enumerable.Range(0, geometry.EdgeCount).Select(index => geometry.EdgeInfo(index)).ToArray();
        Assert.All(lengths, edge => Assert.Equal(StepCurveKind.Line, edge.Kind));
        Assert.Equal(4, lengths.Count(edge => Math.Abs(edge.Length - 10) < 0.000001));
        Assert.Equal(4, lengths.Count(edge => Math.Abs(edge.Length - 20) < 0.000001));
        Assert.Equal(4, lengths.Count(edge => Math.Abs(edge.Length - 30) < 0.000001));

        var faces = Enumerable.Range(0, geometry.FaceCount).Select(index => geometry.FaceInfo(index)).ToArray();
        Assert.All(faces, face => Assert.Equal(StepSurfaceKind.Plane, face.Kind));
        Assert.Equal(2, faces.Count(face => Math.Abs(face.Area - 200) < 0.000001));
        Assert.Equal(2, faces.Count(face => Math.Abs(face.Area - 300) < 0.000001));
        Assert.Equal(2, faces.Count(face => Math.Abs(face.Area - 600) < 0.000001));
        Assert.All(faces, face => Assert.NotNull(face.Axis));

        var mass = geometry.MassProperties();
        Assert.Equal(6000, mass.Volume, 4);
        Assert.Equal(2200, mass.SurfaceArea, 4);
        Assert.Equal(105, mass.Centroid.X, 4);
        Assert.Equal(210, mass.Centroid.Y, 4);
        Assert.Equal(315, mass.Centroid.Z, 4);

        // Minimum distance between two opposite faces is the box depth in that direction.
        var xFaces = faces.Select((face, index) => (face, index))
            .Where(pair => Math.Abs(Math.Abs(pair.face.Axis!.Value.X) - 1) < 0.000001)
            .Select(pair => pair.index).ToArray();
        Assert.Equal(2, xFaces.Length);
        var distance = geometry.Distance(new StepEntityRef(StepEntityKind.Face, xFaces[0]), new StepEntityRef(StepEntityKind.Face, xFaces[1]));
        Assert.NotNull(distance);
        Assert.Equal(10, distance!.Distance, 4);
    }

    [Fact]
    public void Cylinder_faces_and_circular_edges_report_radius_and_axis()
    {
        using var maker = new BRepPrimAPI_MakeCylinder(7.5, 40);
        var shape = maker.Shape();
        var model = StepSolidMeshLoader.Tessellate(shape, keepGeometry: true);
        using var geometry = Assert.IsType<OcctStepGeometry>(model.Geometry);

        var cylinder = Enumerable.Range(0, geometry.FaceCount)
            .Select(index => geometry.FaceInfo(index))
            .Single(face => face.Kind == StepSurfaceKind.Cylinder);
        Assert.Equal(7.5, cylinder.Radius!.Value, 6);
        Assert.Equal(1, Math.Abs(cylinder.Axis!.Value.Z), 6);

        var circles = Enumerable.Range(0, geometry.EdgeCount)
            .Select(index => geometry.EdgeInfo(index))
            .Where(edge => edge.Kind == StepCurveKind.Circle)
            .ToArray();
        Assert.Equal(2, circles.Length);
        Assert.All(circles, circle =>
        {
            Assert.Equal(7.5, circle.Radius!.Value, 6);
            Assert.Equal(2 * Math.PI * 7.5, circle.Length, 4);
        });

        Assert.Equal(Math.PI * 7.5 * 7.5 * 40, geometry.MassProperties().Volume, 2);
    }

    [Fact]
    public void Stl_loader_reads_ascii_and_binary_meshes_with_shared_vertices()
    {
        const string ascii = """
            solid cube
              facet normal 0 0 -1
                outer loop
                  vertex 0 0 0
                  vertex 10 10 0
                  vertex 10 0 0
                endloop
              endfacet
              facet normal 0 0 -1
                outer loop
                  vertex 0 0 0
                  vertex 0 10 0
                  vertex 10 10 0
                endloop
              endfacet
            endsolid cube
            """;
        var asciiModel = StlMeshLoader.Parse(Encoding.ASCII.GetBytes(ascii));
        Assert.Equal(2, asciiModel.Triangles.Count);
        Assert.Equal(4, asciiModel.Points.Count);
        Assert.False(asciiModel.HasTopology);
        Assert.IsType<MeshStepGeometry>(asciiModel.Geometry);
        Assert.Equal(100, asciiModel.Geometry!.MassProperties().SurfaceArea, 6);

        var binary = new byte[84 + 50 * 12];
        var offset = 84;
        void Triangle((float, float, float) a, (float, float, float) b, (float, float, float) c)
        {
            offset += 12;
            foreach (var (x, y, z) in new[] { a, b, c })
            {
                BitConverter.GetBytes(x).CopyTo(binary, offset);
                BitConverter.GetBytes(y).CopyTo(binary, offset + 4);
                BitConverter.GetBytes(z).CopyTo(binary, offset + 8);
                offset += 12;
            }
            offset += 2;
        }
        BitConverter.GetBytes(12u).CopyTo(binary, 80);
        // A 10×10×10 cube as 12 outward-facing triangles.
        (float, float, float)[] v =
        [
            (0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0),
            (0, 0, 10), (10, 0, 10), (10, 10, 10), (0, 10, 10)
        ];
        Triangle(v[0], v[2], v[1]); Triangle(v[0], v[3], v[2]);
        Triangle(v[4], v[5], v[6]); Triangle(v[4], v[6], v[7]);
        Triangle(v[0], v[1], v[5]); Triangle(v[0], v[5], v[4]);
        Triangle(v[1], v[2], v[6]); Triangle(v[1], v[6], v[5]);
        Triangle(v[2], v[3], v[7]); Triangle(v[2], v[7], v[6]);
        Triangle(v[3], v[0], v[4]); Triangle(v[3], v[4], v[7]);

        var binaryModel = StlMeshLoader.Parse(binary);
        Assert.Equal(12, binaryModel.Triangles.Count);
        Assert.Equal(8, binaryModel.Points.Count);
        var mass = binaryModel.Geometry!.MassProperties();
        Assert.Equal(1000, mass.Volume, 6);
        Assert.Equal(600, mass.SurfaceArea, 6);
        Assert.Equal(5, mass.Centroid.X, 6);
        Assert.Equal(5, mass.Centroid.Y, 6);
        Assert.Equal(5, mass.Centroid.Z, 6);
    }
}
