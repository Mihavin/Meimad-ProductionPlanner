using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>The option to enable AI (owner request 2026-10-09): load, probe the runtime, pick a model, save.</summary>
public sealed class LocalCorpusViewModelTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task An_administrator_enables_AI_by_picking_an_installed_model_and_saving()
    {
        var api = new FakeApi();
        var viewModel = new LocalCorpusViewModel();
        viewModel.AttachSession(api, manage: true);

        await viewModel.SettingsAsync("load");
        Assert.False(viewModel.AiEnabled);
        Assert.Equal(0, viewModel.AiSettings!.Version);
        Assert.Contains("disabled", viewModel.AiSettingsStatus);
        Assert.Contains("Server configuration file", viewModel.AiSettingsStatus);

        await viewModel.SettingsAsync("probe");
        Assert.Equal(2, viewModel.InstalledModels.Count);
        Assert.Equal("0.12.3", viewModel.RuntimeVersion);
        Assert.Equal("Runtime 0.12.3: 2 installed model(s).", viewModel.AiSettingsStatus);

        viewModel.SelectedModel = viewModel.InstalledModels[0];
        Assert.Equal("qwen3:8b", viewModel.ModelId);
        Assert.Equal(Digest, viewModel.ModelDigest);
        viewModel.AiEnabled = true;
        viewModel.CpuThreads = "4"; viewModel.KeepAliveSeconds = "120"; viewModel.MaxModelMemoryMb = "6000";

        await viewModel.SettingsAsync("save");
        Assert.Equal((0, true, "qwen3:8b", Digest, "0.12.3"), api.Saved);
        Assert.Equal((4, 120, 6000), api.Limits);
        Assert.Contains("4 CPU threads of 8", viewModel.AiSettingsStatus);
        Assert.Contains("memory budget 6000 MB", viewModel.AiSettingsStatus);
        Assert.Equal(1, viewModel.AiSettings!.Version);
        Assert.True(viewModel.AiEnabled);
        Assert.Contains("Local AI enabled", viewModel.AiSettingsStatus);
        Assert.Contains("by admin", viewModel.AiSettingsStatus);
        Assert.Same(viewModel.InstalledModels[0], viewModel.SelectedModel);
        Assert.Null(viewModel.InferenceControl); // the control is reloaded after enabling

        viewModel.CpuThreads = "four";
        await viewModel.SettingsAsync("save");
        Assert.Equal((4, 120, 6000), api.Limits); // nothing sent
        Assert.Contains("whole numbers", viewModel.AiSettingsStatus);
    }

    [Fact]
    public async Task A_refused_save_keeps_the_entered_values_and_shows_the_reason()
    {
        var api = new FakeApi { Refuse = "Pin an installed local model ID, 64-character SHA-256 digest and tested runtime version before enabling AI." };
        var viewModel = new LocalCorpusViewModel();
        viewModel.AttachSession(api, manage: true);
        await viewModel.SettingsAsync("load");
        viewModel.AiEnabled = true;
        viewModel.ModelId = "qwen3:8b";
        await viewModel.SettingsAsync("save");
        Assert.Null(api.Saved);
        Assert.True(viewModel.AiEnabled);
        Assert.Equal("qwen3:8b", viewModel.ModelId);
        Assert.StartsWith("Pin an installed local model ID", viewModel.AiSettingsStatus);
        Assert.True(viewModel.SaveAiSettingsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Without_the_setup_permission_the_AI_settings_are_not_touched()
    {
        var api = new FakeApi();
        var viewModel = new LocalCorpusViewModel();
        viewModel.AttachSession(api, manage: false);
        Assert.False(viewModel.LoadAiSettingsCommand.CanExecute(null));
        Assert.False(viewModel.ProbeRuntimeCommand.CanExecute(null));
        Assert.False(viewModel.SaveAiSettingsCommand.CanExecute(null));
        await viewModel.SettingsAsync("load");
        await viewModel.SettingsAsync("probe");
        Assert.Null(viewModel.AiSettings);
        Assert.Empty(viewModel.InstalledModels);
        Assert.Equal(0, api.Calls);
    }

    private sealed class FakeApi : StubPlannerApiClient, IPlannerApiClient
    {
        private LocalAiSettings current = new(0, false, "", "", "", "http://127.0.0.1:11434/", null, null);
        internal (int Version, bool Enabled, string ModelId, string Digest, string Runtime)? Saved { get; private set; }
        internal (int CpuThreads, int KeepAliveSeconds, int MaxModelMemoryMb)? Limits { get; private set; }
        internal string? Refuse { get; init; }
        internal int Calls { get; private set; }

        public Task<LocalAiSettings> GetLocalAiSettingsAsync(CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(current); }

        public Task<LocalAiSettings> UpdateLocalAiSettingsAsync(int version, bool enabled, string modelId, string modelDigest, string runtimeVersion, int cpuThreads, int keepAliveSeconds, int maxModelMemoryMb, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Refuse is not null) throw new InvalidOperationException(Refuse);
            Saved = (version, enabled, modelId, modelDigest, runtimeVersion);
            Limits = (cpuThreads, keepAliveSeconds, maxModelMemoryMb);
            current = new(version + 1, enabled, modelId, modelDigest, runtimeVersion, current.BaseUrl, new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero), "admin",
                cpuThreads, keepAliveSeconds, maxModelMemoryMb, 8);
            return Task.FromResult(current);
        }

        public Task<LocalAiRuntimeProbe> ProbeLocalAiRuntimeAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new LocalAiRuntimeProbe(true, current.BaseUrl, "0.12.3",
                [new LocalAiInstalledModel("qwen3:8b", Digest, false, 4984), new LocalAiInstalledModel("gpt-oss:120b-cloud", "abc", true)],
                "Runtime 0.12.3: 2 installed model(s)."));
        }

    }
}
