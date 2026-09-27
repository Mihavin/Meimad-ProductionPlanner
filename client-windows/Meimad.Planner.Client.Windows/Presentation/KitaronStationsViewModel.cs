using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// Setup page for the Kitaron station lookup (OD-038): every station the synchronization has seen,
/// the role Kitaron's flags suggest, and the planner's decision about what its route steps become in
/// Meimad. Reading is open to every client; deciding needs Edit Mode.
/// </summary>
internal sealed class KitaronStationsViewModel : INotifyPropertyChanged
{
    /// <summary>A saved decision asks the Server to synchronize Kitaron at once.</summary>
    internal const string SynchronizationNote = "The Server synchronizes Kitaron now; the Case operations follow within about a minute.";

    private IPlannerApiClient? apiClient;
    private string clientId = string.Empty;
    private long editGeneration;
    private bool isEditor;
    private bool isBusy;
    private string statusMessage = "Connect to decide how Kitaron stations are imported.";
    private PlannerKitaronStation? selectedStation;
    private string importRole = "UNDECIDED";
    private string machineType = string.Empty;
    private PlannerWorkstationType? selectedWorkstationType;
    private PlannerExternalResource? selectedExternalResource;
    private string minutesPerPart = "0";
    private string minutesPerBatch = "0";
    private string capacityRequired = "1";
    private string notes = string.Empty;
    private bool showDecided = true;

    internal KitaronStationsViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => apiClient is not null && !IsBusy);
        SaveDecisionCommand = new AsyncCommand(SaveDecisionAsync, () => CanEdit && SelectedStation is not null);
        AcceptMachineSuggestionsCommand = new AsyncCommand(AcceptMachineSuggestionsAsync, () => CanEdit
            && Stations.Any(station => station.IsUndecided && station.SuggestedRole == "MACHINE"));
        IgnoreEmptySuggestionsCommand = new AsyncCommand(IgnoreEmptySuggestionsAsync, () => CanEdit
            && Stations.Any(station => station.IsUndecided && station.SuggestedRole == "IGNORE"));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PlannerKitaronStation> Stations { get; } = [];

    public ObservableCollection<PlannerKitaronStation> VisibleStations { get; } = [];

    public ObservableCollection<PlannerWorkstationType> WorkstationTypes { get; } = [];

    public ObservableCollection<PlannerExternalResource> ExternalResources { get; } = [];

    public ObservableCollection<string> MachineTypeOptions { get; } = [string.Empty];

    public IReadOnlyList<string> ImportRoles => KitaronStationRoleLabels.Roles;

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand SaveDecisionCommand { get; }

    public AsyncCommand AcceptMachineSuggestionsCommand { get; }

    public AsyncCommand IgnoreEmptySuggestionsCommand { get; }

    public bool IsEditor => isEditor;

    public bool CanEdit => apiClient is not null && isEditor && !IsBusy;

    public bool IsBusy
    {
        get => isBusy;
        private set { if (SetField(ref isBusy, value)) { OnPropertyChanged(nameof(CanEdit)); RaiseCommands(); } }
    }

    public string StatusMessage { get => statusMessage; private set => SetField(ref statusMessage, value); }

    public bool ShowDecided
    {
        get => showDecided;
        set { if (SetField(ref showDecided, value)) ApplyFilter(); }
    }

    public int UndecidedCount => Stations.Count(station => station.IsUndecided);

    public string Summary => Stations.Count == 0
        ? "No Kitaron stations have been discovered yet. Run a Kitaron synchronization on the Server first."
        : $"{Stations.Count} station(s), {UndecidedCount} undecided. Undecided stations import nothing; their route steps are skipped and counted by the synchronization.";

    public PlannerKitaronStation? SelectedStation
    {
        get => selectedStation;
        set
        {
            if (!SetField(ref selectedStation, value)) return;
            if (value is not null)
            {
                ImportRole = value.IsUndecided ? value.SuggestedRole : value.ImportRole;
                MachineType = value.MachineType ?? string.Empty;
                SelectedWorkstationType = WorkstationTypes.FirstOrDefault(type => type.Id == value.WorkstationTypeId);
                SelectedExternalResource = ExternalResources.FirstOrDefault(resource => resource.Id == value.ExternalResourceId);
                MinutesPerPart = value.DefaultMinutesPerPart.ToString(CultureInfo.InvariantCulture);
                MinutesPerBatch = value.DefaultMinutesPerBatch.ToString(CultureInfo.InvariantCulture);
                CapacityRequired = value.CapacityRequired.ToString(CultureInfo.InvariantCulture);
                Notes = value.Notes ?? string.Empty;
            }
            OnPropertyChanged(nameof(SelectedStationHint));
            RaiseCommands();
        }
    }

    public string SelectedStationHint => SelectedStation is null
        ? "Select a station to decide it."
        : $"{SelectedStation.StationName}: {SelectedStation.RouteRows} route step(s), {SelectedStation.PlannedRows} planned by Kitaron, {SelectedStation.SupplierRows} with a supplier. Suggested: {SelectedStation.SuggestedRoleLabel}.";

    public string ImportRole
    {
        get => importRole;
        set
        {
            if (SetField(ref importRole, value))
            {
                OnPropertyChanged(nameof(IsMachineRole));
                OnPropertyChanged(nameof(IsWorkstationRole));
                OnPropertyChanged(nameof(IsExternalRole));
                OnPropertyChanged(nameof(HasTimeDefaults));
            }
        }
    }

    public bool IsMachineRole => ImportRole == "MACHINE";

    public bool IsWorkstationRole => ImportRole == "WORKSTATION";

    public bool IsExternalRole => ImportRole == "EXTERNAL";

    public bool HasTimeDefaults => ImportRole == "WORKSTATION";

    public string MachineType { get => machineType; set => SetField(ref machineType, value); }

    public PlannerWorkstationType? SelectedWorkstationType { get => selectedWorkstationType; set => SetField(ref selectedWorkstationType, value); }

    public PlannerExternalResource? SelectedExternalResource { get => selectedExternalResource; set => SetField(ref selectedExternalResource, value); }

    public string MinutesPerPart { get => minutesPerPart; set => SetField(ref minutesPerPart, value); }

    public string MinutesPerBatch { get => minutesPerBatch; set => SetField(ref minutesPerBatch, value); }

    public string CapacityRequired { get => capacityRequired; set => SetField(ref capacityRequired, value); }

    public string Notes { get => notes; set => SetField(ref notes, value); }

    internal void AttachSession(IPlannerApiClient? client, string newClientId, long generation, bool editor)
    {
        apiClient = client;
        clientId = newClientId;
        editGeneration = generation;
        isEditor = editor;
        OnPropertyChanged(nameof(IsEditor));
        OnPropertyChanged(nameof(CanEdit));
        RaiseCommands();
    }

    internal async Task RefreshAsync()
    {
        if (apiClient is null) return;
        var selectedId = SelectedStation?.KitaronStationId;
        IsBusy = true;
        try
        {
            var stations = apiClient.ListKitaronStationsAsync();
            var types = apiClient.ListWorkstationTypesAsync();
            var external = apiClient.ListExternalResourcesAsync();
            var machineTypes = apiClient.ListMachineTypesAsync();
            await Task.WhenAll(stations, types, external, machineTypes);
            Replace(WorkstationTypes, (await types).Where(type => type.IsActive));
            Replace(ExternalResources, (await external).Where(resource => resource.IsActive));
            MachineTypeOptions.Clear();
            MachineTypeOptions.Add(string.Empty);
            foreach (var type in (await machineTypes).OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase))
                MachineTypeOptions.Add(type.Name);
            Replace(Stations, await stations);
            ApplyFilter();
            SelectedStation = Stations.FirstOrDefault(station => station.KitaronStationId == selectedId);
            StatusMessage = $"Kitaron stations refreshed at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = Friendly(exception); }
        finally { IsBusy = false; }
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(UndecidedCount));
    }

    /// <summary>Builds the decision the form describes, or explains why it cannot be saved.</summary>
    internal KitaronStationDecision? BuildDecision(out string? problem)
    {
        problem = null;
        if (SelectedStation is null) { problem = "Select a station first."; return null; }
        if (!double.TryParse(MinutesPerPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var perPart) || perPart < 0
            || !double.TryParse(MinutesPerBatch, NumberStyles.Float, CultureInfo.InvariantCulture, out var perBatch) || perBatch < 0)
        {
            problem = "Default minutes must be numbers of zero or more.";
            return null;
        }
        if (!int.TryParse(CapacityRequired, NumberStyles.None, CultureInfo.InvariantCulture, out var capacity) || capacity < 1)
        {
            problem = "Capacity required must be a positive whole number.";
            return null;
        }
        if (ImportRole == "WORKSTATION" && SelectedWorkstationType is null)
        {
            problem = "A Workstation step needs a Workstation type. Create one under Resource Types & Skills first.";
            return null;
        }
        if (ImportRole == "EXTERNAL" && SelectedExternalResource is null)
        {
            problem = "An External resource step needs an External Resource. Create one under Resource Types & Skills first.";
            return null;
        }
        return new KitaronStationDecision(
            ImportRole,
            ImportRole == "MACHINE" && !string.IsNullOrWhiteSpace(MachineType) ? MachineType.Trim() : null,
            ImportRole == "WORKSTATION" ? SelectedWorkstationType!.Id : null,
            ImportRole == "EXTERNAL" ? SelectedExternalResource!.Id : null,
            perPart, perBatch, capacity,
            string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
            SelectedStation.Version);
    }

    private async Task SaveDecisionAsync()
    {
        var decision = BuildDecision(out var problem);
        if (decision is null) { StatusMessage = problem!; return; }
        var station = SelectedStation!;
        await MutateAsync(async () => await apiClient!.DecideKitaronStationAsync(station.KitaronStationId, decision, clientId, editGeneration),
            $"{station.StationName} saved as {KitaronStationRoleLabels.Label(decision.ImportRole)}. " + SynchronizationNote);
    }

    private Task AcceptMachineSuggestionsAsync() => DecideSuggestedAsync("MACHINE");

    private Task IgnoreEmptySuggestionsAsync() => DecideSuggestedAsync("IGNORE");

    /// <summary>
    /// Applies the suggested role to every undecided station that suggests it. Only roles without a
    /// target (Machine operation without a Machine Type, Ignore) can be accepted in bulk; Workstation
    /// and External steps need their type or resource chosen one by one.
    /// </summary>
    private async Task DecideSuggestedAsync(string role)
    {
        var candidates = Stations.Where(station => station.IsUndecided && station.SuggestedRole == role).ToArray();
        await MutateAsync(async () =>
        {
            foreach (var station in candidates)
            {
                await apiClient!.DecideKitaronStationAsync(station.KitaronStationId, new KitaronStationDecision(
                    role, null, null, null, 0, 0, 1, null, station.Version), clientId, editGeneration);
            }
        }, $"{candidates.Length} station(s) decided as {KitaronStationRoleLabels.Label(role)}. " + SynchronizationNote);
    }

    private async Task MutateAsync(Func<Task> action, string success)
    {
        if (!CanEdit) { StatusMessage = "Edit Mode is required to decide Kitaron stations."; return; }
        IsBusy = true;
        var succeeded = false;
        try
        {
            await action();
            succeeded = true;
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = Friendly(exception); }
        finally { IsBusy = false; }
        if (!succeeded) return;
        await RefreshAsync();
        StatusMessage = success;
    }

    private void ApplyFilter()
    {
        Replace(VisibleStations, Stations.Where(station => ShowDecided || station.IsUndecided));
    }

    private void RaiseCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        SaveDecisionCommand.RaiseCanExecuteChanged();
        AcceptMachineSuggestionsCommand.RaiseCanExecuteChanged();
        IgnoreEmptySuggestionsCommand.RaiseCanExecuteChanged();
    }

    private static bool IsExpected(Exception exception) =>
        exception is PlannerApiException or PlannerProtocolException or HttpRequestException or TaskCanceledException;

    private static string Friendly(Exception exception) => exception is PlannerApiException api ? api.Message : exception.Message;

    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> values)
    {
        destination.Clear();
        foreach (var value in values) destination.Add(value);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
