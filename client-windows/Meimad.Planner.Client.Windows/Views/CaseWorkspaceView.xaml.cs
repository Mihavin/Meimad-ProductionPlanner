using System.ComponentModel;
using System.IO;
using System.Windows.Controls;
using System.Windows;
using Meimad.Planner.Client.Windows.Presentation;
using Microsoft.Win32;

namespace Meimad.Planner.Client.Windows.Views;

public partial class CaseWorkspaceView : UserControl
{
    public CaseWorkspaceView()
    {
        InitializeComponent();
        StepViewer.ModelStateChanged += (_, _) => UpdateStepSnapshotState();
        StepViewer.MeasurementChanged += (_, _) => StepMeasurementText.Text = StepViewer.MeasurementText;
        StepViewer.ReferenceChanged += (_, _) => UpdateStepReferenceState();
        StepDisplayModeCombo.SelectionChanged += StepDisplayMode_SelectionChanged;
        DataContextChanged += CaseWorkspaceView_DataContextChanged;
    }

    private void CaseWorkspaceView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is CaseWorkspaceViewModel oldViewModel)
        {
            oldViewModel.PropertyChanged -= CaseWorkspace_PropertyChanged;
        }
        if (e.NewValue is CaseWorkspaceViewModel newViewModel)
        {
            newViewModel.PropertyChanged += CaseWorkspace_PropertyChanged;
            newViewModel.ConfirmBatchRemoval = ConfirmBatchRemoval;
        }
        StepViewer.ClearModel();
        UpdateStepSnapshotState();
    }

    private static bool ConfirmBatchRemoval(int batchCount) => MessageBox.Show(
        $"Adding a child component converts this Case into a parent. {batchCount} direct Production Batch{(batchCount == 1 ? string.Empty : "es")} and their assignments, execution history, allocations, and generated job-package records will be permanently removed. Continue?",
        "Remove direct Production Batches?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void CaseWorkspace_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CaseWorkspaceViewModel.SelectedCase)
            or nameof(CaseWorkspaceViewModel.IsFormReadOnly)
            or nameof(CaseWorkspaceViewModel.IsCreating))
        {
            if (e.PropertyName == nameof(CaseWorkspaceViewModel.SelectedCase))
            {
                StepViewer.ClearModel();
            }
            UpdateStepSnapshotState();
        }
    }

    private void OpenStep_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a STEP model",
            Filter = "STEP models|*.stp;*.step|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            StepViewer.LoadStep(dialog.FileName);
            StepDisplayModeCombo.SelectedIndex = 0;
            StepBoundingBoxToggle.IsChecked = false;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or FormatException)
        {
            MessageBox.Show(exception.Message, "STEP preview", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateStepSnapshotState();
    }

    private void BrowseGCode_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select released production G-code",
            Filter = "G-code|*.nc;*.tap;*.gcode;*.cnc;*.iso;*.mpf;*.spf|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.SetGCodeFileSelection(dialog.FileName);
        }
    }

    private void BrowseToolTable_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select the released physical tool table",
            Filter = "Tool tables|*.json;*.csv;*.txt;*.xml;*.mht;*.tools;*.tooltable|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.SetToolTableFileSelection(dialog.FileName);
        }
    }

    private void SnapshotStep_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel
            || !viewModel.CanEditForm
            || !StepViewer.HasModel)
        {
            return;
        }

        var safePartNumber = string.Concat((viewModel.PartNumber ?? "case")
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var dialog = new SaveFileDialog
        {
            Title = "Save STEP snapshot as the Case image",
            Filter = "PNG image|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = $"{safePartNumber}-step-preview.png",
            OverwritePrompt = true
        };
        if (!string.IsNullOrWhiteSpace(viewModel.WorkingFolderPath)
            && Directory.Exists(viewModel.WorkingFolderPath))
        {
            dialog.InitialDirectory = viewModel.WorkingFolderPath;
        }
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            StepViewer.SaveSnapshot(dialog.FileName);
            viewModel.SetPreviewSelection(dialog.FileName);
            MessageBox.Show(
                "The PNG was saved and selected as the Case picture. Press Save Case to commit the picture path.",
                "STEP snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            MessageBox.Show(exception.Message, "STEP snapshot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StepIsometric_Click(object sender, RoutedEventArgs e) => StepViewer.SetView("isometric");
    private void StepFront_Click(object sender, RoutedEventArgs e) => StepViewer.SetView("front");
    private void StepTop_Click(object sender, RoutedEventArgs e) => StepViewer.SetView("top");
    private void StepRight_Click(object sender, RoutedEventArgs e) => StepViewer.SetView("right");
    private void StepFit_Click(object sender, RoutedEventArgs e) => StepViewer.FitToWindow();
    private void StepDisplayMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var token = (StepDisplayModeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        StepViewer.SetDisplayMode(token switch
        {
            "visibleEdges" => StepDisplayMode.VisibleEdges,
            "wireframe" => StepDisplayMode.Wireframe,
            _ => StepDisplayMode.Shaded
        });
    }

    private void StepBoundingBox_Changed(object sender, RoutedEventArgs e)
    {
        if (StepViewer is not null)
        {
            StepViewer.ShowBoundingBox(StepBoundingBoxToggle.IsChecked == true);
        }
    }
    private void StepMeasureDistance_Click(object sender, RoutedEventArgs e) => StepViewer.BeginDistanceMeasurement();
    private void StepClearMeasurement_Click(object sender, RoutedEventArgs e) => StepViewer.ClearMeasurement();
    private void StepReferencePoints_Click(object sender, RoutedEventArgs e)
    {
        StepBoundingBoxToggle.IsChecked = true;
        StepViewer.BeginCustomReferenceByPoints();
    }
    private void StepReferenceFace_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StepBoundingBoxToggle.IsChecked = true;
            StepViewer.BeginCustomReferenceByFaceAndEdge();
        }
        catch (InvalidOperationException exception) { MessageBox.Show(exception.Message, "STEP reference", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void StepReferenceClear_Click(object sender, RoutedEventArgs e) => StepViewer.ClearCustomReference();
    private void StepFlipX_Click(object sender, RoutedEventArgs e) => StepViewer.FlipReferenceAxis("X");
    private void StepFlipY_Click(object sender, RoutedEventArgs e) => StepViewer.FlipReferenceAxis("Y");
    private void StepFlipZ_Click(object sender, RoutedEventArgs e) => StepViewer.FlipReferenceAxis("Z");

    private void UpdateStepReferenceState()
    {
        var box = StepViewer.CurrentBoundingBox;
        StepBoundingBoxText.Text = !StepViewer.IsBoundingBoxVisible
            ? "Bounding box hidden. Enable Show bounding box to display it."
            : box is null
            ? "Open a STEP model to calculate its bounding box."
            : $"{(box.UsesCustomReference ? "Custom" : "Model XYZ")}\nX {box.X:0.####}   Y {box.Y:0.####}   Z {box.Z:0.####}";
        StepMeasurementText.Text = StepViewer.MeasurementText;
    }

    private void UpdateStepSnapshotState()
    {
        SnapshotStepButton.IsEnabled = StepViewer.HasModel
            && DataContext is CaseWorkspaceViewModel { CanEditForm: true };
        StepDisplayModeCombo.IsEnabled = StepViewer.IsSolidModel;
        StepBoundingBoxToggle.IsEnabled = StepViewer.HasModel;
        if (!StepViewer.HasModel)
        {
            StepBoundingBoxToggle.IsChecked = false;
        }
        StepDisplayModeCombo.ToolTip = StepViewer.IsSolidModel
            ? "Choose shaded faces, shaded faces with visible edges, or wireframe."
            : "Display modes become available after the STEP file is loaded as a tessellated solid.";
        UpdateStepReferenceState();
    }

    private void BrowseFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select the external Case Working Folder",
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.SetWorkingFolderSelection(dialog.FolderName);
        }
    }

    private void BrowsePicture_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select the Case picture",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.SetPreviewSelection(dialog.FileName);
        }
    }

    private async void DeleteCase_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaseWorkspaceViewModel viewModel
            && Confirm("Delete the selected Case? It must have no Operations, Orders, or Production Batches."))
            await viewModel.DeleteSelectedCaseAsync();
    }

    private async void DeleteOperation_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaseWorkspaceViewModel viewModel
            && Confirm("Delete the selected Case Operation? Referenced or instantiated Operations cannot be deleted."))
            await viewModel.DeleteSelectedOperationAsync();
    }

    private async void DeleteOrder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaseWorkspaceViewModel viewModel
            && Confirm("Delete the selected Order? Orders allocated to a Production Batch cannot be deleted."))
            await viewModel.DeleteSelectedOrderAsync();
    }

    private async void DeleteBatch_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaseWorkspaceViewModel viewModel
            && Confirm("Delete the selected Production Batch and all of its assignments, Operation execution/pause history, allocations, and generated job-package records? This cannot be undone."))
            await viewModel.DeleteSelectedBatchAsync();
    }

    private static bool Confirm(string message) => MessageBox.Show(
        message, "Confirm deletion", MessageBoxButton.YesNo,
        MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}
