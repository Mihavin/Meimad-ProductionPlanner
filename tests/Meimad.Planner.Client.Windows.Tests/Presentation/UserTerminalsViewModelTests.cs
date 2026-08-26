using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class UserTerminalsViewModelTests
{
    [Fact]
    public async Task View_mode_loads_monitoring_but_blocks_all_administration_commands()
    {
        var terminal = Terminal();
        var api = new FakeApiClient([terminal], [Machine()]);
        var viewModel = new UserTerminalsViewModel();
        viewModel.AttachSession(api, "viewer", Status(ClientEditState.Viewer));

        await viewModel.RefreshAsync();
        viewModel.Selected = Assert.Single(viewModel.Terminals);

        Assert.Equal("M-1 — Mill One", viewModel.Selected.BindingText);
        Assert.Equal("3.86 V / 72%", viewModel.Selected.BatteryText);
        Assert.Equal("192.168.50.31 / -61 dBm", viewModel.Selected.WifiText);
        Assert.Equal("IN_SETUP", viewModel.Selected.WorkflowText);
        Assert.False(viewModel.NewCommand.CanExecute(null));
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.MarkSpareCommand.CanExecute(null));
        Assert.False(viewModel.ToggleEnabledCommand.CanExecute(null));
        Assert.False(viewModel.RotateCredentialCommand.CanExecute(null));
        Assert.Contains("monitoring only", viewModel.EditModeText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Edit_mode_enables_assignment_revoke_rotation_and_spare_actions()
    {
        var api = new FakeApiClient([Terminal()], [Machine()]);
        var viewModel = new UserTerminalsViewModel();
        viewModel.AttachSession(api, "editor", Status(ClientEditState.Editor));

        await viewModel.RefreshAsync();
        viewModel.Selected = Assert.Single(viewModel.Terminals);

        Assert.True(viewModel.SaveCommand.CanExecute(null));
        Assert.True(viewModel.MarkSpareCommand.CanExecute(null));
        Assert.True(viewModel.ToggleEnabledCommand.CanExecute(null));
        Assert.True(viewModel.RotateCredentialCommand.CanExecute(null));
        Assert.False(viewModel.CanEditIdentity);
    }

    private static UserTerminal Terminal() => new(
        "device-1", "3041", "A4:CF:12:83:76:91", "Tablet One", "machine-1",
        true, 1, DateTimeOffset.Parse("2026-08-26T08:00:00Z"),
        DateTimeOffset.Parse("2026-08-26T08:00:00Z"), null,
        DateTimeOffset.Parse("2026-08-26T09:00:00Z"),
        DateTimeOffset.Parse("2026-08-26T09:00:00Z"),
        "0.1.0", 3.86m, 72, "192.168.50.31", -61, "M-1", "Mill One",
        "run-1", "IN_SETUP", "R4");

    private static PlannerMachine Machine() => new(
        "machine-1", "M-1", "Mill One", "mill", null, [], "calendar-1",
        true, true, null, null, 1, 1,
        DateTimeOffset.Parse("2026-08-26T08:00:00Z"),
        DateTimeOffset.Parse("2026-08-26T08:00:00Z"));

    private static EditModeStatus Status(ClientEditState state) => new(
        state, 7, null, null, DateTimeOffset.Parse("2026-08-26T09:00:00Z"), 30);

    private sealed class FakeApiClient(
        IReadOnlyList<UserTerminal> terminals,
        IReadOnlyList<PlannerMachine> machines) : IPlannerApiClient
    {
        public Task<IReadOnlyList<UserTerminal>> ListUserTerminalsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(terminals);

        public Task<IReadOnlyList<PlannerMachine>> ListMachinesAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(machines);

        public Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EditModeStatus> GetEditModeAsync(string clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EditModeStatus> RequestEditAsync(string clientId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EditModeStatus> ReleaseEditAsync(string clientId, long generation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EditModeStatus> DecideTransferAsync(string clientId, long generation, string requestId, bool release, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlannerCase>> ListCasesAsync(CaseQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CaseResource> GetCaseAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CaseResource> UpdateCaseAsync(string caseId, CaseUpdate update, string entityTag, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CaseOperation>> ListCaseOperationsAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlannerOrder>> ListOrdersAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProductionBatch>> ListBatchesAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]?> GetCasePreviewAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlanningBoardSnapshot> GetPlanningBoardAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TimelineSnapshot> GetTimelineAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AssignOrMoveOperationAsync(string batchOperationId, string machineId, int backlogPosition, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UnassignOperationAsync(string batchOperationId, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
