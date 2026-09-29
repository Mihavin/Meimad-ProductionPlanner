using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class SpindleLibraryViewModelTests
{
    [Fact]
    public async Task The_library_lists_mills_saves_an_adaptor_and_only_changed_machine_defaults()
    {
        var api = new FakeApiClient();
        var viewModel = new SpindleLibraryViewModel();
        viewModel.AttachSession(api, "windows-1", "planner", editor: true);
        await viewModel.RefreshAsync();

        Assert.Equal(["BT40"], viewModel.Adaptors.Select(adaptor => adaptor.Name));
        // Lathes hold their tools in the turret: only the mill is listed.
        var row = Assert.Single(viewModel.Machines);
        Assert.Equal("07 Doosan", row.MachineText);
        Assert.Equal("None", row.Adaptor.Name);

        viewModel.SelectedAdaptor = null;
        viewModel.AdaptorName = "CAT40";
        viewModel.TaperLength = "68.25";
        viewModel.GaugeDiameter = "44,45";
        viewModel.ToolChangerDiameter = "63.5";
        viewModel.ToolChangerLength = string.Empty;
        await viewModel.SaveAdaptorAsync();
        Assert.Equal("Tool changer length (TCL) is required.", viewModel.Status);
        viewModel.ToolChangerLength = "15.9";
        await viewModel.SaveAdaptorAsync();
        Assert.Equal((null, 44.45, (double?)null), (api.SavedAdaptorId, api.SavedAdaptor!.GaugeDiameter, api.SavedAdaptor.SmallEndDiameter));
        Assert.Equal("Spindle adaptor CAT40 saved.", viewModel.Status);

        await viewModel.SaveMachinesAsync();
        Assert.Empty(api.SavedMachines);
        Assert.Equal("No Machine default was changed.", viewModel.Status);
        viewModel.Machines[0].Adaptor = viewModel.Machines[0].Adaptors.Single(choice => choice.Id == "bt40");
        await viewModel.SaveMachinesAsync();
        Assert.Equal(("machine-07", "bt40", (string?)null, 0), Assert.Single(api.SavedMachines));
    }

    private sealed class FakeApiClient : StubPlannerApiClient, IPlannerApiClient
    {
        private readonly List<PlannerSpindleAdaptor> adaptors = [new("bt40", "BT40", 65.4, 44.45, 25.375, 63, 25, null, true, 1)];

        internal string? SavedAdaptorId { get; private set; }
        internal SpindleAdaptorSave? SavedAdaptor { get; private set; }
        internal List<(string, string?, string?, int)> SavedMachines { get; } = [];

        public Task<PlannerSpindleLibrary> GetSpindleLibraryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlannerSpindleLibrary(adaptors.ToArray(), [], []));

        public Task<IReadOnlyList<PlannerMachine>> ListMachinesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerMachine>>(
            [
                Machine("machine-07", "07", "Doosan", "mill"),
                Machine("machine-20", "20", "Okuma", "lathe")
            ]);

        public Task<PlannerSpindleAdaptor> SaveSpindleAdaptorAsync(
            string? spindleAdaptorId, SpindleAdaptorSave value, string clientId, string userId, CancellationToken cancellationToken = default)
        {
            (SavedAdaptorId, SavedAdaptor) = (spindleAdaptorId, value);
            var saved = new PlannerSpindleAdaptor("cat40", value.Name, value.TaperLength, value.GaugeDiameter, 24.54,
                value.ToolChangerDiameter, value.ToolChangerLength, value.Notes, value.IsActive, 1);
            adaptors.Add(saved);
            return Task.FromResult(saved);
        }

        public Task<PlannerMachineSpindleInterface> SaveMachineSpindleInterfaceAsync(
            string machineId, string? spindleAdaptorId, string? pullStudId, int expectedVersion, string clientId, string userId,
            CancellationToken cancellationToken = default)
        {
            SavedMachines.Add((machineId, spindleAdaptorId, pullStudId, expectedVersion));
            return Task.FromResult(new PlannerMachineSpindleInterface(machineId, spindleAdaptorId, pullStudId, expectedVersion + 1));
        }

        private static PlannerMachine Machine(string id, string number, string name, string processType) => new(
            id, number, name, processType, null, [], "calendar-1", true, true, null, null, 0, 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    }
}
