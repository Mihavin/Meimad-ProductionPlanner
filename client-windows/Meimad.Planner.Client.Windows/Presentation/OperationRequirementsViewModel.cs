using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// The auxiliary steps of the selected Case Operation: Workstation steps (inspection, deburring,
/// packing), External Resource steps (plating, painting) and Employee Skill requirements, before or
/// after the Machine. Kitaron-imported steps are shown with their origin; the planner may edit,
/// add or delete them, and the Server keeps a deleted Kitaron step out of the next synchronization.
/// </summary>
internal sealed class OperationRequirementsViewModel : INotifyPropertyChanged
{
    internal static readonly IReadOnlyList<string> ResourceClasses = ["WORKSTATION", "EXTERNAL", "EMPLOYEE"];
    internal static readonly IReadOnlyList<string> Directions = ["FORWARD", "BACKWARD"];

    private IPlannerApiClient? apiClient;
    private string clientId = string.Empty;
    private long editGeneration;
    private bool isEditor;
    private bool isBusy;
    private string? caseOperationId;
    private string statusMessage = string.Empty;
    private RequirementRowViewModel? selectedRequirement;
    private bool isEditing;
    private string editorHeading = "Add auxiliary step";
    private string name = string.Empty;
    private string stepNumber = string.Empty;
    private string resourceClass = "WORKSTATION";
    private string direction = "FORWARD";
    private PlannerWorkstationType? selectedWorkstationType;
    private PlannerExternalResource? selectedExternalResource;
    private PlannerSkill? selectedSkill;
    private string capabilityText = string.Empty;
    private string capacity = "1";
    private string minutesPerBatch = "0";
    private string minutesPerPart = "0";
    private RequirementRowViewModel? selectedPredecessor;
    private bool isActive = true;
    private string? editingId;
    private int editingVersion;

    internal OperationRequirementsViewModel()
    {
        BeginAddCommand = new AsyncCommand(BeginAddAsync, () => CanEdit && caseOperationId is not null);
        BeginEditCommand = new AsyncCommand(BeginEditAsync, () => CanEdit && SelectedRequirement is not null);
        CancelCommand = new AsyncCommand(CancelAsync, () => IsEditing && !IsBusy);
        SaveCommand = new AsyncCommand(SaveAsync, () => CanEdit && IsEditing);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => CanEdit && SelectedRequirement is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RequirementRowViewModel> Requirements { get; } = [];

    public ObservableCollection<PlannerWorkstationType> WorkstationTypes { get; } = [];

    public ObservableCollection<PlannerExternalResource> ExternalResources { get; } = [];

    public ObservableCollection<PlannerSkill> Skills { get; } = [];

    public ObservableCollection<RequirementRowViewModel> PredecessorOptions { get; } = [];

    public IReadOnlyList<string> ResourceClassOptions => ResourceClasses;

    public IReadOnlyList<string> DirectionOptions => Directions;

    public AsyncCommand BeginAddCommand { get; }

    public AsyncCommand BeginEditCommand { get; }

    public AsyncCommand CancelCommand { get; }

    public AsyncCommand SaveCommand { get; }

    public AsyncCommand DeleteCommand { get; }

    public bool CanEdit => apiClient is not null && isEditor && !IsBusy;

    public bool IsBusy
    {
        get => isBusy;
        private set { if (SetField(ref isBusy, value)) { OnPropertyChanged(nameof(CanEdit)); RaiseCommands(); } }
    }

    public string StatusMessage { get => statusMessage; private set => SetField(ref statusMessage, value); }

    public bool HasOperation => caseOperationId is not null;

    public string Summary => caseOperationId is null
        ? "Select an Operation to see its auxiliary steps."
        : Requirements.Count == 0
            ? "No auxiliary steps. The Machine operation stands alone on the Timeline."
            : $"{Requirements.Count} auxiliary step(s): {Requirements.Count(row => row.IsBackward)} before and {Requirements.Count(row => !row.IsBackward)} after the Machine; {Requirements.Count(row => row.IsKitaronManaged)} from Kitaron.";

    public RequirementRowViewModel? SelectedRequirement
    {
        get => selectedRequirement;
        set { if (SetField(ref selectedRequirement, value)) RaiseCommands(); }
    }

    public bool IsEditing { get => isEditing; private set { if (SetField(ref isEditing, value)) RaiseCommands(); } }

    public string EditorHeading { get => editorHeading; private set => SetField(ref editorHeading, value); }

    public string Name { get => name; set => SetField(ref name, value); }

    public string StepNumber { get => stepNumber; set => SetField(ref stepNumber, value); }

    public string ResourceClass
    {
        get => resourceClass;
        set
        {
            if (SetField(ref resourceClass, value))
            {
                OnPropertyChanged(nameof(IsWorkstationClass));
                OnPropertyChanged(nameof(IsExternalClass));
                OnPropertyChanged(nameof(IsEmployeeClass));
                OnPropertyChanged(nameof(HasDurations));
            }
        }
    }

    public bool IsWorkstationClass => ResourceClass == "WORKSTATION";

    public bool IsExternalClass => ResourceClass == "EXTERNAL";

    public bool IsEmployeeClass => ResourceClass == "EMPLOYEE";

    public bool HasDurations => ResourceClass != "EXTERNAL";

    public string Direction { get => direction; set => SetField(ref direction, value); }

    public PlannerWorkstationType? SelectedWorkstationType { get => selectedWorkstationType; set => SetField(ref selectedWorkstationType, value); }

    public PlannerExternalResource? SelectedExternalResource { get => selectedExternalResource; set => SetField(ref selectedExternalResource, value); }

    public PlannerSkill? SelectedSkill { get => selectedSkill; set => SetField(ref selectedSkill, value); }

    public string CapabilityText { get => capabilityText; set => SetField(ref capabilityText, value); }

    public string Capacity { get => capacity; set => SetField(ref capacity, value); }

    public string MinutesPerBatch { get => minutesPerBatch; set => SetField(ref minutesPerBatch, value); }

    public string MinutesPerPart { get => minutesPerPart; set => SetField(ref minutesPerPart, value); }

    public RequirementRowViewModel? SelectedPredecessor { get => selectedPredecessor; set => SetField(ref selectedPredecessor, value); }

    public bool IsActive { get => isActive; set => SetField(ref isActive, value); }

    internal void AttachSession(IPlannerApiClient? client, string newClientId, long generation, bool editor)
    {
        if (!ReferenceEquals(apiClient, client))
        {
            apiClient = client;
            WorkstationTypes.Clear();
            ExternalResources.Clear();
            Skills.Clear();
        }
        clientId = newClientId;
        editGeneration = generation;
        isEditor = editor;
        OnPropertyChanged(nameof(CanEdit));
        RaiseCommands();
    }

    /// <summary>Loads the steps of an Operation; null clears the panel.</summary>
    internal async Task LoadAsync(string? operationId)
    {
        caseOperationId = operationId;
        IsEditing = false;
        SelectedRequirement = null;
        Requirements.Clear();
        OnPropertyChanged(nameof(HasOperation));
        if (operationId is null || apiClient is null)
        {
            OnPropertyChanged(nameof(Summary));
            RaiseCommands();
            return;
        }
        IsBusy = true;
        try
        {
            if (WorkstationTypes.Count == 0 && ExternalResources.Count == 0 && Skills.Count == 0)
            {
                var types = apiClient.ListWorkstationTypesAsync();
                var external = apiClient.ListExternalResourcesAsync();
                var skills = apiClient.ListSkillsAsync();
                await Task.WhenAll(types, external, skills);
                Replace(WorkstationTypes, await types);
                Replace(ExternalResources, await external);
                Replace(Skills, await skills);
            }
            var rows = await apiClient.ListOperationRequirementsAsync(operationId);
            if (!string.Equals(caseOperationId, operationId, StringComparison.Ordinal)) return;
            var byId = rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
            Replace(Requirements, rows
                .OrderBy(row => row.Direction == "BACKWARD" ? 0 : 1)
                .ThenBy(row => row.SequencePosition)
                .Select(row => new RequirementRowViewModel(row, Describe(row), byId)));
            StatusMessage = string.Empty;
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = Friendly(exception); }
        finally { IsBusy = false; }
        OnPropertyChanged(nameof(Summary));
        RaiseCommands();
    }

    private Task BeginAddAsync()
    {
        editingId = null;
        editingVersion = 0;
        EditorHeading = "Add auxiliary step";
        Name = string.Empty;
        StepNumber = string.Empty;
        ResourceClass = "WORKSTATION";
        Direction = "FORWARD";
        SelectedWorkstationType = WorkstationTypes.FirstOrDefault(type => type.IsActive);
        SelectedExternalResource = ExternalResources.FirstOrDefault(resource => resource.IsActive);
        SelectedSkill = Skills.FirstOrDefault(skill => skill.IsActive);
        CapabilityText = string.Empty;
        Capacity = "1";
        MinutesPerBatch = "0";
        MinutesPerPart = "0";
        IsActive = true;
        RefreshPredecessorOptions(null);
        SelectedPredecessor = null;
        IsEditing = true;
        return Task.CompletedTask;
    }

    private Task BeginEditAsync()
    {
        if (SelectedRequirement is not { } row) return Task.CompletedTask;
        var value = row.Value;
        editingId = value.Id;
        editingVersion = value.Version;
        EditorHeading = value.IsKitaronManaged ? "Edit auxiliary step (from Kitaron)" : "Edit auxiliary step";
        Name = value.Name ?? string.Empty;
        StepNumber = value.StepNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ResourceClass = ResourceClasses.Contains(value.ResourceClass) ? value.ResourceClass : "WORKSTATION";
        Direction = value.Direction;
        SelectedWorkstationType = WorkstationTypes.FirstOrDefault(type => type.Id == value.WorkstationTypeId);
        SelectedExternalResource = ExternalResources.FirstOrDefault(resource => resource.Id == value.ExternalResourceId);
        SelectedSkill = Skills.FirstOrDefault(skill => skill.Id == value.RequiredSkillId);
        CapabilityText = value.RequiredCapability ?? string.Empty;
        Capacity = value.CapacityRequired.ToString(CultureInfo.InvariantCulture);
        MinutesPerBatch = Minutes(value.EstimatedDurationSeconds);
        MinutesPerPart = Minutes(value.DurationPerUnitSeconds);
        IsActive = value.IsActive;
        RefreshPredecessorOptions(value.Id);
        SelectedPredecessor = PredecessorOptions.FirstOrDefault(option => option.Value.Id == value.PredecessorRequirementId);
        IsEditing = true;
        return Task.CompletedTask;
    }

    private Task CancelAsync()
    {
        IsEditing = false;
        return Task.CompletedTask;
    }

    /// <summary>Validates the editor and builds the create or update payload.</summary>
    internal (OperationRequirementCreate? Create, OperationRequirementUpdate? Update, string? Problem) BuildPayload()
    {
        if (!int.TryParse(Capacity, NumberStyles.None, CultureInfo.InvariantCulture, out var capacityValue) || capacityValue < 1)
            return (null, null, "Capacity must be a positive whole number.");
        if (!TryMinutes(MinutesPerBatch, out var perBatch) || !TryMinutes(MinutesPerPart, out var perPart))
            return (null, null, "Minutes must be numbers of zero or more.");
        int? step = null;
        if (!string.IsNullOrWhiteSpace(StepNumber))
        {
            if (!int.TryParse(StepNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var stepValue) || stepValue < 1)
                return (null, null, "The step number must be a positive whole number.");
            step = stepValue;
        }
        if (ResourceClass == "WORKSTATION" && SelectedWorkstationType is null)
            return (null, null, "A Workstation step needs a Workstation type.");
        if (ResourceClass == "EXTERNAL" && SelectedExternalResource is null)
            return (null, null, "An External step needs an External Resource.");
        if (ResourceClass == "EMPLOYEE" && SelectedSkill is null)
            return (null, null, "An Employee step needs a Skill.");
        var position = editingId is null
            ? Requirements.Count(row => row.Value.Direction == Direction)
            : Requirements.First(row => row.Value.Id == editingId).Value.SequencePosition;
        var workstationType = ResourceClass == "WORKSTATION" ? SelectedWorkstationType!.Id : null;
        var external = ResourceClass == "EXTERNAL" ? SelectedExternalResource!.Id : null;
        var skill = ResourceClass == "EMPLOYEE" ? SelectedSkill!.Id : null;
        var capability = ResourceClass == "WORKSTATION" && !string.IsNullOrWhiteSpace(CapabilityText) ? CapabilityText.Trim() : null;
        var stepName = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();
        var durationSeconds = ResourceClass == "EXTERNAL" ? 0 : perBatch;
        var perUnitSeconds = ResourceClass == "EXTERNAL" ? 0 : perPart;
        var predecessor = SelectedPredecessor?.Value.Id;
        return editingId is null
            ? (new OperationRequirementCreate(position, ResourceClass, workstationType, external, capability, skill, capacityValue,
                durationSeconds, Direction, null, predecessor, stepName, step, perUnitSeconds), null, null)
            : (null, new OperationRequirementUpdate(position, ResourceClass, workstationType, external, capability, skill, capacityValue,
                durationSeconds, Direction, null, predecessor, stepName, step, perUnitSeconds, IsActive, editingVersion), null);
    }

    private async Task SaveAsync()
    {
        if (caseOperationId is null) return;
        var (create, update, problem) = BuildPayload();
        if (problem is not null) { StatusMessage = problem; return; }
        var operationId = caseOperationId;
        await MutateAsync(async () =>
        {
            if (create is not null) await apiClient!.CreateOperationRequirementAsync(operationId, create, clientId, editGeneration);
            else if (update is not null && editingId is not null) await apiClient!.UpdateOperationRequirementAsync(editingId, update, clientId, editGeneration);
            IsEditing = false;
        }, "Auxiliary step saved.", operationId);
    }

    private async Task DeleteAsync()
    {
        if (SelectedRequirement is not { } row || caseOperationId is null) return;
        var operationId = caseOperationId;
        await MutateAsync(async () => await apiClient!.DeleteOperationRequirementAsync(row.Value.Id, row.Value.Version, clientId, editGeneration),
            row.IsKitaronManaged
                ? "Auxiliary step removed. Kitaron will not bring it back until the step is added again."
                : "Auxiliary step removed.", operationId);
    }

    private async Task MutateAsync(Func<Task> action, string success, string operationId)
    {
        if (!CanEdit) { StatusMessage = "Edit Mode is required to change auxiliary steps."; return; }
        IsBusy = true;
        try
        {
            await action();
            StatusMessage = success;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = Friendly(exception);
            IsBusy = false;
            return;
        }
        finally { IsBusy = false; }
        await LoadAsync(operationId);
        StatusMessage = success;
    }

    private void RefreshPredecessorOptions(string? excludedId)
    {
        Replace(PredecessorOptions, Requirements.Where(row => row.Value.Id != excludedId));
    }

    private string Describe(PlannerOperationRequirement row) => row.ResourceClass switch
    {
        "WORKSTATION" => WorkstationTypes.FirstOrDefault(type => type.Id == row.WorkstationTypeId)?.Name
            ?? row.WorkstationTypeId ?? "Workstation",
        "EXTERNAL" => ExternalResources.FirstOrDefault(resource => resource.Id == row.ExternalResourceId)?.Name
            ?? row.ExternalResourceId ?? "External resource",
        "EMPLOYEE" => Skills.FirstOrDefault(skill => skill.Id == row.RequiredSkillId)?.Name ?? row.RequiredSkillId ?? "Employee",
        _ => row.ResourceClass
    };

    private void RaiseCommands()
    {
        BeginAddCommand.RaiseCanExecuteChanged();
        BeginEditCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }

    internal static string Minutes(int seconds) => (seconds / 60d).ToString("0.##", CultureInfo.InvariantCulture);

    private static bool TryMinutes(string text, out int seconds)
    {
        seconds = 0;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) || minutes < 0 || !double.IsFinite(minutes))
            return false;
        seconds = (int)Math.Round(minutes * 60, MidpointRounding.AwayFromZero);
        return true;
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

internal sealed class RequirementRowViewModel
{
    internal RequirementRowViewModel(
        PlannerOperationRequirement value,
        string resourceText,
        IReadOnlyDictionary<string, PlannerOperationRequirement> byId)
    {
        Value = value;
        ResourceText = resourceText;
        PredecessorText = value.PredecessorRequirementId is { } id && byId.TryGetValue(id, out var predecessor)
            ? predecessor.Name ?? predecessor.StepNumber?.ToString(CultureInfo.InvariantCulture) ?? predecessor.ResourceClass
            : string.Empty;
    }

    public PlannerOperationRequirement Value { get; }

    public string StepText => Value.StepNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string Name => Value.Name ?? string.Empty;

    public string ClassLabel => Value.ResourceClass switch
    {
        "WORKSTATION" => "Workstation",
        "EXTERNAL" => "External",
        "EMPLOYEE" => "Employee",
        _ => Value.ResourceClass
    };

    public string ResourceText { get; }

    public bool IsBackward => string.Equals(Value.Direction, "BACKWARD", StringComparison.Ordinal);

    public string DirectionLabel => IsBackward ? "Before" : "After";

    public string PerBatchText => Value.ResourceClass == "EXTERNAL" ? "lead time" : OperationRequirementsViewModel.Minutes(Value.EstimatedDurationSeconds);

    public string PerPartText => Value.ResourceClass == "EXTERNAL" ? string.Empty : OperationRequirementsViewModel.Minutes(Value.DurationPerUnitSeconds);

    public string PredecessorText { get; }

    public bool IsKitaronManaged => Value.IsKitaronManaged;

    public string OriginLabel => Value.IsKitaronManaged ? "Kitaron" : "Manual";

    public string ActiveLabel => Value.IsActive ? "Active" : "Inactive";

    public string DisplayName => string.IsNullOrEmpty(Name) ? $"{ClassLabel} {StepText}".Trim() : $"{StepText} {Name}".Trim();
}
