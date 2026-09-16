using System.Threading;
using System.Windows;
using Meimad.Planner.Client.Windows.Views;
using OCCSharp;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class StepViewerSceneTests
{
    [Fact]
    public void Layers_overlay_hide_and_remove_without_moving_the_camera_center()
    {
        using var part = new BRepPrimAPI_MakeBox(10, 10, 10);
        using var stock = new BRepPrimAPI_MakeBox(new gp_Pnt(-5, -5, -5), 20, 20, 20);
        var partModel = StepSolidMeshLoader.Tessellate(part.Shape());
        var stockModel = StepSolidMeshLoader.Tessellate(stock.Shape());
        RunOnSta(() =>
        {
            var viewer = NewViewer();
            viewer.LoadModel(partModel, "part.step");
            var center = viewer.ModelCenter;
            var partTriangles = viewer.TriangleCount;

            var stockLayer = viewer.AddLayer(stockModel, "stock.step", "Rest stock", "stock", opacity: 0.4);
            Assert.Equal(2, viewer.Layers.Count);
            Assert.Equal(partTriangles + stockModel.Triangles.Count, viewer.TriangleCount);
            Assert.Equal(center, viewer.ModelCenter);
            Assert.Equal(partTriangles + stockModel.Triangles.Count, viewer.RenderedSurfaceTriangleCount);

            viewer.SetLayerVisible(stockLayer, false);
            Assert.False(viewer.Layers.Single(layer => layer.Id == stockLayer).IsVisible);
            Assert.Equal(partTriangles, viewer.RenderedSurfaceTriangleCount);
            Assert.Equal(10, viewer.CurrentBoundingBox!.X, 6);

            viewer.SetLayerVisible(stockLayer, true);
            Assert.Equal(20, viewer.CurrentBoundingBox!.X, 6);

            viewer.RemoveLayer(stockLayer);
            Assert.Single(viewer.Layers);
            Assert.Equal(partTriangles, viewer.TriangleCount);
            Assert.Equal("part.step", viewer.LoadedPath);
        });
    }

    [Fact]
    public void Perspective_projection_enlarges_near_geometry_and_keeps_the_center_fixed()
    {
        using var box = new BRepPrimAPI_MakeBox(10, 10, 10);
        var model = StepSolidMeshLoader.Tessellate(box.Shape());
        RunOnSta(() =>
        {
            var viewer = NewViewer();
            viewer.LoadModel(model, "box.step");
            viewer.SetView("front");
            viewer.FitToWindow();
            var orthographic = Enumerable.Range(0, model.Points.Count).Select(viewer.EdgeProjectionForTest).ToArray();

            viewer.SetProjection(StepProjectionMode.Perspective);
            Assert.Equal(StepProjectionMode.Perspective, viewer.Projection);
            var perspective = Enumerable.Range(0, model.Points.Count).Select(viewer.EdgeProjectionForTest).ToArray();

            // Front view looks along -Z: faces at larger Z are nearer the camera and spread wider.
            var nearIndices = Enumerable.Range(0, model.Points.Count).Where(index => model.Points[index].Z > 5).ToArray();
            var farIndices = Enumerable.Range(0, model.Points.Count).Where(index => model.Points[index].Z < 5).ToArray();
            double Width(int[] indices, Point[] projected) => indices.Max(i => projected[i].X) - indices.Min(i => projected[i].X);
            Assert.Equal(Width(nearIndices, orthographic), Width(farIndices, orthographic), 4);
            Assert.True(Width(nearIndices, perspective) > Width(farIndices, perspective));
            Assert.True(viewer.IsSolidSurfaceVisible);
            Assert.Equal(model.Triangles.Count, viewer.RenderedSurfaceTriangleCount);

            viewer.SetProjection(StepProjectionMode.Orthographic);
            var restored = Enumerable.Range(0, model.Points.Count).Select(viewer.EdgeProjectionForTest).ToArray();
            for (var index = 0; index < restored.Length; index++)
            {
                Assert.Equal(orthographic[index].X, restored[index].X, 6);
                Assert.Equal(orthographic[index].Y, restored[index].Y, 6);
            }
        });
    }

    [Fact]
    public void Hidden_line_and_transparent_modes_draw_brep_edges()
    {
        using var box = new BRepPrimAPI_MakeBox(10, 20, 30);
        var model = StepSolidMeshLoader.Tessellate(box.Shape());
        Assert.NotNull(model.Edges);
        RunOnSta(() =>
        {
            var viewer = NewViewer();
            viewer.LoadModel(model, "box.step");

            viewer.SetDisplayMode(StepDisplayMode.HiddenLine);
            Assert.False(viewer.IsSolidSurfaceVisible);
            Assert.Equal(0, viewer.RenderedSurfaceTriangleCount);
            var hiddenLineEdges = viewer.RenderedEdgeCount;
            Assert.True(hiddenLineEdges > 0);

            viewer.SetDisplayMode(StepDisplayMode.Wireframe);
            var wireframeEdges = viewer.RenderedEdgeCount;
            Assert.Equal(12, wireframeEdges);
            Assert.True(wireframeEdges > hiddenLineEdges);

            viewer.SetDisplayMode(StepDisplayMode.Transparent);
            Assert.True(viewer.IsSolidSurfaceVisible);
            Assert.Equal(model.Triangles.Count, viewer.RenderedSurfaceTriangleCount);
            Assert.Equal(12, viewer.RenderedEdgeCount);
        });
    }

    [Fact]
    public void Volume_tool_reports_mass_properties_and_measurements_accumulate_until_cleared()
    {
        using var box = new BRepPrimAPI_MakeBox(10, 20, 30);
        var model = StepSolidMeshLoader.Tessellate(box.Shape(), keepGeometry: true);
        RunOnSta(() =>
        {
            var viewer = NewViewer();
            viewer.LoadModel(model, "box.step");

            viewer.BeginMeasurement(StepMeasurementTool.Volume);
            Assert.Equal(StepMeasurementTool.None, viewer.ActiveTool);
            var volume = Assert.Single(viewer.MeasurementResults);
            Assert.Equal(StepMeasurementTool.Volume, volume.Tool);
            Assert.Equal(6000, volume.Value, 3);
            Assert.Contains("surface 2200", viewer.MeasurementText);
            Assert.Contains("Bounding box 10 × 20 × 30", viewer.MeasurementText);

            var vertices = model.VertexPointIndices!;
            viewer.MeasureBetweenVertices(vertices[0], vertices[1]);
            Assert.Equal(2, viewer.MeasurementResults.Count);
            Assert.NotNull(viewer.CurrentMeasurement);
            Assert.Contains("mm", viewer.MeasurementText);

            viewer.ClearMeasurement();
            Assert.Empty(viewer.MeasurementResults);
            Assert.Null(viewer.CurrentMeasurement);
        });
    }

    private static StepViewerControl NewViewer()
    {
        var viewer = new StepViewerControl { Width = 640, Height = 420 };
        viewer.Measure(new Size(640, 420));
        viewer.Arrange(new Rect(0, 0, 640, 420));
        viewer.UpdateLayout();
        return viewer;
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }
}
