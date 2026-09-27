using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Threading;
using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows.Tests.Localization;

public sealed class LocalizationBehaviorTests
{
    // A localized value is observed so later changes are translated too. The observation must
    // not keep the element alive once the interface discards it (a closed dialog, a regenerated
    // item template, a rebuilt timeline block).
    [Fact]
    public void Localized_elements_are_released_when_the_interface_discards_them()
    {
        Exception? failure = null;
        var released = false;
        var thread = new Thread(() =>
        {
            var originalLanguage = LocalizationService.Current.CurrentLanguage;
            try
            {
                LocalizationService.Current.SetLanguage("he", persist: false);
                var elements = LocalizeDiscardedElements();
                for (var attempt = 0; attempt < 5 && elements.Any(item => item.TryGetTarget(out _)); attempt++)
                {
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                released = elements.All(item => !item.TryGetTarget(out _));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                LocalizationService.Current.SetLanguage(originalLanguage, persist: false);
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The localization release check timed out.");
        Assert.Null(failure);
        Assert.True(released, "An element stayed in memory after its localized text was discarded.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object>[] LocalizeDiscardedElements()
    {
        var text = new TextBlock { Text = "Planning Board" };
        var button = new Button { Content = "Refresh", ToolTip = "Timeline" };
        var page = new StackPanel { Children = { text, button } };
        LocalizationBehavior.LocalizeTree(page);

        Assert.Equal(LocalizationService.Current.Translate("he", "Planning Board"), text.Text);
        Assert.Equal(LocalizationService.Current.Translate("he", "Refresh"), button.Content);
        Assert.Equal(LocalizationService.Current.Translate("he", "Timeline"), button.ToolTip);

        // The observation still follows a later change of the source text.
        text.Text = "Setup Queue";
        Assert.Equal(LocalizationService.Current.Translate("he", "Setup Queue"), text.Text);

        return [new WeakReference<object>(text), new WeakReference<object>(button), new WeakReference<object>(page)];
    }
}
