using System.Net;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class WorkOrderTargetTimeViewModelTests
{
    [Fact]
    public async Task Inputs_are_prefilled_calculated_by_the_server_and_saved_with_the_entity_tag()
    {
        var api = new FakeApi();
        var viewModel = new WorkOrderTargetTimeViewModel();
        viewModel.AttachSession(api, editor: true);

        await viewModel.LoadAsync(Batch());
        Assert.Equal(540.ToString("0.##"), viewModel.PartPrice);
        Assert.Equal(string.Empty, viewModel.MaterialTotal);
        Assert.Equal(["300", "240 (default)"], viewModel.Rows.Select(row => row.HourlyRateText));
        Assert.Contains("taken from the Work Order's Orders", viewModel.Status, StringComparison.Ordinal);
        Assert.False(viewModel.DeleteCommand.CanExecute(null));

        // A missing material total is asked for; nothing reaches the Server.
        await viewModel.CalculateAsync();
        Assert.Contains("material cost", viewModel.Status, StringComparison.Ordinal);
        Assert.Empty(api.Calculations);

        viewModel.MaterialTotal = "1400";
        await viewModel.CalculateAsync();
        Assert.Equal((540d, 1400d), (api.Calculations.Single().PartPriceNis, api.Calculations.Single().MaterialTotalNis));
        Assert.Equal(["✔ Planned within target", "⚠ Planned over target"], viewModel.Rows.Select(row => row.Verdict));
        Assert.Contains("Margin", viewModel.Summary, StringComparison.Ordinal);

        await viewModel.SaveAsync();
        Assert.Equal((string?)null, api.SavedWithTag.Single());
        Assert.Contains("saved on the Work Order", viewModel.Status, StringComparison.Ordinal);
        Assert.True(viewModel.DeleteCommand.CanExecute(null));

        // Someone saved meanwhile: the current values come back and the user is told who.
        api.StaleBy = "dana";
        await viewModel.SaveAsync();
        Assert.Contains("dana", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal("\"work-order-target-time:b-1:v1\"", api.SavedWithTag.Last());
    }

    [Fact]
    public void Hourly_prices_parse_empty_as_default_and_refuse_negative_values()
    {
        Assert.True(SetupViewModel.TryParseHourlyRate("  ", out var empty));
        Assert.Null(empty);
        Assert.True(SetupViewModel.TryParseHourlyRate("275.5", out var value));
        Assert.Equal(275.5, value);
        Assert.False(SetupViewModel.TryParseHourlyRate("-1", out _));
        Assert.False(SetupViewModel.TryParseHourlyRate("abc", out _));
        Assert.False(WorkOrderTargetTimeViewModel.TryParse("", out _));
        Assert.True(WorkOrderTargetTimeViewModel.TryParse("0", out var zero));
        Assert.Equal(0, zero);
    }

    private static ProductionBatch Batch() => new(
        "b-1", "case-1", "B-100", "planned", 10, null, 2);

    private sealed class FakeApi : StubPlannerApiClient, IPlannerApiClient
    {
        private WorkOrderTargetTime? saved;

        public List<WorkOrderTargetTimeInput> Calculations { get; } = [];
        public List<string?> SavedWithTag { get; } = [];
        public string? StaleBy { get; set; }

        private static TargetTimeCalculation Calculate(WorkOrderTargetTimeInput input) => new(
            input.PartPriceNis, input.MaterialTotalNis, 10, input.PartPriceNis * 10 - input.MaterialTotalNis,
            [
                new TargetTimeResource("MACHINE", "machine-1", "M-1 Mill", 300, false, 13.333, 80, 2.667, [10]),
                new TargetTimeResource("MACHINE_TYPE", null, "Lathe", 240, true, 16.667, 100, 20, [20]),
            ]);

        public Task<WorkOrderTargetTimeOverview> GetWorkOrderTargetTimeAsync(string batchId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkOrderTargetTimeOverview(batchId, "B-100", 10, 540, 240, 100,
            [
                new TargetTimeResourcePrice("MACHINE", "machine-1", "M-1 Mill", 300, false, [10]),
                new TargetTimeResourcePrice("MACHINE_TYPE", null, "Lathe", 240, true, [20]),
            ], saved));

        public Task<TargetTimeCalculation> CalculateWorkOrderTargetTimeAsync(string batchId, WorkOrderTargetTimeInput input, CancellationToken cancellationToken = default)
        {
            Calculations.Add(input);
            return Task.FromResult(Calculate(input));
        }

        public Task<WorkOrderTargetTime> SaveWorkOrderTargetTimeAsync(string batchId, WorkOrderTargetTimeInput input, string? entityTag, CancellationToken cancellationToken = default)
        {
            SavedWithTag.Add(entityTag);
            if (StaleBy is not null)
                throw new PlannerApiException(HttpStatusCode.PreconditionFailed, "resource_version_stale", "changed",
                    conflict: new PlannerConflict("Work Order target time", StaleBy, DateTimeOffset.UnixEpoch, "Reload."));
            saved = new WorkOrderTargetTime(batchId, Calculate(input), input.Notes, 1, "\"work-order-target-time:b-1:v1\"", DateTimeOffset.UnixEpoch, "planner");
            return Task.FromResult(saved);
        }
    }
}
