using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.ToolCatalog;
using Meimad.Planner.Client.Windows.Presentation.ToolPreparation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class ToolCatalogViewModelTests
{
    [Fact]
    public async Task Search_filter_create_edit_and_delete_go_through_the_catalog_api_with_the_client_identity()
    {
        var api = new PreparationQueueViewModelTests.FakeApiClient([]);
        api.CatalogTools.Add(Tool("catalog-1", 1, "PCLNR 2525 M12", "TURNING_TOOL", "RIGHT", [new("ERP", "K-4711")]));
        api.CatalogTools.Add(Tool("catalog-2", 2, "Flat end mill D10", "END_MILL", null, [new("CAM", "EM10")]));
        var viewModel = new ToolCatalogViewModel();
        Assert.False(viewModel.IsConnected);

        viewModel.AttachSession(api, "client-1", "tool-room-1");
        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsConnected);
        Assert.Equal(["MT-00001", "MT-00002"], viewModel.Tools.Select(tool => tool.InternalCode));
        Assert.Equal("2 tools", viewModel.CountText);

        // Search by an external id, then filter by type.
        viewModel.SearchText = "K-4711";
        await viewModel.RefreshAsync();
        Assert.Equal(["MT-00001"], viewModel.Tools.Select(tool => tool.InternalCode));
        viewModel.SearchText = string.Empty;
        viewModel.TypeFilter = viewModel.TypeFilters.First(option => option.Id == "END_MILL");
        await viewModel.RefreshAsync();
        Assert.Equal(["MT-00002"], viewModel.Tools.Select(tool => tool.InternalCode));
        viewModel.TypeFilter = viewModel.TypeFilters[0];
        await viewModel.RefreshAsync();
        Assert.Equal(2, viewModel.Tools.Count);

        // Selecting a tool loads the editor; a turning tool shows its hand.
        viewModel.Selected = viewModel.Tools[0];
        Assert.Equal("PCLNR 2525 M12", viewModel.Editor.Name);
        Assert.Equal("TURNING_TOOL", viewModel.Editor.Shape.Id);
        Assert.True(viewModel.Editor.HasHand);
        Assert.Equal("RIGHT", viewModel.Editor.Hand.Id);
        Assert.Equal("MT-00001 (version 1)", viewModel.Editor.Heading);
        Assert.Equal("0.8", viewModel.Editor.DimensionFields.First(field => field.Key == "cornerRadius").Text);
        Assert.Equal("K-4711", Assert.Single(viewModel.Editor.ExternalIds).Value);
        Assert.False(viewModel.IsDirty);

        // A new tool: the Server assigns the code; the client identity travels with the request.
        viewModel.BeginNew();
        Assert.True(viewModel.Editor.IsNew);
        Assert.Equal("New catalog tool", viewModel.Editor.Heading);
        viewModel.Editor.Name = "GX24 parting blade";
        viewModel.Editor.Shape = ToolPreparationCatalog.Shape("PARTING");
        viewModel.Editor.Hand = ToolPreparationCatalog.Hand("LEFT");
        Assert.Equal(["cuttingWidth", "maxDepth", "cornerRadius", "shankWidth", "shankHeight", "overallLength"], viewModel.Editor.DimensionFields.Select(field => field.Key));
        viewModel.Editor.DimensionFields.First(field => field.Key == "cuttingWidth").Text = "3";
        viewModel.Editor.InsertCode = "GX24-3E300N020-CE4";
        viewModel.Editor.Manufacturer = "Walter";
        var external = viewModel.Editor.AddExternalId();
        external.System = "ERP";
        external.Value = "K-9";
        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.SaveCommand.CanExecute(null));

        await viewModel.SaveAsync();

        var created = Assert.Single(api.CatalogTools, tool => tool.Name == "GX24 parting blade");
        Assert.Equal("MT-00003", created.InternalCode);
        Assert.Equal("PARTING", created.ToolType);
        Assert.Equal("LEFT", created.Hand);
        Assert.Equal(3, created.Shape["cuttingWidth"]);
        Assert.Equal("GX24-3E300N020-CE4", created.Attributes["insertCode"]);
        Assert.Equal("Walter", created.Attributes["manufacturer"]);
        Assert.Equal("K-9", Assert.Single(created.ExternalIds).Value);
        Assert.Null(api.LastCatalogUpdate!.ExpectedVersion);
        Assert.Equal("client-1", api.CatalogClientId);
        Assert.Equal("tool-room-1", api.CatalogUserId);
        Assert.Equal(created.CatalogToolId, viewModel.Selected?.CatalogToolId);
        Assert.False(viewModel.IsDirty);
        Assert.Contains("MT-00003", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal(3, viewModel.Tools.Count);

        // Editing replaces the tool at its version.
        viewModel.Editor.Name = "GX24 parting blade 3 mm";
        Assert.True(viewModel.IsDirty);
        await viewModel.SaveAsync();
        Assert.Equal(1, api.LastCatalogUpdate!.ExpectedVersion);
        Assert.Equal(2, api.CatalogTools.Single(tool => tool.CatalogToolId == created.CatalogToolId).Version);
        Assert.Equal("MT-00003 (version 2)", viewModel.Editor.Heading);

        // Somebody else saved meanwhile: the entries are kept and the status says to refresh.
        var index = api.CatalogTools.FindIndex(tool => tool.CatalogToolId == created.CatalogToolId);
        api.CatalogTools[index] = api.CatalogTools[index] with { Version = 7 };
        viewModel.Editor.Description = "changed here";
        await viewModel.SaveAsync();
        Assert.Contains("Refresh", viewModel.Status, StringComparison.Ordinal);
        Assert.True(viewModel.IsDirty);
        Assert.Equal("changed here", viewModel.Editor.Description);

        // Validation happens before any request.
        viewModel.BeginNew();
        viewModel.Editor.DimensionFields[0].Text = "abc";
        await viewModel.SaveAsync();
        Assert.Equal("The tool needs a name.", viewModel.Status);
        viewModel.Editor.Name = "Bad number";
        await viewModel.SaveAsync();
        Assert.Contains("must be a number", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal(3, api.CatalogTools.Count);

        // Delete.
        viewModel.Selected = viewModel.Tools.First(tool => tool.InternalCode == "MT-00002");
        await viewModel.DeleteSelectedAsync();
        Assert.DoesNotContain(api.CatalogTools, tool => tool.InternalCode == "MT-00002");
        Assert.Equal(["MT-00001", "MT-00003"], viewModel.Tools.Select(tool => tool.InternalCode));
        Assert.True(viewModel.Editor.IsNew);

        viewModel.AttachSession(null, null, null);
        Assert.False(viewModel.IsConnected);
        Assert.Empty(viewModel.Tools);
    }

    private static PlannerCatalogTool Tool(string id, int number, string name, string type, string? hand, PlannerCatalogToolExternalId[] externalIds)
    {
        var now = DateTimeOffset.Parse("2026-09-25T08:00:00Z");
        return new PlannerCatalogTool(
            id, number, $"MT-{number:D5}", name, type, type == "TURNING_TOOL" ? "TURNING" : "MILLING", hand, null,
            new Dictionary<string, double> { ["cornerRadius"] = 0.8 }, new Dictionary<string, string>(), externalIds, true, 1, now, now, "tool-room-1");
    }
}
