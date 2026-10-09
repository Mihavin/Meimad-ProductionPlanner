using System.ComponentModel;
using Meimad.Planner.Client.Windows.Api;
namespace Meimad.Planner.Client.Windows.Presentation;
internal sealed class LocalCorpusViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? api;
    private bool canManage, busy;
    private int version, generation;
    public string Folders { get; set; } = "";
    public string Query { get; set; } = "";
    public string Status { get; private set; } = "No folder is implicitly approved. Load the current configuration before editing.";
    public IReadOnlyList<CorpusMatch> Matches { get; private set; } = [];
    public AsyncCommand LoadCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand IndexCommand { get; }
    public AsyncCommand SearchCommand { get; }
    private bool loaded;
    public LocalAiControl? InferenceControl { get; private set; }
    public string InferenceStatus { get; private set; } = "Load inference control to inspect the Server configuration.";
    public AsyncCommand LoadInferenceCommand { get; }
    public AsyncCommand SuspendInferenceCommand { get; }
    public AsyncCommand ResumeInferenceCommand { get; }
    // The Server-held AI setting: enable local inference and pin an installed model and runtime.
    public LocalAiSettings? AiSettings { get; private set; }
    public bool AiEnabled { get; set; }
    public string ModelId { get; set; } = "";
    public string ModelDigest { get; set; } = "";
    public string RuntimeVersion { get; set; } = "";
    public string BaseUrl { get; private set; } = "";
    // Resource limits, so the model cannot starve the Server PC (text fields: whole numbers).
    public string CpuThreads { get; set; } = "0";
    public string KeepAliveSeconds { get; set; } = "300";
    public string MaxModelMemoryMb { get; set; } = "0";
    public int ProcessorCount { get; private set; }
    public IReadOnlyList<LocalAiInstalledModel> InstalledModels { get; private set; } = [];
    private LocalAiInstalledModel? selectedModel;
    public LocalAiInstalledModel? SelectedModel
    {
        get => selectedModel;
        set
        {
            selectedModel = value;
            if (value is not null) { ModelId = value.Name; ModelDigest = value.Digest; PropertyChanged?.Invoke(this, new(null)); }
        }
    }
    public string AiSettingsStatus { get; private set; } = "Load the AI settings to see whether local inference is enabled on the Server.";
    public AsyncCommand LoadAiSettingsCommand { get; }
    public AsyncCommand ProbeRuntimeCommand { get; }
    public AsyncCommand SaveAiSettingsCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public LocalCorpusViewModel()
    {
        LoadCommand = new(() => RunAsync("load"), () => api is not null && canManage && !busy);
        SaveCommand = new(() => RunAsync("save"), () => api is not null && canManage && loaded && !busy);
        IndexCommand = new(() => RunAsync("index"), () => api is not null && canManage && loaded && !busy);
        SearchCommand = new(() => RunAsync("search"), () => api is not null && !busy);
        LoadInferenceCommand = new(() => ControlAsync(null), () => api is not null && canManage && !busy);
        SuspendInferenceCommand = new(() => ControlAsync(true), () => api is not null && canManage && !busy && InferenceControl is not null);
        ResumeInferenceCommand = new(() => ControlAsync(false), () => api is not null && canManage && !busy && InferenceControl is { ConfiguredEnabled: true, Suspended: true });
        LoadAiSettingsCommand = new(() => SettingsAsync("load"), () => api is not null && canManage && !busy);
        ProbeRuntimeCommand = new(() => SettingsAsync("probe"), () => api is not null && canManage && !busy);
        SaveAiSettingsCommand = new(() => SettingsAsync("save"), () => api is not null && canManage && !busy && AiSettings is not null);
    }
    internal async Task SettingsAsync(string action)
    {
        if (api is not { } client || !canManage || busy || action == "save" && AiSettings is null) return;
        var current = generation; busy = true; Changed();
        try
        {
            if (action == "probe")
            {
                var probe = await client.ProbeLocalAiRuntimeAsync();
                if (current != generation) return;
                InstalledModels = probe.Models; BaseUrl = probe.BaseUrl;
                if (probe.RuntimeVersion is { } runtimeVersion) RuntimeVersion = runtimeVersion;
                selectedModel = probe.Models.FirstOrDefault(model => model.Name == ModelId);
                AiSettingsStatus = probe.Detail;
            }
            else
            {
                LocalAiSettings settings;
                if (action == "load") settings = await client.GetLocalAiSettingsAsync();
                else
                {
                    if (!int.TryParse(CpuThreads.Trim(), out var threads) || !int.TryParse(KeepAliveSeconds.Trim(), out var keepAlive) || !int.TryParse(MaxModelMemoryMb.Trim(), out var memory))
                    { AiSettingsStatus = "CPU threads, keep-alive seconds and the memory budget must be whole numbers (0 = runtime default / no limit)."; return; }
                    settings = await client.UpdateLocalAiSettingsAsync(AiSettings!.Version, AiEnabled, ModelId.Trim(), ModelDigest.Trim(), RuntimeVersion.Trim(), threads, keepAlive, memory);
                }
                if (current != generation) return;
                AiSettings = settings; AiEnabled = settings.Enabled; ModelId = settings.ModelId; ModelDigest = settings.ModelDigest;
                RuntimeVersion = settings.RuntimeVersion; BaseUrl = settings.BaseUrl; ProcessorCount = settings.ProcessorCount;
                CpuThreads = settings.CpuThreads.ToString(); KeepAliveSeconds = settings.KeepAliveSeconds.ToString(); MaxModelMemoryMb = settings.MaxModelMemoryMb.ToString();
                selectedModel = InstalledModels.FirstOrDefault(model => model.Name == settings.ModelId);
                AiSettingsStatus = $"Local AI {(settings.Enabled ? "enabled" : "disabled")} (v{settings.Version}, runtime at {settings.BaseUrl})."
                    + (settings.UpdatedAt is { } at ? $" Changed {at.ToLocalTime():g} by {settings.UpdatedBy}." : " Server configuration file values.")
                    + (settings.Enabled ? " Explanations use the pinned model when the runtime answers; otherwise the deterministic evidence is shown." : "")
                    + $" Limits: {(settings.CpuThreads > 0 ? settings.CpuThreads + " CPU threads" : "runtime default threads")} of {settings.ProcessorCount}, model kept {settings.KeepAliveSeconds} s after a request, memory budget {(settings.MaxModelMemoryMb > 0 ? settings.MaxModelMemoryMb + " MB" : "unlimited")}.";
                if (action == "save" && settings.Enabled) InferenceControl = null;
            }
        }
        catch (Exception exception) { if (current == generation) AiSettingsStatus = exception.Message; }
        finally { if (current == generation) { busy = false; Changed(); } }
    }
    internal void AttachSession(IPlannerApiClient? client, bool manage)
    {
        generation++; api = client; canManage = manage; loaded = false; busy = false; Folders = ""; Query = ""; Matches = []; InferenceControl = null;
        AiSettings = null; AiEnabled = false; ModelId = ""; ModelDigest = ""; RuntimeVersion = ""; BaseUrl = ""; InstalledModels = []; selectedModel = null;
        CpuThreads = "0"; KeepAliveSeconds = "300"; MaxModelMemoryMb = "0"; ProcessorCount = 0;
        AiSettingsStatus = "Load the AI settings to see whether local inference is enabled on the Server."; Changed();
    }
    internal async Task ControlAsync(bool? suspend)
    {
        if (api is not { } client || !canManage || busy || suspend is not null && InferenceControl is null) return;
        var current = generation; busy = true; Changed();
        try
        {
            var control = suspend is null ? await client.GetLocalAiControlAsync()
                : await client.UpdateLocalAiControlAsync(InferenceControl!.Version, suspend.Value);
            if (current != generation) return;
            InferenceControl = control;
            InferenceStatus = $"Configuration: {(control.ConfiguredEnabled ? "enabled" : "disabled")}. Inference: {(control.Suspended ? "suspended" : "not suspended")}. Version {control.Version}. Deterministic explanations remain available.";
        }
        catch (Exception exception) { if (current == generation) InferenceStatus = exception.Message; }
        finally { if (current == generation) { busy = false; Changed(); } }
    }
    internal async Task RunAsync(string action)
    {
        if (api is not { } client || busy || action != "search" && !canManage || action is "save" or "index" && !loaded) return;
        var current = generation; busy = true; Changed();
        try
        {
            if (action is "load" or "save")
            {
                var value = action == "load" ? await client.GetLocalCorpusAsync()
                    : await client.UpdateLocalCorpusAsync(version, Folders.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                if (current != generation) return;
                version = value.Version; Folders = string.Join(Environment.NewLine, value.Roots.Select(root => root.Path)); loaded = true;
                Matches = []; Status = $"Configuration v{version}: {value.Roots.Count} approved folders. Rebuild the index after changes.";
            }
            else if (action == "index")
            {
                var result = await client.IndexLocalCorpusAsync(version);
                if (current != generation) return;
                Status = $"Indexed {result.IndexedFiles} files; skipped {result.SkippedFiles}. Completed {result.CompletedAt:u}.";
            }
            else
            {
                var rows = await client.SearchLocalCorpusAsync(Query);
                if (current != generation) return;
                Matches = rows; Status = $"{rows.Count} current matches (maximum 20). Historical references require applicability review.";
            }
        }
        catch (Exception exception) { if (current == generation) Status = exception.Message; }
        finally { if (current == generation) { busy = false; Changed(); } }
    }
    private void Changed()
    {
        PropertyChanged?.Invoke(this, new(null)); LoadCommand.RaiseCanExecuteChanged(); SaveCommand.RaiseCanExecuteChanged(); IndexCommand.RaiseCanExecuteChanged(); SearchCommand.RaiseCanExecuteChanged();
        LoadInferenceCommand.RaiseCanExecuteChanged(); SuspendInferenceCommand.RaiseCanExecuteChanged(); ResumeInferenceCommand.RaiseCanExecuteChanged();
        LoadAiSettingsCommand.RaiseCanExecuteChanged(); ProbeRuntimeCommand.RaiseCanExecuteChanged(); SaveAiSettingsCommand.RaiseCanExecuteChanged();
    }
}
