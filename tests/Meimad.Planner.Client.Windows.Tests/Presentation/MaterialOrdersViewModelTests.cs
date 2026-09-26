using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class MaterialOrdersViewModelTests
{
    [Fact]
    public async Task The_list_shows_open_lines_first_and_filters_by_status_and_search()
    {
        var viewModel = new MaterialOrdersViewModel();
        viewModel.AttachSession(new FakeApiClient(
            Line("1", "AL-6061", "Metals Ltd", "late"),
            Line("2", "SS-304", "Steel Co", "received"),
            Line("3", "AL-7075", "Metals Ltd", "supplier_confirmed")));
        await viewModel.RefreshAsync();

        // By default only lines not yet received are shown.
        Assert.Equal(["1", "3"], viewModel.Items.Select(item => item.SourceKey));
        Assert.Contains("2 of 3", viewModel.Status);
        Assert.Contains("1 late", viewModel.Status);

        viewModel.StatusFilter = MaterialOrdersViewModel.AllStatuses;
        Assert.Equal(3, viewModel.Items.Count);

        viewModel.StatusFilter = "✓ Received";
        Assert.Equal(["2"], viewModel.Items.Select(item => item.SourceKey));

        viewModel.StatusFilter = MaterialOrdersViewModel.AllStatuses;
        viewModel.SearchText = "metals";
        Assert.Equal(["1", "3"], viewModel.Items.Select(item => item.SourceKey));
        Assert.Equal("⚠ Late", viewModel.Items[0].DeliveryStatusText);
    }

    private static KitaronMaterialOrder Line(string key, string material, string supplier, string status) =>
        new(key, "PO-" + key, "1", material, null, supplier, 10, null, "m", null, null, null, null, null,
            status is "received" or "closed", status, DateTimeOffset.UnixEpoch);

    private sealed class FakeApiClient(params KitaronMaterialOrder[] lines) : StubPlannerApiClient, IPlannerApiClient
    {
        public Task<IReadOnlyList<KitaronMaterialOrder>> ListKitaronMaterialOrdersAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KitaronMaterialOrder>>(lines);
    }
}
