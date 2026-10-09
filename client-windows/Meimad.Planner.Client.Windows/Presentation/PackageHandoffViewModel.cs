using System.ComponentModel;
using Meimad.Planner.Client.Windows.Api;
namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class PackageHandoffViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? api;
    private ProductionPackageContext? context;
    private string? packageId;
    private HandoffRequest? pending;
    private bool canRecord, busy;
    private int generation;
    public HandoffView? View { get; private set; }
    public HandoffWorker? SelectedWorker { get; set; }
    public string Status { get; private set; } = "Physical handoff is optional tracking. It does not start setup or change readiness.";
    public bool CanChooseWorker => pending is null && !busy;
    public AsyncCommand RecordCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public PackageHandoffViewModel()
    {
        RecordCommand = new(RecordAsync, () => api is not null && canRecord && !busy && View?.PackageIsCurrent == true && context is not null);
        RefreshCommand = new(RefreshAsync, () => api is not null && !busy && packageId is not null);
    }
    internal void AttachSession(IPlannerApiClient? client, bool permitted)
    { generation++; api = client; canRecord = permitted; packageId = null; context = null; pending = null; View = null; SelectedWorker = null; busy = false; Changed(); }
    internal async Task OpenAsync(string id, ProductionPackageContext exact)
    {
        generation++; packageId = id; context = exact; pending = null; View = null; SelectedWorker = null; busy = false;
        await RefreshAsync();
    }
    internal async Task RefreshAsync()
    {
        if (api is not { } client || packageId is null || busy) return;
        var current = generation; busy = true; Changed();
        try
        {
            var view = await client.GetPackageHandoffsAsync(packageId);
            if (current != generation) return;
            View = view;
            Status = pending is not null ? "A prior handoff response is unconfirmed. Record retries the same operator/package request."
                : view.HandoffIsCurrent ? "Physical handoff recorded for this current package and qualified operator. Setup/loader execution is separate."
                : "No current physical handoff evidence. Select a qualified operator only after the physical delivery.";
        }
        catch (Exception exception) { if (current == generation) Status = exception.Message; }
        finally { if (current == generation) { busy = false; Changed(); } }
    }
    internal async Task RecordAsync()
    {
        if (api is not { } client || !canRecord || busy || View is not { PackageIsCurrent: true } view || context is null || packageId is null) return;
        if (pending is null && SelectedWorker is null) { Status = "Select the qualified setup operator who received this package."; Changed(); return; }
        pending ??= new(Guid.NewGuid().ToString("N"), SelectedWorker!.EmployeeId, SelectedWorker.QualificationStamp, view.Version, context);
        var current = generation; busy = true; Changed();
        try
        {
            await client.RecordPackageHandoffAsync(packageId, pending);
            if (current != generation) return;
            pending = null; busy = false; await RefreshAsync();
        }
        catch (Exception exception)
        {
            if (current == generation)
            {
                if (exception is PlannerApiException { StatusCode: System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Forbidden }) pending = null;
                Status = pending is null ? "Handoff refused. Refresh and review: " + exception.Message
                    : "Handoff response unconfirmed. Record retries the exact same request: " + exception.Message;
            }
        }
        finally { if (current == generation) { busy = false; Changed(); } }
    }
    private void Changed()
    { PropertyChanged?.Invoke(this, new(null)); RecordCommand.RaiseCanExecuteChanged(); RefreshCommand.RaiseCanExecuteChanged(); }
}
