using System.Windows;
using System.Windows.Threading;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Configuration;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly DispatcherTimer refreshTimer;
    private TimelineWindow? timelineWindow;

    public MainWindow()
    {
        InitializeComponent();
        viewModel = new MainWindowViewModel(
            new ClientSettingsStore(),
            new PlannerApiClientFactory(),
            RequestAssignmentOverrideReason);
        DataContext = viewModel;
        viewModel.TimelineViewRequested += (_, _) =>
            WorkspaceTabs.SelectedItem = TimelineTab;
        refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        refreshTimer.Tick += RefreshTimerOnTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private string? RequestAssignmentOverrideReason(AssignmentOverridePrompt prompt)
    {
        var dialog = new AssignmentOverrideDialog(prompt) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Reason : null;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await viewModel.InitializeAsync();
        refreshTimer.Start();
    }

    private async void RefreshTimerOnTick(object? sender, EventArgs e)
    {
        await viewModel.RefreshAsync();
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
}
