using Meimad.Planner.Server.Application.LocalAi;
using Meimad.Planner.Server.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;
namespace Meimad.Planner.Server.Tests.LocalAi;

public sealed class LocalAiQueueTests
{
    [Fact]
    public async Task Queue_bounds_per_user_and_total_work_and_cancellation_releases_the_worker()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = new WaitingModel();
        using var service = new LocalAiRequestService(fixture.App.Services.GetRequiredService<LocalEvidenceService>(), model,
            new LocalAiOptions { Enabled = true }, fixture.Database, TimeProvider.System, NullLogger<LocalAiRequestService>.Instance);
        await service.StartAsync(default);
        try
        {
            var request = new ExplanationRequest("ExplainBlockers", "run:batch-operation:operation-package");
            var first = await service.SubmitAsync("first", request, default);
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Assert.ThrowsAsync<LocalAiBusyException>(() => service.SubmitAsync("first", request, default));
            for (var i = 0; i < 8; i++) await service.SubmitAsync("queued-" + i, request, default);
            await Assert.ThrowsAsync<LocalAiBusyException>(() => service.SubmitAsync("overflow", request, default));
            Assert.True(service.Cancel("first", first.RequestId));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await service.ReadAsync("first", first.RequestId, timeout.Token))!.Status is "QUEUED" or "RUNNING")
                await Task.Delay(20, timeout.Token);
            Assert.Equal("CANCELLED", (await service.ReadAsync("first", first.RequestId, default))!.Status);
        }
        finally { await service.StopAsync(default); }
    }

    private sealed class WaitingModel : ILocalModelClient
    {
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ModelResult> CompleteAsync(EvidenceBundle bundle, CancellationToken token)
        { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }
    }
}
