using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// The Kitaron material orders of the selected Work Order. Kitaron records no link between a
/// purchase line and a work order, so the open purchase lines of the Work Order's raw material are
/// shown as candidates and a planner verifies the right ones by hand.
/// </summary>
internal sealed class WorkOrderMaterialOrdersViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? apiClient;
    private string clientId = string.Empty;
    private long editGeneration;
    private bool isEditor;
    private bool isBusy;
    private string? batchId;
    private WorkOrderMaterialOrder? selected;
    private string status = "Select a Work Order to see its material orders.";

    internal WorkOrderMaterialOrdersViewModel()
    {
        VerifyCommand = new AsyncCommand(() => SetVerifiedAsync(true), () => CanChange && selected is { Verified: false });
        UnverifyCommand = new AsyncCommand(() => SetVerifiedAsync(false), () => CanChange && selected is { Verified: true });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<WorkOrderMaterialOrder> Items { get; } = [];

    public AsyncCommand VerifyCommand { get; }

    public AsyncCommand UnverifyCommand { get; }

    public WorkOrderMaterialOrder? Selected
    {
        get => selected;
        set
        {
            if (EqualityComparer<WorkOrderMaterialOrder?>.Default.Equals(selected, value)) return;
            selected = value;
            OnPropertyChanged();
            RaiseCommands();
        }
    }

    public string Status
    {
        get => status;
        private set { if (status == value) return; status = value; OnPropertyChanged(); }
    }

    private bool CanChange => isEditor && apiClient is not null && batchId is not null && !isBusy;

    internal void AttachSession(IPlannerApiClient? client, string newClientId, long generation, bool editor)
    {
        apiClient = client;
        clientId = newClientId;
        editGeneration = generation;
        isEditor = editor;
        RaiseCommands();
    }

    internal async Task LoadAsync(ProductionBatch? batch)
    {
        batchId = batch?.BatchId;
        Items.Clear();
        Selected = null;
        if (batch is null || apiClient is null)
        {
            Status = "Select a Work Order to see its material orders.";
            return;
        }
        try
        {
            Apply(await apiClient.ListWorkOrderMaterialOrdersAsync(batch.BatchId));
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    private async Task SetVerifiedAsync(bool verified)
    {
        if (!CanChange || selected is null) return;
        isBusy = true;
        RaiseCommands();
        try
        {
            var key = selected.SourceKey;
            Apply(await apiClient!.SetWorkOrderMaterialOrderVerifiedAsync(batchId!, key, verified, clientId, editGeneration));
            Selected = Items.FirstOrDefault(item => item.SourceKey == key);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            isBusy = false;
            RaiseCommands();
        }
    }

    private void Apply(IReadOnlyList<WorkOrderMaterialOrder> values)
    {
        Items.Clear();
        foreach (var value in values) Items.Add(value);
        var verified = values.Count(item => item.Verified);
        Status = values.Count == 0
            ? "No open Kitaron purchase line exists for this Work Order's raw material."
            : $"{verified} verified; {values.Count - verified} candidate(s) share the raw material. Kitaron does not record which purchase serves which work order, so verify the right ones.";
    }

    private void RaiseCommands()
    {
        VerifyCommand.RaiseCanExecuteChanged();
        UnverifyCommand.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
