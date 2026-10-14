using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using System.Text;
using Microsoft.Win32;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Configuration;
using Meimad.Planner.Client.Windows.Localization;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;
using Meimad.Planner.Client.Windows.Presentation.ToolPreparation;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly DispatcherTimer refreshTimer;
    private TimelineWindow? timelineWindow;
    private AskAiWindow? askAiWindow;
    private bool hasInitialized;
    private bool refreshInProgress;

    public MainWindow()
    {
        InitializeComponent();
        try { LegacyNavigation.IsChecked = System.IO.File.Exists(NavigationPreference) && System.IO.File.ReadAllText(NavigationPreference) == "classic"; }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException) { }
        ThemeToggle.Content = Themes.WorkbenchTheme.Current == "graphite" ? "Light workshop" : "Graphite control room";
        LanguageSelector.SelectedValue = LocalizationService.Current.CurrentLanguage;
        viewModel = new MainWindowViewModel(
            new ClientSettingsStore(),
            new PlannerApiClientFactory(),
            RequestAssignmentOverrideReason);
        DataContext = viewModel;
        viewModel.NcCreatorQueue.ActionRequested += PreparationActionRequested;
        viewModel.ToolRoomQueue.ActionRequested += PreparationActionRequested;
        viewModel.SetupQueue.ActionRequested += PreparationActionRequested;
        viewModel.Shop.OpenRequested += async (_, context) => await OpenWorkbenchAsync(context);
        viewModel.OperationWorkbench.NcRequested += (_, request) => NcViewerWindow.Open(request);
        viewModel.OperationWorkbench.ExportRequested += async (_, package) =>
        {
            try { await ExportReviewedPackageAsync(package); }
            catch (Exception exception) { LocalizedMessageBox.Show(this, exception.Message, "Package export", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        viewModel.ClientUpdateAvailable += (_, update) =>
            Dispatcher.BeginInvoke(new Action(() => ClientUpdateWindow.Show(this, update)));
        viewModel.SignInRequired += (_, _) =>
            Dispatcher.BeginInvoke(new Action(ShowSignIn), DispatcherPriority.Background);
        PlannerApiException.ConflictRaised += OnConflictRaised;
        PlanningBoardView.OpenOperationRequested += PlanningBoardOpenOperationRequested;
        MainTimelineView.OperationActionRequested += TimelineOperationActionRequested;
        refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        refreshTimer.Tick += RefreshTimerOnTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        Themes.WorkbenchTheme.Apply(Themes.WorkbenchTheme.Current == "graphite" ? "light" : "graphite");
        ThemeToggle.Content = LocalizationService.Current.Translate(
            Themes.WorkbenchTheme.Current == "graphite" ? "Light workshop" : "Graphite control room");
    }

    private void Workspace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: "Shop" })
        { WorkspaceTabs.SelectedItem = ShopWorkspaceTab; _ = viewModel.Shop.RefreshAsync(); return; }
        if (sender is System.Windows.Controls.Button { Tag: "Operation" })
        { WorkspaceTabs.SelectedItem = OperationWorkspaceTab; return; }
        if (sender is System.Windows.Controls.Button { Tag: "Preparation" })
        { WorkspaceTabs.SelectedItem = PreparationWorkspaceTab; return; }
        if (sender is System.Windows.Controls.Button { Tag: "Integrations" })
        {
            WorkspaceTabs.SelectedItem = IntegrationsWorkspaceTab;
            _ = viewModel.OperationalHealth.ReadAsync(false);
            return;
        }
        if (sender is System.Windows.Controls.Button { Tag: string value } && int.TryParse(value, out var index))
        {
            WorkspaceTabs.SelectedIndex = index;
            if (index == 4) AdministrationView.OpenIntegrations();
        }
    }

    private async void PlanningBoardOpenOperationRequested(object? sender, PlanningOperationViewModel operation)
    {
        if (LegacyNavigation.IsChecked != true)
        {
            await OpenWorkbenchAsync(new(operation.CaseId, operation.CaseOperationId, operation.BatchId,
                operation.BatchOperationId, operation.MachineId));
            return;
        }
        WorkspaceTabs.SelectedIndex = 0;
        await viewModel.CaseWorkspace.NavigateToOperationAsync(operation.CaseId, operation.CaseOperationId);
    }

    private static string NavigationPreference => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meimad Planner", "workbench-navigation.txt");

    private void LegacyNavigation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(NavigationPreference)!);
            System.IO.File.WriteAllText(NavigationPreference, LegacyNavigation.IsChecked == true ? "classic" : "workbench");
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        { Trace.WriteLine("Navigation preference could not be saved."); }
    }

    private async Task OpenWorkbenchAsync(WorkbenchContext context)
    {
        viewModel.Navigation.Open(WorkspaceTabs.SelectedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), viewModel.Navigation.Context);
        viewModel.Navigation.Open("Operation", context);
        WorkspaceTabs.SelectedItem = OperationWorkspaceTab;
        await viewModel.OperationWorkbench.OpenAsync(context);
    }

    private async void WorkbenchBack_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.Navigation.Back()) return;
        if (int.TryParse(viewModel.Navigation.Workspace, out var index)) WorkspaceTabs.SelectedIndex = index;
        else if (viewModel.Navigation.Context is { } context)
        { WorkspaceTabs.SelectedItem = OperationWorkspaceTab; await viewModel.OperationWorkbench.OpenAsync(context); }
        else WorkspaceTabs.SelectedIndex = 1;
    }

    private async void OpenWorkbenchRecipe_Click(object sender, RoutedEventArgs e)
    {
        var details = viewModel.OperationWorkbench.Details;
        if (details is null) return;
        try { WorkspaceTabs.SelectedIndex = 0; await viewModel.CaseWorkspace.NavigateToOperationAsync(details.RecipeCaseId, details.RecipeOperationId); }
        catch (Exception exception) { LocalizedMessageBox.Show(this, exception.Message, "Open operation", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void TimelineOperationActionRequested(object? sender, TimelineOperationActionRequest request)
    {
        var operation = viewModel.MachinePlanningBoard.FindOperation(request.OperationId);
        if (request.Action == TimelineOperationAction.ShowInPlanningBoard)
        {
            WorkspaceTabs.SelectedIndex = 1;
            viewModel.MachinePlanningBoard.FocusOperation(request.OperationId);
            return;
        }
        if (request.Action == TimelineOperationAction.ViewIn3D)
        {
            if (operation is null)
            {
                LocalizedMessageBox.Show(this, "The operation is not loaded on the Planning Board yet. Refresh and try again.",
                    "View in 3D", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var context = viewModel.MachinePlanningBoard.CreateModelViewerContext();
            if (context is null)
            {
                LocalizedMessageBox.Show(this, "Connect to the Server before opening the 3D viewer.",
                    "View in 3D", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ModelViewerWindow.Open(this, context, operation.CaseId, operation.DisplayTitle);
            return;
        }
        if (operation is not null)
        {
            WorkspaceTabs.SelectedIndex = 0;
            await viewModel.CaseWorkspace.NavigateToOperationAsync(operation.CaseId, operation.CaseOperationId);
        }
    }

    private void LanguageSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LanguageSelector.SelectedValue is string language)
        {
            LocalizationService.Current.SetLanguage(language);
        }
    }

    private string? RequestAssignmentOverrideReason(AssignmentOverridePrompt prompt)
    {
        var dialog = new AssignmentOverrideDialog(prompt) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Reason : null;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (hasInitialized)
        {
            return;
        }

        hasInitialized = true;
        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            // Event-handler exceptions otherwise escape onto the Dispatcher and can
            // terminate the entire client. Expected transport failures are already
            // presented by the view model; retain diagnostics for any unexpected one.
            Trace.WriteLine($"Client initialization failed: {exception}");
        }
        finally
        {
            if (IsLoaded)
            {
                refreshTimer.Start();
            }
        }
    }

    private async void RefreshTimerOnTick(object? sender, EventArgs e)
    {
        if (refreshInProgress)
        {
            return;
        }

        refreshInProgress = true;
        try
        {
            await viewModel.RefreshAsync();
        }
        catch (Exception exception)
        {
            // Keep an unexpected background polling failure from tearing down WPF.
            Trace.WriteLine($"Client background refresh failed: {exception}");
        }
        finally
        {
            refreshInProgress = false;
        }
    }

    private async void ShowSignIn()
    {
        if (!IsLoaded) return;
        try
        {
            SignInWindow.Show(this, viewModel);
            await viewModel.CompleteSignInAsync();
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Sign-in failed: {exception}");
        }
    }

    private void ChangePassword_Click(object sender, RoutedEventArgs e) =>
        SignInWindow.ShowChangePassword(this, viewModel);

    /// <summary>
    /// Another user changed what this user was changing: explain what happened, who did it and
    /// when, and what to do next, whichever screen made the change.
    /// </summary>
    private void OnConflictRaised(PlannerApiException conflict)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var details = conflict.Conflict!;
            var who = details.ChangedBy is null
                ? string.Empty
                : details.ChangedAt is { } at
                    ? $"\n\nChanged by {details.ChangedBy} at {at.ToLocalTime():g}."
                    : $"\n\nChanged by {details.ChangedBy}.";
            LocalizedMessageBox.Show(
                this,
                $"{conflict.Message}{who}\n\nWhat to do: {details.Advice}",
                $"Someone else changed this {details.Resource}",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        PlannerApiException.ConflictRaised -= OnConflictRaised;
        refreshTimer.Stop();
        timelineWindow?.Close();
        askAiWindow?.Close();
        viewModel.Dispose();
    }

    private async void OpenNcViewer_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as System.Windows.Controls.Button;
        if (button is not null) button.IsEnabled = false;
        try
        {
            NcViewerWindow.Open(await Presentation.NcViewer.NcViewerRequests.StandaloneAsync(viewModel.AiClient));
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"The NC viewer could not be opened: {exception.Message}", "NC viewer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private void AskAi_Click(object sender, RoutedEventArgs e)
    {
        if (askAiWindow is not null)
        {
            if (askAiWindow.WindowState == WindowState.Minimized) askAiWindow.WindowState = WindowState.Normal;
            askAiWindow.Activate();
            return;
        }
        askAiWindow = new AskAiWindow(new AskAiViewModel(() => viewModel.AiClient, CurrentAiSelection))
        {
            Owner = this,
            Left = Left + Math.Max(0, ActualWidth - 600),
            Top = Top + 90,
        };
        void SessionChanged(object? s, EventArgs a) => askAiWindow?.Close();
        viewModel.AiSessionChanged += SessionChanged;
        askAiWindow.Closed += (_, _) => { viewModel.AiSessionChanged -= SessionChanged; askAiWindow = null; };
        askAiWindow.Show();
    }

    /// <summary>
    /// The selection of the workspace the user is looking at: the Operation workbench, Shop or Timeline when
    /// active, otherwise the Cases tab. The Server re-reads every ID, so a stale one is reported, not trusted.
    /// </summary>
    private AskAiSelection CurrentAiSelection()
    {
        string T(string text) => LocalizationService.Current.Translate(text);
        var active = WorkspaceTabs.SelectedItem;
        var workbench = viewModel.OperationWorkbench;
        if (ReferenceEquals(active, OperationWorkspaceTab) && workbench.Details is { } details)
        {
            var exact = workbench.SelectedContext;
            return new(new AiChatContext(details.CaseId, details.RecipeOperationId, details.BatchId, details.BatchOperationId,
                details.ProcessRevisionId, workbench.Package?.GCodeReleaseId, exact?.MachineId, exact?.ProductionRunId), details.Breadcrumb);
        }
        if (ReferenceEquals(active, ShopWorkspaceTab) && viewModel.Shop.SelectedMachine is { } machine)
        {
            var output = viewModel.Shop.SelectedOutput;
            return new(new AiChatContext(MachineId: machine.MachineId, ProductionRunId: output?.RunId),
                $"{T("Machine")} {machine.Number} {machine.Name}" + (output is null ? "" : " · " + T("Production Run")));
        }
        if (ReferenceEquals(active, TimelineTab) && viewModel.Timeline.SelectedBatch is { } batch)
            return new(new AiChatContext(BatchId: batch.BatchId), $"{batch.PartNumber} → {T("Batch")} {batch.BatchNumber}");
        var cases = viewModel.CaseWorkspace;
        if (cases.SelectedCase is { } selected)
        {
            var operation = cases.SelectedOperation;
            var release = cases.SelectedGCodeRelease;
            var parts = new List<string> { selected.PartNumber };
            if (operation is not null) parts.Add($"OP{operation.OperationNumber} {operation.Name}");
            if (cases.SelectedBatch is { } caseBatch) parts.Add(T("Batch") + " " + caseBatch.BatchNumber);
            if (release is not null) parts.Add($"NC {release.PostprocessorName} r{release.PostSpecificRevision} ({release.OriginalFileName})");
            return new(new AiChatContext(selected.CaseId, operation?.CaseOperationId, cases.SelectedBatch?.BatchId, null,
                cases.ActiveProcessRevision?.ProcessRevisionId, release?.GCodeReleaseId), string.Join(" → ", parts));
        }
        return new(null, T("Nothing selected: general question"));
    }

    private void OpenTimelineWindow_Click(object sender, RoutedEventArgs e)
    {
        if (timelineWindow is not null)
        {
            if (timelineWindow.WindowState == WindowState.Minimized)
            {
                timelineWindow.WindowState = WindowState.Normal;
            }

            timelineWindow.Activate();
            return;
        }

        timelineWindow = new TimelineWindow(viewModel.Timeline)
        {
            Owner = this
        };
        timelineWindow.OperationActionRequested += TimelineOperationActionRequested;
        timelineWindow.Closed += (_, _) => timelineWindow = null;
        timelineWindow.Show();
    }

    private async void PreparationActionRequested(object? sender, PreparationQueueActionRequest request)
    {
        try
        {
            switch (request.Action)
            {
                case "OPEN_OPERATION" when LegacyNavigation.IsChecked != true:
                    await OpenWorkbenchAsync(new(request.Item.CaseId, request.Item.CaseOperationId, null,
                        request.Item.BatchOperationId, request.Item.MachineId, request.Item.Context));
                    break;
                case "OPEN_CASE":
                case "OPEN_OPERATION":
                case "UPLOAD_GCODE":
                    WorkspaceTabs.SelectedIndex = 0;
                    await viewModel.CaseWorkspace.NavigateToOperationAsync(
                        request.Item.CaseId!, request.Action == "OPEN_CASE" ? null : request.Item.CaseOperationId);
                    break;
                case "OPEN_TOOL_TABLE" when request.Payload is byte[] toolBytes:
                    ShowReadOnlyText("Current Tool Table", Encoding.UTF8.GetString(toolBytes));
                    break;
                case "OPEN_TOOL_PREPARATION" when request.Payload is ToolPreparationViewModel toolPreparation:
                    new ToolPreparationWindow(toolPreparation) { Owner = this }.Show();
                    break;
                case "VIEW_NC_READ_ONLY" when request.Payload is NcViewerOpenRequest ncViewer:
                    NcViewerWindow.Open(ncViewer);
                    break;
                case "OPEN_PRODUCTION_PACKAGE" when request.Payload is ProductionPackageInfo package:
                    await ExportReviewedPackageAsync(package);
                    break;
            }
        }
        catch (Exception exception)
        {
            LocalizedMessageBox.Show(this, exception.Message, "Preparation action", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ExportReviewedPackageAsync(ProductionPackageInfo package)
    {
        var picker = new OpenFolderDialog
        {
            Title = $"Export reviewed package #{package.PackageNumber}: {package.ProductionPackageId}",
            Multiselect = false
        }.Localized();
        if (picker.ShowDialog(this) == true)
        {
            await viewModel.SetupQueue.ExportCurrentProductionPackageAsync(package, picker.FolderName);
            Process.Start(new ProcessStartInfo(picker.FolderName) { UseShellExecute = true });
        }
    }

    private void ShowReadOnlyText(string title, string text)
    {
        var viewer = new Window
        {
            Owner = this,
            Title = title,
            Width = 900,
            Height = 650,
            Content = new System.Windows.Controls.TextBox
            {
                Text = text,
                IsReadOnly = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 14,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
            }
        };
        viewer.Show();
    }
}
