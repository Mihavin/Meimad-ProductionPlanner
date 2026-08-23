using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

// WPF permits one Application per process. ViewStartupTests owns it and invokes
// this audit on that Application's STA thread so the full suite cannot inherit a
// shutdown Application from another test and hang while showing this Window.
internal static class StepViewerPerformanceAudit
{
    internal static void RunAndAssert()
    {
        Window? window = null;
        try
        {
            var viewer = new StepViewerControl();
            var viewerTab = new TabItem { Header = "STEP", Content = viewer };
            var otherTab = new TabItem { Header = "Other", Content = new Border() };
            var tabs = new TabControl { Items = { viewerTab, otherTab } };
            window = new Window { Content = tabs, Width = 900, Height = 650 };
            window.Show();
            window.UpdateLayout();
            viewer.LoadModel(
                new StepModelData(
                    [new(0, 0, 0), new(100, 0, 0), new(0, 100, 0)],
                    [new(0, 1), new(1, 2), new(2, 0)],
                    [],
                    [0, 1, 2]),
                "performance.step");
            viewer.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            tabs.SelectedItem = otherTab;
            window.UpdateLayout();
            Assert.False(viewer.IsVisible);
            var rendersBeforeHiddenResizes = viewer.RenderInvocationCount;

            // A hidden nested tab may receive several layout sizes while the main
            // window, language direction, or sibling tab changes. None should
            // rebuild the CAD drawing while it cannot be seen.
            var sizeChanged = typeof(StepViewerControl).GetMethod(
                "Viewer_SizeChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            for (var index = 0; index < 20; index++)
            {
                sizeChanged.Invoke(viewer, [viewer, null]);
            }
            viewer.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.Equal(rendersBeforeHiddenResizes, viewer.RenderInvocationCount);

            tabs.SelectedItem = viewerTab;
            window.UpdateLayout();
            viewer.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.True(viewer.IsVisible);
            Assert.Equal(rendersBeforeHiddenResizes + 1, viewer.RenderInvocationCount);
        }
        finally
        {
            window?.Close();
        }
    }
}
