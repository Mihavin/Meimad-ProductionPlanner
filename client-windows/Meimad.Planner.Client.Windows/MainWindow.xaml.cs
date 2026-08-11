using System.Windows;
using System.Windows.Threading;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Configuration;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly DispatcherTimer refreshTimer;

    public MainWindow()
    {
        InitializeComponent();
        viewModel = new MainWindowViewModel(
            new ClientSettingsStore(),
            new PlannerApiClientFactory());
        DataContext = viewModel;
        refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        refreshTimer.Tick += RefreshTimerOnTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
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
        viewModel.Dispose();
    }
}
