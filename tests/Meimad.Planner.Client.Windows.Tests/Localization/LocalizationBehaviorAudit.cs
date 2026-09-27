using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows.Tests.Localization;

// WPF permits one Application per process. ViewStartupTests owns it and invokes this audit
// with the real MainWindow. The audit never forces a synchronous layout pass: the application
// does not force one either, and a page must be localized by the time the dispatcher is idle.
internal static class LocalizationBehaviorAudit
{
    internal static void RunAndAssert(MainWindow window)
    {
        var originalLanguage = LocalizationService.Current.CurrentLanguage;
        var workspaceTabs = Assert.IsType<TabControl>(window.FindName("WorkspaceTabs"));
        var originalIndex = workspaceTabs.SelectedIndex;
        try
        {
            AssertContentRealizedAfterLoad(window.Dispatcher);

            // Pages hidden during a language change catch up when they are shown.
            foreach (var language in new[] { "he", "ru" })
            {
                workspaceTabs.SelectedIndex = 0;
                Flush(window.Dispatcher);
                LocalizationService.Current.SetLanguage(language, persist: false);
                Flush(window.Dispatcher);
                var failures = new SortedSet<string>(StringComparer.Ordinal);
                VisitTabs(workspaceTabs, language, failures, new HashSet<TabControl>());
                Assert.True(
                    failures.Count == 0,
                    $"Pages shown after switching to '{language}' kept catalog text untranslated:{Environment.NewLine}" +
                    string.Join(Environment.NewLine, failures.Take(40)));
            }
        }
        finally
        {
            workspaceTabs.SelectedIndex = originalIndex;
            LocalizationService.Current.SetLanguage(originalLanguage, persist: false);
            Flush(window.Dispatcher);
        }
    }

    private static void AssertContentRealizedAfterLoad(Dispatcher dispatcher)
    {
        LocalizationService.Current.SetLanguage("he", persist: false);
        Flush(dispatcher);

        var hiddenPageText = new TextBlock { Text = "Timeline" };
        var firstPage = new StackPanel();
        var releaseTarget = new ReleaseTarget { Name = "Haas NGC" };
        var labelRun = new Run("Release target: ");
        var nameRun = new Run();
        BindingOperations.SetBinding(nameRun, Run.TextProperty, new Binding(nameof(ReleaseTarget.Name)));
        var composedText = new TextBlock { DataContext = releaseTarget };
        composedText.Inlines.Add(labelRun);
        composedText.Inlines.Add(nameRun);
        var menuItem = new MenuItem { Header = "Open operation" };
        var menuOwner = new Button { Content = "Refresh" };
        menuOwner.ContextMenu = new ContextMenu { Items = { menuItem }, PlacementTarget = menuOwner };
        firstPage.Children.Add(composedText);
        firstPage.Children.Add(menuOwner);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Cases", Content = firstPage });
        tabs.Items.Add(new TabItem { Header = "Setup", Content = new StackPanel { Children = { hiddenPageText } } });
        var probeWindow = new Window
        {
            Title = "Setup",
            Content = tabs,
            Width = 480,
            Height = 260,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        try
        {
            probeWindow.Show();
            Flush(dispatcher);
            Assert.Equal(Translate("he", "Setup"), probeWindow.Title);

            // Plain text realized after the window loaded never receives Loaded.
            tabs.SelectedIndex = 1;
            Flush(dispatcher);
            Assert.Equal(Translate("he", "Timeline"), hiddenPageText.Text);

            tabs.SelectedIndex = 0;
            Flush(dispatcher);
            var addedText = new TextBlock { Text = "Tool Room" };
            firstPage.Children.Add(addedText);
            Flush(dispatcher);
            Assert.Equal(Translate("he", "Tool Room"), addedText.Text);
            addedText.Text = "Setup Queue";
            Flush(dispatcher);
            Assert.Equal(Translate("he", "Setup Queue"), addedText.Text);

            // A TextBlock composed of runs keeps its runs and their bindings.
            Assert.Equal(Translate("he", "Release target: "), labelRun.Text);
            Assert.True(BindingOperations.IsDataBound(nameRun, Run.TextProperty));
            releaseTarget.Name = "Okuma";
            Flush(dispatcher);
            Assert.Equal("Okuma", nameRun.Text);

            // A context menu is outside every window tree and is localized when it opens.
            menuOwner.ContextMenu.IsOpen = true;
            Flush(dispatcher);
            Assert.Equal(Translate("he", "Open operation"), menuItem.Header);
            menuOwner.ContextMenu.IsOpen = false;
            Flush(dispatcher);

            // A page hidden during the language change catches up when it is shown again.
            LocalizationService.Current.SetLanguage("ru", persist: false);
            Flush(dispatcher);
            tabs.SelectedIndex = 1;
            Flush(dispatcher);
            Assert.Equal(Translate("ru", "Timeline"), hiddenPageText.Text);
            tabs.SelectedIndex = 0;
            Flush(dispatcher);
            Assert.Equal(Translate("ru", "Setup Queue"), addedText.Text);
            menuOwner.ContextMenu.IsOpen = true;
            Flush(dispatcher);
            Assert.Equal(Translate("ru", "Open operation"), menuItem.Header);
            menuOwner.ContextMenu.IsOpen = false;
            Flush(dispatcher);

            LocalizationService.Current.SetLanguage("en", persist: false);
            Flush(dispatcher);
            Assert.Equal("Setup", probeWindow.Title);
            Assert.Equal("Release target: ", labelRun.Text);
            Assert.Equal("Setup Queue", addedText.Text);
            Assert.Equal("Okuma", nameRun.Text);
        }
        finally
        {
            menuOwner.ContextMenu.IsOpen = false;
            probeWindow.Close();
            Flush(dispatcher);
        }
    }

    private static void VisitTabs(
        TabControl tabs,
        string language,
        ISet<string> failures,
        ISet<TabControl> visited)
    {
        if (!visited.Add(tabs))
        {
            return;
        }

        var originalIndex = tabs.SelectedIndex;
        for (var index = 0; index < tabs.Items.Count; index++)
        {
            tabs.SelectedIndex = index;
            Flush(tabs.Dispatcher);
            CollectUntranslated(tabs, language, failures);
            foreach (var nested in Descendants<TabControl>(tabs).ToArray())
            {
                VisitTabs(nested, language, failures, visited);
            }
        }

        tabs.SelectedIndex = originalIndex;
        Flush(tabs.Dispatcher);
    }

    // Visible text that is itself a catalog key still shows English.
    private static void CollectUntranslated(DependencyObject root, string language, ISet<string> failures)
    {
        foreach (var text in Descendants<TextBlock>(root))
        {
            if (text.IsVisible
                && !string.IsNullOrWhiteSpace(text.Text)
                && LocalizationService.Current.TryTranslateExact(language, text.Text, out var translation)
                && !string.Equals(translation, text.Text, StringComparison.Ordinal))
            {
                failures.Add(text.Text);
            }
        }
    }

    private static string Translate(string language, string value) =>
        LocalizationService.Current.Translate(language, value);

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

    private sealed class ReleaseTarget : INotifyPropertyChanged
    {
        private string name = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name
        {
            get => name;
            set
            {
                name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }
    }
}
