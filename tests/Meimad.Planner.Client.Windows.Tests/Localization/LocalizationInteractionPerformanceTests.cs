using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows.Tests.Localization;

// WPF permits one Application per process. ViewStartupTests owns it and invokes this
// audit with the real MainWindow rather than creating a second process-global App.
internal static class LocalizationInteractionPerformanceAudit
{
    private static readonly TimeSpan InteractionBudget = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PhaseBudget = TimeSpan.FromSeconds(8);

    internal static void RunAndAssert(MainWindow window)
    {
        Window? probeWindow = null;
        var originalLanguage = LocalizationService.Current.CurrentLanguage;
        try
        {
            var workspaceTabs = Assert.IsType<TabControl>(window.FindName("WorkspaceTabs"));
            var navigationLabel = Descendants<TextBlock>(window).First(text =>
                LocalizationService.Current.ResolveSource(text.Text) == "Planning Board");

            // New controls must localize at Loaded without recurring background polling.
            LocalizationService.Current.SetLanguage("he", persist: false);
            Flush(window.Dispatcher);
            LocalizationBehavior.ResetDiagnostics();
            var probeText = new TextBlock { Text = "Planning Board" };
            probeWindow = new Window
            {
                Title = "Setup",
                Content = probeText,
                Width = 240,
                Height = 120,
                ShowInTaskbar = false
            };
            probeWindow.Show();
            probeWindow.UpdateLayout();
            Flush(window.Dispatcher);
            var loaded = LocalizationBehavior.Diagnostics;
            Assert.Equal(LocalizationService.Current.Translate("he", "Planning Board"), probeText.Text);
            Assert.Equal(LocalizationService.Current.Translate("he", "Setup"), probeWindow.Title);
            Assert.Equal(FlowDirection.RightToLeft, probeWindow.FlowDirection);
            Assert.True(loaded.ApplyCount >= 2);
            Assert.True(loaded.FullTreePassCount <= 1,
                $"Loading one window required {loaded.FullTreePassCount} localization passes.");
            probeWindow.Close();
            probeWindow = null;
            Flush(window.Dispatcher);

            // Realize every nested page once without charging deferred XAML creation to
            // the measured language-switch phases.
            var warmup = ExerciseAllTabs(window, workspaceTabs, navigationLabel, changeLanguage: false);
            Assert.True(warmup.InteractionCount >= 20,
                $"Only {warmup.InteractionCount} tab pages were exercised.");
            LocalizationService.Current.SetLanguage("en", persist: false);
            Flush(window.Dispatcher);

            LocalizationBehavior.ResetDiagnostics();
            PumpFor(window.Dispatcher, TimeSpan.FromMilliseconds(650));
            var idle = LocalizationBehavior.Diagnostics;
            Assert.Equal(0, idle.ApplyCount);
            Assert.Equal(0, idle.FullTreePassCount);
            Assert.False(idle.PollingEnabled);

            // Rapid selector changes before the next render must be coalesced. One pass per
            // currently open window is the upper bound; only MainWindow is open here.
            LocalizationBehavior.ResetDiagnostics();
            LocalizationService.Current.SetLanguage("he", persist: false);
            LocalizationService.Current.SetLanguage("ru", persist: false);
            LocalizationService.Current.SetLanguage("en", persist: false);
            Flush(window.Dispatcher);
            var coalesced = LocalizationBehavior.Diagnostics;
            var openWindowCount = Application.Current.Windows.Cast<Window>().Count(item => item.IsLoaded);
            Assert.Equal(3, coalesced.LanguageChangeCount);
            Assert.True(coalesced.FullTreePassCount <= Math.Max(1, openWindowCount),
                $"Three coalesced language changes traversed {coalesced.FullTreePassCount} windows; " +
                $"{openWindowCount} loaded window(s) were open.");

            var first = RunMeasuredPass(window, workspaceTabs, navigationLabel);
            var second = RunMeasuredPass(window, workspaceTabs, navigationLabel);

            Assert.Equal(first.Phase.InteractionCount, first.Phase.HeartbeatCount);
            Assert.Equal(second.Phase.InteractionCount, second.Phase.HeartbeatCount);
            Assert.Equal(0, first.Phase.StaleLocalizationCount);
            Assert.Equal(0, second.Phase.StaleLocalizationCount);
            Assert.Equal(0, first.Phase.InvalidSelectionCount);
            Assert.Equal(0, second.Phase.InvalidSelectionCount);
            Assert.Equal(first.Phase.InteractionCount, first.Diagnostics.LanguageChangeCount);
            Assert.Equal(second.Phase.InteractionCount, second.Diagnostics.LanguageChangeCount);
            Assert.True(
                first.Diagnostics.FullTreePassCount <= first.Diagnostics.LanguageChangeCount * openWindowCount,
                $"First pass used {first.Diagnostics.FullTreePassCount} tree passes for " +
                $"{first.Diagnostics.LanguageChangeCount} language changes and " +
                $"{first.Phase.InteractionCount} tab interactions across {openWindowCount} windows.");
            Assert.True(
                second.Diagnostics.FullTreePassCount <= second.Diagnostics.LanguageChangeCount * openWindowCount,
                $"Second pass used {second.Diagnostics.FullTreePassCount} tree passes for " +
                $"{second.Diagnostics.LanguageChangeCount} language changes and " +
                $"{second.Phase.InteractionCount} tab interactions across {openWindowCount} windows.");
            Assert.True(first.Diagnostics.ApplyCount > 0);
            Assert.True(second.Diagnostics.ApplyCount > 0);

            var visitGrowth = Math.Max(100, first.Diagnostics.VisitedObjectCount / 20);
            Assert.True(second.Diagnostics.VisitedObjectCount <= first.Diagnostics.VisitedObjectCount + visitGrowth,
                $"Tree visits grew from {first.Diagnostics.VisitedObjectCount} to " +
                $"{second.Diagnostics.VisitedObjectCount} in an identical pass.");
            var applyGrowth = Math.Max(100, first.Diagnostics.ApplyCount / 20);
            Assert.True(second.Diagnostics.ApplyCount <= first.Diagnostics.ApplyCount + applyGrowth,
                $"Translation writes grew from {first.Diagnostics.ApplyCount} to " +
                $"{second.Diagnostics.ApplyCount} in an identical pass.");

            Assert.True(first.Phase.Elapsed <= PhaseBudget,
                $"First rapid-switch pass took {first.Phase.Elapsed.TotalMilliseconds:N0} ms.");
            Assert.True(second.Phase.Elapsed <= PhaseBudget,
                $"Second rapid-switch pass took {second.Phase.Elapsed.TotalMilliseconds:N0} ms.");
            Assert.True(first.Phase.LongestInteraction <= InteractionBudget,
                $"One first-pass interaction blocked for {first.Phase.LongestInteraction.TotalMilliseconds:N0} ms.");
            Assert.True(second.Phase.LongestInteraction <= InteractionBudget,
                $"One second-pass interaction blocked for {second.Phase.LongestInteraction.TotalMilliseconds:N0} ms.");
        }
        finally
        {
            probeWindow?.Close();
            LocalizationService.Current.SetLanguage(originalLanguage, persist: false);
            Flush(window.Dispatcher);
        }
    }

    private static MeasuredPass RunMeasuredPass(
        MainWindow window,
        TabControl workspaceTabs,
        TextBlock navigationLabel)
    {
        LocalizationService.Current.SetLanguage("en", persist: false);
        Flush(window.Dispatcher);
        LocalizationBehavior.ResetDiagnostics();
        var phase = ExerciseAllTabs(window, workspaceTabs, navigationLabel, changeLanguage: true);
        return new MeasuredPass(phase, LocalizationBehavior.Diagnostics);
    }

    private static PhaseResult ExerciseAllTabs(
        MainWindow window,
        TabControl root,
        TextBlock navigationLabel,
        bool changeLanguage)
    {
        var visited = new HashSet<TabControl>();
        var interactions = 0;
        var heartbeats = 0;
        var stale = 0;
        var invalidSelections = 0;
        var longest = TimeSpan.Zero;
        var phaseWatch = Stopwatch.StartNew();
        Visit(root);
        Flush(window.Dispatcher);
        phaseWatch.Stop();
        return new PhaseResult(interactions, heartbeats, stale, invalidSelections, phaseWatch.Elapsed, longest);

        void Visit(TabControl tabs)
        {
            if (!visited.Add(tabs))
            {
                return;
            }

            var originalIndex = tabs.SelectedIndex;
            for (var index = 0; index < tabs.Items.Count; index++)
            {
                var interactionWatch = Stopwatch.StartNew();
                tabs.SelectedIndex = index;
                var language = LocalizationService.Current.CurrentLanguage;
                if (changeLanguage)
                {
                    language = NextLanguage(language);
                    LocalizationService.Current.SetLanguage(language, persist: false);
                }
                window.UpdateLayout();
                window.Dispatcher.BeginInvoke(
                    new Action(() => heartbeats++),
                    DispatcherPriority.Background);
                Flush(window.Dispatcher);
                interactionWatch.Stop();
                interactions++;
                if (changeLanguage && interactionWatch.Elapsed > longest)
                {
                    longest = interactionWatch.Elapsed;
                }
                if (tabs.SelectedIndex != index)
                {
                    invalidSelections++;
                }

                var expectedDirection = language == "he"
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight;
                if (navigationLabel.Text != LocalizationService.Current.Translate(language, "Planning Board")
                    || window.FlowDirection != expectedDirection)
                {
                    stale++;
                }

                foreach (var nested in Descendants<TabControl>(tabs).ToArray())
                {
                    Visit(nested);
                }
            }
            tabs.SelectedIndex = originalIndex;
        }
    }

    private static string NextLanguage(string current) => current switch
    {
        "en" => "he",
        "he" => "ru",
        _ => "en"
    };

    private static void PumpFor(Dispatcher dispatcher, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Flush(Dispatcher dispatcher) =>
        dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

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

    private sealed record MeasuredPass(PhaseResult Phase, LocalizationDiagnostics Diagnostics);

    private sealed record PhaseResult(
        int InteractionCount,
        int HeartbeatCount,
        int StaleLocalizationCount,
        int InvalidSelectionCount,
        TimeSpan Elapsed,
        TimeSpan LongestInteraction);
}
