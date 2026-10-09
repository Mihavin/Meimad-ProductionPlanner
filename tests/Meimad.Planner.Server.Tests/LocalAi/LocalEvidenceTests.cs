using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Application.LocalAi;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;

namespace Meimad.Planner.Server.Tests.LocalAi;

public sealed class LocalEvidenceTests
{
    [Fact]
    public async Task Currency_compares_scoped_facts_and_detects_late_events_without_confusing_metadata_writes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var evidence = fixture.App.Services.GetRequiredService<LocalEvidenceService>();
        var at = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        var bundle = await evidence.ReadAsync(new("SummarizeShift", From: at, To: at.AddHours(1), MachineId: "machine-package"), default);
        var answer = new LocalExplanation("test", "DETERMINISTIC", bundle, bundle.Claims);
        Assert.Equal("CURRENT_AT_CHECK", (await evidence.CheckCurrencyAsync(answer, default)).Currency);
        var events = fixture.App.Services.GetRequiredService<IStructuredEventLogRepository>();
        await events.AppendAsync(new("late-event", at, "test", new Dictionary<string, string> { ["machineId"] = "other" }), default);
        Assert.Equal("CURRENT_AT_CHECK", (await evidence.CheckCurrencyAsync(answer, default)).Currency);
        await events.AppendAsync(new("late-event", at, "test", new Dictionary<string, string> { ["machineId"] = "machine-package" }), default);
        Assert.Equal("STALE", (await evidence.CheckCurrencyAsync(answer, default)).Currency);
    }

    [Fact]
    public async Task Incident_search_matches_recorded_context_but_excludes_unapproved_event_fields()
    {
        await using var fixture = await Fixture.CreateAsync();
        var at = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        var events = fixture.App.Services.GetRequiredService<IStructuredEventLogRepository>();
        await events.AppendAsync(new("historical-failure", at, "test", new Dictionary<string, string>
        { ["machineId"] = "machine-package", ["productionRunId"] = "RUN-EXACT-123", ["privateNote"] = "UNAPPROVED" }), default);
        var service = fixture.App.Services.GetRequiredService<LocalEvidenceService>();
        var request = new ExplanationRequest("FindSimilarIncidents", From: at, To: at.AddHours(1), MachineId: "machine-package", SearchTerm: "RUN-EXACT-123");
        var bundle = await service.ReadAsync(request, default);
        var fact = Assert.Single(bundle.Facts);
        Assert.Equal("HISTORICAL_REFERENCE", fact.Validity);
        Assert.Equal("RUN-EXACT-123", fact.Value.GetProperty("Context").GetProperty("productionRunId").GetString());
        Assert.DoesNotContain("UNAPPROVED", fact.Value.GetRawText());
        Assert.NotNull(fact.SourceVersion);
        Assert.Empty((await service.ReadAsync(request with { SearchTerm = "UNAPPROVED" }, default)).Facts);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadAsync(request with { MachineId = "missing" }, default));
    }

    [Theory]
    [InlineData("2026-10-25T01:30:00+03:00", "2026-10-25T01:30:00+02:00")]
    [InlineData("2026-03-27T01:30:00+02:00", "2026-03-27T03:30:00+03:00")]
    public void Explicit_offsets_resolve_DST_intervals_without_inventing_local_time(string from, string to)
    {
        var request = JsonSerializer.Deserialize<ExplanationRequest>(JsonSerializer.Serialize(new { Intent = "SummarizeShift", From = from, To = to }))!;
        Assert.Equal(TimeSpan.FromHours(1), request.To - request.From);
        LocalEvidenceService.Validate(request);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ExplanationRequest>("{\"Intent\":\"SummarizeShift\",\"From\":\"2026-10-25T01:30:00\"}"));
    }
    [Fact]
    public async Task Run_explanation_uses_the_same_action_decisions_and_quantities_as_readiness()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = "run:batch-operation:operation-package";
        var expected = await fixture.App.Services.GetRequiredService<ProductionRunReadinessService>().ReadAsync(id);
        var evidence = await fixture.App.Services.GetRequiredService<LocalEvidenceService>().ReadAsync(new("ExplainBlockers", id), default);
        Assert.NotEmpty(evidence.PlanRevision);
        Assert.Equal("machine-package", evidence.MachineId);
        foreach (var action in expected.Actions!)
            Assert.Equal(action.IsAllowed, Assert.Single(evidence.Facts, x => x.Kind == "ActionGate" && x.Code == action.Action).Value.GetBoolean());
        Assert.All(evidence.Claims, claim => Assert.All(claim.EvidenceIds,
            reference => Assert.Contains(evidence.Facts, x => x.EvidenceId == reference)));
        Assert.Equal(evidence.Facts.Count, evidence.Facts.Select(x => x.EvidenceId).Distinct().Count());
        using (fixture.Client.SignedOut())
        {
            using var denied = await fixture.Client.PostAsJsonAsync("/api/v1/local-ai/explanations", new ExplanationRequest("ExplainBlockers", id));
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using (fixture.Client.SignedInWithOnly())
        {
            using var allowed = await fixture.Client.PostAsJsonAsync("/api/v1/local-ai/explanations", new ExplanationRequest("ExplainBlockers", id));
            allowed.EnsureSuccessStatusCode();
            Assert.Equal("DETERMINISTIC", (await allowed.Content.ReadFromJsonAsync<LocalExplanation>())!.Mode);
        }
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.App.Services.GetRequiredService<LocalEvidenceService>()
            .ReadAsync(new("ExplainBlockers", id, MachineId: "other-machine"), default));
    }

    [Fact]
    public async Task Shift_counts_all_scoped_events_and_excludes_the_end_instant()
    {
        await using var fixture = await Fixture.CreateAsync();
        var at = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        var repository = fixture.App.Services.GetRequiredService<IStructuredEventLogRepository>();
        await repository.AppendMissingAsync(Enumerable.Range(0, 510).Select(i => new StructuredEventWrite("shift-test", at, "test",
            new Dictionary<string, string> { ["machineId"] = "machine-package" }, EventKey: "shift-" + i)).ToArray(), default);
        await repository.AppendAsync(new("shift-test", at.AddHours(1), "test", new Dictionary<string, string> { ["machineId"] = "machine-package" }), default);
        await repository.AppendAsync(new("shift-test", at, "test", new Dictionary<string, string> { ["machineId"] = "different" }), default);
        var bundle = await fixture.App.Services.GetRequiredService<LocalEvidenceService>().ReadAsync(
            new("SummarizeShift", From: at, To: at.AddHours(1), MachineId: "machine-package"), default);
        Assert.Equal(510, Assert.Single(bundle.Facts, x => x.Code == "shift-test").Value.GetInt64());
        Assert.Contains(bundle.KnownLimitations, text => text.Contains("not parts"));
    }

    [Fact]
    public async Task Disabled_queue_returns_deterministic_answer_and_metadata_contains_no_question_body()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.App.Services.GetRequiredService<LocalAiRequestService>();
        var request = await service.SubmitAsync("owner", new("ExplainBlockers", "run:batch-operation:operation-package", Question: "private question"), default);
        Assert.Null(await service.ReadAsync("someone-else", request.RequestId, default));
        Assert.False(service.Cancel("someone-else", request.RequestId));
        LocalAiRequestStatus? result = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        do
        {
            await Task.Delay(20, timeout.Token);
            result = await service.ReadAsync("owner", request.RequestId, timeout.Token);
        } while (result!.Status is "QUEUED" or "RUNNING");
        Assert.Equal("COMPLETED", result.Status);
        Assert.Equal("DETERMINISTIC", result.Result!.Mode);
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM local_ai_requests WHERE status='COMPLETED' AND model_id IS NULL AND evidence_json NOT LIKE '%private question%'"));
    }

    [Theory]
    [InlineData("{\"claimOrder\":[0,0]}")]
    [InlineData("{\"claimOrder\":[0]}")]
    [InlineData("{\"claimOrder\":[0,2]}")]
    [InlineData("{\"claimOrder\":[0,1],\"text\":\"ready\"}")]
    [InlineData("{\"claimOrder\":[0,1],\"url\":\"http://outside\"}")]
    public void Model_cannot_invent_omit_duplicate_claims_or_add_prose_and_links(string response)
    {
        Assert.Throws<ArgumentException>(() => ModelClaimValidator.Validate(response, Bundle()));
        Assert.Equal(new[] { "second", "first" }, ModelClaimValidator.Validate("{\"claimOrder\":[1,0]}", Bundle()).Select(x => x.Text));
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("http://127.0.0.1/path")]
    [InlineData("http://user:pass@127.0.0.1/")]
    [InlineData("https://127.0.0.1/")]
    public void Adapter_configuration_rejects_non_loopback_roots(string address)
    {
        Assert.Throws<ArgumentException>(() => new LocalAiOptions { BaseUrl = address, ModelId = "test", ModelDigest = new('a', 64), RuntimeVersion = "tested" }.Validate());
    }
    private static EvidenceBundle Bundle() => new(1, "ExplainBlockers", DateTimeOffset.UtcNow, "v", "run", null, null, null,
        [new("E1", "test", "test", JsonSerializer.SerializeToElement(false), null, new("productionRun", "run"), "1", null, null, "RECORDED", "fact")],
        [new("first", "first", ["E1"]), new("second", "second", ["E1"])], []);
}
