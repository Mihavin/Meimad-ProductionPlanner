using System.Threading;
using System.Windows.Controls;
using System.Windows.Documents;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class MachinePlanningBoardDragTests
{
    [Fact]
    public void Drag_ancestor_lookup_handles_inline_content_without_visual_tree_exception()
    {
        Exception? failure = null;
        var found = false;
        var thread = new Thread(() =>
        {
            try
            {
                var run = new Run("Operation");
                var text = new TextBlock();
                text.Inlines.Add(run);

                found = Enumerable.Range(0, 1_000).All(_ => ReferenceEquals(
                    text,
                    MachinePlanningBoardView.FindAncestor<TextBlock>(run)));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The drag ancestor lookup timed out.");
        Assert.Null(failure);
        Assert.True(found);
    }
}
