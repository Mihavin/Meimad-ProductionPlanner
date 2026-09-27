using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class ToolRequirementsViewModelTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Calculating_lists_the_tools_of_the_whole_last_day_and_selecting_one_shows_its_uses_and_route()
    {
        var api = new FakeApi();
        var viewModel = new ToolRequirementsViewModel
        {
            FromDate = new DateTime(2026, 9, 28),
            ToDate = new DateTime(2026, 9, 30)
        };
        viewModel.AttachSession(api);

        await viewModel.RefreshAsync();

        Assert.Equal(new DateTimeOffset(new DateTime(2026, 10, 1)), api.RequestedTo);
        var row = Assert.Single(viewModel.Items);
        Assert.Equal("Aluminum", row.MaterialText);
        Assert.Equal("Flat end mill", row.TypeText);
        Assert.Equal("10", row.DiameterText);
        Assert.Equal("Moves between machines", row.SharingText);
        Assert.Single(viewModel.OperationsWithoutToolTable);
        Assert.Equal(1, viewModel.WithoutToolTableCount);

        viewModel.SelectedItem = row;
        Assert.Equal(2, viewModel.SelectedUses.Count);
        Assert.Contains("M-05", viewModel.SelectedRoute, StringComparison.Ordinal);
        Assert.Contains("M-07", viewModel.SelectedRoute, StringComparison.Ordinal);

        viewModel.SearchText = "no such tool";
        Assert.Empty(viewModel.Items);
        viewModel.SearchText = "WO-2";
        Assert.Single(viewModel.Items);

        Assert.Equal([1, 2, 3], await viewModel.ExportAsync());
    }

    [Fact]
    public async Task A_period_ending_before_it_starts_is_not_sent()
    {
        var api = new FakeApi();
        var viewModel = new ToolRequirementsViewModel { FromDate = new DateTime(2026, 9, 30), ToDate = new DateTime(2026, 9, 28) };
        viewModel.AttachSession(api);

        await viewModel.RefreshAsync();

        Assert.Null(api.RequestedTo);
        Assert.Contains("last day", viewModel.Status, StringComparison.Ordinal);
    }

    private sealed class FakeApi : StubPlannerApiClient, IPlannerApiClient
    {
        internal DateTimeOffset? RequestedTo { get; private set; }

        public Task<PlannerToolRequirementReport> GetToolRequirementsAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            RequestedTo = to;
            PlannerToolUse Use(string machine, string workOrder, int hour) => new(
                $"op-{workOrder}", workOrder, "PN-1", 10, "Mill", machine, machine, ["T10"], 1, "HOLDER-50_NEW", 34,
                "AL 7075", Monday.AddHours(hour), Monday.AddHours(hour + 2));
            var tool = new PlannerToolRequirement(
                "ALUMINUM", "END_MILL", "name", 10, "FIN_10", 1, ["M-05", "M-07"], 1, Monday, Monday.AddHours(6),
                [new PlannerToolCopyRoute(1,
                [
                    new PlannerToolCopyStop("M-05", "M-05", Monday, Monday.AddHours(2)),
                    new PlannerToolCopyStop("M-07", "M-07", Monday.AddHours(4), Monday.AddHours(6))
                ])],
                [Use("M-05", "WO-1", 0), Use("M-07", "WO-2", 4)]);
            return Task.FromResult(new PlannerToolRequirementReport(
                from, to, Monday, "UTC", 3, [tool],
                [new PlannerOperationWithoutTools("op-3", "WO-3", "PN-3", 10, "Turn", "M-09", "TITANIUM", Monday, Monday.AddHours(3))]));
        }

        public Task<byte[]> ExportToolRequirementsAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]>([1, 2, 3]);
    }
}
