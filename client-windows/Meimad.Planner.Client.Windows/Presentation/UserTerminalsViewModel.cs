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

    internal UserTerminalsViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => api is not null);
        NewCommand = new AsyncCommand(NewAsync, () => isEditor);
        SaveCommand = new AsyncCommand(
            SaveAsync,
            () => isEditor
                && !string.IsNullOrWhiteSpace(Name)
                && (Selected is not null || !string.IsNullOrWhiteSpace(HardwareId)));
        ToggleEnabledCommand = new AsyncCommand(
            ToggleEnabledAsync,
            () => isEditor && Selected is not null);
        MarkSpareCommand = new AsyncCommand(
            MarkSpareAsync,
            () => isEditor && Selected is not null && SelectedMachine is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<UserTerminal> Terminals { get; } = [];
    public ObservableCollection<PlannerMachine> Machines { get; } = [];
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand NewCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand ToggleEnabledCommand { get; }
    public AsyncCommand MarkSpareCommand { get; }

    public UserTerminal? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return;
            if (value is not null)
            {
                Name = value.DeviceName;
                HardwareId = value.HardwareId ?? string.Empty;
                SelectedMachine = Machines.FirstOrDefault(
                    machine => machine.MachineId == value.MachineId);
            }
            Raise(nameof(CanEditIdentity));
            RaiseCommandStates();
        }
    }

    public PlannerMachine? SelectedMachine
    {
        get => selectedMachine;
        set
        {
            if (Set(ref selectedMachine, value)) MarkSpareCommand.RaiseCanExecuteChanged();
        }
    }

    public string Name
    {
        get => name;
        set
        {
            if (Set(ref name, value)) SaveCommand.RaiseCanExecuteChanged();
        }
    }

    public string HardwareId
    {
        get => hardwareId;
        set
        {
            if (Set(ref hardwareId, value)) SaveCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status { get => status; private set => Set(ref status, value); }
    public bool IsEditor => isEditor;
    public bool CanEditIdentity => isEditor && Selected is null;
    public string EditModeText => isEditor
        ? "Edit Mode — terminal administration enabled"
        : "View Mode — monitoring only";
    public string ToggleEnabledText => Selected?.IsEnabled == true
        ? "Disable"
        : "Enable";

    internal void AttachSession(IPlannerApiClient? client, string id, EditModeStatus? edit)
    {
        api = client;
        clientId = id;
        generation = edit?.Generation ?? 0;
        isEditor = edit?.State == ClientEditState.Editor;
        Raise(nameof(IsEditor));
        Raise(nameof(CanEditIdentity));
        Raise(nameof(EditModeText));
        RaiseCommandStates();
        if (api is not null) _ = RefreshAsync();
    }

    private Task NewAsync()
    {
        Selected = null;
        SelectedMachine = null;
        Name = string.Empty;
        HardwareId = string.Empty;
        Status = "Enter a device name and hardware MAC, then optionally bind a Machine.";
        return Task.CompletedTask;
    }

    internal async Task RefreshAsync()
    {
        if (api is null) return;
        var selectedId = Selected?.DeviceId;
        try
        {
            var terminalTask = api.ListUserTerminalsAsync();
            var machineTask = api.ListMachinesAsync();
            await Task.WhenAll(terminalTask, machineTask);

            Machines.Clear();
            foreach (var item in await machineTask) Machines.Add(item);
            Terminals.Clear();
            foreach (var item in await terminalTask) Terminals.Add(item);
            Selected = selectedId is null
                ? null
                : Terminals.FirstOrDefault(item => item.DeviceId == selectedId);
            Status = $"{Terminals.Count} registered tablet(s). Monitoring remains available in View Mode.";
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    private async Task SaveAsync()
    {
        if (api is null) return;
        try
        {
            UserTerminal item;
            if (Selected is null)
            {
                item = await api.CreateUserTerminalAsync(
                    new(
                        Name.Trim(),
                        SelectedMachine?.MachineId,
                        string.IsNullOrWhiteSpace(HardwareId) ? null : HardwareId.Trim()),
                    clientId,
                    generation);
            }
            else
            {
                item = await api.UpdateUserTerminalAsync(
                    Selected.DeviceId,
                    new(SelectedMachine?.MachineId, Selected.IsEnabled),
                    clientId,
                    generation);
            }

            await RefreshAsync();
            Selected = Terminals.FirstOrDefault(value => value.DeviceId == item.DeviceId);
            Status = "Terminal identity and Machine binding saved.";
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    private Task ToggleEnabledAsync() =>
        UpdateSelectedAsync(!Selected!.IsEnabled);

    private async Task MarkSpareAsync()
    {
        SelectedMachine = null;
        await SaveAsync();
    }

    private async Task UpdateSelectedAsync(bool enabled)
    {
        if (api is null || Selected is null) return;
        try
        {
            var deviceId = Selected.DeviceId;
            var item = await api.UpdateUserTerminalAsync(
                deviceId,
                new(SelectedMachine?.MachineId, enabled),
                clientId,
                generation);
            await RefreshAsync();
            Selected = Terminals.FirstOrDefault(value => value.DeviceId == deviceId);
            Status = enabled ? "Terminal enabled." : "Terminal disabled.";
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        NewCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        ToggleEnabledCommand.RaiseCanExecuteChanged();
        MarkSpareCommand.RaiseCanExecuteChanged();
        Raise(nameof(ToggleEnabledText));
    }

    private bool Set<T>(
        ref T field,
        T value,
        [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(property);
        return true;
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new(property));
}
