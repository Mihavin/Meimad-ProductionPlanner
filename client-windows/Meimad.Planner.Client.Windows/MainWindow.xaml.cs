using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using System.Text;
using Microsoft.Win32;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Configuration;
using Meimad.Planner.Client.Windows.Localization;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly DispatcherTimer refreshTimer;
    private TimelineWindow? timelineWindow;
    private bool hasInitialized;
    private bool refreshInProgress;

    public MainWindow()
    {
        InitializeComponent();
        LanguageSelector.SelectedValue = LocalizationService.Current.CurrentLanguage;
        viewModel = new MainWindowViewModel(
            new ClientSettingsStore(),
            new PlannerApiClientFactory(),
            RequestAssignmentOverrideReason);
        DataContext = viewModel;
        viewModel.NcCreatorQueue.ActionRequested += PreparationActionRequested;
        viewModel.ToolRoomQueue.ActionRequested += PreparationActionRequested;
        viewModel.SetupQueue.ActionRequested += PreparationActionRequested;
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

    private async void PlanningBoardOpenOperationRequested(object? sender, PlanningOperationViewModel operation)
    {
        WorkspaceTabs.SelectedIndex = 0;
        await viewModel.CaseWorkspace.NavigateToOperationAsync(operation.CaseId, operation.CaseOperationId);
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

    private void OnClosed(object? sender, EventArgs e)
    {
        refreshTimer.Stop();
        timelineWindow?.Close();
        viewModel.Dispose();
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

    private async void ToggleEditMode_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.ModeLevel == "editor")
        {
            await viewModel.ReleaseEditAsync();
        }
        else if (viewModel.ModeLevel == "viewer")
        {
            await viewModel.RequestEditAsync();
        }
    }

    private async void PreparationActionRequested(object? sender, PreparationQueueActionRequest request)
    {
        try
        {
            switch (request.Action)
            {
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
                case "VIEW_NC_READ_ONLY" when request.Payload is string ncText:
                    ShowReadOnlyText("Current NC release - read only", ncText);
                    break;
                case "OPEN_PRODUCTION_PACKAGE" when request.Payload is ProductionPackageInfo package:
                    var picker = new OpenFolderDialog
                    {
                        Title = $"Export Production Package {package.ProductionPackageId}",
                        Multiselect = false
                    };
                    if (picker.ShowDialog(this) == true)
                    {
                        await viewModel.SetupQueue.ExportCurrentProductionPackageAsync(package, picker.FolderName);
                        Process.Start(new ProcessStartInfo(picker.FolderName) { UseShellExecute = true });
                    }
                    break;
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Preparation action", MessageBoxButton.OK, MessageBoxImage.Warning);
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
