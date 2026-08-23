using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class SessionAttachmentPerformanceTests
{
    [Fact]
    public void Unchanged_edit_session_does_not_raise_redundant_view_updates()
    {
        var caseWorkspace = new CaseWorkspaceViewModel(new NoOpFolderLauncher());
        var board = new MachinePlanningBoardViewModel();
        var setup = new SetupViewModel(
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => true,
            () => true);
        var status = EditorStatus(generation: 12);

        caseWorkspace.AttachSession(null, "client-1", status);
        board.AttachSession(null, "client-1", status);
        setup.AttachSession(null, "client-1", status);

        var caseNotifications = 0;
        var boardNotifications = 0;
        var setupNotifications = 0;
        caseWorkspace.PropertyChanged += (_, _) => caseNotifications++;
        board.PropertyChanged += (_, _) => boardNotifications++;
        setup.PropertyChanged += (_, _) => setupNotifications++;

        // ServerTime changes on every poll, but it does not change authorization.
        // The large tab view models must remain quiet until state or generation changes.
        var nextPoll = status with { ServerTime = status.ServerTime.AddSeconds(5) };
        caseWorkspace.AttachSession(null, "client-1", nextPoll);
        board.AttachSession(null, "client-1", nextPoll);
        setup.AttachSession(null, "client-1", nextPoll);

        Assert.Equal(0, caseNotifications);
        Assert.Equal(0, boardNotifications);
        Assert.Equal(0, setupNotifications);

        var nextGeneration = EditorStatus(generation: 13);
        caseWorkspace.AttachSession(null, "client-1", nextGeneration);
        board.AttachSession(null, "client-1", nextGeneration);
        setup.AttachSession(null, "client-1", nextGeneration);

        Assert.True(caseNotifications > 0);
        Assert.True(boardNotifications > 0);
        Assert.True(setupNotifications > 0);
    }

    private static EditModeStatus EditorStatus(long generation) => new(
        ClientEditState.Editor,
        generation,
        new EditModeHolder("client-1", "operator", generation, DateTimeOffset.UtcNow),
        null,
        DateTimeOffset.UtcNow,
        30);

    private sealed class NoOpFolderLauncher : IWorkingFolderLauncher
    {
        public void Open(string path)
        {
        }
    }
}
