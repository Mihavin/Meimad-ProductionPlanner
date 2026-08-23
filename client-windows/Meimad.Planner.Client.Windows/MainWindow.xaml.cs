using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
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
        refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        refreshTimer.Tick += RefreshTimerOnTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
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
