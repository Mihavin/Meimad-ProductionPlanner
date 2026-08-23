using System.Windows.Threading;
using System.Windows;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

internal static class TimelineViewPerformanceAudit
{
    internal static void RunAndAssert(TimelineView view, TimelineViewModel viewModel)
    {
        var originalMachines = viewModel.Machines.ToArray();
        var rendersBeforeProjection = view.RenderInvocationCount;

        // TimelineViewModel.Apply publishes one Server projection through a
        // Clear/Add collection sequence. The view must draw the final state once,
        // rather than rebuilding an increasingly large Canvas for every Machine.
        viewModel.Machines.Clear();
        for (var index = 0; index < 50; index++)
        {
            viewModel.Machines.Add(new TimelineMachine(
                $"performance-machine-{index}",
                $"P-{index:00}",
                $"Performance Machine {index:00}",
                []));
        }

        view.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal(rendersBeforeProjection + 1, view.RenderInvocationCount);

        // Restore the fixture projection so the existing visual correctness audit
        // continues to inspect its intended operation, phase, and dependency data.
        viewModel.Machines.Clear();
        foreach (var machine in originalMachines)
        {
            viewModel.Machines.Add(machine);
        }
        view.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        var rendersBeforeUnchangedTabCycle = view.RenderInvocationCount;
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        view.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal(rendersBeforeUnchangedTabCycle, view.RenderInvocationCount);
    }
}
