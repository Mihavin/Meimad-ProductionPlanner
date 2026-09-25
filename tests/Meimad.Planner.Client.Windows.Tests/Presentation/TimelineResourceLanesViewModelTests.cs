using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class TimelineResourceLanesViewModelTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-08-11T00:00:00Z");

    [Fact]
    public async Task Resource_lanes_and_predicted_completion_are_applied_from_the_snapshot()
    {
        var api = new FakeApiClient(Snapshot());
        var viewModel = new TimelineViewModel { FromDate = Start.UtcDateTime, ToDate = Start.AddDays(10).UtcDateTime };
        viewModel.AttachSession(api, "client", null);

        await viewModel.RefreshAsync();

        var lane = Assert.Single(viewModel.Resources);
        Assert.Equal("Workstation", lane.ClassLabel);
        var interval = Assert.Single(lane.Intervals);
        Assert.Equal("B-1 OP10 · 50 Final inspection", interval.Label);
        Assert.Equal("after the Machine", interval.DirectionLabel);
        Assert.Equal(Start.AddHours(4), viewModel.Batches.Single().PredictedCompletion);
        Assert.Contains("Provisional placement", TimelineView.ResourceIntervalToolTip(interval, TimeZoneInfo.Utc));
        Assert.False(viewModel.CanPin);
    }

    [Fact]
    public async Task Editors_can_pin_and_unpin_a_step_and_the_projection_is_refreshed()
    {
        var api = new FakeApiClient(Snapshot());
        var viewModel = new TimelineViewModel { FromDate = Start.UtcDateTime, ToDate = Start.AddDays(10).UtcDateTime };
        viewModel.AttachSession(api, "editor", new EditModeStatus(ClientEditState.Editor, 5, null, null, DateTimeOffset.UtcNow, 30));
        await viewModel.RefreshAsync();
        Assert.True(viewModel.CanPin);
        var interval = viewModel.Resources.Single().Intervals.Single();

        await viewModel.PinAsync(interval, pinStart: true);

        var pin = Assert.Single(api.Pins);
        Assert.Equal(("op-1", "req-final", "station-inspection", true, "editor", 5L), (pin.Request.BatchOperationId, pin.Request.RequirementId, pin.Request.WorkstationId, pin.Request.PinStart, pin.ClientId, pin.Generation));
        Assert.Equal(interval.StartsAt, pin.Request.PlannedStartsAt);
        Assert.Equal(2, api.RequestCount);
        Assert.Contains("pinned to its resource and start", viewModel.StatusMessage);

        await viewModel.UnpinAsync(interval);

        Assert.Equal(("op-1", "req-final"), Assert.Single(api.Cleared));
        Assert.Equal(3, api.RequestCount);
        Assert.Contains("unpinned", viewModel.StatusMessage);
    }

    private static TimelineSnapshot Snapshot() => new(
        Start, Start, Start.AddDays(10),
        [new TimelineBatch("batch-1", "B-1", "PN-1", new DateOnly(2026, 8, 19), Start.AddHours(4))],
        [new TimelineMachine("machine-1", "M-1", "Mill", [
            new TimelineInterval("operation", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10, "Mill",
                Start.AddHours(2), Start.AddHours(3), null, TimingKind: "forecast", OperationStatus: "not_started", MachineAssignmentId: "assignment-1")
        ])],
        [], [],
        Resources:
        [
            new TimelineResourceLane("station-inspection", "workstation", "Inspection bench",
            [
                new TimelineResourceInterval("op-1|req-final", "op-1", "req-final", "batch-1", "B-1", "PN-1", 10, "Mill", 50,
                    "Final inspection", "FORWARD", Start.AddHours(3), Start.AddHours(4), false,
                    "Earliest feasible slot after the predecessor/anchor.", "station-inspection", null, null, "WORKSTATION")
            ])
        ]);

    private sealed class FakeApiClient(TimelineSnapshot snapshot) : StubPlannerApiClient, IPlannerApiClient
    {
        internal int RequestCount { get; private set; }

        internal List<(TimelineAuxiliaryPinRequest Request, string ClientId, long Generation)> Pins { get; } = [];

        internal List<(string OperationId, string RequirementId)> Cleared { get; } = [];

        public override Task<TimelineSnapshot> GetTimelineAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.FromResult(snapshot);
        }

        public Task<TimelineAuxiliaryPin> SetTimelineAuxiliaryPinAsync(TimelineAuxiliaryPinRequest request, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            Pins.Add((request, clientId, editGeneration));
            return Task.FromResult(new TimelineAuxiliaryPin(request.BatchOperationId, request.RequirementId, request.WorkstationId, request.EmployeeId, request.PinStart ? request.PlannedStartsAt : null));
        }

        public Task ClearTimelineAuxiliaryPinAsync(string batchOperationId, string requirementId, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            Cleared.Add((batchOperationId, requirementId));
            return Task.CompletedTask;
        }

    }
}
