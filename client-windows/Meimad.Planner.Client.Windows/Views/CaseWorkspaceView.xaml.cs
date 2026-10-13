using Meimad.Planner.Client.Windows.Presentation.CimatronImport;
using System.Globalization;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;
using Microsoft.Win32;
using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows.Views;

public partial class CaseWorkspaceView : UserControl
{
    // Outstanding 3D model loads for the currently selected Case. Cancelled (and replaced)
    // whenever the selection changes or a new load starts, so a model that finishes parsing
    // after the user has already moved to another Case is discarded instead of drawn.
    private CancellationTokenSource? modelLoads;

    private CancellationToken BeginModelLoad()
    {
        CancelModelLoads();
        modelLoads = new CancellationTokenSource();
        return modelLoads.Token;
    }

    private void CancelModelLoads()
    {
        modelLoads?.Cancel();
        modelLoads?.Dispose();
        modelLoads = null;
    }

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
            oldViewModel.ModelFilesLoaded -= CaseWorkspace_ModelFilesLoaded;
        }
        if (e.NewValue is CaseWorkspaceViewModel newViewModel)
        {
            newViewModel.PropertyChanged += CaseWorkspace_PropertyChanged;
            newViewModel.ModelFilesLoaded += CaseWorkspace_ModelFilesLoaded;
            newViewModel.ConfirmBatchRemoval = ConfirmBatchRemoval;
            newViewModel.ConfirmSetupRestart = ConfirmSetupRestart;
        }
        CancelModelLoads();
        StepViewer.ClearModel();
        UpdateStepSnapshotState();
    }

    private static bool ConfirmSetupRestart(string question) => LocalizedMessageBox.Show(
        question, "Back to setup", MessageBoxButton.YesNo, MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    private static bool ConfirmBatchRemoval(int batchCount) => LocalizedMessageBox.Show(
        $"Adding a child component converts this Case into a parent. {batchCount} direct Production Work Order{(batchCount == 1 ? string.Empty : "s")} and their assignments, execution history, allocations, and generated job-package records will be permanently removed. Continue?",
        "Remove direct Work Orders?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void CaseWorkspace_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CaseWorkspaceViewModel.SelectedCase)
            or nameof(CaseWorkspaceViewModel.CanEditUnlockedFields)
            or nameof(CaseWorkspaceViewModel.IsCreating))
        {
            if (e.PropertyName == nameof(CaseWorkspaceViewModel.SelectedCase))
            {
                CancelModelLoads();
                StepViewer.ClearModel();
            }
            UpdateStepSnapshotState();
        }
    }

    private async void OpenStep_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Preview a STEP or STL model",
            Filter = "CAD models|*.stp;*.step;*.stl|STEP models|*.stp;*.step|STL meshes|*.stl|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        }.Localized();
        if (DataContext is CaseWorkspaceViewModel stepViewModel && stepViewModel.CaseBrowseStartFolder() is { } stepStart)
        {
            dialog.InitialDirectory = stepStart;
        }
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // Off the UI thread -- see StepViewerControl.LoadFileAsync's remarks. A large or
            // malformed file can take a long time (or, in the worst case, effectively hang inside
            // the native OpenCascade call); this at least keeps the rest of the app usable while
            // that happens instead of freezing the whole window.
            await StepViewer.LoadFileAsync(dialog.FileName, BeginModelLoad());
            StepDisplayModeCombo.SelectedIndex = 0;
            StepBoundingBoxToggle.IsChecked = false;
        }
        catch (OperationCanceledException)
        {
            return; // the Case changed while the file was loading; its result was discarded
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or FormatException)
        {
            LocalizedMessageBox.Show(exception.Message, "Model preview", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateStepSnapshotState();
    }

    // ------------------------------------------------------------ linked model files

    private async void CaseWorkspace_ModelFilesLoaded(object? sender, EventArgs e) => await LoadLinkedModelsIntoViewerAsync();

    /// <summary>
    /// Draws every linked file: the primary part model first, then stock/fixtures translucent.
    /// Runs automatically right after a Case's model files are loaded (see
    /// CaseWorkspace_ModelFilesLoaded), which used to mean selecting any Case with a linked STEP
    /// file froze the whole application for as long as that file took to parse and tessellate --
    /// each file's heavy native OpenCascade work now runs off the UI thread (see
    /// StepViewerControl.AddFileAsync), so the app stays responsive even for a large or slow model.
    /// </summary>
    private async Task LoadLinkedModelsIntoViewerAsync()
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel)
        {
            return;
        }

        // The user may switch to a different Case while a previous one's linked models are still
        // loading in the background; a stale result must never be drawn into whatever Case (or
        // no Case) is selected by the time it comes back.
        var caseId = viewModel.SelectedCase?.CaseId;
        bool IsStale() => DataContext != viewModel || viewModel.SelectedCase?.CaseId != caseId;
        var token = BeginModelLoad();

        StepViewer.ClearModel();
        var failures = new List<string>();
        foreach (var item in viewModel.ModelFiles.OrderByDescending(file => file.IsPrimary).ThenBy(file => file.File.SortOrder))
        {
            string? layerId = null;
            string? loadError = null;
            try
            {
                layerId = await StepViewer.AddFileAsync(item.FilePath, item.Label, item.Kind, null, ModelFileKinds.DefaultOpacity(item.Kind), token);
            }
            catch (OperationCanceledException)
            {
                return; // superseded by a newer selection or load
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or FormatException
                or InvalidOperationException)
            {
                loadError = exception is FileNotFoundException or DirectoryNotFoundException
                    ? "File not found at the linked path."
                    : exception.Message;
            }

            if (IsStale())
            {
                return;
            }
            item.LayerId = layerId;
            item.LoadError = loadError;
            if (loadError is not null)
            {
                failures.Add($"{item.Label}: {loadError}");
            }
        }

        if (IsStale())
        {
            return;
        }
        StepDisplayModeCombo.SelectedIndex = 0;
        StepProjectionCombo.SelectedIndex = 0;
        StepBoundingBoxToggle.IsChecked = false;
        if (failures.Count > 0)
        {
            StepMeasurementText.Text = "Some linked files could not be loaded:\n" + string.Join('\n', failures);
        }
        UpdateStepSnapshotState();
    }

    private async void LoadModelFiles_Click(object sender, RoutedEventArgs e) => await LoadLinkedModelsIntoViewerAsync();

    private async void ModelFilesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is CaseWorkspaceViewModel { SelectedModelFile: { } item })
        {
            try
            {
                await StepViewer.LoadFileAsync(item.FilePath, BeginModelLoad());
                item.LoadError = null;
                UpdateStepSnapshotState();
            }
            catch (OperationCanceledException)
            {
                return; // the Case changed while the file was loading; its result was discarded
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or FormatException)
            {
                item.LoadError = exception.Message;
                LocalizedMessageBox.Show(exception.Message, "Model preview", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    /// <summary>
    /// Imports a Cimatron simulation package folder (manifest.json + COMPLETE) into the selected Operation.
    /// A blocked import shows the Server's report; when the planner answers its questions (stock, Machine,
    /// replacing a manual Work CS) the import is sent again with those answers.
    /// </summary>
    private async void ImportCimatronPackage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel) return;
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the Cimatron package folder (with manifest.json and COMPLETE)"
        }.Localized();
        var start = viewModel.CamSetup.SuggestedPackageFolder ?? viewModel.CaseBrowseStartFolder();
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start)) dialog.InitialDirectory = start;
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var folder = dialog.FolderName;
        SimulationPackageChoices? choices = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = await viewModel.CamSetup.ImportPackageAsync(folder, choices);
            if (result is null) return;
            if (result is { Outcome: SimulationPackageImportResult.Imported, Import: { } imported }) await SavePartThumbnailAsync(viewModel, imported);
            var hasNotes = result.Report.Diagnostics?.Any(item => item.Severity is "error" or "warning") == true;
            if (!result.IsBlocked && !hasNotes) return;
            choices = SimulationPackageReportWindow.Show(Window.GetWindow(this), result, canManageSetup: true);
            if (choices is null || !result.IsBlocked) return;
        }
    }

    /// <summary>
    /// Renders the package's finished part (STL or STEP) offscreen with the Case model viewer and saves the
    /// picture for the TV dashboard; a part that cannot be rendered only leaves the picture out.
    /// </summary>
    private static async Task SavePartThumbnailAsync(CaseWorkspaceViewModel viewModel, SimulationPackageImport imported)
    {
        if (imported.FilePath("part") is not { } part || !File.Exists(part)) return;
        try
        {
            var png = await PartThumbnailRenderer.RenderAsync(part);
            await viewModel.SaveOperationThumbnailAsync(imported, part, png);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException
            or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            viewModel.ReportThumbnailProblem($"The finished-part picture could not be drawn: {exception.Message}");
        }
    }

    private void CimatronPackageReport_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel) return;
        if (viewModel.CamSetup.LastPackageResult is { } last)
        {
            SimulationPackageReportWindow.Show(Window.GetWindow(this), last, canManageSetup: false);
            return;
        }

        if (viewModel.CamSetup.CurrentPackage is { Report: { } report } current)
        {
            SimulationPackageReportWindow.Show(Window.GetWindow(this),
                new SimulationPackageImportResult(SimulationPackageImportResult.Imported, current, report), canManageSetup: false);
            return;
        }

        LocalizedMessageBox.Show("No Cimatron package was imported for this Operation yet.", "Package report", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void ImportCimatronFiles_Click(object sender, RoutedEventArgs e) => await AddCimatronFilesAsync(null);

    /// <summary>
    /// Import from Cimatron (specification of 2026-10-13): one window reads the active Cimatron document, offers its CAM
    /// operations by setup and imports the chosen ones into this Operation; Open simulation then opens the program.
    /// </summary>
    private async void ImportFromCimatron_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel) return;
        try
        {
            var context = viewModel.CreateCimatronImportContext();
            if (context is null)
            {
                LocalizedMessageBox.Show(Window.GetWindow(this), "Select an Operation, with an account that may edit Cases, to import from Cimatron.",
                    "Import from Cimatron", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var model = new CimatronImportViewModel(context, new CimatronBridgeProcess());
            var window = CimatronImportWindow.Show(Window.GetWindow(this), model);
            if (model.Imported is { } imported) await SavePartThumbnailAsync(viewModel, imported);
            if (window.OpenSimulationRequested && model.ImportedProgram is not null) await OpenNcEditorAsync(editSelectedFile: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LocalizedMessageBox.Show(Window.GetWindow(this), exception.Message, "Import from Cimatron", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Opens the saved CAM file of the Operation in Cimatron (the file is opened, never changed).</summary>
    private void OpenInCimatron_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel || viewModel.CamSetup.CamSource is not { } source) return;
        if (!source.Shared && source.SourceHost is { } host && !string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            LocalizedMessageBox.Show(Window.GetWindow(this),
                string.Format(CultureInfo.InvariantCulture, "The CAM file is on a local disk of {0} ({1}); open it on that PC, or save it to the shared folder and import again.", host, source.DocumentPath),
                "Open in Cimatron", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!File.Exists(source.DocumentPath))
        {
            LocalizedMessageBox.Show(Window.GetWindow(this),
                string.Format(CultureInfo.InvariantCulture, "The CAM file {0} cannot be found from this PC.", source.DocumentPath),
                "Open in Cimatron", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(source.DocumentPath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LocalizedMessageBox.Show(Window.GetWindow(this), exception.Message, "Open in Cimatron", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Opens the imported Meimad-format program with the Operation's stock, fixtures and part in the NC viewer.</summary>
    private async void OpenCimatronSimulation_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel || viewModel.CamSetup.CamSource is not { } source) return;
        if (source.PackagePath is { } package && source.NcFileName is { } ncFile
            && Path.GetDirectoryName(package) is { } dataset && File.Exists(Path.Combine(dataset, ncFile)))
        {
            viewModel.GCodeFilePath = Path.Combine(dataset, ncFile);
        }

        await OpenNcEditorAsync(editSelectedFile: !string.IsNullOrWhiteSpace(viewModel.GCodeFilePath) && File.Exists(viewModel.GCodeFilePath));
    }

    private async void AddCimatronFixture_Click(object sender, RoutedEventArgs e) => await AddCimatronFilesAsync("fixture");

    private async void AddCimatronRestMaterial_Click(object sender, RoutedEventArgs e) => await AddCimatronFilesAsync("stock");

    /// <summary>Links Cimatron exports (STL or STEP) to the selected Operation; null kind guesses from the name.</summary>
    private async Task AddCimatronFilesAsync(string? kind)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel) return;
        var dialog = new OpenFileDialog
        {
            Title = kind switch
            {
                "fixture" => "Choose fixture files exported from Cimatron",
                "stock" => "Choose rest-material files exported from Cimatron",
                _ => "Choose files exported from Cimatron (fixtures and rest material)"
            },
            Filter = "3D models (STL, STEP)|*.stl;*.stp;*.step|All files|*.*",
            CheckFileExists = true,
            Multiselect = true
        }.Localized();
        var start = viewModel.CamSetup.SuggestedFolder ?? viewModel.CaseBrowseStartFolder();
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start)) dialog.InitialDirectory = start;
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await viewModel.CamSetup.ImportFilesAsync(dialog.FileNames, kind);
    }

    private async void AddModelFile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel || !viewModel.CanManageModelFiles)
        {
            return;
        }

        var kind = (StepAddKindCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? ModelFileKinds.Part;
        var dialog = new OpenFileDialog
        {
            Title = $"Link a {ModelFileKinds.DisplayName(kind).ToLowerInvariant()} file to the Case",
            Filter = "CAD models|*.stp;*.step;*.stl|STEP models|*.stp;*.step|STL meshes|*.stl|All files|*.*",
            CheckFileExists = true,
            Multiselect = true
        }.Localized();
        if (viewModel.CaseBrowseStartFolder() is { } modelStart)
        {
            dialog.InitialDirectory = modelStart;
        }
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var path in dialog.FileNames)
        {
            await viewModel.AddModelFileAsync(path, kind, caseOperationId: null);
        }
        await LoadLinkedModelsIntoViewerAsync();
    }

    private async void SetPrimaryModelFile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaseWorkspaceViewModel { SelectedModelFile: { } item } viewModel)
        {
            await viewModel.SetPrimaryModelFileAsync(item);
        }
    }

    private async void RemoveModelFile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel { SelectedModelFile: { } item } viewModel)
        {
            return;
        }
        if (LocalizedMessageBox.Show($"Remove the link to {item.Label}? The file on disk is not deleted.",
                "Remove model link", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var layerId = item.LayerId;
        if (await viewModel.RemoveModelFileAsync(item) && layerId is not null)
        {
            StepViewer.RemoveLayer(layerId);
            UpdateStepSnapshotState();
        }
    }

    private void OpenModelWindow_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel || viewModel.SelectedCase is null)
        {
            LocalizedMessageBox.Show("Select a Case first.", "View in 3D", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var context = viewModel.CreateModelViewerContext();
        if (context is null)
        {
            LocalizedMessageBox.Show("Connect to the Server before opening the 3D viewer.", "View in 3D", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ModelViewerWindow.Open(Window.GetWindow(this), context, viewModel.SelectedCase.CaseId,
            $"{viewModel.PartNumber} {viewModel.Name}".Trim());
    }

    private void StepProjection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StepViewer is null) return;
        var token = (StepProjectionCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        StepViewer.SetProjection(token == "perspective" ? StepProjectionMode.Perspective : StepProjectionMode.Orthographic);
    }

    private void StepTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string token })
        {
            return;
        }
        if (!StepViewer.HasModel)
        {
            StepMeasurementText.Text = "Load a model first.";
            return;
        }
        StepViewer.BeginMeasurement(token switch
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
        StepViewer.Focus();
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
            Filter = "G-code|*.nc;*.tap;*.gcode;*.cnc;*.iso;*.mpf;*.spf;*.eia|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        }.Localized();
        if (dialog.ShowDialog() == true)
        {
            viewModel.SetGCodeFileSelection(dialog.FileName);
        }
    }

    private void AddSubprogramFiles_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Add subprogram files",
            Filter = "NC programs|*.nc;*.tap;*.cnc;*.txt;*.gcode;*.iso;*.mpf;*.spf;*.min;*.ngc;*.eia|All files|*.*",
            CheckFileExists = true,
            Multiselect = true,
            InitialDirectory = string.IsNullOrWhiteSpace(viewModel.GCodeFilePath)
                ? string.Empty
                : Path.GetDirectoryName(viewModel.GCodeFilePath) ?? string.Empty
        }.Localized();
        if (dialog.ShowDialog() == true)
        {
            viewModel.AddReleaseSubprogramFiles(dialog.FileNames);
        }
    }

    /// <summary>
    /// The NC viewer in edit mode: the selected G-code file when one exists, otherwise a new
    /// program with the Meimad canonical block. Open, edit, save, format and release happen there.
    /// </summary>
    private async void OpenNcViewer_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel) return;
        var editSelectedFile = !string.IsNullOrWhiteSpace(viewModel.GCodeFilePath) && File.Exists(viewModel.GCodeFilePath);
        await OpenNcEditorAsync(editSelectedFile);
    }

    private async Task OpenNcEditorAsync(bool editSelectedFile)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel) return;
        try
        {
            var request = await viewModel.CreateNcEditorRequestAsync(editSelectedFile);
            if (request is null)
            {
                LocalizedMessageBox.Show(Window.GetWindow(this), "Select an Operation first.", "NC editor",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            NcViewerWindow.Open(request);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An async void handler must not let a file or Server error reach the Dispatcher.
            LocalizedMessageBox.Show(Window.GetWindow(this), exception.Message, "NC editor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Right-clicking an Operation selects it, so the menu acts on the row under the mouse.</summary>
    private void OperationRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row) row.IsSelected = true;
    }

    private async void ShowOperationStatistics_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel) return;
        if (sender is FrameworkElement { DataContext: CaseOperation clicked } && viewModel.SelectedOperation != clicked)
            viewModel.SelectedOperation = clicked;
        if (viewModel.TimeStatisticsContext is not { } context)
        {
            LocalizedMessageBox.Show(Window.GetWindow(this), "Connect to the Server and select the Operation first.", "Operation statistics",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (OperationTimeStatisticsWindow.Open(Window.GetWindow(this), context.Client, context.ClientId,
                context.Operation.CaseId, context.Operation.CaseOperationId))
        {
            await viewModel.ReloadOperationsAfterTimeChangeAsync(context.Operation.CaseOperationId);
        }
    }

    private async void ViewReleaseNcFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlannerGCodeRelease release }) await OpenReleaseAsync(release);
    }

    private async void ReleaseRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow { Item: PlannerGCodeRelease release })
        {
            e.Handled = true;
            await OpenReleaseAsync(release);
        }
    }

    private async Task OpenReleaseAsync(PlannerGCodeRelease release)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel) return;
        try
        {
            var request = await viewModel.CreateReleaseViewerRequestAsync(release);
            if (request is null)
            {
                LocalizedMessageBox.Show(Window.GetWindow(this), "Connect to the Server and select the Operation first.", "View NC file",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            NcViewerWindow.Open(request);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LocalizedMessageBox.Show(Window.GetWindow(this), exception.Message, "View NC file", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            Filter = "Tool tables|*.mht;*.mhtml;*.json;*.csv;*.txt|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        }.Localized();
        if (dialog.ShowDialog() == true)
        {
            viewModel.SetToolTableFileSelection(dialog.FileName);
        }
    }

    private void SnapshotStep_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel
            || !viewModel.CanEditUnlockedFields
            || !StepViewer.HasModel)
        {
            return;
        }

        var safePartNumber = string.Concat((viewModel.PartNumber ?? "case")
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        try
        {
            // The snapshot goes to the Working Folder's _MeimadPlanner folder and to the Server as the Case picture.
            var png = StepViewer.SnapshotPng();
            _ = viewModel.SaveSnapshotAsync(png, $"{safePartNumber}-step-preview.png");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            LocalizedMessageBox.Show(exception.Message, "STEP snapshot", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            "hiddenLine" => StepDisplayMode.HiddenLine,
            "wireframe" => StepDisplayMode.Wireframe,
            "transparent" => StepDisplayMode.Transparent,
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
        catch (InvalidOperationException exception) { LocalizedMessageBox.Show(exception.Message, "STEP reference", MessageBoxButton.OK, MessageBoxImage.Warning); }
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
            && DataContext is CaseWorkspaceViewModel { CanEditUnlockedFields: true };
        StepDisplayModeCombo.IsEnabled = StepViewer.IsSolidModel;
        StepProjectionCombo.IsEnabled = StepViewer.HasModel;
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
        }.Localized();
        if (viewModel.CaseBrowseStartFolder() is { } folderStart)
        {
            dialog.InitialDirectory = folderStart;
        }
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
        }.Localized();
        if (viewModel.CaseBrowseStartFolder() is { } pictureStart)
        {
            dialog.InitialDirectory = pictureStart;
        }
        if (dialog.ShowDialog() != true) return;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LocalizedMessageBox.Show(exception.Message, "Case picture", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // The chosen image is uploaded and stored on the Server, not linked.
        _ = viewModel.UploadPictureAsync(bytes, Path.GetFileName(dialog.FileName));
    }

    // The TV Dashboard picture of the selected Operation: an image file becomes its snapshot on the Server.
    private void ChooseOperationThumbnail_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel || !viewModel.TvPicture.CanEdit) return;
        var dialog = new OpenFileDialog
        {
            Title = "Select the TV picture of the Operation",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        }.Localized();
        if (viewModel.CaseBrowseStartFolder() is { } start) dialog.InitialDirectory = start;
        if (dialog.ShowDialog() != true) return;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LocalizedMessageBox.Show(exception.Message, "TV picture", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _ = viewModel.TvPicture.UploadSnapshotAsync(bytes, Path.GetFileName(dialog.FileName), dialog.FileName);
    }

    // The current view of the 3D Models tab becomes the Operation's snapshot (nothing is written to the Working Folder).
    private void SnapshotOperationThumbnail_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel || !viewModel.TvPicture.CanEdit) return;
        if (!StepViewer.HasModel)
        {
            LocalizedMessageBox.Show("Open the part model in the 3D Models tab first.", "TV picture", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var safePartNumber = string.Concat((viewModel.PartNumber ?? "case")
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        try
        {
            var png = StepViewer.SnapshotPng();
            var operation = viewModel.SelectedOperation?.OperationNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "op";
            _ = viewModel.TvPicture.UploadSnapshotAsync(png, $"{safePartNumber}-op{operation}-tv.png", "3D view snapshot");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LocalizedMessageBox.Show(exception.Message, "TV picture", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemovePicture_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaseWorkspaceViewModel viewModel
            && LocalizedMessageBox.Show(Window.GetWindow(this), "Remove the Case picture stored on the Server?", "Case picture",
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK)
        {
            _ = viewModel.RemovePictureAsync();
        }
    }

    private async void DeleteCase_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaseWorkspaceViewModel viewModel
            && Confirm("Delete the selected Case? It must have no Operations, Orders, Work Orders, active component links, or verified material receipt history."))
            await viewModel.DeleteSelectedCaseAsync();
    }

    private CasePoolFilterWindow? poolFilterWindow;

    private void OpenPoolFilters_Click(object sender, RoutedEventArgs e)
    {
        if (poolFilterWindow is { IsLoaded: true })
        {
            poolFilterWindow.Activate();
            return;
        }
        poolFilterWindow = new CasePoolFilterWindow
        {
            DataContext = DataContext,
            Owner = Window.GetWindow(this)
        };
        poolFilterWindow.Closed += (_, _) => poolFilterWindow = null;
        poolFilterWindow.Show();
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
            && Confirm("Delete the selected Order? Orders allocated to a Work Order cannot be deleted."))
            await viewModel.DeleteSelectedOrderAsync();
    }

    private async void DeleteBatch_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaseWorkspaceViewModel viewModel
            && Confirm("Delete the selected Work Order and all of its assignments, Operation execution/pause history, allocations, material reservations, and generated job-package records? Verified receipt history remains. This cannot be undone."))
            await viewModel.DeleteSelectedBatchAsync();
    }

    private async void CancelBatchProduction_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaseWorkspaceViewModel viewModel
            && Confirm(
                "Cancel production for the selected Work Order? The Work Order, its Operations, Runs, and Programs will be cancelled; active Machine assignments and material reservations will be released; and Done parts will be reset to 0. Immutable CNC cycle and workflow history is retained. This cannot resume the same Production Run.",
                "Confirm production cancellation"))
            await viewModel.CancelSelectedBatchProductionAsync();
    }

    private static bool Confirm(string message, string title = "Confirm deletion") => LocalizedMessageBox.Show(
        message, title, MessageBoxButton.YesNo,
        MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}
