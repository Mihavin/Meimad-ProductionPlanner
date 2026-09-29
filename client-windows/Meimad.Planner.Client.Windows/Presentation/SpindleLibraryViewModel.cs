using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.ToolPreparation;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>A Machine's default spindle adaptor and pull stud in the Setup grid.</summary>
internal sealed class MachineSpindleRow : INotifyPropertyChanged
{
    private ToolSpindleChoice adaptor;
    private ToolSpindleChoice pullStud;

    internal MachineSpindleRow(PlannerMachine machine, PlannerMachineSpindleInterface? current,
        IReadOnlyList<ToolSpindleChoice> adaptors, IReadOnlyList<ToolSpindleChoice> pullStuds)
    {
        MachineId = machine.MachineId;
        MachineText = $"{machine.Number} {machine.Name}";
        Version = current?.Version ?? 0;
        Adaptors = adaptors;
        PullStuds = pullStuds;
        adaptor = adaptors.FirstOrDefault(choice => choice.Id == current?.SpindleAdaptorId) ?? adaptors[0];
        pullStud = pullStuds.FirstOrDefault(choice => choice.Id == current?.PullStudId) ?? pullStuds[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string MachineId { get; }
    public string MachineText { get; }
    public int Version { get; }
    public IReadOnlyList<ToolSpindleChoice> Adaptors { get; }
    public IReadOnlyList<ToolSpindleChoice> PullStuds { get; }
    public bool IsChanged { get; private set; }

    public ToolSpindleChoice Adaptor
    {
        get => adaptor;
        set { if (value is not null && value != adaptor) { adaptor = value; IsChanged = true; Raise(); } }
    }

    public ToolSpindleChoice PullStud
    {
        get => pullStud;
        set { if (value is not null && value != pullStud) { pullStud = value; IsChanged = true; Raise(); } }
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Setup → Spindle adaptors and pull studs (owner decisions 2026-09-29, schema v91): the library of
/// spindle adaptors (taper length, gauge diameter, small end, tool-changer flange TCD × TCL) and pull
/// studs, and each Machine's default. The Tool Room draws every milling tool with its Machine's
/// default unless the tool picks another. Everyone signed in sees it; changes need the Setup permission.
/// </summary>
internal sealed class SpindleLibraryViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? api;
    private string clientId = string.Empty;
    private string userId = string.Empty;
    private bool canEdit;
    private bool isBusy;
    private string status = "Spindle adaptors and pull studs are drawn above the gauge line of every milling tool in the Tool Room.";
    private PlannerSpindleAdaptor? selectedAdaptor;
    private PlannerPullStud? selectedPullStud;
    private string adaptorName = string.Empty, taperLength = string.Empty, gaugeDiameter = string.Empty, smallEndDiameter = string.Empty;
    private string toolChangerDiameter = string.Empty, toolChangerLength = string.Empty, adaptorNotes = string.Empty;
    private bool adaptorActive = true;
    private string studName = string.Empty, studThread = string.Empty, studAngle = string.Empty, studOverall = string.Empty;
    private string studExposed = string.Empty, studKnob = string.Empty, studNeck = string.Empty, studPilot = string.Empty, studNotes = string.Empty;
    private bool studActive = true;

    internal SpindleLibraryViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => api is not null && !isBusy);
        NewAdaptorCommand = new AsyncCommand(() => { SelectedAdaptor = null; ClearAdaptor(); return Task.CompletedTask; }, () => canEdit && !isBusy);
        SaveAdaptorCommand = new AsyncCommand(SaveAdaptorAsync, () => canEdit && !isBusy);
        DeleteAdaptorCommand = new AsyncCommand(DeleteAdaptorAsync, () => canEdit && !isBusy && selectedAdaptor is not null);
        NewPullStudCommand = new AsyncCommand(() => { SelectedPullStud = null; ClearPullStud(); return Task.CompletedTask; }, () => canEdit && !isBusy);
        SavePullStudCommand = new AsyncCommand(SavePullStudAsync, () => canEdit && !isBusy);
        DeletePullStudCommand = new AsyncCommand(DeletePullStudAsync, () => canEdit && !isBusy && selectedPullStud is not null);
        SaveMachinesCommand = new AsyncCommand(SaveMachinesAsync, () => canEdit && !isBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PlannerSpindleAdaptor> Adaptors { get; } = [];
    public ObservableCollection<PlannerPullStud> PullStuds { get; } = [];
    public ObservableCollection<MachineSpindleRow> Machines { get; } = [];

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand NewAdaptorCommand { get; }
    public AsyncCommand SaveAdaptorCommand { get; }
    public AsyncCommand DeleteAdaptorCommand { get; }
    public AsyncCommand NewPullStudCommand { get; }
    public AsyncCommand SavePullStudCommand { get; }
    public AsyncCommand DeletePullStudCommand { get; }
    public AsyncCommand SaveMachinesCommand { get; }

    public bool CanEdit => canEdit;
    public string Status { get => status; private set => Set(ref status, value); }

    public PlannerSpindleAdaptor? SelectedAdaptor
    {
        get => selectedAdaptor;
        set
        {
            if (!Set(ref selectedAdaptor, value)) return;
            if (value is not null)
            {
                AdaptorName = value.Name;
                TaperLength = Text(value.TaperLength);
                GaugeDiameter = Text(value.GaugeDiameter);
                SmallEndDiameter = Text(value.SmallEndDiameter);
                ToolChangerDiameter = Text(value.ToolChangerDiameter);
                ToolChangerLength = Text(value.ToolChangerLength);
                AdaptorNotes = value.Notes ?? string.Empty;
                AdaptorActive = value.IsActive;
            }
            RaiseCommands();
        }
    }

    public PlannerPullStud? SelectedPullStud
    {
        get => selectedPullStud;
        set
        {
            if (!Set(ref selectedPullStud, value)) return;
            if (value is not null)
            {
                StudName = value.Name;
                StudThread = value.Thread ?? string.Empty;
                StudAngle = Text(value.Angle);
                StudOverallLength = Text(value.OverallLength);
                StudExposedLength = Text(value.ExposedLength);
                StudKnobDiameter = Text(value.KnobDiameter);
                StudNeckDiameter = Text(value.NeckDiameter);
                StudPilotDiameter = Text(value.PilotDiameter);
                StudNotes = value.Notes ?? string.Empty;
                StudActive = value.IsActive;
            }
            RaiseCommands();
        }
    }

    public string AdaptorName { get => adaptorName; set => Set(ref adaptorName, value ?? string.Empty); }
    public string TaperLength { get => taperLength; set => Set(ref taperLength, value ?? string.Empty); }
    public string GaugeDiameter { get => gaugeDiameter; set => Set(ref gaugeDiameter, value ?? string.Empty); }
    public string SmallEndDiameter { get => smallEndDiameter; set => Set(ref smallEndDiameter, value ?? string.Empty); }
    public string ToolChangerDiameter { get => toolChangerDiameter; set => Set(ref toolChangerDiameter, value ?? string.Empty); }
    public string ToolChangerLength { get => toolChangerLength; set => Set(ref toolChangerLength, value ?? string.Empty); }
    public string AdaptorNotes { get => adaptorNotes; set => Set(ref adaptorNotes, value ?? string.Empty); }
    public bool AdaptorActive { get => adaptorActive; set => Set(ref adaptorActive, value); }
    public string StudName { get => studName; set => Set(ref studName, value ?? string.Empty); }
    public string StudThread { get => studThread; set => Set(ref studThread, value ?? string.Empty); }
    public string StudAngle { get => studAngle; set => Set(ref studAngle, value ?? string.Empty); }
    public string StudOverallLength { get => studOverall; set => Set(ref studOverall, value ?? string.Empty); }
    public string StudExposedLength { get => studExposed; set => Set(ref studExposed, value ?? string.Empty); }
    public string StudKnobDiameter { get => studKnob; set => Set(ref studKnob, value ?? string.Empty); }
    public string StudNeckDiameter { get => studNeck; set => Set(ref studNeck, value ?? string.Empty); }
    public string StudPilotDiameter { get => studPilot; set => Set(ref studPilot, value ?? string.Empty); }
    public string StudNotes { get => studNotes; set => Set(ref studNotes, value ?? string.Empty); }
    public bool StudActive { get => studActive; set => Set(ref studActive, value); }

    internal void AttachSession(IPlannerApiClient? client, string newClientId, string newUserId, bool editor)
    {
        var changed = !ReferenceEquals(api, client);
        api = client;
        clientId = newClientId;
        userId = newUserId;
        canEdit = editor;
        OnPropertyChanged(nameof(CanEdit));
        RaiseCommands();
        if (changed && client is not null) _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (api is null) return;
        await RunAsync(async () =>
        {
            var library = await api.GetSpindleLibraryAsync();
            var machines = await api.ListMachinesAsync();
            Apply(library, machines);
            Status = $"{library.Adaptors.Count} spindle adaptor(s), {library.PullStuds.Count} pull stud(s).";
        });
    }

    internal async Task SaveAdaptorAsync()
    {
        if (api is null) return;
        await RunAsync(async () =>
        {
            var value = new SpindleAdaptorSave(
                selectedAdaptor?.Version ?? 0, AdaptorName.Trim(),
                Required(TaperLength, "Taper length"), Required(GaugeDiameter, "Gauge diameter"), Optional(SmallEndDiameter, "Small end diameter"),
                Required(ToolChangerDiameter, "Tool changer diameter (TCD)"), Required(ToolChangerLength, "Tool changer length (TCL)"),
                Blank(AdaptorNotes), AdaptorActive);
            var saved = await api.SaveSpindleAdaptorAsync(selectedAdaptor?.SpindleAdaptorId, value, clientId, userId);
            await ReloadAsync();
            SelectedAdaptor = Adaptors.FirstOrDefault(adaptor => adaptor.SpindleAdaptorId == saved.SpindleAdaptorId);
            Status = $"Spindle adaptor {saved.Name} saved.";
        });
    }

    internal async Task SavePullStudAsync()
    {
        if (api is null) return;
        await RunAsync(async () =>
        {
            var value = new PullStudSave(
                selectedPullStud?.Version ?? 0, StudName.Trim(), Blank(StudThread), Optional(StudAngle, "Angle"),
                Optional(StudOverallLength, "Overall length"), Required(StudExposedLength, "Length above the taper"),
                Required(StudKnobDiameter, "Knob diameter"), Optional(StudNeckDiameter, "Neck diameter"),
                Optional(StudPilotDiameter, "Collar diameter"), Blank(StudNotes), StudActive);
            var saved = await api.SavePullStudAsync(selectedPullStud?.PullStudId, value, clientId, userId);
            await ReloadAsync();
            SelectedPullStud = PullStuds.FirstOrDefault(stud => stud.PullStudId == saved.PullStudId);
            Status = $"Pull stud {saved.Name} saved.";
        });
    }

    internal async Task DeleteAdaptorAsync()
    {
        if (api is null || selectedAdaptor is not { } adaptor) return;
        await RunAsync(async () =>
        {
            await api.DeleteSpindleLibraryEntryAsync("spindle-adaptors", adaptor.SpindleAdaptorId, adaptor.Version, clientId, userId);
            await ReloadAsync();
            Status = $"Spindle adaptor {adaptor.Name} deleted.";
        });
    }

    internal async Task DeletePullStudAsync()
    {
        if (api is null || selectedPullStud is not { } stud) return;
        await RunAsync(async () =>
        {
            await api.DeleteSpindleLibraryEntryAsync("pull-studs", stud.PullStudId, stud.Version, clientId, userId);
            await ReloadAsync();
            Status = $"Pull stud {stud.Name} deleted.";
        });
    }

    internal async Task SaveMachinesAsync()
    {
        if (api is null) return;
        await RunAsync(async () =>
        {
            var changed = Machines.Where(row => row.IsChanged).ToArray();
            foreach (var row in changed)
            {
                await api.SaveMachineSpindleInterfaceAsync(row.MachineId, row.Adaptor.Id, row.PullStud.Id, row.Version, clientId, userId);
            }
            await ReloadAsync();
            Status = changed.Length == 0 ? "No Machine default was changed." : $"Spindle defaults saved for {changed.Length} Machine(s).";
        });
    }

    private async Task ReloadAsync() => Apply(await api!.GetSpindleLibraryAsync(), await api.ListMachinesAsync());

    private void Apply(PlannerSpindleLibrary library, IReadOnlyList<PlannerMachine> machines)
    {
        Replace(Adaptors, library.Adaptors);
        Replace(PullStuds, library.PullStuds);
        IReadOnlyList<ToolSpindleChoice> adaptorChoices = [new(null, "None"), .. library.Adaptors.Select(value => new ToolSpindleChoice(value.SpindleAdaptorId, value.Name))];
        IReadOnlyList<ToolSpindleChoice> studChoices = [new(null, "None"), .. library.PullStuds.Select(value => new ToolSpindleChoice(value.PullStudId, value.Name))];
        Replace(Machines, machines
            // Lathes hold their tools in the turret, not in a spindle taper.
            .Where(machine => !machine.ProcessType.Contains("lathe", StringComparison.OrdinalIgnoreCase)
                && !machine.ProcessType.Contains("turn", StringComparison.OrdinalIgnoreCase))
            .OrderBy(machine => machine.Number, StringComparer.OrdinalIgnoreCase)
            .Select(machine => new MachineSpindleRow(machine, library.Machines.FirstOrDefault(value => value.MachineId == machine.MachineId), adaptorChoices, studChoices)));
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (isBusy) return;
        isBusy = true;
        RaiseCommands();
        try
        {
            await action();
        }
        catch (SpindleInputException exception)
        {
            Status = exception.Message;
        }
        catch (Exception exception) when (exception is PlannerApiException or HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            Status = exception.Message;
        }
        finally
        {
            isBusy = false;
            RaiseCommands();
        }
    }

    private void ClearAdaptor()
    {
        AdaptorName = TaperLength = GaugeDiameter = SmallEndDiameter = ToolChangerDiameter = ToolChangerLength = AdaptorNotes = string.Empty;
        AdaptorActive = true;
    }

    private void ClearPullStud()
    {
        StudName = StudThread = StudAngle = StudOverallLength = StudExposedLength = StudKnobDiameter = StudNeckDiameter = StudPilotDiameter = StudNotes = string.Empty;
        StudActive = true;
    }

    private static double Required(string text, string field) =>
        Optional(text, field) ?? throw new SpindleInputException($"{field} is required.");

    private static double? Optional(string text, string field)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        return double.TryParse(trimmed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0
            ? value
            : throw new SpindleInputException($"{field} must be a number of millimetres.");
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Text(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, NewAdaptorCommand, SaveAdaptorCommand, DeleteAdaptorCommand, NewPullStudCommand, SavePullStudCommand, DeletePullStudCommand, SaveMachinesCommand })
            command.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private sealed class SpindleInputException(string message) : Exception(message);
}
