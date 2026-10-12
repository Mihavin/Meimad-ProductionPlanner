using System.IO;
using System.Windows;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>
/// Renders a finished-part model (STL, or STEP tessellated with OpenCascade) to a PNG without showing a
/// window, with the Case workspace's own model viewer (<see cref="StepViewerControl"/>), the same
/// renderer the STEP snapshot uses. The file is read in the background; drawing happens on the calling
/// UI (STA) thread. The model file is only read.
/// </summary>
internal static class PartThumbnailRenderer
{
    internal const int Width = 480;
    internal const int Height = 320;

    internal static async Task<byte[]> RenderAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(modelPath).ToLowerInvariant();
        var model = await Task.Run(() => extension == ".stl" ? StlMeshLoader.Load(modelPath) : StepSolidMeshLoader.Load(modelPath), cancellationToken);
        return Render(model, Path.GetFileName(modelPath));
    }

    /// <summary>Draws the model isometric, fitted and shaded, and returns the PNG bytes.</summary>
    internal static byte[] Render(StepModelData model, string displayPath)
    {
        var viewer = new StepViewerControl { Width = Width, Height = Height };
        viewer.Measure(new Size(Width, Height));
        viewer.Arrange(new Rect(0, 0, Width, Height));
        viewer.UpdateLayout();
        viewer.LoadModel(model, displayPath);
        viewer.SetDisplayMode(StepDisplayMode.Shaded);
        viewer.SetView("isometric");
        viewer.FitToWindow();
        viewer.UpdateLayout();
        return viewer.SnapshotPng();
    }
}
