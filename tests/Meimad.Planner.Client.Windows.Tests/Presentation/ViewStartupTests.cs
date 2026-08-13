using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class ViewStartupTests
{
    [Fact]
    public void Wpf_views_start_and_separate_timeline_reuses_shared_live_projection()
    {
        Exception? startupException = null;
        IReadOnlyList<string> renderedText = [];
        var timelineSharesViewModel = false;
        var timelineWindowWasReused = false;
        var timelineWindowReopenedAfterClose = false;
        var timelineWindowWasReadOnlyAndSecondMonitorReady = false;
        var closedTimelineWasDetached = false;
        var mainViewRemainedVisible = false;
        var serverIndicatorWasCompactAndAccessible = false;
        var mainHeaderHidConnectionText = false;
        var editModeButtonWasCompactAndStateful = false;
        var operationActionsWereCompactPlayerIcons = false;
        var operationRowWasDenseAndComplete = false;
        var backwardContextActionWasReadOnlyAndVisible = false;
        var timelineModeSelectorWasSharedAndAccessible = false;
        string? timelineStatusAfterClose = null;
        var thread = new Thread(() =>
        {
            App? application = null;
            Window? window = null;
            MainWindow? plannerWindow = null;
            TimelineWindow? timelineWindow = null;
            Window? playerWindow = null;
            try
            {
                application = new App
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                application.InitializeComponent();
                var thumbnail = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);
                window = new Window
                {
                    Content = new CaseWorkspaceView
                    {
                        DataContext = new PreviewCaseWorkspace(
                            [new PreviewCaseItem(thumbnail, "Preview available")])
                    }
                };

                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    DispatcherPriority.ApplicationIdle);
                renderedText = Descendants<TextBlock>(window)
                    .Select(value => value.Text)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();

                plannerWindow = new MainWindow();
                var loadedMethod = typeof(MainWindow).GetMethod(
                    "OnLoaded",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    binder: null,
                    types: [typeof(object), typeof(RoutedEventArgs)],
                    modifiers: null)!;
                plannerWindow.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(
                    typeof(RoutedEventHandler),
                    plannerWindow,
                    loadedMethod);
                plannerWindow.Show();
                plannerWindow.UpdateLayout();

                var plannerViewModel = Assert.IsType<MainWindowViewModel>(plannerWindow.DataContext);
                var serverIndicator = Assert.IsType<Ellipse>(plannerWindow.FindName("ServerStatusIndicator"));
                var serverIndicatorHost = Assert.IsAssignableFrom<FrameworkElement>(
                    VisualTreeHelper.GetParent(serverIndicator));
                serverIndicatorWasCompactAndAccessible = serverIndicator.Width <= 12
                    && serverIndicator.Height <= 12
                    && AutomationProperties.GetName(serverIndicator) == plannerViewModel.HealthHeadline
                    && AutomationProperties.GetHelpText(serverIndicator) == plannerViewModel.HealthDetail
                    && serverIndicatorHost.ToolTip is not null;
                var mainWindowText = Descendants<TextBlock>(plannerWindow)
                    .Select(value => value.Text)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
                mainHeaderHidConnectionText = !mainWindowText.Contains(plannerViewModel.HealthHeadline)
                    && !mainWindowText.Any(value => value.StartsWith("Local user:", StringComparison.Ordinal));

                var editModeButton = Assert.IsType<Button>(plannerWindow.FindName("EditModeToggleButton"));
                var lockedIcon = Assert.IsType<Grid>(plannerWindow.FindName("LockedIcon"));
                var unlockedIcon = Assert.IsType<Grid>(plannerWindow.FindName("UnlockedIcon"));
                var modeLevelProperty = typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.ModeLevel))!;
                modeLevelProperty.SetValue(plannerViewModel, "viewer");
                plannerWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                var viewerStateWasLocked = editModeButton.IsEnabled
                    && AutomationProperties.GetName(editModeButton) == "Request Edit Mode"
                    && lockedIcon.Visibility == Visibility.Visible
                    && unlockedIcon.Visibility == Visibility.Collapsed;
                modeLevelProperty.SetValue(plannerViewModel, "editor");
                plannerWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                var editorStateWasUnlocked = editModeButton.IsEnabled
                    && AutomationProperties.GetName(editModeButton) == "Release Edit Mode"
                    && lockedIcon.Visibility == Visibility.Collapsed
                    && unlockedIcon.Visibility == Visibility.Visible;
                editModeButtonWasCompactAndStateful = editModeButton.Width <= 30
                    && editModeButton.Height <= 30
                    && editModeButton.ToolTip is not null
                    && Descendants<System.Windows.Shapes.Path>(editModeButton).Count() == 2
                    && viewerStateWasLocked
                    && editorStateWasUnlocked;

                var boardView = new MachinePlanningBoardView();
                var operationTemplate = Assert.IsType<DataTemplate>(
                    boardView.Resources["CompactOperationRowTemplate"]);
                var operationCard = Assert.IsAssignableFrom<FrameworkElement>(
                    operationTemplate.LoadContent());
                operationCard.DataContext = new PlanningOperationViewModel(new PlanningBoardOperation(
                    "op-player", "batch-player", "B-1", "case-player", "PN-1", 10,
                    "Milling", "mill", 60, 30, "in_progress", "machine-1", 0,
                    4, ["SO-1"], 180) with { CaseName = "Widget" });
                playerWindow = new Window { Content = operationCard, SizeToContent = SizeToContent.WidthAndHeight };
                playerWindow.Show();
                playerWindow.UpdateLayout();
                var playerButtons = Descendants<Button>(operationCard).ToArray();
                operationActionsWereCompactPlayerIcons = playerButtons.Length == 4
                    && playerButtons.All(button => button.Width <= 24 && button.MinHeight <= 21)
                    && playerButtons.All(button => button.ToolTip is not null)
                    && playerButtons.All(button => Descendants<System.Windows.Shapes.Path>(button).Any())
                    && playerButtons.Single(button => AutomationProperties.GetName(button) == "Start operation").IsEnabled == false
                    && playerButtons.Single(button => AutomationProperties.GetName(button) == "Pause operation").IsEnabled == false
                    && playerButtons.Single(button => AutomationProperties.GetName(button) == "Reset operation").IsEnabled == false;
                var rowText = Descendants<TextBlock>(operationCard).Select(text => text.Text).ToArray();
                var operationThumbnail = Assert.Single(Descendants<Image>(operationCard));
                var thumbnailHost = Assert.IsType<Border>(VisualTreeHelper.GetParent(operationThumbnail));
                operationRowWasDenseAndComplete = operationCard.ActualHeight <= 60
                    && thumbnailHost.Width <= 38 && thumbnailHost.Height <= 38
                    && !Descendants<TextBlock>(thumbnailHost).Any()
                    && rowText.Contains("PN-1 / Widget")
                    && rowText.Contains("OP10 Milling")
                    && rowText.Contains("B-1 / SO-1")
                    && rowText.Contains("Qty 4")
                    && rowText.Contains("Time 00:03:00")
                    && Descendants<Ellipse>(operationCard).Any(ellipse => ellipse.Width <= 8 && ellipse.ToolTip is not null);
                var operationBorder = Assert.IsType<Border>(operationCard);
                var backwardItem = Assert.IsType<MenuItem>(Assert.Single(operationBorder.ContextMenu!.Items));
                backwardContextActionWasReadOnlyAndVisible =
                    Equals(backwardItem.Header, "View backward from delivery date")
                    && backwardItem.ToolTip?.ToString()?.Contains("visual-only", StringComparison.Ordinal) == true;

                var openMethod = typeof(MainWindow).GetMethod(
                    "OpenTimelineWindow_Click",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var timelineField = typeof(MainWindow).GetField(
                    "timelineWindow",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                openMethod.Invoke(plannerWindow, [plannerWindow, new RoutedEventArgs()]);
                timelineWindow = Assert.IsType<TimelineWindow>(timelineField.GetValue(plannerWindow));
                timelineWindow.Dispatcher.Invoke(
                    () => { },
                    DispatcherPriority.ApplicationIdle);
                var timelineViewModel = plannerViewModel.Timeline;
                timelineSharesViewModel = ReferenceEquals(timelineViewModel, timelineWindow.DataContext)
                    && ReferenceEquals(
                        timelineViewModel,
                        Descendants<TimelineView>(timelineWindow).Single().DataContext);
                var modeSelector = Descendants<ComboBox>(timelineWindow).Single(combo =>
                    AutomationProperties.GetName(combo) == "Timeline projection mode");
                timelineModeSelectorWasSharedAndAccessible = modeSelector.Items.Count == 2
                    && ReferenceEquals(modeSelector.SelectedItem, timelineViewModel.SelectedPlanningMode)
                    && timelineViewModel.PlanningModeBanner.Contains("stored Machine backlog order", StringComparison.OrdinalIgnoreCase);
                timelineWindowWasReadOnlyAndSecondMonitorReady = timelineWindow.ResizeMode == ResizeMode.CanResizeWithGrip
                    && timelineWindow.ShowInTaskbar
                    && Descendants<UIElement>(timelineWindow).All(element => !element.AllowDrop);

                openMethod.Invoke(plannerWindow, [plannerWindow, new RoutedEventArgs()]);
                timelineWindowWasReused = ReferenceEquals(
                    timelineWindow,
                    timelineField.GetValue(plannerWindow));
                var firstWindow = timelineWindow;
                timelineWindow.Close();
                closedTimelineWasDetached = firstWindow.DataContext is null
                    && Descendants<TimelineView>(firstWindow).All(view => view.DataContext is null);
                timelineViewModel.Invalidate();
                timelineStatusAfterClose = timelineViewModel.StatusMessage;
                mainViewRemainedVisible = plannerWindow.IsVisible;

                openMethod.Invoke(plannerWindow, [plannerWindow, new RoutedEventArgs()]);
                timelineWindow = Assert.IsType<TimelineWindow>(timelineField.GetValue(plannerWindow));
                timelineWindowReopenedAfterClose = !ReferenceEquals(firstWindow, timelineWindow)
                    && ReferenceEquals(timelineViewModel, timelineWindow.DataContext);
            }
            catch (Exception exception)
            {
                startupException = exception;
            }
            finally
            {
                if (timelineWindow?.IsVisible == true)
                {
                    timelineWindow.Close();
                }

                plannerWindow?.Close();
                playerWindow?.Close();
                window?.Close();
                application?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The WPF startup check timed out.");
        Assert.Null(startupException);
        Assert.DoesNotContain("Preview available", renderedText);
        Assert.True(timelineSharesViewModel);
        Assert.True(timelineWindowWasReused);
        Assert.True(timelineWindowReopenedAfterClose);
        Assert.True(timelineWindowWasReadOnlyAndSecondMonitorReady);
        Assert.True(closedTimelineWasDetached);
        Assert.True(mainViewRemainedVisible);
        Assert.True(serverIndicatorWasCompactAndAccessible);
        Assert.True(mainHeaderHidConnectionText);
        Assert.True(editModeButtonWasCompactAndStateful);
        Assert.True(operationActionsWereCompactPlayerIcons);
        Assert.True(operationRowWasDenseAndComplete);
        Assert.True(backwardContextActionWasReadOnlyAndVisible);
        Assert.True(timelineModeSelectorWasSharedAndAccessible);
        Assert.Contains("plan changed", timelineStatusAfterClose, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed record PreviewCaseWorkspace(IReadOnlyList<PreviewCaseItem> Cases)
    {
        public string CurrentSetupTime => "00:00:00";

        public string CurrentCycleTimePerPart => "00:00:00";
    }

    private sealed record PreviewCaseItem(ImageSource Thumbnail, string PreviewStatus);
}
