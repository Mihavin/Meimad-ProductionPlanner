using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Meimad.Planner.Client.Windows.Presentation;
using Microsoft.Win32;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>
/// Detached "View in 3D" window: every model file linked to a Case in one scene, with display,
/// projection, reference-frame and measurement tools. Opened from the Timeline, the Planning Board
/// and the Case workspace; several windows may be open at once.
/// </summary>
public partial class ModelViewerWindow : Window
{
    private static readonly Dictionary<string, ModelViewerWindow> OpenWindows = new(StringComparer.Ordinal);
    private readonly ModelViewerViewModel viewModel;
    private bool suppressResultSelection;

    internal ModelViewerWindow(ModelViewerViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Viewer.MeasurementChanged += (_, _) => UpdateMeasurementState();
        Viewer.ReferenceChanged += (_, _) => UpdateReferenceState();
        Viewer.ModelStateChanged += (_, _) => UpdateReferenceState();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            OpenWindows.Remove(viewModel.CaseId);
            Viewer.ClearModel();
        };
    }

    /// <summary>Opens (or focuses) the 3D window of a Case.</summary>
    internal static void Open(Window? owner, ModelViewerContext context, string caseId, string title)
    {
        if (OpenWindows.TryGetValue(caseId, out var existing))
        {
            existing.Activate();
            return;
        }

        var window = new ModelViewerWindow(new ModelViewerViewModel(context, caseId, $"3D · {title}"));
        if (owner is not null)
        {
            window.Owner = owner;
        }
        OpenWindows[caseId] = window;
        window.Show();
    }

    private async Task LoadAsync()
    {
        await viewModel.LoadAsync();
        LoadModelsIntoViewer();
    }

    private void LoadModelsIntoViewer()
    {
        Viewer.ClearModel();
        foreach (var item in viewModel.Files)
        {
            item.LayerId = null;
            item.LoadError = null;
        }
        // The primary part model first so the camera orbits around it; overlays follow.
        foreach (var item in viewModel.Files.OrderByDescending(file => file.IsPrimary).ThenBy(file => file.File.SortOrder))
        {
            LoadItem(item);
        }
        UpdateMeasurementState();
        UpdateReferenceState();
    }

    private void LoadItem(ModelFileItemViewModel item)
    {
        item.VisibilityChanged -= Item_VisibilityChanged;
        try
        {
            item.LayerId = Viewer.AddFile(
                item.FilePath,
                item.Label,
                item.Kind,
                color: null,
                opacity: ModelFileKinds.DefaultOpacity(item.Kind));
            item.LoadError = null;
            if (!item.IsVisible)
            {
                Viewer.SetLayerVisible(item.LayerId, false);
            }
            item.VisibilityChanged += Item_VisibilityChanged;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or FormatException
            or InvalidOperationException)
        {
            item.LayerId = null;
            item.LoadError = exception is FileNotFoundException or DirectoryNotFoundException
                ? "File not found at the linked path."
                : exception.Message;
        }
    }

    private void Item_VisibilityChanged(object? sender, EventArgs e)
    {
        if (sender is ModelFileItemViewModel { LayerId: { } layerId } item)
        {
            Viewer.SetLayerVisible(layerId, item.IsVisible);
            UpdateReferenceState();
        }
    }

    private async void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var kind = (AddKindCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? ModelFileKinds.Part;
        var dialog = new OpenFileDialog
        {
            Title = $"Link a {ModelFileKinds.DisplayName(kind).ToLowerInvariant()} file to the Case",
            Filter = "CAD models|*.stp;*.step;*.stl|STEP models|*.stp;*.step|STL meshes|*.stl|All files|*.*",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var path in dialog.FileNames)
        {
            var item = await viewModel.AddAsync(path, kind, caseOperationId: null);
            if (item is not null)
            {
                LoadItem(item);
                viewModel.SelectedFile = item;
            }
        }
        UpdateReferenceState();
    }

    private async void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedFile is not { } item)
        {
            MessageBox.Show(this, "Select a model file first.", "Remove link", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this,
                $"Remove the link to {item.Label}? The file on disk is not deleted.",
                "Remove model link", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var layerId = item.LayerId;
        if (await viewModel.RemoveAsync(item) && layerId is not null)
        {
            Viewer.RemoveLayer(layerId);
            UpdateReferenceState();
        }
    }

    private async void SetPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedFile is { } item)
        {
            await viewModel.SetPrimaryAsync(item);
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string view })
        {
            Viewer.SetView(view);
        }
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Viewer.FitToWindow();

    private void Projection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Viewer is null) return;
        var token = (ProjectionCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        Viewer.SetProjection(token == "perspective" ? StepProjectionMode.Perspective : StepProjectionMode.Orthographic);
    }

    private void DisplayMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Viewer is null) return;
        var token = (DisplayModeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        Viewer.SetDisplayMode(token switch
        {
            "visibleEdges" => StepDisplayMode.VisibleEdges,
            "hiddenLine" => StepDisplayMode.HiddenLine,
            "wireframe" => StepDisplayMode.Wireframe,
            "transparent" => StepDisplayMode.Transparent,
            _ => StepDisplayMode.Shaded
        });
    }

    private void BoundingBox_Changed(object sender, RoutedEventArgs e)
    {
        if (Viewer is not null)
        {
            Viewer.ShowBoundingBox(BoundingBoxToggle.IsChecked == true);
        }
    }

    private void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        if (!Viewer.HasModel)
        {
            MessageBox.Show(this, "Load a model first.", "Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Save the 3D view as PNG",
            Filter = "PNG image|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = "3d-view.png",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            Viewer.SaveSnapshot(dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "Snapshot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string token })
        {
            return;
        }
        if (!Viewer.HasModel)
        {
            MeasurementText.Text = "Load a model first.";
            return;
        }
        Viewer.BeginMeasurement(token switch
        {
            "point" => StepMeasurementTool.Point,
            "distance" => StepMeasurementTool.Distance,
            "minimumDistance" => StepMeasurementTool.MinimumDistance,
            "edgeLength" => StepMeasurementTool.EdgeLength,
            "radius" => StepMeasurementTool.Radius,
            "angle" => StepMeasurementTool.Angle,
            "faceArea" => StepMeasurementTool.FaceArea,
            "volume" => StepMeasurementTool.Volume,
            _ => StepMeasurementTool.None
        });
        Viewer.Focus();
    }

    private void ClearMeasurement_Click(object sender, RoutedEventArgs e) => Viewer.ClearMeasurement();

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressResultSelection || ResultsList.SelectedIndex < 0)
        {
            return;
        }
        var results = Viewer.MeasurementResults;
        if (ResultsList.SelectedIndex < results.Count)
        {
            MeasurementText.Text = results[ResultsList.SelectedIndex].Details;
        }
    }

    private void ReferencePoints_Click(object sender, RoutedEventArgs e)
    {
        BoundingBoxToggle.IsChecked = true;
        Viewer.BeginCustomReferenceByPoints();
        Viewer.Focus();
    }

    private void ReferenceFace_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BoundingBoxToggle.IsChecked = true;
            Viewer.BeginCustomReferenceByFaceAndEdge();
            Viewer.Focus();
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(this, exception.Message, "Reference", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ReferenceClear_Click(object sender, RoutedEventArgs e) => Viewer.ClearCustomReference();

    private void Flip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string axis })
        {
            Viewer.FlipReferenceAxis(axis);
        }
    }

    private void UpdateMeasurementState()
    {
        MeasurementText.Text = Viewer.MeasurementText;
        suppressResultSelection = true;
        try
        {
            var results = Viewer.MeasurementResults;
            ResultsList.ItemsSource = results.Select((result, index) => $"{index + 1}. {ToolName(result.Tool)}: {result.Label}").ToArray();
            ResultsList.SelectedIndex = results.Count - 1;
        }
        finally
        {
            suppressResultSelection = false;
        }
    }

    private static string ToolName(StepMeasurementTool tool) => tool switch
    {
        StepMeasurementTool.Point => "Point",
        StepMeasurementTool.Distance => "Distance",
        StepMeasurementTool.MinimumDistance => "Min. distance",
        StepMeasurementTool.EdgeLength => "Edge length",
        StepMeasurementTool.Radius => "Radius",
        StepMeasurementTool.Angle => "Angle",
        StepMeasurementTool.FaceArea => "Area",
        StepMeasurementTool.Volume => "Volume",
        _ => "Measurement"
    };

    private void UpdateReferenceState()
    {
        var box = Viewer.CurrentBoundingBox;
        BoundingBoxText.Text = !Viewer.IsBoundingBoxVisible
            ? "Enable Bounding box to display it."
            : box is null
            ? "Load a model to calculate its bounding box."
            : $"{(box.UsesCustomReference ? "Custom reference" : "Model XYZ")}\nX {box.X:0.####}   Y {box.Y:0.####}   Z {box.Z:0.####}";
        MeasurementText.Text = Viewer.MeasurementText;
    }
}
