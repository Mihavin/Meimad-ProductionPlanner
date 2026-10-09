using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Meimad.Planner.Client.Windows.Localization;
using Meimad.Planner.Client.Windows.Presentation;
namespace Meimad.Planner.Client.Windows.Tests.Presentation;

// Runs inside the existing single WPF Application. Rendered scale is a layout simulation;
// real monitor/DPI changes and operator acceptance remain separate commissioning checks.
internal static class WorkbenchVisualAudit
{
    internal static void Run(MainWindow window)
    {
        var output = Environment.GetEnvironmentVariable("MEIMAD_WORKBENCH_PREVIEW");
        if (string.IsNullOrWhiteSpace(output)) return;
        var folder = Path.GetFullPath(output); Directory.CreateDirectory(folder);
        var tabs = (TabControl)window.FindName("WorkspaceTabs");
        var model = (MainWindowViewModel)window.DataContext;
        var oldLanguage = LocalizationService.Current.CurrentLanguage;
        var oldTheme = Themes.WorkbenchTheme.Current;
        var oldTab = tabs.SelectedItem; var oldWidth = window.Width; var oldHeight = window.Height;
        try
        {
            for (var i = 1; i <= 48; i++) model.Shop.Machines.Add(new("preview-" + i, i.ToString(),
                "Synthetic preview / מכונה / Станок " + i, i % 2 == 0 ? "OFFLINE" : "ONLINE", "STALE", "UNKNOWN", null, i, "RUN-PREVIEW-ONLY", []));
            model.OperationalHealth.Items.Add(new("CNC", "preview", "Synthetic preview", "ONLINE", "STALE", null, null,
                DateTimeOffset.Parse("2026-10-01T10:00:00Z"), null, "Connection and observation freshness are separate facts."));
            foreach (var language in new[] { "en", "he", "ru" })
            foreach (var theme in new[] { "light", "graphite" })
            foreach (var size in new[] { (1366, 768), (1920, 1080) })
            foreach (var scale in new[] { 1d, 1.25, 1.5 })
            {
                LocalizationService.Current.SetLanguage(language, persist: false);
                Themes.WorkbenchTheme.Apply(theme, persist: false);
                window.Width = size.Item1 / scale; window.Height = size.Item2 / scale;
                foreach (var name in new[] { "ShopWorkspaceTab", "PreparationWorkspaceTab", "OperationWorkspaceTab", "IntegrationsWorkspaceTab" })
                {
                    tabs.SelectedItem = window.FindName(name);
                    window.UpdateLayout(); window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    var content = (FrameworkElement)window.Content;
                    Save(name);
                    if (name == "ShopWorkspaceTab")
                    {
                        var expander = Find<Expander>(content, "ShopShiftEvidence");
                        Assert.NotNull(expander);
                        expander.IsExpanded = true; window.UpdateLayout();
                        var panel = Find<FrameworkElement>(content, "ShiftEvidencePanel");
                        Assert.NotNull(panel); Assert.InRange(panel.ActualHeight, 90, content.ActualHeight);
                        Save(name + "-expanded");
                        expander.IsExpanded = false; window.UpdateLayout();
                    }
                    void Save(string viewName)
                    {
                    Assert.True(content.ActualWidth > 0 && content.ActualHeight > 0);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * scale),
                        (int)Math.Ceiling(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    // RenderTargetBitmap omits the Window ancestor's RTL compensation.
                    BitmapSource pixels = content.FlowDirection == FlowDirection.RightToLeft
                        ? new TransformedBitmap(bitmap, new ScaleTransform(-1, 1)) : bitmap;
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pixels));
                    using var stream = File.Create(Path.Combine(folder, $"{viewName}-{language}-{theme}-{size.Item1}-{(int)(scale * 100)}.png"));
                    encoder.Save(stream);
                    }
                }
            }
        }
        finally
        {
            model.Shop.Machines.Clear(); model.OperationalHealth.Items.Clear();
            window.Width = oldWidth; window.Height = oldHeight; tabs.SelectedItem = oldTab;
            LocalizationService.Current.SetLanguage(oldLanguage, persist: false); Themes.WorkbenchTheme.Apply(oldTheme, persist: false);
            window.UpdateLayout(); window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
        }
    }
    private static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T match && match.Name == name) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (Find<T>(VisualTreeHelper.GetChild(root, index), name) is { } child) return child;
        return null;
    }
}
