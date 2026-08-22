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
        var externalTimelineContainsOnlyGraphAndLegend = false;
        var closedTimelineWasDetached = false;
        var mainViewRemainedVisible = false;
        var serverIndicatorWasCompactAndAccessible = false;
        var mainHeaderHidConnectionText = false;
        var editModeButtonWasCompactAndStateful = false;
        var operationActionsWereCompactPlayerIcons = false;
        var operationRowWasDenseAndComplete = false;
        var assignmentModeActionsWereVisible = false;
        var timelineHadNoGlobalModeSelector = false;
        var oneAssignmentRenderedAsOneCanvasBlock = false;
        var timelineLegendDistinguishedActualHistory = false;
        var timelineLegendExplainedBlankSpace = false;
        var partialPrimaryIntervalsWerePartitioned = false;
        var blockedAssignmentWasInCapacityBand = false;
        var renderedHorizontalPositionsWereSourceDerived = false;
        var blockedIntervalsWereNotOperationEndpoints = false;
        var boundaryBlockedMarkerRendered = false;
        var splitPhasesRenderedInsideOneHost = false;
        var idleIntervalsWereNotRendered = false;
        var anonymousWaitingWasNotRendered = false;
        var nonWorkingCalendarWasBackgroundOnly = false;
        var nonWorkingCalendarWasBehindForeground = false;
        var timeScaleShowHoursAndDaylightBands = false;
        var currentTimeMarkerWasUniqueAndBounded = false;
        var currentTimeTimerStoppedOnUnload = false;
        string? renderedWaitingDescription = null;
        string? timelineStatusAfterClose = null;
        var thread = new Thread(() =>
        {
            App? application = null;
            Window? window = null;
            MainWindow? plannerWindow = null;
            TimelineWindow? timelineWindow = null;
            Window? playerWindow = null;
            Window? timelineRenderWindow = null;
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
                    && rowText.Contains("Mode Manual")
                    && Descendants<Ellipse>(operationCard).Any(ellipse => ellipse.Width <= 8 && ellipse.ToolTip is not null);
                var operationBorder = Assert.IsType<Border>(operationCard);
                var modeItems = operationBorder.ContextMenu!.Items.Cast<MenuItem>().ToArray();
                assignmentModeActionsWereVisible = modeItems.Length == 4
                    && Equals(modeItems[0].Header, "Schedule from delivery date")
                    && Equals(modeItems[1].Header, "Schedule forward")
                    && Equals(modeItems[2].Header, "Set manual mode")
                    && Equals(modeItems[3].Header, "Production readiness...")
                    && modeItems[0].ToolTip?.ToString()?.Contains("existing assignment", StringComparison.Ordinal) == true;

                var renderStart = DateTimeOffset.Parse("2026-08-18T04:00:00Z");
                var renderViewModel = new TimelineViewModel();
                typeof(TimelineViewModel).GetProperty(nameof(TimelineViewModel.HorizonStart))!
                    .SetValue(renderViewModel, renderStart);
                typeof(TimelineViewModel).GetProperty(nameof(TimelineViewModel.HorizonEnd))!
                    .SetValue(renderViewModel, renderStart.AddHours(8));
                typeof(TimelineViewModel).GetProperty(nameof(TimelineViewModel.DisplayTimeZoneId))!
                    .SetValue(renderViewModel, "UTC");
                typeof(TimelineViewModel).GetProperty(nameof(TimelineViewModel.DayStartsAtLocal))!
                    .SetValue(renderViewModel, "06:00");
                typeof(TimelineViewModel).GetProperty(nameof(TimelineViewModel.DayEndsAtLocal))!
                    .SetValue(renderViewModel, "18:00");
                renderViewModel.Machines.Add(new TimelineMachine(
                    "machine-render", "M-9", "Render verifier",
                    [
                        new TimelineInterval(
                            "operation", "machine-render", "op-render", "batch-render",
                            "B-R", "PN-R", 10, "Rendered once", renderStart.AddHours(2),
                            renderStart.AddHours(5), null, PlanningMode: "backward",
                            MachineAssignmentId: "assignment-render",
                            Phases:
                            [
                                new TimelinePhase("setup", renderStart.AddHours(2), renderStart.AddHours(3), "Setup"),
                                new TimelinePhase("qa", renderStart.AddHours(3), renderStart.AddHours(3.5), "QC"),
                                new TimelinePhase("loadunload", renderStart.AddHours(3.5), renderStart.AddHours(4), "Part reload 1"),
                                new TimelinePhase("production", renderStart.AddHours(4), renderStart.AddHours(4.5), "Production 1"),
                                new TimelinePhase("loadunload", renderStart.AddHours(4.5), renderStart.AddHours(4.75), "Part reload 2"),
                                new TimelinePhase("production", renderStart.AddHours(4.75), renderStart.AddHours(5), "Production 2")
                            ]),
                        new TimelineInterval(
                            "operation", "machine-render", "op-render-2", "batch-render",
                            "B-R", "PN-R", 20, "Rendered twice", renderStart.AddHours(3),
                            renderStart.AddHours(6), null, PlanningMode: "forward",
                            MachineAssignmentId: "assignment-render-2"),
                        new TimelineInterval(
                            "waiting", "machine-render", "op-blocked", "batch-render",
                            "B-R", "PN-R", 30, "Blocked operation", renderStart,
                            renderStart.AddHours(3), "Partially overlapping capacity wait.",
                            TimingKind: "blocked", MachineAssignmentId: "assignment-render-blocked"),
                        new TimelineInterval(
                            "waiting", "machine-render", "anonymous-wait", null,
                            null, null, null, "Anonymous capacity wait", renderStart.AddHours(1),
                            renderStart.AddHours(1.5), "Ordinary capacity wait."),
                        new TimelineInterval(
                            "downtime", "machine-render", null, null, null, null, null, null,
                            renderStart.AddHours(4), renderStart.AddHours(6),
                            "Partially overlapping Machine downtime."),
                        new TimelineInterval(
                            "waiting", "machine-render", "op-boundary", "batch-render",
                            "B-R", "PN-R", 30, "Boundary blocked", renderStart.AddHours(8),
                            renderStart.AddHours(8), "Blocked at horizon boundary.",
                            TimingKind: "blocked", MachineAssignmentId: "assignment-boundary"),
                        new TimelineInterval(
                            "waiting", "machine-render", "op-boundary-2", "batch-render",
                            "B-R", "PN-R", 31, "Boundary blocked 2", renderStart.AddHours(8),
                            renderStart.AddHours(8), "Second blocked marker at horizon boundary.",
                            TimingKind: "blocked", MachineAssignmentId: "assignment-boundary-2"),
                        new TimelineInterval(
                            "idle", "machine-render", null, null, null, null, null, null,
                            renderStart, renderStart.AddHours(8), "Available time")
                    ],
                    NonWorkingWindows:
                    [
                        new TimelineNonWorkingWindow(
                            renderStart.AddHours(1), renderStart.AddHours(2),
                            "Machine calendar: non-working time.")
                    ]));
                var renderTimeline = new TimelineView(() => renderStart.AddHours(4))
                {
                    DataContext = renderViewModel
                };
                Assert.False(TimelineView.IsDefaultTimelineIntervalVisible(
                    renderViewModel.Machines[0].Intervals.Single(interval => interval.OperationId == "anonymous-wait")));
                timelineRenderWindow = new Window { Content = renderTimeline, Width = 1000, Height = 700 };
                timelineRenderWindow.Show();
                timelineRenderWindow.UpdateLayout();
                timelineRenderWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                oneAssignmentRenderedAsOneCanvasBlock =
                    renderTimeline.RenderedMachineAssignmentIds.Count(value => value == "assignment-render") == 1;
                var renderedIntervalBlocks = Descendants<Border>(renderTimeline)
                    .Where(border => border.Tag is TimelineInterval)
                    .ToArray();
                idleIntervalsWereNotRendered = !renderedIntervalBlocks.Any(border =>
                    border.Tag is TimelineInterval { Type: "idle" });
                anonymousWaitingWasNotRendered = !renderedIntervalBlocks.Any(border =>
                    border.Tag is TimelineInterval { Type: "waiting", OperationId: "anonymous-wait" });
                renderedWaitingDescription = string.Join(" | ", renderedIntervalBlocks
                    .Select(border => border.Tag is TimelineInterval interval
                        ? $"{interval.Type}:{interval.OperationId}:{interval.MachineAssignmentId}:{interval.TimingKind}"
                        : "non-interval"));
                var calendarBackgrounds = Descendants<Border>(renderTimeline)
                    .Where(border => border.Tag is null
                        && border.ToolTip is string tooltip
                        && tooltip.Contains("NON-WORKING CALENDAR", StringComparison.Ordinal))
                    .ToArray();
                var timeScaleBands = Descendants<Border>(renderTimeline)
                    .Where(border => border.Tag is null
                        && border.ToolTip is string tooltip
                        && (tooltip.Contains("DAYLIGHT HOURS", StringComparison.Ordinal)
                            || tooltip.Contains("DARK HOURS", StringComparison.Ordinal)))
                    .ToArray();
                var timeScaleLabels = Descendants<TextBlock>(renderTimeline)
                    .Where(textBlock => textBlock.Text == "04")
                    .ToArray();
                timeScaleShowHoursAndDaylightBands =
                    timeScaleBands.Any(border => border.ToolTip is string tooltip
                        && tooltip.Contains("DAYLIGHT HOURS", StringComparison.Ordinal))
                    && timeScaleBands.Any(border => border.ToolTip is string tooltip
                        && tooltip.Contains("DARK HOURS", StringComparison.Ordinal))
                    && timeScaleBands.All(border => Canvas.GetTop(border) == 0
                        && border.Height == 42
                        && border.Tag is not TimelineInterval)
                    && timeScaleLabels.Length > 0
                    && timeScaleBands.All(border => border.ToolTip is string tooltip
                        && (tooltip.Contains("DAYLIGHT HOURS", StringComparison.Ordinal)
                            || tooltip.Contains("DARK HOURS", StringComparison.Ordinal)));
                var timelineCanvas = Descendants<Canvas>(renderTimeline)
                    .First(canvas => canvas.Width > 1000);
                var markerUpdate = typeof(TimelineView).GetMethod(
                    "UpdateCurrentTimeMarkerFromClock",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                markerUpdate.Invoke(renderTimeline, null);
                markerUpdate.Invoke(renderTimeline, null);
                var currentTimeMarkers = Descendants<Canvas>(renderTimeline)
                    .Where(canvas => canvas.Uid == "CurrentTimeMarker")
                    .ToArray();
                var currentTimeMarker = Assert.Single(currentTimeMarkers);
                var currentTimeBadge = Assert.Single(currentTimeMarker.Children.OfType<Border>());
                var chartStart = TimelineView.LabelWidthForTests;
                var chartEnd = chartStart + (timelineCanvas.Width - chartStart - 18);
                var badgeLeft = Canvas.GetLeft(currentTimeBadge);
                var badgeWidth = currentTimeBadge.DesiredSize.Width;
                currentTimeMarkerWasUniqueAndBounded = currentTimeMarker.Children
                        .OfType<FrameworkElement>()
                        .All(element => element.Tag is not TimelineInterval)
                    && badgeLeft >= chartStart
                    && badgeLeft + badgeWidth <= chartEnd + 0.1
                    && currentTimeBadge.Child is TextBlock { Text: var nowText }
                    && nowText.StartsWith("NOW ", StringComparison.Ordinal);
                var assignmentBlock = Assert.Single(renderedIntervalBlocks, border =>
                    border.Tag is TimelineInterval { MachineAssignmentId: "assignment-render" });
                var secondAssignmentBlock = Assert.Single(renderedIntervalBlocks, border =>
                    border.Tag is TimelineInterval { MachineAssignmentId: "assignment-render-2" });
                var blockedAssignmentBlock = Assert.Single(renderedIntervalBlocks, border =>
                    border.Tag is TimelineInterval { MachineAssignmentId: "assignment-render-blocked" });
                nonWorkingCalendarWasBackgroundOnly = calendarBackgrounds.Length == 1
                    && calendarBackgrounds[0].Tag is null
                    && calendarBackgrounds[0].Background is SolidColorBrush calendarBrush
                    && calendarBrush.Color == Color.FromArgb(224, 224, 228, 232)
                    && calendarBackgrounds[0].Width > 0
                    && calendarBackgrounds[0].Height == TimelineView.CompactRowHeight
                    && calendarBackgrounds[0].ToolTip is string calendarTooltip
                    && calendarTooltip.Contains("configured working calendar", StringComparison.OrdinalIgnoreCase)
                    && !renderedIntervalBlocks.Contains(calendarBackgrounds[0]);
                var backgroundIndex = timelineCanvas.Children.IndexOf(calendarBackgrounds[0]);
                var foregroundIndex = timelineCanvas.Children.IndexOf(assignmentBlock);
                nonWorkingCalendarWasBehindForeground = backgroundIndex >= 0
                    && foregroundIndex >= 0
                    && backgroundIndex < foregroundIndex;
                var assignmentBottom = Canvas.GetTop(assignmentBlock) + assignmentBlock.Height;
                if (assignmentBlock.Child is Canvas phaseCanvas)
                {
                    var phaseBorders = phaseCanvas.Children.OfType<Border>().ToArray();
                    var reloadBorders = phaseBorders
                        .Where(phase => phase.ToolTip?.ToString()?.StartsWith("PART RELOAD:", StringComparison.Ordinal) == true)
                        .ToArray();
                    splitPhasesRenderedInsideOneHost = phaseBorders.Length == 6
                        && phaseBorders.All(phase => phase.Tag is null)
                        && phaseBorders.Select(phase => ((SolidColorBrush)phase.Background).Color).ToHashSet().SetEquals(
                            [Color.FromRgb(251, 192, 45), Color.FromRgb(67, 160, 71), Color.FromRgb(123, 31, 162), Color.FromRgb(30, 136, 229)])
                        && phaseBorders.Any(phase => phase.ToolTip?.ToString() == "SETUP: Setup")
                        && phaseBorders.Any(phase => phase.ToolTip?.ToString() == "QC: QC")
                        && reloadBorders.Length == 2
                        && reloadBorders[0].ToolTip?.ToString() == "PART RELOAD: Part reload 1"
                        && reloadBorders[1].ToolTip?.ToString() == "PART RELOAD: Part reload 2"
                        && Canvas.GetLeft(reloadBorders[0]) < Canvas.GetLeft(reloadBorders[1])
                        && phaseBorders.Any(phase => phase.ToolTip?.ToString() == "PRODUCTION: Production 1")
                        && phaseBorders.Any(phase => phase.ToolTip?.ToString() == "PRODUCTION: Production 2")
                        && phaseBorders.Select(phase => ((SolidColorBrush)phase.Background).Color).Distinct().Count() == 4;
                }
                partialPrimaryIntervalsWerePartitioned =
                    Canvas.GetTop(assignmentBlock) != Canvas.GetTop(secondAssignmentBlock);
                renderedHorizontalPositionsWereSourceDerived =
                    Math.Abs(Canvas.GetLeft(assignmentBlock) - 410) < 0.1
                    && Math.Abs(Canvas.GetLeft(secondAssignmentBlock) - 522.5) < 0.1
                    && assignmentBlock.Tag is TimelineInterval first
                    && secondAssignmentBlock.Tag is TimelineInterval second
                    && first.StartsAt == renderStart.AddHours(2)
                    && first.EndsAt == renderStart.AddHours(5)
                    && second.StartsAt == renderStart.AddHours(3)
                    && second.EndsAt == renderStart.AddHours(6);
                blockedAssignmentWasInCapacityBand =
                    Canvas.GetTop(blockedAssignmentBlock) >= assignmentBottom
                    && blockedAssignmentBlock.Height <= TimelineView.CapacityLaneHeight
                    && blockedAssignmentBlock.Child is Canvas blockedCanvas
                    && blockedCanvas.Children.OfType<TextBlock>().Single().Text.Contains("BLOCKED", StringComparison.Ordinal)
                    && blockedCanvas.Children.OfType<TextBlock>().Single().Text.Contains("OP30", StringComparison.Ordinal);
                blockedIntervalsWereNotOperationEndpoints =
                    renderedIntervalBlocks
                        .Where(border => border.Tag is TimelineInterval { MachineAssignmentId: "assignment-render-blocked" })
                        .All(border => border.Tag is TimelineInterval interval
                            && !TimelineView.IsOperationWorkInterval(interval));
                var boundaryBlock = Assert.Single(renderedIntervalBlocks, border =>
                    border.Tag is TimelineInterval { MachineAssignmentId: "assignment-boundary" });
                var boundaryBlock2 = Assert.Single(renderedIntervalBlocks, border =>
                    border.Tag is TimelineInterval { MachineAssignmentId: "assignment-boundary-2" });
                boundaryBlockedMarkerRendered =
                    boundaryBlock.Tag is TimelineInterval boundary
                    && boundary.StartsAt == renderStart.AddHours(8)
                    && boundary.EndsAt == renderStart.AddHours(8)
                    && boundary.IsBlocked
                    && TimelineView.IsBoundaryBlockedMarker(
                        boundary, renderViewModel.HorizonStart, renderViewModel.HorizonEnd)
                    && boundaryBlock.Width == 8
                    && Canvas.GetLeft(boundaryBlock) >= 1077
                    && Canvas.GetLeft(boundaryBlock) <= 1077.1
                    && Canvas.GetTop(boundaryBlock) >= assignmentBottom
                    && boundaryBlock2.Tag is TimelineInterval boundary2
                    && boundary2.StartsAt == renderStart.AddHours(8)
                    && boundary2.EndsAt == renderStart.AddHours(8)
                    && boundary2.IsBlocked
                    && boundaryBlock2.Width == 8
                    && Canvas.GetLeft(boundaryBlock2) >= 1077
                    && Canvas.GetLeft(boundaryBlock2) <= 1077.1
                    && Canvas.GetTop(boundaryBlock2) >= assignmentBottom
                    && Canvas.GetTop(boundaryBlock) != Canvas.GetTop(boundaryBlock2)
                    && boundaryBlock.Child is Canvas boundaryCanvas
                    && boundaryBlock2.Child is Canvas boundaryCanvas2
                    && boundaryCanvas.Children.OfType<TextBlock>().Single().Text.Contains("BLOCKED", StringComparison.Ordinal)
                    && boundaryCanvas.Children.OfType<TextBlock>().Single().Text.Contains("OP30", StringComparison.Ordinal)
                    && boundaryCanvas2.Children.OfType<TextBlock>().Single().Text.Contains("BLOCKED", StringComparison.Ordinal)
                    && boundaryCanvas2.Children.OfType<TextBlock>().Single().Text.Contains("OP31", StringComparison.Ordinal);
                var actualHistoryLegendText = Assert.Single(
                    Descendants<TextBlock>(renderTimeline),
                    textBlock => textBlock.Text == "ACTUAL HISTORY");
                var actualHistoryLegend = Assert.IsType<StackPanel>(
                    VisualTreeHelper.GetParent(actualHistoryLegendText));
                var actualHistorySwatch = Assert.Single(Descendants<Border>(actualHistoryLegend));
                var actualHistoryLegendBrush = Assert.IsType<SolidColorBrush>(
                    actualHistorySwatch.Background);
                timelineLegendDistinguishedActualHistory =
                    actualHistoryLegendBrush.Color == Color.FromRgb(0, 137, 123)
                    && !Descendants<TextBlock>(renderTimeline)
                        .Any(textBlock => textBlock.Text == "ANNOTATION")
                    || Descendants<TextBlock>(renderTimeline)
                        .Any(textBlock => textBlock.Text == "ACTUAL HISTORY");
                var blankSpaceLegendText = Assert.Single(
                    Descendants<TextBlock>(renderTimeline),
                    textBlock => textBlock.Text == "BLANK = NO OPERATION");
                var blankSpaceLegend = Assert.IsType<StackPanel>(
                    VisualTreeHelper.GetParent(blankSpaceLegendText));
                var blankSpaceSwatch = Assert.Single(Descendants<Border>(blankSpaceLegend));
                timelineLegendExplainedBlankSpace =
                    blankSpaceLegend.ToolTip is string tooltip
                    && tooltip.Contains("ordinary calculated waiting", StringComparison.OrdinalIgnoreCase)
                    && blankSpaceSwatch.Background is SolidColorBrush swatchBrush
                    && swatchBrush.Color == Color.FromRgb(250, 250, 250)
                    && blankSpaceSwatch.BorderBrush is SolidColorBrush borderBrush
                    && borderBrush.Color == Color.FromRgb(158, 158, 158);

                renderTimeline.RaiseEvent(new RoutedEventArgs(UserControl.UnloadedEvent));
                timelineRenderWindow.Close();
                timelineRenderWindow = null;
                currentTimeTimerStoppedOnUnload = typeof(TimelineView)
                    .GetField("currentTimeTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(renderTimeline) is null;

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
                timelineHadNoGlobalModeSelector = !Descendants<ComboBox>(timelineWindow).Any(combo =>
                    AutomationProperties.GetName(combo) == "Timeline projection mode");
                timelineWindowWasReadOnlyAndSecondMonitorReady = timelineWindow.ResizeMode == ResizeMode.CanResizeWithGrip
                    && timelineWindow.ShowInTaskbar
                    && Descendants<UIElement>(timelineWindow).All(element => !element.AllowDrop);
                var externalTimelineView = Descendants<TimelineView>(timelineWindow).Single();
                externalTimelineContainsOnlyGraphAndLegend = externalTimelineView.FindName("TimelineHeaderPanel") is FrameworkElement header
                    && header.Visibility == Visibility.Collapsed
                    && externalTimelineView.FindName("TimelineDetailsPanel") is FrameworkElement details
                    && details.Visibility == Visibility.Collapsed
                    && externalTimelineView.FindName("TimelineLegendPanel") is FrameworkElement legend
                    && legend.Visibility == Visibility.Visible
                    && externalTimelineView.FindName("TimelineGraphPanel") is FrameworkElement graph
                    && graph.Visibility == Visibility.Visible;

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
                timelineRenderWindow?.Close();
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
        Assert.True(externalTimelineContainsOnlyGraphAndLegend);
        Assert.True(closedTimelineWasDetached);
        Assert.True(mainViewRemainedVisible);
        Assert.True(serverIndicatorWasCompactAndAccessible);
        Assert.True(mainHeaderHidConnectionText);
        Assert.True(editModeButtonWasCompactAndStateful);
        Assert.True(operationActionsWereCompactPlayerIcons);
        Assert.True(operationRowWasDenseAndComplete);
        Assert.True(assignmentModeActionsWereVisible);
        Assert.True(timelineHadNoGlobalModeSelector);
        Assert.True(oneAssignmentRenderedAsOneCanvasBlock);
        Assert.True(timelineLegendDistinguishedActualHistory);
        Assert.True(timelineLegendExplainedBlankSpace);
        Assert.True(partialPrimaryIntervalsWerePartitioned);
        Assert.True(blockedAssignmentWasInCapacityBand);
        Assert.True(renderedHorizontalPositionsWereSourceDerived);
        Assert.True(blockedIntervalsWereNotOperationEndpoints);
        Assert.True(boundaryBlockedMarkerRendered);
        Assert.True(splitPhasesRenderedInsideOneHost);
        Assert.True(idleIntervalsWereNotRendered);
        Assert.True(anonymousWaitingWasNotRendered,
            $"Anonymous waiting was rendered in the Timeline visual tree: {renderedWaitingDescription}");
        Assert.True(nonWorkingCalendarWasBackgroundOnly);
        Assert.True(nonWorkingCalendarWasBehindForeground);
        Assert.True(timeScaleShowHoursAndDaylightBands);
        Assert.True(currentTimeMarkerWasUniqueAndBounded);
        Assert.True(currentTimeTimerStoppedOnUnload);
        Assert.Contains("plan changed", timelineStatusAfterClose, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_readiness_dialog_opens_with_textual_blockers_and_read_only_material()
    {
        Exception? startupException = null;
        var rendered = false;
        var thread = new Thread(() =>
        {
            ProductionReadinessDialog? dialog = null;
            try
            {
                var readiness = new PlannerProductionReadiness(
                    "NOT_READY", false, true, "Not ready: 2 blocking component(s)",
                    [
                        new("material", "Material", "UNVERIFIED", "Material is not confirmed.", true),
                        new("toolOffsets", "Tool Offsets", "MISSING", "Offsets are missing.", true)
                    ],
                    "release-1", false,
                    [new("release-1", "process-1", "post-1", "Doosan 3X", "program.nc", 1)]);
                dialog = new ProductionReadinessDialog("PN-1 / B-1 / OP10", readiness);
                dialog.Show();
                dialog.UpdateLayout();
                rendered = dialog.Components.Items.Count == 2
                    && dialog.OffsetsReady is not null
                    && dialog.Release.Items.Count == 1;
            }
            catch (Exception exception)
            {
                startupException = exception;
            }
            finally
            {
                dialog?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The readiness dialog startup check timed out.");
        Assert.Null(startupException);
        Assert.True(rendered);
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
