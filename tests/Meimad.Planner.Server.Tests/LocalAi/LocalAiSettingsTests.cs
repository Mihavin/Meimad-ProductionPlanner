using System.Net;
using System.Net.Http.Json;
using Meimad.Planner.Server.Application.LocalAi;
using Meimad.Planner.Server.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;
namespace Meimad.Planner.Server.Tests.LocalAi;

/// <summary>
/// The Server-held AI setting (owner request 2026-10-09: an option to enable AI). It is administrator-only,
/// versioned, persistent, applies without a restart and cannot enable inference without complete pins.
/// </summary>
public sealed class LocalAiSettingsTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Enabling_AI_is_permissioned_versioned_persistent_and_needs_complete_pins()
    {
        await using var fixture = await Fixture.CreateAsync();
        using (fixture.Client.SignedInWithOnly())
        {
            using var denied = await fixture.Client.GetAsync("/api/v1/local-ai/settings");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var initial = await fixture.Client.GetAsync("/api/v1/local-ai/settings");
        var settings = (await initial.Content.ReadFromJsonAsync<LocalAiSettings>())!;
        Assert.Equal(0, settings.Version);
        Assert.False(settings.Enabled);
        Assert.Equal("http://127.0.0.1:11434/", settings.BaseUrl);

        using var incomplete = await fixture.Client.PutAsJsonAsync("/api/v1/local-ai/settings",
            new LocalAiSettingsUpdate(0, true, "qwen3:8b", "not-a-digest", ""));
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        Assert.Contains("ai_settings_invalid", await incomplete.Content.ReadAsStringAsync());
        var options = fixture.App.Services.GetRequiredService<LocalAiOptions>();
        Assert.False(options.Enabled);

        using var greedy = await fixture.Client.PutAsJsonAsync("/api/v1/local-ai/settings",
            new LocalAiSettingsUpdate(0, true, "qwen3:8b", Digest, "0.12.3", CpuThreads: 999));
        Assert.Equal(HttpStatusCode.BadRequest, greedy.StatusCode);
        using var allCores = await fixture.Client.PutAsJsonAsync("/api/v1/local-ai/settings",
            new LocalAiSettingsUpdate(0, false, "", "", "", CpuThreads: Environment.ProcessorCount));
        Assert.Equal(Environment.ProcessorCount > 1 ? HttpStatusCode.BadRequest : HttpStatusCode.OK, allCores.StatusCode);
        if (Environment.ProcessorCount > 1) Assert.Contains("at most", await allCores.Content.ReadAsStringAsync());

        using var enabled = await fixture.Client.PutAsJsonAsync("/api/v1/local-ai/settings",
            new LocalAiSettingsUpdate(0, true, "qwen3:8b", "sha256:" + Digest, "0.12.3", CpuThreads: 1, KeepAliveSeconds: 60, MaxModelMemoryMb: 6000));
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var saved = (await enabled.Content.ReadFromJsonAsync<LocalAiSettings>())!;
        Assert.Equal(1, saved.Version);
        Assert.True(saved.Enabled);
        Assert.Equal((1, 60, 6000, Environment.ProcessorCount), (saved.CpuThreads, saved.KeepAliveSeconds, saved.MaxModelMemoryMb, saved.ProcessorCount));
        Assert.Equal((1, 60, 6000), (options.CpuThreads, options.KeepAliveSeconds, options.MaxModelMemoryMb));
        Assert.Equal(Digest, saved.ModelDigest); // the sha256: prefix is stripped
        Assert.Equal("test-user", saved.UpdatedBy); // the signed-in test account

        // The live options follow the setting without a restart, and the inference control sees it.
        Assert.True(options.Enabled);
        Assert.Equal("qwen3:8b", options.ModelId);
        Assert.Equal("0.12.3", options.RuntimeVersion);
        var service = fixture.App.Services.GetRequiredService<LocalAiRequestService>();
        Assert.True((await service.ReadControlAsync(default)).ConfiguredEnabled);
        Assert.Equal("NOT_PROBED", fixture.App.Services.GetRequiredService<LocalAiRuntimeDiagnostics>().State.Status);

        using var stale = await fixture.Client.PutAsJsonAsync("/api/v1/local-ai/settings",
            new LocalAiSettingsUpdate(0, false, "", "", ""));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("ai_settings_changed", await stale.Content.ReadAsStringAsync());

        using var disabled = await fixture.Client.PutAsJsonAsync("/api/v1/local-ai/settings",
            new LocalAiSettingsUpdate(1, false, "qwen3:8b", Digest, "0.12.3"));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.False(options.Enabled);
        Assert.Equal("DISABLED", fixture.App.Services.GetRequiredService<LocalAiRuntimeDiagnostics>().State.Status);
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM application_settings WHERE key='local_ai_settings'"));
        Assert.Equal(2L, await fixture.ScalarAsync("SELECT COUNT(*) FROM structured_event_log WHERE event_type='ai_settings_changed'"));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM structured_event_log WHERE event_type='ai_settings_changed' AND reason_code='ai_enabled'"));
    }

    [Fact]
    public async Task The_stored_setting_is_applied_at_startup_over_the_configuration_file()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var enabled = await fixture.Client.PutAsJsonAsync("/api/v1/local-ai/settings",
            new LocalAiSettingsUpdate(0, true, "qwen3:8b", Digest, "0.12.3"));
        enabled.EnsureSuccessStatusCode();
        // A fresh service over the same database (what a Server restart does) starts from the stored setting.
        var options = new LocalAiOptions();
        using var restarted = new LocalAiRequestService(fixture.App.Services.GetRequiredService<LocalEvidenceService>(),
            new OllamaModelClient(options), options, fixture.Database, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalAiRequestService>.Instance);
        await restarted.StartAsync(default);
        try
        {
            Assert.True(options.Enabled);
            Assert.Equal("qwen3:8b", options.ModelId);
            Assert.Equal(Digest, options.ModelDigest);
            Assert.True((await restarted.ReadControlAsync(default)).ConfiguredEnabled);
        }
        finally { await restarted.StopAsync(default); }
    }

    [Fact]
    public async Task The_runtime_probe_reports_an_absent_runtime_without_failing()
    {
        await using var fixture = await Fixture.CreateAsync("--LocalAi:BaseUrl=http://127.0.0.1:1/");
        using var probe = await fixture.Client.GetAsync("/api/v1/local-ai/runtime");
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        var result = (await probe.Content.ReadFromJsonAsync<LocalAiRuntimeProbe>())!;
        Assert.False(result.Reachable);
        Assert.Empty(result.Models);
        Assert.Contains("No local runtime answered", result.Detail);
        Assert.Contains("deterministic explanations remain available", result.Detail);
    }

    [Fact]
    public async Task The_runtime_probe_lists_installed_models_and_marks_remote_ones()
    {
        var client = new OllamaModelClient(new LocalAiOptions())
        {
            HandlerFactory = () => new StubHandler(path => path switch
            {
                "/api/version" => """{"version":"0.12.3"}""",
                "/api/tags" => """{"models":[{"name":"qwen3:8b","digest":"sha256:abc","size":5225000000},{"name":"gpt-oss:120b-cloud","digest":"def","remote_host":"https://ollama.com"}]}""",
                _ => throw new InvalidOperationException(path)
            })
        };
        var probe = await client.ProbeAsync(default);
        Assert.True(probe.Reachable);
        Assert.Equal("0.12.3", probe.RuntimeVersion);
        Assert.Equal([new LocalAiInstalledModel("qwen3:8b", "abc", false, 4983), new LocalAiInstalledModel("gpt-oss:120b-cloud", "def", true)], probe.Models);
        Assert.Contains("2 installed model(s)", probe.Detail);
    }

    private sealed class StubHandler(Func<string, string> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(request.RequestUri!.AbsolutePath)) });
    }
}
