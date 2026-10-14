using System.Collections.ObjectModel;
using System.ComponentModel;
using Meimad.Planner.Client.Windows.Api;
namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed record ShopOutput(string RunId, string ProgramId, ProductionRunPlanningOutput Output, string RunState, string Readiness);
internal sealed record ShopMachineRow(string MachineId, string Number, string Name, string Connection,
    string Freshness, string Observation, DateTimeOffset? ObservedAt, int BacklogRuns,
    string NextCommittedRun, IReadOnlyList<ShopOutput> Outputs);
/// <summary>One retained CNC cycle end of the selected Run that the planner may tick to count.</summary>
internal sealed class UncountedCycleRow(UncountedCncCycle cycle) : INotifyPropertyChanged
{
    private bool isSelected;
    public UncountedCncCycle Cycle => cycle;
    public string Reason => cycle.AnomalyType == "CYCLE_END_SEQUENCE_MISMATCH" ? "End did not follow its start" : "End without a start";
    public bool IsSelected { get => isSelected; set { isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class ShopOverviewViewModel : INotifyPropertyChanged
{
    private const string UncountedHint = "Select a Run output, then load the CNC cycle ends the Server kept without counting.";
    private ShopOutput? selectedOutput;
    private string? uncountedRunId;
    private string? uncountedTag;
    private IPlannerApiClient? api;
    private int generation;
    private bool busy;
    private ShopMachineRow? selected;
    public ObservableCollection<ShopMachineRow> Machines { get; } = [];
    public LocalAiPanelViewModel Ai { get; } = new() { ShowRunControls = false };
    public ShopMachineRow? SelectedMachine
    {
        get => selected;
        set { if (selected?.MachineId != value?.MachineId) Ai.SelectMachineScope(value?.MachineId); selected = value; SelectedOutput = null; Changed(); }
    }
    public ShopOutput? SelectedOutput
    {
        get => selectedOutput;
        set { selectedOutput = value; Changed(); }
    }
    public ObservableCollection<UncountedCycleRow> UncountedCycles { get; } = [];
    public string UncountedStatus { get; private set; } = UncountedHint;
    public AsyncCommand LoadUncountedCommand { get; }
    public AsyncCommand CountUncountedCommand { get; }
    public string Status { get; private set; } = "Refresh to inspect Machines, recorded counts and committed backlog.";
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand OpenCommand { get; }
    internal event EventHandler<WorkbenchContext>? OpenRequested;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ShopOverviewViewModel()
    {
        RefreshCommand = new(RefreshAsync, () => api is not null && !busy);
        OpenCommand = new(OpenAsync, () => api is not null && selected is not null && !busy);
        LoadUncountedCommand = new(LoadUncountedAsync, () => api is not null && selectedOutput is not null && !busy);
        CountUncountedCommand = new(CountUncountedAsync, () => api is not null && uncountedTag is not null && !busy);
    }
    internal void AttachSession(IPlannerApiClient? client)
    { generation++; api = client; Ai.AttachSession(client); busy = false; Machines.Clear(); selected = null; SelectedOutput = null; ClearUncounted(); Status = "Refresh to inspect Machines, recorded counts and committed backlog."; Changed(); }
    internal async Task RefreshAsync()
    {
        if (api is not { } client || busy) return;
        var current = generation; busy = true; Changed();
        try
        {
            var board = await client.GetPlanningBoardAsync();
            var health = new Dictionary<string, OperationalHealthItem>(StringComparer.Ordinal);
            string? cursor = null; DateTimeOffset? healthAt = null;
            for (var pageNumber = 0; pageNumber < 100; pageNumber++)
            {
                var page = await client.GetOperationalHealthAsync(cursor);
                if (current != generation) return;
                foreach (var item in page.Items.Where(x => x.Category == "CNC")) health[item.EntityId] = item;
                healthAt = page.ReadAt; cursor = page.NextCursor; if (cursor is null) break;
            }
            if (current != generation) return;
            var previous = selected?.MachineId;
            Machines.Clear();
            foreach (var machine in board.Machines.Where(x => x.IsActive))
            {
                var facts = health.GetValueOrDefault(machine.MachineId);
                var runs = (board.ProductionRuns ?? []).Where(x => x.MachineId == machine.MachineId)
                    .OrderBy(x => x.BacklogPosition).ThenBy(x => x.ProductionRunId, StringComparer.Ordinal).ToArray();
                Machines.Add(new(machine.MachineId, machine.Number, machine.Name, facts?.Status ?? "UNKNOWN", facts?.Freshness ?? "UNKNOWN",
                    facts?.ObservedState ?? "UNKNOWN", facts?.ObservedAt, runs.Length,
                    runs.FirstOrDefault(x => x.Status is "DRAFT" or "PLANNED")?.ProductionRunId ?? "None recorded",
                    runs.SelectMany(run => run.Programs.OrderBy(p => p.SequencePosition).SelectMany(program => program.Outputs.Select(output =>
                        new ShopOutput(run.ProductionRunId, program.ProductionRunProgramId, output, run.Status, run.ReadinessState)))).ToArray()));
            }
            SelectedMachine = Machines.FirstOrDefault(x => x.MachineId == previous);
            Status = $"{Machines.Count} active Machines. Planning sampled {board.ReadAt:u}; health sampled separately {healthAt:u}. " +
                "Counts are Server records; telemetry is a dated observation. Backlog remains manually committed." + (cursor is null ? "" : " Health pages are incomplete; refresh Integrations for more.");
        }
        catch (Exception exception) { if (current == generation) Status = "Refresh failed; retained rows are previous observations. " + exception.Message; }
        finally { if (current == generation) { busy = false; Changed(); } }
    }
    internal async Task OpenAsync()
    {
        if (api is not { } client || SelectedOutput is not { } output || selected is null) { Status = "Select a Run output to inspect its exact context."; Changed(); return; }
        var current = generation;
        try
        {
            var contexts = await client.GetExecutionContextsAsync(output.Output.BatchOperationId);
            if (current != generation || !ReferenceEquals(output, SelectedOutput)) return;
            var exact = contexts.SingleOrDefault(x => x.ProductionRunId == output.RunId && x.ProductionRunProgramId == output.ProgramId && x.ProductionRunOutputId == output.Output.ProductionRunOutputId);
            if (exact is null) { Status = "The selected output is no longer active. Refresh Shop."; Changed(); return; }
            OpenRequested?.Invoke(this, new(output.Output.CaseId, null, null, output.Output.BatchOperationId, exact.MachineId, exact));
        }
        catch (Exception exception) { if (current == generation) { Status = exception.Message; Changed(); } }
    }
    internal async Task LoadUncountedAsync()
    {
        if (api is not { } client || selectedOutput is not { } output || busy) return;
        var current = generation; busy = true; Changed();
        try
        {
            var list = await client.GetUncountedCncCyclesAsync(output.RunId);
            if (current != generation) return;
            UncountedCycles.Clear();
            foreach (var cycle in list.Value.Items) UncountedCycles.Add(new(cycle));
            uncountedRunId = output.RunId;
            uncountedTag = list.Value.Items.Count == 0 ? null : list.EntityTag;
            var count = list.Value.Items.Count;
            UncountedStatus = count == 0
                ? $"Run {output.RunId}: no uncounted CNC cycle ends."
                : $"Run {output.RunId}: {count} CNC cycle ends were kept without counting. Tick the ones that made a good part and count them.";
        }
        catch (Exception exception) { if (current == generation) UncountedStatus = exception.Message; }
        finally { if (current == generation) { busy = false; Changed(); } }
    }

    internal async Task CountUncountedAsync()
    {
        if (api is not { } client || uncountedRunId is not { } runId || uncountedTag is not { } tag || busy) return;
        var ids = UncountedCycles.Where(x => x.IsSelected).Select(x => x.Cycle.WorkflowEventId).ToArray();
        if (ids.Length == 0) { UncountedStatus = "Tick at least one cycle end to count."; Changed(); return; }
        var current = generation; busy = true; Changed();
        try
        {
            var counted = (await client.CountUncountedCncCyclesAsync(runId, ids, tag)).CountedCycles;
            if (current != generation) return;
            ClearUncounted();
            UncountedStatus = $"Counted {counted} cycles on Run {runId}. Refresh to see the new counts.";
        }
        catch (Exception exception) { if (current == generation) UncountedStatus = exception.Message; }
        finally { if (current == generation) { busy = false; Changed(); } }
    }

    private void ClearUncounted() { UncountedCycles.Clear(); uncountedRunId = null; uncountedTag = null; UncountedStatus = UncountedHint; }

    private void Changed()
    {
        PropertyChanged?.Invoke(this, new(null)); RefreshCommand.RaiseCanExecuteChanged(); OpenCommand.RaiseCanExecuteChanged();
        LoadUncountedCommand.RaiseCanExecuteChanged(); CountUncountedCommand.RaiseCanExecuteChanged();
    }
}
