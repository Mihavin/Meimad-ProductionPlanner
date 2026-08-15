using System.Threading;
using System.Windows;
using Meimad.Planner.Client.Windows.Views;
using OCCSharp;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class StepViewerControlTests
{
    [Fact]
    public void Step_viewer_tessellates_a_step_body_as_a_shaded_solid()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"meimad-solid-step-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var stepPath = Path.Combine(directory, "box.step");
            using (var progress = new Message_ProgressRange())
            using (var box = new BRepPrimAPI_MakeBox(new gp_Pnt(1000, 2000, 3000), 10, 20, 30))
            using (var shape = box.Shape())
            using (var writer = new STEPControl_Writer())
            {
                Assert.False(shape.IsNull());
                writer.Transfer(shape, STEPControl_StepModelType.STEPControl_ManifoldSolidBrep, false, progress);
                Assert.Equal(IFSelect_ReturnStatus.IFSelect_RetDone, writer.Write(stepPath));
            }

            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var viewer = new StepViewerControl { Width = 640, Height = 420 };
                    viewer.Measure(new Size(640, 420));
                    viewer.Arrange(new Rect(0, 0, 640, 420));
                    viewer.UpdateLayout();
                    viewer.LoadStep(stepPath);
                    Assert.True(viewer.IsSolidModel);
                    Assert.True(viewer.TriangleCount >= 12);
                    Assert.Equal(1005, viewer.ModelCenter.X, 4);
                    Assert.Equal(2010, viewer.ModelCenter.Y, 4);
                    Assert.Equal(3015, viewer.ModelCenter.Z, 4);
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
        }
        finally
        {
            Directory.Delete(directory, true);
        }
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
