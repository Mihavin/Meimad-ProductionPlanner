using Meimad.Planner.Server.Application.LocalAi;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Application.Maintenance;
using Meimad.Planner.Server.Application.PlanningBoard;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Application.Projections;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Domain.Timeline;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Application.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;

namespace Meimad.Planner.Server.Tests.LocalAi;

public sealed class LocalOperationalEvidenceTests
{
    private const string Run = "run:batch-operation:operation-package";

    [Fact]
    public async Task Diagnostic_write_retries_evidence_without_recalculating_unchanged_forecast()
    {
        await using var fixture = await Fixture.CreateAsync(); var services = fixture.App.Services;
        var source = new DiagnosticSource(services.GetRequiredService<ITimelineSourceRepository>(),
            services.GetRequiredService<IStructuredEventLogRepository>());
        var options = services.GetRequiredService<TimelineOptions>();
        var timeline = new TimelineProjectionService(source, services.GetRequiredService<TimelineCalculationEngine>(), options,
            services.GetRequiredService<IStructuredEventLogRepository>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<TimelineProjectionService>.Instance,
            productionRuns: services.GetRequiredService<IProductionRunPlanningProjectionRepository>(),
            consistency: services.GetRequiredService<ProjectionConsistency>());
        var evidence = new LocalEvidenceService(services.GetRequiredService<ProductionRunService>(), services.GetRequiredService<ProductionRunReadinessService>(),
            services.GetRequiredService<OperationalEvidenceReader>(), services.GetRequiredService<ProjectionConsistency>(), fixture.Database, TimeProvider.System,
            services.GetRequiredService<LocalCorpusService>(), timeline, Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalEvidenceService>.Instance, options);
        var result = await evidence.ReadAsync(new("ExplainBlockers", Run), default);
        Assert.Equal(1, source.Calls);
        Assert.Contains(result.Facts, x => x.Kind == "Event" && x.Code == "during-evidence-forecast");
    }

    [Fact]
    public async Task Forecast_budget_preserves_recorded_facts_reuses_timeout_on_retry_and_reports_unchecked_currency()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (evidence, source) = BudgetedEvidence(fixture);
        var bundle = await evidence.ReadAsync(new("ExplainBlockers", Run), default);
        Assert.Equal(1, source.Calls);
        Assert.Contains(bundle.Facts, x => x.Kind == "ActionGate");
        Assert.Contains(bundle.Facts, x => x.Kind == "Quantity");
        var missing = Assert.Single(bundle.Facts, x => x.Kind == "MissingEvidence");
        Assert.Equal("timeline.budget_exceeded", missing.Code); Assert.Equal("UNKNOWN", missing.Validity);
        Assert.Equal("CHECK_UNAVAILABLE", (await evidence.CheckCurrencyAsync(new("test", "DETERMINISTIC", bundle, bundle.Claims), default)).Currency);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_converted_to_a_forecast_budget_result()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (evidence, _) = BudgetedEvidence(fixture);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evidence.ReadAsync(new("ExplainBlockers", Run), cancel.Token));
    }

    private static (LocalEvidenceService Evidence, DiagnosticSource Source) BudgetedEvidence(Fixture fixture)
    {
        var services = fixture.App.Services;
        var source = new DiagnosticSource(services.GetRequiredService<ITimelineSourceRepository>(), services.GetRequiredService<IStructuredEventLogRepository>(), block: true);
        var options = services.GetRequiredService<TimelineOptions>();
        var timeline = new TimelineProjectionService(source, services.GetRequiredService<TimelineCalculationEngine>(), options,
            services.GetRequiredService<IStructuredEventLogRepository>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<TimelineProjectionService>.Instance,
            productionRuns: services.GetRequiredService<IProductionRunPlanningProjectionRepository>(), consistency: services.GetRequiredService<ProjectionConsistency>());
        return (new(services.GetRequiredService<ProductionRunService>(), services.GetRequiredService<ProductionRunReadinessService>(),
            services.GetRequiredService<OperationalEvidenceReader>(), services.GetRequiredService<ProjectionConsistency>(), fixture.Database, TimeProvider.System,
            services.GetRequiredService<LocalCorpusService>(), timeline, Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalEvidenceService>.Instance,
            options, new LocalAiOptions { ForecastTimeoutSeconds = 1 }), source);
    }

    private sealed class DiagnosticSource(ITimelineSourceRepository inner, IStructuredEventLogRepository log, bool block = false) : ITimelineSourceRepository
    {
        internal int Calls;
        public async Task<TimelineSourceSnapshot> ReadAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken token)
        {
            Calls++;
            var snapshot = await inner.ReadAsync(start, end, token);
            await log.AppendAsync(new("during-evidence-forecast", start.AddMinutes(-1), "test",
                new Dictionary<string, string> { ["productionRunId"] = Run }), token);
            if (block) await Task.Delay(Timeout.Infinite, token);
            return snapshot;
        }
    }

    [Fact]
    public async Task Run_evidence_matches_Timeline_scope_and_preserves_exact_package_identity_without_mutations()
    {
        await using var fixture = await Fixture.CreateAsync();
        var package = await fixture.App.Services.GetRequiredService<ProductionPackageService>().CreateAsync("operation-package", "test");
        var eventCount = await fixture.ScalarAsync("SELECT COUNT(*) FROM production_run_workflow_events");
        var bundle = await fixture.App.Services.GetRequiredService<LocalEvidenceService>().ReadAsync(new("ExplainBlockers", Run), default);
        var projection = await fixture.App.Services.GetRequiredService<TimelineProjectionService>().CalculateAsync(
            bundle.AsOfUtc, bundle.AsOfUtc + PlanningBoardService.ConflictHorizon, bundle.AsOfUtc);
        var expected = projection.Conflicts.Where(x => x.OperationIds.Contains("operation-package")).Select(x => x.Code).Order().ToArray();
        Assert.Equal(expected, bundle.Facts.Where(x => x.Kind == "TimelineConflict").Select(x => x.Code).Order().ToArray());
        var pointer = Assert.Single(bundle.Facts, x => x.Kind == "Package");
        Assert.Equal(package.ProductionPackageId, pointer.Source.EntityId);
        Assert.Equal("RECORDED_POINTER", pointer.Validity);
        Assert.Equal(package.ManifestHash, pointer.SourceVersion);
        Assert.Equal("machine-package", Assert.Single(bundle.Facts, x => x.Kind == "MachineHealth").Source.EntityId);
        Assert.Equal(eventCount, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_run_workflow_events"));
        Assert.Contains(bundle.KnownLimitations, x => x.Contains("Responsible employee/role is not resolved"));
    }

    [Fact]
    public async Task Shift_cycle_samples_use_completion_window_server_duration_and_exclude_open_invalid_and_duplicate_samples()
    {
        await using var fixture = await Fixture.CreateAsync();
        var from = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        await Cycle(fixture, "crossing", from.AddMinutes(-5), from.AddMinutes(2), true);
        await Cycle(fixture, "boundary", from.AddMinutes(59), from.AddHours(1), true);
        await Cycle(fixture, "invalid", from.AddMinutes(4), from.AddMinutes(3), true);
        await Cycle(fixture, "open", from.AddMinutes(5), from.AddMinutes(6), false);
        await fixture.ExecuteAsync("""
            INSERT OR IGNORE INTO production_run_cycle_events SELECT * FROM production_run_cycle_events WHERE id='cycle-crossing';
            """);
        var bundle = await fixture.App.Services.GetRequiredService<LocalEvidenceService>().ReadAsync(
            new("SummarizeShift", From: from, To: from.AddHours(1), MachineId: "machine-package"), default);
        var sample = Assert.Single(bundle.Facts, x => x.Kind == "CycleTiming").Value;
        Assert.Equal(2, sample.GetProperty("CompletedCycles").GetInt64());
        Assert.Equal(1, sample.GetProperty("ValidDurationSamples").GetInt64());
        Assert.InRange(sample.GetProperty("TotalSeconds").GetDouble(), 419.99, 420.01);
        Assert.Equal(sample.GetProperty("TotalSeconds").GetDouble(), sample.GetProperty("AverageSeconds").GetDouble());
        Assert.Equal(2, Assert.Single(bundle.Facts, x => x.Code == "workflow.CYCLE_END").Value.GetInt64());
        Assert.All(bundle.Facts.Where(x => x.Kind == "WorkflowEvent"), x => Assert.NotNull(x.ReceivedAt));
        Assert.Contains(bundle.KnownLimitations, x => x.Contains("not clipped shift occupancy"));
    }

    [Fact]
    public async Task Unassigned_Run_does_not_expand_health_scope_to_every_Machine()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("UPDATE machine_assignments SET released_at='2026-10-08T00:00:00Z',version=version+1 WHERE id='assignment-package'");
        var bundle = await fixture.App.Services.GetRequiredService<LocalEvidenceService>().ReadAsync(new("ExplainBlockers", Run), default);
        Assert.Null(bundle.MachineId);
        Assert.DoesNotContain(bundle.Facts, x => x.Kind == "MachineHealth");
    }

    [Fact]
    public async Task Factory_timezone_display_preserves_both_offsets_of_the_repeated_DST_hour()
    {
        await using var fixture = await Fixture.CreateAsync("--Timeline:TimeZoneId=Asia/Jerusalem");
        var from = DateTimeOffset.Parse("2026-10-25T01:30:00+03:00");
        var to = DateTimeOffset.Parse("2026-10-25T01:30:00+02:00");
        var bundle = await fixture.App.Services.GetRequiredService<LocalEvidenceService>().ReadAsync(
            new("SummarizeShift", From: from, To: to, MachineId: "machine-package"), default);
        var interval = Assert.Single(bundle.Facts, x => x.Kind == "TimeScope").Value;
        var localFrom = interval.GetProperty("LocalFrom").GetDateTimeOffset();
        var localTo = interval.GetProperty("LocalTo").GetDateTimeOffset();
        Assert.Equal(localFrom.TimeOfDay, localTo.TimeOfDay);
        Assert.NotEqual(localFrom.Offset, localTo.Offset);
        Assert.Equal(TimeSpan.FromHours(1), localTo - localFrom);
    }

    [Fact]
    public async Task Empty_shift_has_unknown_cycle_duration_and_no_invented_parts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var from = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        var bundle = await fixture.App.Services.GetRequiredService<LocalEvidenceService>().ReadAsync(
            new("SummarizeShift", From: from, To: from.AddHours(1), MachineId: "machine-package"), default);
        var sample = Assert.Single(bundle.Facts, x => x.Kind == "CycleTiming").Value;
        Assert.Equal(0, sample.GetProperty("CompletedCycles").GetInt64());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, sample.GetProperty("AverageSeconds").ValueKind);
        Assert.DoesNotContain(bundle.Facts, x => x.Unit == "parts");
    }

    private static async Task Cycle(Fixture fixture, string id, DateTimeOffset start, DateTimeOffset end, bool completed)
    {
        // Fixture-only evidence, through the normal immutable evidence tables/triggers. Machine
        // timestamps deliberately differ; these samples must match Timeline's Server-time basis.
        await fixture.ExecuteAsync($"""
            INSERT INTO production_run_workflow_events(id,production_run_id,machine_id,event_type,source,source_event_id,source_sequence,
                server_received_at,machine_timestamp,metadata_json)
            SELECT 'start-{id}','{Run}','machine-package','CYCLE_START','{id}','start-{id}',1,'{start:O}','{start.AddHours(1):O}',
                json_object('productionRunProgramId',id) FROM production_run_programs WHERE production_run_id='{Run}' LIMIT 1;
            """);
        if (!completed) return;
        await fixture.ExecuteAsync($"""
            INSERT INTO production_run_workflow_events(id,production_run_id,machine_id,event_type,source,source_event_id,source_sequence,
                server_received_at,machine_timestamp,metadata_json)
            SELECT 'end-{id}','{Run}','machine-package','CYCLE_END','{id}','end-{id}',2,'{end:O}','{end.AddHours(2):O}',
                json_object('productionRunProgramId',id) FROM production_run_programs WHERE production_run_id='{Run}' LIMIT 1;
            INSERT INTO production_run_cycle_events(id,production_run_id,production_run_program_id,source,source_event_id,observed_at,
                completed_cycle_count,created_at,updated_at)
            SELECT 'cycle-{id}','{Run}',id,'{id}','end-{id}','{end:O}',1,'{end:O}','{end:O}'
            FROM production_run_programs WHERE production_run_id='{Run}' LIMIT 1;
            """);
    }
}
