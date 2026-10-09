using Meimad.Planner.Server.Application.Projections;
using Meimad.Planner.Server.Application.PlanningBoard;
using Meimad.Planner.Server.Application.Timeline;
using Meimad.Planner.Server.Domain.Projections;
using Meimad.Planner.Server.Tests.ProductionPackages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Timeline;

public sealed class ProjectionConsistencyTests
{
    private sealed record Result(int Value, ProjectionFreshness? Freshness = null);
    private sealed class Revisions : IProjectionRevisionReader
    {
        internal int Value;
        public Task<string> ReadAsync(CancellationToken token) => Task.FromResult($"epoch:{Value}");
    }

    [Fact]
    public async Task Parallel_requests_keep_independent_evaluation_instants()
    {
        var consistency = new ProjectionConsistency(new Revisions(), TimeProvider.System);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var firstTime = DateTimeOffset.Parse("2026-10-08T01:00:00Z");
        async Task<Result> Read(DateTimeOffset time) => await consistency.ReadAsync(async token =>
        {
            if (Interlocked.Increment(ref count) == 2) entered.SetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Equal(time, ProjectionConsistency.EvaluationAt);
            return new Result(1);
        }, (value, freshness) => value with { Freshness = freshness }, default, time);
        var results = await Task.WhenAll(Read(firstTime), Read(firstTime.AddHours(1)));
        Assert.NotEqual(results[0].Freshness!.EvaluationAt, results[1].Freshness!.EvaluationAt);
        Assert.Null(ProjectionConsistency.EvaluationAt);
    }

    [Fact]
    public async Task Changed_inputs_retry_with_one_evaluation_instant_and_discard_failed_diagnostics()
    {
        var revisions = new Revisions();
        var consistency = new ProjectionConsistency(revisions, TimeProvider.System);
        var attempts = 0;
        var diagnostics = 0;
        var instants = new List<DateTimeOffset?>();
        var result = await consistency.ReadAsync(async token =>
        {
            attempts++;
            instants.Add(ProjectionConsistency.EvaluationAt);
            await ProjectionConsistency.AfterValidationAsync(() => { diagnostics++; return Task.CompletedTask; });
            if (attempts == 1) revisions.Value++;
            return new Result(revisions.Value);
        }, (value, freshness) => value with { Freshness = freshness }, default);
        Assert.Equal(2, result.Freshness!.Attempts);
        Assert.Equal("epoch:1", result.Freshness.Revision);
        Assert.Single(instants.Distinct());
        Assert.Equal(1, diagnostics);
        Assert.Null(ProjectionConsistency.EvaluationAt);
    }

    [Fact]
    public async Task Continuous_changes_end_after_three_attempts_without_publishing_any_result()
    {
        var revisions = new Revisions();
        var consistency = new ProjectionConsistency(revisions, TimeProvider.System);
        await Assert.ThrowsAsync<ProjectionChangedException>(() => consistency.ReadAsync(token =>
        {
            revisions.Value++;
            return Task.FromResult(new Result(revisions.Value));
        }, (value, _) => throw new InvalidOperationException("Unstable result must not be stamped"), default));
        Assert.Equal(3, revisions.Value);
        Assert.Null(ProjectionConsistency.EvaluationAt);
    }

    [Fact]
    public async Task Stable_errors_and_cancellation_are_not_hidden_or_retried()
    {
        var consistency = new ProjectionConsistency(new Revisions(), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidDataException>(() => consistency.ReadAsync<Result>(
            _ => throw new InvalidDataException("bad configuration"), (value, _) => value, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consistency.ReadAsync<Result>(
            _ => throw new OperationCanceledException(), (value, _) => value, default));
        Assert.Null(ProjectionConsistency.EvaluationAt);
    }

    [Fact]
    public async Task Evaluation_time_and_runtime_are_distinct_from_persisted_revision()
    {
        var revisions = new Revisions();
        var consistency = new ProjectionConsistency(revisions, TimeProvider.System);
        Task<Result> Read(DateTimeOffset instant) => consistency.ReadAsync(
            _ => Task.FromResult(new Result(42)), (value, freshness) => value with { Freshness = freshness }, default, instant);
        var first = await Read(DateTimeOffset.Parse("2026-10-08T00:00:00Z"));
        var second = await Read(first.Freshness!.EvaluationAt.AddHours(1));
        Assert.Equal(first.Freshness.Revision, second.Freshness!.Revision);
        Assert.Equal(first.Freshness.RuntimeId, second.Freshness.RuntimeId);
        Assert.NotEqual(first.Freshness.EvaluationAt, second.Freshness.EvaluationAt);
        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public async Task Board_does_not_retry_an_unrelated_diagnostic_append_between_repository_reads()
    {
        await using var fixture = await ProductionPackageContextTests.Fixture.CreateAsync();
        var services = fixture.App.Services;
        var runs = new ChangingRuns(services.GetRequiredService<IProductionRunPlanningProjectionRepository>(), async () =>
            await fixture.ExecuteAsync("INSERT INTO structured_event_log(id,event_type,occurred_at,user_id,related_entity_ids_json) VALUES('during-board','connection_observed','2026-10-08T00:00:00Z','test','{}')"));
        var board = new PlanningBoardService(services.GetRequiredService<IPlanningBoardRepository>(), runs,
            services.GetRequiredService<TimelineProjectionService>(), NullLogger<PlanningBoardService>.Instance,
            services.GetRequiredService<ProjectionConsistency>());
        var snapshot = await board.ReadAsync();
        Assert.Equal(1, snapshot.Freshness!.Attempts);
        Assert.Equal(1, runs.Calls);
        Assert.Equal("current", snapshot.ConflictCalculationStatus);
    }

    [Fact]
    public async Task Board_retries_a_real_assignment_change_between_repository_reads_and_reuses_run_cards()
    {
        await using var fixture = await ProductionPackageContextTests.Fixture.CreateAsync();
        var services = fixture.App.Services;
        var runs = new ChangingRuns(services.GetRequiredService<IProductionRunPlanningProjectionRepository>(), async () =>
            await fixture.ExecuteAsync("UPDATE machine_assignments SET backlog_position=1 WHERE id='assignment-package'; UPDATE working_calendars SET name='Changed during read';"));
        var board = new PlanningBoardService(services.GetRequiredService<IPlanningBoardRepository>(), runs,
            services.GetRequiredService<TimelineProjectionService>(), NullLogger<PlanningBoardService>.Instance,
            services.GetRequiredService<ProjectionConsistency>());
        var snapshot = await board.ReadAsync();
        Assert.Equal(2, snapshot.Freshness!.Attempts);
        Assert.Equal(2, runs.Calls); // One run read per attempt; Timeline reuses it.
        Assert.Equal(1, snapshot.Machines.SelectMany(m => m.Backlog).Single().BacklogPosition);
        Assert.Equal("current", snapshot.ConflictCalculationStatus);
        Assert.Equal(1, await fixture.ScalarAsync("SELECT backlog_position FROM machine_assignments WHERE id='assignment-package'"));
    }

    private sealed class ChangingRuns(IProductionRunPlanningProjectionRepository inner, Func<Task> change)
        : IProductionRunPlanningProjectionRepository
    {
        internal int Calls;
        public async Task<IReadOnlyList<ProductionRunPlanningCard>> ReadAsync(CancellationToken token)
        {
            if (++Calls == 1) await change();
            return await inner.ReadAsync(token);
        }
    }
}
