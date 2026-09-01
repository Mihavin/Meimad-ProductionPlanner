using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class ResourceMasterDataViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? apiClient;
    private string clientId = string.Empty;
    private long editGeneration;
    private bool isEditor;
    private bool isBusy;
    private string statusMessage = "Connect to manage resource types and Skills.";
    private string skillName = string.Empty;
    private string skillDescription = string.Empty;
    private string workstationTypeName = string.Empty;
    private string workstationTypeDescription = string.Empty;
    private string workstationTypeSchema = "{}";
    private string workstationName = string.Empty;
    private PlannerWorkstationType? selectedType;
    private WorkingCalendar? selectedCalendar;
    private string workstationCapacity = "1";
    private string workstationCapabilities = string.Empty;
    private string workstationProperties = "{}";
    private string externalName = string.Empty;
    private string externalSupplier = string.Empty;
    private string externalLeadMinutes = "0";
    private string externalBufferMinutes = "0";
    private string externalSemantics = "CALENDAR_TIME";
    private WorkingCalendar? selectedExternalCalendar;
    private string externalProperties = "{}";
    private PlannerResource? selectedEmployee;
    private PlannerSkill? selectedSkill;
    private PlannerWorkstationType? selectedWorkstationType;
    private PlannerWorkstation? selectedWorkstation;
    private PlannerExternalResource? selectedExternalResource;
    private bool skillIsActive=true, workstationTypeIsActive=true, workstationIsActive=true, externalIsActive=true;

    internal ResourceMasterDataViewModel()
    {
        RefreshCommand = new(RefreshAsync, CanRead);
        AddSkillCommand = new(AddSkillAsync, CanManage);
        AddWorkstationTypeCommand = new(AddWorkstationTypeAsync, CanManage);
        AddWorkstationCommand = new(AddWorkstationAsync, CanManage);
        AddExternalResourceCommand = new(AddExternalResourceAsync, CanManage);
        SaveEmployeeSkillsCommand = new(SaveEmployeeSkillsAsync, () => CanManage() && SelectedEmployee is not null);
        DeleteSkillCommand=new(DeleteSkillAsync,()=>CanManage()&&SelectedSkill is not null);
        DeleteWorkstationTypeCommand=new(DeleteWorkstationTypeAsync,()=>CanManage()&&SelectedWorkstationType is not null);
        DeleteWorkstationCommand=new(DeleteWorkstationAsync,()=>CanManage()&&SelectedWorkstation is not null);
        DeleteExternalResourceCommand=new(DeleteExternalResourceAsync,()=>CanManage()&&SelectedExternalResource is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<PlannerSkill> Skills { get; } = [];
    public ObservableCollection<PlannerWorkstationType> WorkstationTypes { get; } = [];
    public ObservableCollection<PlannerWorkstation> Workstations { get; } = [];
    public ObservableCollection<PlannerExternalResource> ExternalResources { get; } = [];
    public ObservableCollection<PlannerResource> Employees { get; } = [];
    public ObservableCollection<WorkingCalendar> Calendars { get; } = [];
    public ObservableCollection<EmployeeSkillOption> EmployeeSkillOptions { get; } = [];
    public IReadOnlyList<string> LeadTimeSemantics { get; } = ["CALENDAR_TIME", "WORKING_TIME"];

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand AddSkillCommand { get; }
    public AsyncCommand AddWorkstationTypeCommand { get; }
    public AsyncCommand AddWorkstationCommand { get; }
    public AsyncCommand AddExternalResourceCommand { get; }
    public AsyncCommand SaveEmployeeSkillsCommand { get; }
    public AsyncCommand DeleteSkillCommand { get; }
    public AsyncCommand DeleteWorkstationTypeCommand { get; }
    public AsyncCommand DeleteWorkstationCommand { get; }
    public AsyncCommand DeleteExternalResourceCommand { get; }

    public bool IsEditor => isEditor;
    public string StatusMessage { get => statusMessage; private set => SetField(ref statusMessage, value); }
    public string SkillName { get => skillName; set => SetField(ref skillName, value); }
    public string SkillDescription { get => skillDescription; set => SetField(ref skillDescription, value); }
    public bool SkillIsActive {get=>skillIsActive;set=>SetField(ref skillIsActive,value);}
    public PlannerSkill? SelectedSkill {get=>selectedSkill;set{if(!SetField(ref selectedSkill,value))return;if(value is not null){SkillName=value.Name;SkillDescription=value.Description??"";SkillIsActive=value.IsActive;}RaiseCommands();}}
    public string WorkstationTypeName { get => workstationTypeName; set => SetField(ref workstationTypeName, value); }
    public string WorkstationTypeDescription { get => workstationTypeDescription; set => SetField(ref workstationTypeDescription, value); }
    public string WorkstationTypeSchema { get => workstationTypeSchema; set => SetField(ref workstationTypeSchema, value); }
    public bool WorkstationTypeIsActive {get=>workstationTypeIsActive;set=>SetField(ref workstationTypeIsActive,value);}
    public PlannerWorkstationType? SelectedWorkstationType {get=>selectedWorkstationType;set{if(!SetField(ref selectedWorkstationType,value))return;if(value is not null){WorkstationTypeName=value.Name;WorkstationTypeDescription=value.Description??"";WorkstationTypeSchema=value.PropertySchemaJson;WorkstationTypeIsActive=value.IsActive;}RaiseCommands();}}
    public string WorkstationName { get => workstationName; set => SetField(ref workstationName, value); }
    public PlannerWorkstationType? SelectedType { get => selectedType; set { if (SetField(ref selectedType, value)) RaiseCommands(); } }
    public WorkingCalendar? SelectedCalendar { get => selectedCalendar; set { if (SetField(ref selectedCalendar, value)) RaiseCommands(); } }
    public string WorkstationCapacity { get => workstationCapacity; set => SetField(ref workstationCapacity, value); }
    public string WorkstationCapabilities { get => workstationCapabilities; set => SetField(ref workstationCapabilities, value); }
    public string WorkstationProperties { get => workstationProperties; set => SetField(ref workstationProperties, value); }
    public bool WorkstationIsActive {get=>workstationIsActive;set=>SetField(ref workstationIsActive,value);}
    public PlannerWorkstation? SelectedWorkstation {get=>selectedWorkstation;set{if(!SetField(ref selectedWorkstation,value))return;if(value is not null){WorkstationName=value.Name;SelectedType=WorkstationTypes.FirstOrDefault(x=>x.Id==value.WorkstationTypeId);SelectedCalendar=Calendars.FirstOrDefault(x=>x.WorkingCalendarId==value.WorkingCalendarId);WorkstationCapacity=value.Capacity.ToString(CultureInfo.InvariantCulture);WorkstationCapabilities=string.Join(", ",value.Capabilities);WorkstationProperties=value.PropertiesJson;WorkstationIsActive=value.IsActive;}RaiseCommands();}}
    public string ExternalName { get => externalName; set => SetField(ref externalName, value); }
    public string ExternalSupplier { get => externalSupplier; set => SetField(ref externalSupplier, value); }
    public string ExternalLeadMinutes { get => externalLeadMinutes; set => SetField(ref externalLeadMinutes, value); }
    public string ExternalBufferMinutes { get => externalBufferMinutes; set => SetField(ref externalBufferMinutes, value); }
    public string ExternalSemantics { get => externalSemantics; set => SetField(ref externalSemantics, value); }
    public WorkingCalendar? SelectedExternalCalendar { get => selectedExternalCalendar; set => SetField(ref selectedExternalCalendar, value); }
    public string ExternalProperties { get => externalProperties; set => SetField(ref externalProperties, value); }
    public bool ExternalIsActive {get=>externalIsActive;set=>SetField(ref externalIsActive,value);}
    public PlannerExternalResource? SelectedExternalResource {get=>selectedExternalResource;set{if(!SetField(ref selectedExternalResource,value))return;if(value is not null){ExternalName=value.Name;ExternalSupplier=value.SupplierName??"";ExternalLeadMinutes=value.PromisedLeadTimeMinutes.ToString(CultureInfo.InvariantCulture);ExternalBufferMinutes=value.SafetyBufferMinutes.ToString(CultureInfo.InvariantCulture);ExternalSemantics=value.LeadTimeSemantics;SelectedExternalCalendar=Calendars.FirstOrDefault(x=>x.WorkingCalendarId==value.WorkingCalendarId);ExternalProperties=value.PropertiesJson;ExternalIsActive=value.IsActive;}RaiseCommands();}}
    public PlannerResource? SelectedEmployee
    {
        get => selectedEmployee;
        set
        {
            if (!SetField(ref selectedEmployee, value)) return;
            RaiseCommands();
            _ = LoadEmployeeSkillsAsync();
        }
    }

    internal void AttachSession(IPlannerApiClient? client, string newClientId, long generation, bool editor)
    {
        apiClient = client;
        clientId = newClientId;
        editGeneration = generation;
        isEditor = editor;
        OnPropertyChanged(nameof(IsEditor));
        RaiseCommands();
    }

    internal async Task RefreshAsync()
    {
        if (!CanRead()) return;
        var employeeId = SelectedEmployee?.ResourceId;
        IsBusy = true;
        try
        {
            var skills = apiClient!.ListSkillsAsync();
            var types = apiClient.ListWorkstationTypesAsync();
            var stations = apiClient.ListWorkstationsAsync();
            var external = apiClient.ListExternalResourcesAsync();
            var employees = apiClient.ListResourcesAsync();
            var calendars = apiClient.ListWorkingCalendarsAsync();
            await Task.WhenAll(skills, types, stations, external, employees, calendars);
            Replace(Skills, await skills);
            Replace(WorkstationTypes, await types);
            Replace(Workstations, await stations);
            Replace(ExternalResources, await external);
            Replace(Employees, await employees);
            Replace(Calendars, await calendars);
            SelectedType ??= WorkstationTypes.FirstOrDefault(value => value.IsActive);
            SelectedCalendar ??= Calendars.FirstOrDefault();
            SelectedExternalCalendar ??= Calendars.FirstOrDefault();
            SelectedEmployee = Employees.FirstOrDefault(value => value.ResourceId == employeeId) ?? Employees.FirstOrDefault();
            StatusMessage = $"Resource master data refreshed at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = Friendly(exception); }
        finally { IsBusy = false; }
    }

    private async Task AddSkillAsync()
    {
        if (string.IsNullOrWhiteSpace(SkillName)) { StatusMessage = "Skill name is required."; return; }
        await MutateAsync(async () =>
        {
            if(SelectedSkill is null) await apiClient!.CreateSkillAsync(new(SkillName.Trim(), Blank(SkillDescription)), clientId, editGeneration);
            else await apiClient!.UpdateSkillAsync(SelectedSkill.Id,new(SkillName.Trim(),Blank(SkillDescription),SkillIsActive,SelectedSkill.Version),clientId,editGeneration);
            SelectedSkill=null;
            SkillName = SkillDescription = string.Empty;
        }, "Skill saved.");
    }

    private async Task AddWorkstationTypeAsync()
    {
        if (string.IsNullOrWhiteSpace(WorkstationTypeName)) { StatusMessage = "Workstation type name is required."; return; }
        await MutateAsync(async () =>
        {
            if(SelectedWorkstationType is null) await apiClient!.CreateWorkstationTypeAsync(new(WorkstationTypeName.Trim(), Blank(WorkstationTypeDescription),JsonOrEmpty(WorkstationTypeSchema)), clientId, editGeneration);
            else await apiClient!.UpdateWorkstationTypeAsync(SelectedWorkstationType.Id,new(WorkstationTypeName.Trim(),Blank(WorkstationTypeDescription),JsonOrEmpty(WorkstationTypeSchema),WorkstationTypeIsActive,SelectedWorkstationType.Version),clientId,editGeneration);
            SelectedWorkstationType=null;
            WorkstationTypeName = WorkstationTypeDescription = string.Empty;
            WorkstationTypeSchema = "{}";
        }, "Workstation type saved.");
    }

    private async Task AddWorkstationAsync()
    {
        if (string.IsNullOrWhiteSpace(WorkstationName) || SelectedType is null || SelectedCalendar is null)
        { StatusMessage = "Name, Workstation type, and calendar are required."; return; }
        if (!int.TryParse(WorkstationCapacity, NumberStyles.None, CultureInfo.InvariantCulture, out var capacity) || capacity < 1)
        { StatusMessage = "Capacity must be a positive whole number."; return; }
        await MutateAsync(async () =>
        {
            if(SelectedWorkstation is null) await apiClient!.CreateWorkstationAsync(new(WorkstationName.Trim(), SelectedType.Id,SelectedCalendar.WorkingCalendarId, capacity, Tokens(WorkstationCapabilities), JsonOrEmpty(WorkstationProperties)),clientId, editGeneration);
            else await apiClient!.UpdateWorkstationAsync(SelectedWorkstation.Id,new(WorkstationName.Trim(),SelectedType.Id,SelectedCalendar.WorkingCalendarId,capacity,Tokens(WorkstationCapabilities),JsonOrEmpty(WorkstationProperties),WorkstationIsActive,SelectedWorkstation.Version),clientId,editGeneration);
            SelectedWorkstation=null;
            WorkstationName = WorkstationCapabilities = string.Empty;
            WorkstationCapacity = "1"; WorkstationProperties = "{}";
        }, "Workstation saved.");
    }

    private async Task AddExternalResourceAsync()
    {
        if (string.IsNullOrWhiteSpace(ExternalName)) { StatusMessage = "External service name is required."; return; }
        if (!NonNegative(ExternalLeadMinutes, out var lead) || !NonNegative(ExternalBufferMinutes, out var buffer))
        { StatusMessage = "Lead time and safety buffer must be non-negative whole minutes."; return; }
        await MutateAsync(async () =>
        {
            if(SelectedExternalResource is null) await apiClient!.CreateExternalResourceAsync(new(ExternalName.Trim(), Blank(ExternalSupplier), lead, buffer,ExternalSemantics, SelectedExternalCalendar?.WorkingCalendarId, JsonOrEmpty(ExternalProperties)),clientId, editGeneration);
            else await apiClient!.UpdateExternalResourceAsync(SelectedExternalResource.Id,new(ExternalName.Trim(),Blank(ExternalSupplier),lead,buffer,ExternalSemantics,SelectedExternalCalendar?.WorkingCalendarId,JsonOrEmpty(ExternalProperties),ExternalIsActive,SelectedExternalResource.Version),clientId,editGeneration);
            SelectedExternalResource=null;
            ExternalName = ExternalSupplier = string.Empty;
            ExternalLeadMinutes = ExternalBufferMinutes = "0"; ExternalProperties = "{}";
        }, "External service saved.");
    }

    internal Task DeleteSkillAsync()=>DeleteSelectedAsync(()=>apiClient!.DeleteSkillAsync(SelectedSkill!.Id,SelectedSkill.Version,clientId,editGeneration),()=>SelectedSkill=null,"Skill deleted.");
    internal Task DeleteWorkstationTypeAsync()=>DeleteSelectedAsync(()=>apiClient!.DeleteWorkstationTypeAsync(SelectedWorkstationType!.Id,SelectedWorkstationType.Version,clientId,editGeneration),()=>SelectedWorkstationType=null,"Workstation type deleted.");
    internal Task DeleteWorkstationAsync()=>DeleteSelectedAsync(()=>apiClient!.DeleteWorkstationAsync(SelectedWorkstation!.Id,SelectedWorkstation.Version,clientId,editGeneration),()=>SelectedWorkstation=null,"Workstation deleted.");
    internal Task DeleteExternalResourceAsync()=>DeleteSelectedAsync(()=>apiClient!.DeleteExternalResourceAsync(SelectedExternalResource!.Id,SelectedExternalResource.Version,clientId,editGeneration),()=>SelectedExternalResource=null,"External resource deleted.");
    private async Task DeleteSelectedAsync(Func<Task> action,Action clear,string message)=>await MutateAsync(async()=>{await action();clear();},message);

    private async Task LoadEmployeeSkillsAsync()
    {
        EmployeeSkillOptions.Clear();
        if (apiClient is null || SelectedEmployee is null) return;
        try
        {
            var current = await apiClient.GetEmployeeSkillsAsync(SelectedEmployee.ResourceId);
            var selected = current.SkillIds.ToHashSet(StringComparer.Ordinal);
            foreach (var skill in Skills.Where(value => value.IsActive))
                EmployeeSkillOptions.Add(new(skill.Id, skill.Name, selected.Contains(skill.Id)));
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = Friendly(exception); }
    }

    private async Task SaveEmployeeSkillsAsync()
    {
        if (SelectedEmployee is null) return;
        await MutateAsync(async () =>
        {
            await apiClient!.SetEmployeeSkillsAsync(SelectedEmployee.ResourceId,
                new(EmployeeSkillOptions.Where(value => value.IsSelected).Select(value => value.SkillId).ToArray()),
                clientId, editGeneration);
        }, $"Skills saved for {SelectedEmployee.Name}.");
    }

    private async Task MutateAsync(Func<Task> action, string success)
    {
        IsBusy = true;
        try { await action(); IsBusy = false; await RefreshAsync(); StatusMessage = success; }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = Friendly(exception); }
        finally { IsBusy = false; }
    }

    private bool IsBusy { get => isBusy; set { if (SetField(ref isBusy, value)) RaiseCommands(); } }
    private bool CanRead() => apiClient is not null && !IsBusy;
    private bool CanManage() => CanRead() && isEditor;
    private void RaiseCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged(); AddSkillCommand.RaiseCanExecuteChanged();
        AddWorkstationTypeCommand.RaiseCanExecuteChanged(); AddWorkstationCommand.RaiseCanExecuteChanged();
        AddExternalResourceCommand.RaiseCanExecuteChanged(); SaveEmployeeSkillsCommand.RaiseCanExecuteChanged();
        DeleteSkillCommand.RaiseCanExecuteChanged();DeleteWorkstationTypeCommand.RaiseCanExecuteChanged();DeleteWorkstationCommand.RaiseCanExecuteChanged();DeleteExternalResourceCommand.RaiseCanExecuteChanged();
    }
    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string JsonOrEmpty(string value) => string.IsNullOrWhiteSpace(value) ? "{}" : value.Trim();
    private static IReadOnlyList<string> Tokens(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static bool NonNegative(string value, out int parsed) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed >= 0;
    private static bool IsExpected(Exception exception) => exception is PlannerApiException or PlannerProtocolException or HttpRequestException or TaskCanceledException;
    private static string Friendly(Exception exception) => exception is PlannerApiException api ? api.Message : exception.Message;
    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> values) { destination.Clear(); foreach (var value in values) destination.Add(value); }
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); return true; }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

internal sealed class EmployeeSkillOption(string skillId, string name, bool isSelected) : INotifyPropertyChanged
{
    private bool selected = isSelected;
    public string SkillId { get; } = skillId;
    public string Name { get; } = name;
    public bool IsSelected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
