using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class KitaronStationsViewModelTests
{
    private static readonly DateTimeOffset Seen = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Refresh_lists_stations_undecided_first_and_preselects_the_suggested_role()
    {
        var api = new FakeApiClient(
            Station(4, "Inspection", "UNDECIDED", "WORKSTATION", 17576, 55, 0),
            Station(1046, "DOOSAN-1", "MACHINE", "MACHINE", 114, 107, 0, machineType: "Mill 3x"),
            Station(41, "Chrome", "UNDECIDED", "EXTERNAL", 17, 0, 15));
        var viewModel = new KitaronStationsViewModel();
        viewModel.AttachSession(api, "client", 1, editor: true);

        await viewModel.RefreshAsync();

        Assert.Equal(3, viewModel.Stations.Count);
        Assert.Equal(2, viewModel.UndecidedCount);
        Assert.Contains("2 undecided", viewModel.Summary);
        Assert.Equal(["Mill 3x"], viewModel.MachineTypeOptions.Skip(1));

        viewModel.SelectedStation = viewModel.Stations.Single(station => station.KitaronStationId == 4);
        Assert.Equal("WORKSTATION", viewModel.ImportRole);
        Assert.True(viewModel.IsWorkstationRole);
        Assert.True(viewModel.HasTimeDefaults);
        Assert.Contains("17576 route step(s)", viewModel.SelectedStationHint);

        viewModel.SelectedStation = viewModel.Stations.Single(station => station.KitaronStationId == 1046);
        Assert.Equal("MACHINE", viewModel.ImportRole);
        Assert.Equal("Mill 3x", viewModel.MachineType);
        Assert.False(viewModel.HasTimeDefaults);

        viewModel.ShowDecided = false;
        Assert.Equal(2, viewModel.VisibleStations.Count);
        Assert.All(viewModel.VisibleStations, station => Assert.True(station.IsUndecided));
    }

    [Fact]
    public async Task A_workstation_decision_needs_its_type_and_is_sent_with_the_defaults()
    {
        var api = new FakeApiClient(Station(4, "Inspection", "UNDECIDED", "WORKSTATION", 17576, 55, 0));
        var viewModel = new KitaronStationsViewModel();
        viewModel.AttachSession(api, "client", 7, editor: true);
        await viewModel.RefreshAsync();
        viewModel.SelectedStation = viewModel.Stations.Single();

        viewModel.SelectedWorkstationType = null;
        Assert.Null(viewModel.BuildDecision(out var problem));
        Assert.Contains("Workstation type", problem);

        viewModel.SelectedWorkstationType = viewModel.WorkstationTypes.Single();
        viewModel.MinutesPerPart = "2.5";
        viewModel.MinutesPerBatch = "15";
        viewModel.CapacityRequired = "1";
        viewModel.Notes = "QC room";
        var decision = viewModel.BuildDecision(out problem);
        Assert.Null(problem);
        Assert.NotNull(decision);
        Assert.Equal("WORKSTATION", decision.ImportRole);
        Assert.Equal("type-inspection", decision.WorkstationTypeId);
        Assert.Null(decision.MachineType);
        Assert.Equal(2.5, decision.DefaultMinutesPerPart);
        Assert.Equal(15, decision.DefaultMinutesPerBatch);
        Assert.Equal(1, decision.ExpectedVersion);

        viewModel.SaveDecisionCommand.Execute(null);
        await WaitForAsync(() => api.Decisions.Count == 1);
        Assert.Equal((4, "WORKSTATION", "client", 7L), (api.Decisions[0].StationId, api.Decisions[0].Decision.ImportRole, api.Decisions[0].ClientId, api.Decisions[0].Generation));
        Assert.Contains("saved as Workstation step", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Machine_suggestions_are_accepted_in_bulk_only_for_undecided_stations_that_suggest_a_machine()
    {
        var api = new FakeApiClient(
            Station(1046, "DOOSAN-1", "UNDECIDED", "MACHINE", 114, 107, 0),
            Station(1076, "MAZAK-6", "UNDECIDED", "MACHINE", 39, 30, 0),
            Station(4, "Inspection", "UNDECIDED", "WORKSTATION", 17576, 55, 0),
            Station(24, "OKK", "IGNORE", "MACHINE", 0, 0, 0));
        var viewModel = new KitaronStationsViewModel();
        viewModel.AttachSession(api, "client", 1, editor: true);
        await viewModel.RefreshAsync();

        Assert.True(viewModel.AcceptMachineSuggestionsCommand.CanExecute(null));
        viewModel.AcceptMachineSuggestionsCommand.Execute(null);
        await WaitForAsync(() => api.Decisions.Count == 2);

        Assert.Equal([1046, 1076], api.Decisions.Select(decision => decision.StationId).Order().ToArray());
        Assert.All(api.Decisions, decision => Assert.Equal("MACHINE", decision.Decision.ImportRole));
        Assert.All(api.Decisions, decision => Assert.Null(decision.Decision.MachineType));
    }

    [Fact]
    public async Task Viewers_can_read_but_not_decide()
    {
        var api = new FakeApiClient(Station(4, "Inspection", "UNDECIDED", "WORKSTATION", 10, 0, 0));
        var viewModel = new KitaronStationsViewModel();
        viewModel.AttachSession(api, "client", 0, editor: false);
        await viewModel.RefreshAsync();
        viewModel.SelectedStation = viewModel.Stations.Single();

        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.SaveDecisionCommand.CanExecute(null));
        Assert.False(viewModel.AcceptMachineSuggestionsCommand.CanExecute(null));
        Assert.Single(viewModel.Stations);
    }

    private static PlannerKitaronStation Station(
        int id, string name, string role, string suggested, int rows, int planned, int supplier, string? machineType = null) =>
        new(id, name, null, false, rows, planned, supplier, suggested, role, machineType, null, null, 0, 0, 1, null,
            Seen, Seen, null, null, 1, Seen);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class FakeApiClient(params PlannerKitaronStation[] initial) : StubPlannerApiClient, IPlannerApiClient
    {
        private readonly List<PlannerKitaronStation> stations = [.. initial];

        internal List<(int StationId, KitaronStationDecision Decision, string ClientId, long Generation)> Decisions { get; } = [];

        public Task<IReadOnlyList<PlannerKitaronStation>> ListKitaronStationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerKitaronStation>>(stations.ToArray());

        public Task<PlannerKitaronStation> DecideKitaronStationAsync(
            int kitaronStationId, KitaronStationDecision decision, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            Decisions.Add((kitaronStationId, decision, clientId, editGeneration));
            var index = stations.FindIndex(station => station.KitaronStationId == kitaronStationId);
            var updated = stations[index] with
            {
                ImportRole = decision.ImportRole, MachineType = decision.MachineType, WorkstationTypeId = decision.WorkstationTypeId,
                ExternalResourceId = decision.ExternalResourceId, Version = decision.ExpectedVersion + 1
            };
            stations[index] = updated;
            return Task.FromResult(updated);
        }

        public Task<IReadOnlyList<PlannerWorkstationType>> ListWorkstationTypesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerWorkstationType>>([new("type-inspection", "Inspection", null, "{}", true, 1)]);

        public Task<IReadOnlyList<PlannerExternalResource>> ListExternalResourcesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerExternalResource>>([new("external-chrome", "Chrome plating", "Chromate", 4320, 0, "CALENDAR_TIME", null, "{}", true, 1)]);

        public Task<IReadOnlyList<PlannerMachineType>> ListMachineTypesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerMachineType>>([new("type-mill", "Mill 3x", [], 1, Seen, Seen)]);

    }
}
