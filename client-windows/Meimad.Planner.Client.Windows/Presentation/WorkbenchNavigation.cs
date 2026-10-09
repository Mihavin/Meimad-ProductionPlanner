using System.ComponentModel;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed record WorkbenchContext(string? CaseId, string? CaseOperationId, string? BatchId,
    string BatchOperationId, string? MachineId, ProductionPackageContext? ExactContext = null,
    string? PackageId = null);

// Navigation owns IDs only. Every destination resolves data from the Server again.
internal sealed class WorkbenchNavigation : INotifyPropertyChanged
{
    private readonly Stack<(string Workspace, WorkbenchContext? Context)> history = new();
    public string Workspace { get; private set; } = "Shop";
    public WorkbenchContext? Context { get; private set; }
    public bool CanGoBack => history.Count > 0;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Open(string workspace, WorkbenchContext? context)
    {
        if (Workspace == workspace && Context == context) return;
        if (history.Count >= 50) history.Clear();
        history.Push((Workspace, Context));
        Workspace = workspace; Context = context; Changed();
    }
    internal bool Back()
    {
        if (!history.TryPop(out var previous)) return false;
        Workspace = previous.Workspace; Context = previous.Context; Changed(); return true;
    }
    internal void Clear() { history.Clear(); Workspace = "Shop"; Context = null; Changed(); }
    private void Changed() => PropertyChanged?.Invoke(this, new(null));
}
