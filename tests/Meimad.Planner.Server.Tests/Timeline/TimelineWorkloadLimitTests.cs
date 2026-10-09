using System.Net;
using Meimad.Planner.Server.Domain.Timeline;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;

namespace Meimad.Planner.Server.Tests.Timeline;

public sealed class TimelineWorkloadLimitTests
{
    [Fact]
    public async Task Interactive_limit_is_explicit_and_long_report_has_its_own_bound()
    {
        await using var fixture = await Fixture.CreateAsync("--Timeline:MaximumInteractiveDays=7", "--Timeline:MaximumReportDays=30");
        const string range = "?from=2026-10-08T00:00:00Z&to=2026-11-07T00:00:00Z";
        using var interactive = await fixture.Client.GetAsync("/api/v1/timeline" + range);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, interactive.StatusCode);
        Assert.Contains("timeline_horizon_limit", await interactive.Content.ReadAsStringAsync());
        using var report = await fixture.Client.GetAsync("/api/v1/timeline/report" + range);
        report.EnsureSuccessStatusCode(); Assert.True(report.Headers.CacheControl!.NoStore);
        using var excessive = await fixture.Client.GetAsync("/api/v1/timeline/report?from=2026-10-08T00:00:00Z&to=2027-10-08T00:00:00Z");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, excessive.StatusCode);
    }

    [Fact]
    public void Cancellation_during_calculation_is_observed_and_does_not_leak_into_the_next_call()
    {
        using var cancellation = new CancellationTokenSource();
        var start = DateTimeOffset.UtcNow;
        TimelineMachineBacklog[] backlogs = [new("machine", [new("operation", TimeSpan.Zero, TimeSpan.FromMinutes(1))])];
        var input = new TimelineCalculationInput(start, start.AddDays(1),
            new CancellingList<TimelineMachineBacklog>(backlogs, cancellation), [], new([]), [], []);
        var engine = new TimelineCalculationEngine();
        Assert.ThrowsAny<OperationCanceledException>(() => engine.Calculate(input, cancellation.Token));
        Assert.NotNull(engine.Calculate(input with { MachineBacklogs = backlogs }));
    }

    private sealed class CancellingList<T>(IReadOnlyList<T> values, CancellationTokenSource source) : IReadOnlyList<T>
    {
        public int Count => values.Count;
        public T this[int index] => values[index];
        public IEnumerator<T> GetEnumerator() { source.Cancel(); return values.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
