using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class UserTerminalsViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? api;
    private string clientId = string.Empty;
    private long generation;
    private bool isEditor;
    private UserTerminal? selected;
    private PlannerMachine? selectedMachine;
    private string name = string.Empty;
    private string hardwareId = string.Empty;
    private string status = "Connect to view registered tablets.";
    private string provisioningToken = string.Empty;

    internal UserTerminalsViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => api is not null);
        NewCommand = new AsyncCommand(() => { Selected = null; Name = string.Empty; HardwareId = string.Empty; ProvisioningToken = string.Empty; return Task.CompletedTask; }, () => isEditor);
        SaveCommand = new AsyncCommand(SaveAsync, () => isEditor && !string.IsNullOrWhiteSpace(Name));
        ToggleEnabledCommand = new AsyncCommand(ToggleEnabledAsync, () => isEditor && Selected is not null);
        RotateCredentialCommand = new AsyncCommand(RotateCredentialAsync, () => isEditor && Selected is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<UserTerminal> Terminals { get; } = [];
    public ObservableCollection<PlannerMachine> Machines { get; } = [];
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand NewCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand ToggleEnabledCommand { get; }
    public AsyncCommand RotateCredentialCommand { get; }
    public UserTerminal? Selected { get => selected; set { if (Set(ref selected, value) && value is not null) { Name = value.DeviceName; HardwareId = value.HardwareId ?? string.Empty; SelectedMachine = Machines.FirstOrDefault(m => m.MachineId == value.MachineId); ProvisioningToken = string.Empty; } Raise(); } }
    public PlannerMachine? SelectedMachine { get => selectedMachine; set => Set(ref selectedMachine, value); }
    public string Name { get => name; set => Set(ref name, value); }
    public string HardwareId { get => hardwareId; set => Set(ref hardwareId, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string ProvisioningToken { get => provisioningToken; private set => Set(ref provisioningToken, value); }
    public bool IsEditor => isEditor;

    internal void AttachSession(IPlannerApiClient? client, string id, EditModeStatus? edit)
    {
        api = client; clientId = id; generation = edit?.Generation ?? 0; isEditor = edit?.State == ClientEditState.Editor;
        Raise(nameof(IsEditor)); RefreshCommand.RaiseCanExecuteChanged(); NewCommand.RaiseCanExecuteChanged(); SaveCommand.RaiseCanExecuteChanged(); ToggleEnabledCommand.RaiseCanExecuteChanged(); RotateCredentialCommand.RaiseCanExecuteChanged();
        if (api is not null) _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (api is null) return;
        try { var terminals = await api.ListUserTerminalsAsync(); var machines = await api.ListMachinesAsync(); Terminals.Clear(); foreach (var item in terminals) Terminals.Add(item); Machines.Clear(); foreach (var item in machines) Machines.Add(item); Status = $"{Terminals.Count} registered tablet(s). Select one to view or change its configuration."; }
        catch (Exception ex) { Status = ex.Message; }
    }
    private async Task SaveAsync()
    {
        if (api is null) return;
        try { UserTerminal item; if (Selected is null) item = await api.CreateUserTerminalAsync(new(Name.Trim(), SelectedMachine?.MachineId, string.IsNullOrWhiteSpace(HardwareId) ? null : HardwareId.Trim()), clientId, generation); else item = await api.UpdateUserTerminalAsync(Selected.DeviceId, new(SelectedMachine?.MachineId, Selected.IsEnabled, false), clientId, generation); ProvisioningToken = item.RegistrationToken ?? string.Empty; await RefreshAsync(); Selected = Terminals.FirstOrDefault(x => x.DeviceId == item.DeviceId); Status = string.IsNullOrEmpty(ProvisioningToken) ? "Terminal configuration saved." : "Copy the provisioning credential now. It will not be shown again."; }
        catch (Exception ex) { Status = ex.Message; }
    }
    private Task ToggleEnabledAsync() => UpdateSelectedAsync(!Selected!.IsEnabled, false);
    private Task RotateCredentialAsync() => UpdateSelectedAsync(Selected!.IsEnabled, true);
    private async Task UpdateSelectedAsync(bool enabled, bool rotate)
    { if (api is null || Selected is null) return; try { var item = await api.UpdateUserTerminalAsync(Selected.DeviceId, new(SelectedMachine?.MachineId, enabled, rotate), clientId, generation); ProvisioningToken = item.RegistrationToken ?? string.Empty; await RefreshAsync(); Selected = Terminals.FirstOrDefault(x => x.DeviceId == item.DeviceId); Status = rotate ? "Copy the new credential now. The old credential was revoked." : "Terminal state updated."; } catch (Exception ex) { Status = ex.Message; } }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(property); return true; }
    private void Raise([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
