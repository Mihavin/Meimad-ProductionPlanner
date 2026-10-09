using System.Collections.ObjectModel;
using System.ComponentModel;
using Meimad.Planner.Client.Windows.Api;
namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed record ShopOutput(string RunId, string ProgramId, ProductionRunPlanningOutput Output, string RunState, string Readiness);
internal sealed record ShopMachineRow(string MachineId, string Number, string Name, string Connection,
    string Freshness, string Observation, DateTimeOffset? ObservedAt, int BacklogRuns,
    string NextCommittedRun, IReadOnlyList<ShopOutput> Outputs);
internal sealed class ShopOverviewViewModel : INotifyPropertyChanged
{
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
    public ShopOutput? SelectedOutput { get; set; }
    public string Status { get; private set; } = "Refresh to inspect Machines, recorded counts and committed backlog.";
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand OpenCommand { get; }
    internal event EventHandler<WorkbenchContext>? OpenRequested;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ShopOverviewViewModel()
    {
        RefreshCommand = new(RefreshAsync, () => api is not null && !busy);
        OpenCommand = new(OpenAsync, () => api is not null && selected is not null && !busy);
    }
    internal void AttachSession(IPlannerApiClient? client)
    { generation++; api = client; Ai.AttachSession(client); busy = false; Machines.Clear(); selected = null; SelectedOutput = null; Status = "Refresh to inspect Machines, recorded counts and committed backlog."; Changed(); }
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
    private void Changed() { PropertyChanged?.Invoke(this, new(null)); RefreshCommand.RaiseCanExecuteChanged(); OpenCommand.RaiseCanExecuteChanged(); }
}
