using System.Net;
using System.Net.Http.Json;
using Meimad.Planner.Server.Application.LocalAi;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;
namespace Meimad.Planner.Server.Tests.LocalAi;

public sealed class LocalAiPilotTests
{
    [Fact]
    public async Task Inference_control_is_permissioned_versioned_persistent_and_cannot_enable_unconfigured_host()
    {
        await using var fixture = await Fixture.CreateAsync();
        using (fixture.Client.SignedInWithOnly())
        {
            using var denied = await fixture.Client.PutAsJsonAsync("/api/v1/local-ai/control", new LocalAiControlUpdate(0, true));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        var service = fixture.App.Services.GetRequiredService<LocalAiRequestService>();
        var current = await service.ReadControlAsync(default);
        Assert.False(current.ConfiguredEnabled);
        var saved = await service.UpdateControlAsync(new(current.Version, true), "test-admin", default);
        Assert.True(saved.Suspended);
        Assert.Equal(saved, await service.ReadControlAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateControlAsync(new(0, true), "other-admin", default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateControlAsync(new(saved.Version, false), "test-admin", default));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM application_settings WHERE key='local_ai_pilot_control'"));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM structured_event_log WHERE event_type='ai_control_changed'"));
        var answer = await CompletedAsync(service, "owner");
        Assert.Equal("DETERMINISTIC", answer.Result!.Mode);
        Assert.Contains("suspended", answer.Result.FallbackReason);
    }

    [Fact]
    public async Task Feedback_is_owner_scoped_stale_checked_and_never_changes_Run_facts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.App.Services.GetRequiredService<LocalAiRequestService>();
        var work = await CompletedAsync(service, "owner");
        var prior = await fixture.ScalarAsync("SELECT version FROM production_runs WHERE id='run:batch-operation:operation-package'");
        Assert.Null(await service.SaveFeedbackAsync("another", work.RequestId, new(0, true), default));
        var saved = await service.SaveFeedbackAsync("owner", work.RequestId, new(0, false, "Please clarify the missing material evidence."), default);
        Assert.Equal(1, saved!.Version);
        Assert.Null(await service.ReadFeedbackAsync("another", work.RequestId, default));
        Assert.Equal(saved, await service.ReadFeedbackAsync("owner", work.RequestId, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveFeedbackAsync("owner", work.RequestId, new(0, true), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveFeedbackAsync("owner", work.RequestId, new(1, true, new string('x', 501)), default));
        var revised = await service.SaveFeedbackAsync("owner", work.RequestId, new(1, true), default);
        Assert.Equal(2, revised!.Version);
        Assert.Equal(prior, await fixture.ScalarAsync("SELECT version FROM production_runs WHERE id='run:batch-operation:operation-package'"));
    }
    private static async Task<LocalAiRequestStatus> CompletedAsync(LocalAiRequestService service, string owner)
    {
        var submitted = await service.SubmitAsync(owner, new("ExplainBlockers", "run:batch-operation:operation-package"), default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        LocalAiRequestStatus work;
        do { await Task.Delay(20, timeout.Token); work = (await service.ReadAsync(owner, submitted.RequestId, timeout.Token))!; }
        while (work.Status is "QUEUED" or "RUNNING");
        Assert.Equal("COMPLETED", work.Status);
        return work;
    }
}
