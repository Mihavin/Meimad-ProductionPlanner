using System.Threading;
using System.Windows;
using Meimad.Planner.Client.Windows.Views;
using OCCSharp;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class StepViewerControlTests
{
    [Fact]
    public void Step_loader_reads_a_standard_step_file_through_the_packaged_native_runtime()
    {
        var stepPath = Path.Combine(Path.GetTempPath(), $"meimad-roundtrip-{Guid.NewGuid():N}.step");
        try
        {
            using var box = new BRepPrimAPI_MakeBox(10, 20, 30);
            using var shape = box.Shape();
            using var writer = new STEPControl_Writer();
            using var progress = new Message_ProgressRange();
            Assert.Equal(
                IFSelect_ReturnStatus.IFSelect_RetDone,
                writer.Transfer(shape, STEPControl_StepModelType.STEPControl_AsIs, true, progress));
            Assert.Equal(IFSelect_ReturnStatus.IFSelect_RetDone, writer.Write(stepPath));

            var model = StepSolidMeshLoader.Load(stepPath);
            Assert.True(model.Triangles.Count >= 12);
        }
        finally
        {
            File.Delete(stepPath);
        }
    }

    [Fact]
    public void Step_loader_reads_a_solid_from_a_unicode_factory_path()
    {
        var asciiPath = Path.Combine(Path.GetTempPath(), $"meimad-source-{Guid.NewGuid():N}.step");
        var unicodeDirectory = Path.Combine(Path.GetTempPath(), $"מפעל-{Guid.NewGuid():N}");
        Directory.CreateDirectory(unicodeDirectory);
        var unicodePath = Path.Combine(unicodeDirectory, "חלק ייצור.step");
        try
        {
            using var box = new BRepPrimAPI_MakeBox(10, 20, 30);
            using var shape = box.Shape();
            using var writer = new STEPControl_Writer();
            using var progress = new Message_ProgressRange();
            Assert.Equal(IFSelect_ReturnStatus.IFSelect_RetDone,
                writer.Transfer(shape, STEPControl_StepModelType.STEPControl_AsIs, true, progress));
            Assert.Equal(IFSelect_ReturnStatus.IFSelect_RetDone, writer.Write(asciiPath));
            File.Copy(asciiPath, unicodePath);

            var model = StepSolidMeshLoader.Load(unicodePath);

            Assert.True(model.Triangles.Count >= 12);
        }
        finally
        {
            File.Delete(asciiPath);
            Directory.Delete(unicodeDirectory, recursive: true);
        }
    }

    [Fact]
    public void Step_viewer_tessellates_a_cad_body_as_a_shaded_triangle_mesh()
    {
        using var box = new BRepPrimAPI_MakeBox(new gp_Pnt(1000, 2000, 3000), 10, 20, 30);
        using var shape = box.Shape();
        var model = StepSolidMeshLoader.Tessellate(shape);

        Assert.True(model.Triangles.Count >= 12);
        Assert.All(model.Triangles, triangle =>
        {
            Assert.InRange(triangle.FirstIndex, 0, model.Points.Count - 1);
            Assert.InRange(triangle.SecondIndex, 0, model.Points.Count - 1);
            Assert.InRange(triangle.ThirdIndex, 0, model.Points.Count - 1);
        });
        Assert.Equal(1005, (model.Points.Min(point => point.X) + model.Points.Max(point => point.X)) / 2, 4);
        Assert.Equal(2010, (model.Points.Min(point => point.Y) + model.Points.Max(point => point.Y)) / 2, 4);
        Assert.Equal(3015, (model.Points.Min(point => point.Z) + model.Points.Max(point => point.Z)) / 2, 4);
        var centerOfGravity = StepViewerControl.ModelCenterOfGravity(
            model.Points,
            model.Triangles,
            model.SelectablePointIndices);
        Assert.Equal(1005, centerOfGravity.X, 4);
        Assert.Equal(2010, centerOfGravity.Y, 4);
        Assert.Equal(3015, centerOfGravity.Z, 4);

        var snapshotPath = Path.Combine(Path.GetTempPath(), $"meimad-solid-{Guid.NewGuid():N}.png");
        var visibleEdgesPath = Path.Combine(Path.GetTempPath(), $"meimad-visible-edges-{Guid.NewGuid():N}.png");
        var wireframePath = Path.Combine(Path.GetTempPath(), $"meimad-wireframe-{Guid.NewGuid():N}.png");
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewer = new StepViewerControl { Width = 640, Height = 420 };
                viewer.Measure(new Size(640, 420));
                viewer.Arrange(new Rect(0, 0, 640, 420));
                viewer.UpdateLayout();
                viewer.LoadModel(model, "box.step");
                Assert.True(viewer.IsSolidModel);
                Assert.Equal(model.Triangles.Count, viewer.TriangleCount);
                Assert.Equal(StepDisplayMode.Shaded, viewer.DisplayMode);
                Assert.True(viewer.IsSolidSurfaceVisible);
                Assert.Equal(0, viewer.RenderedEdgeCount);

                viewer.SetDisplayMode(StepDisplayMode.VisibleEdges);
                Assert.True(viewer.IsSolidSurfaceVisible);
                Assert.True(viewer.RenderedEdgeCount > 0);
                var visibleEdgeCount = viewer.RenderedEdgeCount;
                viewer.SaveSnapshot(visibleEdgesPath);

                viewer.SetDisplayMode(StepDisplayMode.Wireframe);
                Assert.False(viewer.IsSolidSurfaceVisible);
                Assert.True(viewer.RenderedEdgeCount > visibleEdgeCount);
                viewer.SaveSnapshot(wireframePath);

                viewer.SetDisplayMode(StepDisplayMode.Shaded);
                Assert.True(viewer.IsSolidSurfaceVisible);
                Assert.Equal(0, viewer.RenderedEdgeCount);
                viewer.SaveSnapshot(snapshotPath);
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
        Assert.True(File.ReadAllBytes(snapshotPath).AsSpan(0, 8).SequenceEqual(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.False(File.ReadAllBytes(snapshotPath).SequenceEqual(File.ReadAllBytes(visibleEdgesPath)));
        Assert.False(File.ReadAllBytes(visibleEdgesPath).SequenceEqual(File.ReadAllBytes(wireframePath)));
        File.Delete(snapshotPath);
        File.Delete(visibleEdgesPath);
        File.Delete(wireframePath);
    }

    [Fact]
    public void Bounding_box_supports_model_and_custom_point_references_without_changing_geometry()
    {
        var model = new StepModelData(
            [new(0,0,0), new(10,0,0), new(0,20,0), new(0,0,30), new(10,20,30)],
            [], [], [0,1,2,3,4]);
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewer = new StepViewerControl { Width = 500, Height = 400 };
                viewer.Measure(new Size(500,400));
                viewer.Arrange(new Rect(0,0,500,400));
                viewer.LoadModel(model, "bounds.step");
                Assert.Equal(10, viewer.CurrentBoundingBox!.X, 6);
                Assert.Equal(20, viewer.CurrentBoundingBox.Y, 6);
                Assert.Equal(30, viewer.CurrentBoundingBox.Z, 6);
                viewer.SetCustomReferenceByPoints(0, 1, 2, 0, 1);
                Assert.True(viewer.CurrentBoundingBox!.UsesCustomReference);
                Assert.Equal(10, viewer.CurrentBoundingBox.X, 6);
                viewer.FlipReferenceAxis("X");
                Assert.Equal(10, viewer.CurrentBoundingBox.X, 6);
                viewer.ClearCustomReference();
                Assert.False(viewer.CurrentBoundingBox!.UsesCustomReference);
                Assert.Throws<InvalidOperationException>(() => viewer.SetCustomReferenceByPoints(0, 1, 1, 0, 1));
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }

    [Fact]
    public void Step_viewer_loads_edges_and_writes_a_png_snapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"meimad-step-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var stepPath = Path.Combine(directory, "part.stp");
            var snapshotPath = Path.Combine(directory, "part.png");
            File.WriteAllText(stepPath, """
                ISO-10303-21;
                DATA;
                #1=CARTESIAN_POINT('coordinate system origin',(0.,0.,0.));
                #2=CARTESIAN_POINT('',(1000.,2000.,3000.));
                #3=CARTESIAN_POINT('',(1010.,2000.,3000.));
                #4=CARTESIAN_POINT('',(1010.,2010.,3000.));
                #11=VERTEX_POINT('',#2);
                #12=VERTEX_POINT('',#3);
                #13=VERTEX_POINT('',#4);
                #21=EDGE_CURVE('',#11,#12,#31,.T.);
                #22=EDGE_CURVE('',#12,#13,#32,.T.);
                ENDSEC;
                END-ISO-10303-21;
                """);

            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var viewer = new StepViewerControl
                    {
                        Width = 640,
                        Height = 420
                    };
                    viewer.Measure(new Size(640, 420));
                    viewer.Arrange(new Rect(0, 0, 640, 420));
                    viewer.UpdateLayout();
                    viewer.LoadStep(stepPath);
                    Assert.True(viewer.HasModel);
                    Assert.Equal(stepPath, viewer.LoadedPath);
                    Assert.Equal(1006.666666, viewer.ModelCenter.X, 5);
                    Assert.Equal(2003.333333, viewer.ModelCenter.Y, 5);
                    Assert.Equal(3000, viewer.ModelCenter.Z);
                    viewer.FitToWindow();
                    viewer.MeasureBetweenVertices(1, 2);
                    Assert.NotNull(viewer.CurrentMeasurement);
                    Assert.Equal(10, viewer.CurrentMeasurement.Distance, 6);
                    Assert.Contains("ΔX 10", viewer.MeasurementText);
                    viewer.SaveSnapshot(snapshotPath);
                }
                catch (Exception exception)
                {
                    error = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            Assert.Null(error);
            Assert.True(File.Exists(snapshotPath));
            var signature = File.ReadAllBytes(snapshotPath).Take(8).ToArray();
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, signature);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
