using System.Net;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.ToolPreparation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class ToolPreparationViewModelTests
{
    [Fact]
    public void Rows_follow_the_released_table_and_show_what_is_still_missing()
    {
        var api = new PreparationQueueViewModelTests.FakeApiClient([]) { ToolPreparation = Preparation() };
        var viewModel = new ToolPreparationViewModel(api, "client-1", "tool-room-1", api.ToolPreparation);

        Assert.Equal(["T1", "T2", "T3"], viewModel.Tools.Select(tool => tool.ToolIdentifier));
        var first = viewModel.Tools[0];
        // The offset number defaults to the tool number until the Tool Room enters another one.
        Assert.Equal("1", first.OffsetNumberText);
        Assert.False(first.IsComplete);
        Assert.Equal("Missing", first.CompletionText);
        Assert.Equal("Required", first.RequiredText);
        Assert.Equal("OTHER", first.Shape.Id);

        var second = viewModel.Tools[1];
        Assert.True(second.IsComplete);
        Assert.Equal("Measured", second.CompletionText);
        Assert.Equal("95.5", second.MeasuredLengthText);
        Assert.Equal("8.5", second.CuttingDiameterText);
        Assert.Equal("118", second.PointAngleText);
        Assert.Equal("DRILL", second.Shape.Id);
        Assert.Equal("regrind", second.Notes);
        var holder = Assert.Single(second.Components);
        Assert.Equal("BT40 ER32", holder.Name);
        Assert.Equal("HOLDER", holder.Type.Id);
        Assert.Equal("70", holder.LengthText);

        Assert.Equal("Optional", viewModel.Tools[2].CompletionText);
        Assert.Equal(1, viewModel.RequiredMissingCount);
        Assert.Contains("1 required tool", viewModel.ProgressText, StringComparison.Ordinal);
        Assert.Equal("No measurements saved yet.", viewModel.SavedText);
        Assert.False(viewModel.IsDirty);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.ShowsOlderToolTableWarning);
        Assert.Same(first, viewModel.SelectedTool);
    }

    [Fact]
    public async Task Save_sends_the_next_version_with_measurements_shape_and_components()
    {
        var api = new PreparationQueueViewModelTests.FakeApiClient([]) { ToolPreparation = Preparation() };
        var viewModel = new ToolPreparationViewModel(api, "client-1", "tool-room-1", api.ToolPreparation);
        var tool = viewModel.Tools[0];
        tool.MeasuredLengthText = "123,45"; // a decimal comma is accepted
        tool.MeasuredDiameterText = "10";
        tool.Shape = ToolPreparationCatalog.Shape("BALL_END_MILL");
        tool.CuttingDiameterText = "10";
        tool.FluteLengthText = "22";
        var holder = tool.AddComponent("HOLDER");
        holder.Name = "HSK63 ER32";
        holder.CatalogNumber = "HSK63-ER32-100";
        holder.LengthText = "80";
        holder.DiameterText = "63";
        var extension = tool.AddComponent("EXTENSION");
        extension.Name = "Ext 25x100";
        extension.LengthText = "100";
        extension.DiameterText = "25";
        viewModel.Comment = "Presetter, 2026-09-24";
        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.SaveCommand.CanExecute(null));
        Assert.True(tool.IsComplete);
        Assert.Equal(0, viewModel.RequiredMissingCount);

        await viewModel.SaveAsync();

        var update = Assert.Single(api.SavedUpdates);
        Assert.Equal(0, update.ExpectedVersion);
        Assert.Equal("tools-1", update.ToolTableReleaseId);
        Assert.Equal("Presetter, 2026-09-24", update.Comment);
        Assert.Equal("client-1", api.SavedClientId);
        Assert.Equal("tool-room-1", api.SavedUserId);
        // Every released row is sent, so the version is complete on its own.
        Assert.Equal(["T1", "T2", "T3"], update.Tools.Select(saved => saved.ToolIdentifier));
        var saved = update.Tools[0];
        Assert.Equal(1, saved.OffsetNumber);
        Assert.Equal(123.45, saved.MeasuredLength);
        Assert.Equal(10, saved.MeasuredDiameter);
        Assert.Equal("BALL_END_MILL", saved.ShapeType);
        Assert.Equal(10, saved.Shape["cuttingDiameter"]);
        Assert.Equal(22, saved.Shape["fluteLength"]);
        Assert.DoesNotContain("cornerRadius", saved.Shape.Keys);
        Assert.Equal(["HSK63 ER32", "Ext 25x100"], saved.Components.Select(component => component.Name));
        Assert.Equal([1, 2], saved.Components.Select(component => component.Sequence));
        Assert.Equal("HSK63-ER32-100", saved.Components[0].CatalogNumber);
        Assert.Equal(80, saved.Components[0].Length);
        Assert.Equal(63, saved.Components[0].Diameter);
        Assert.Equal(1, viewModel.Version);
        Assert.False(viewModel.IsDirty);
        Assert.Equal(string.Empty, viewModel.Comment);
        Assert.Contains("Saved version 1", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains("saved", viewModel.SavedText, StringComparison.Ordinal);
        Assert.Equal("123.45", viewModel.Tools[0].MeasuredLengthText);
    }

    [Fact]
    public async Task An_unreadable_number_blocks_the_save_and_names_the_tool()
    {
        var api = new PreparationQueueViewModelTests.FakeApiClient([]) { ToolPreparation = Preparation() };
        var viewModel = new ToolPreparationViewModel(api, "client-1", "tool-room-1", api.ToolPreparation);
        viewModel.Tools[0].MeasuredLengthText = "12.3.4";

        await viewModel.SaveAsync();

        Assert.Empty(api.SavedUpdates);
        Assert.Contains("T1", viewModel.Status, StringComparison.Ordinal);
        Assert.True(viewModel.IsDirty);

        viewModel.Tools[0].MeasuredLengthText = "100";
        viewModel.Tools[0].OffsetNumberText = "12000";
        await viewModel.SaveAsync();
        Assert.Empty(api.SavedUpdates);
        Assert.Contains("offset number", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_version_conflict_keeps_the_entries_and_a_reload_shows_the_server_version()
    {
        var api = new PreparationQueueViewModelTests.FakeApiClient([]) { ToolPreparation = Preparation() };
        var viewModel = new ToolPreparationViewModel(api, "client-1", "tool-room-1", api.ToolPreparation);
        viewModel.Tools[0].MeasuredLengthText = "100";
        viewModel.Tools[0].MeasuredDiameterText = "10";
        api.SaveError = new PlannerApiException(
            HttpStatusCode.Conflict, "tool_preparation_version_conflict", "A newer version was saved.");

        await viewModel.SaveAsync();

        Assert.Single(api.SavedUpdates);
        Assert.Equal("100", viewModel.Tools[0].MeasuredLengthText);
        Assert.True(viewModel.IsDirty);
        Assert.Contains("Reload", viewModel.Status, StringComparison.Ordinal);

        api.SaveError = null;
        api.ToolPreparation = Preparation(version: 2);
        await viewModel.ReloadAsync();

        Assert.Equal(2, viewModel.Version);
        Assert.False(viewModel.IsDirty);
        Assert.Equal(string.Empty, viewModel.Tools[0].MeasuredLengthText);
        Assert.Contains("Version 2 saved", viewModel.SavedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Measurements_saved_for_an_older_tool_table_release_are_flagged()
    {
        var api = new PreparationQueueViewModelTests.FakeApiClient([])
        {
            ToolPreparation = Preparation(version: 1) with { SavedForToolTableReleaseId = "tools-0" }
        };
        var viewModel = new ToolPreparationViewModel(api, "client-1", "tool-room-1", api.ToolPreparation);

        Assert.True(viewModel.ShowsOlderToolTableWarning);
        Assert.Contains("previous Tool Table release", viewModel.OlderToolTableWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void Components_and_dimensions_redraw_the_preview()
    {
        var api = new PreparationQueueViewModelTests.FakeApiClient([]) { ToolPreparation = Preparation() };
        var viewModel = new ToolPreparationViewModel(api, "client-1", "tool-room-1", api.ToolPreparation);
        var tool = viewModel.Tools[0];
        var redraws = 0;
        tool.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(tool.Geometry)) redraws++; };
        var before = tool.Geometry.Segments.Count;

        var component = tool.AddComponent("EXTENSION");
        component.LengthText = "120";
        component.DiameterText = "20";
        tool.Shape = ToolPreparationCatalog.Shape("DRILL");

        // The extension is drawn from the gauge line (no invented holder) and the drill shape adds its point.
        Assert.Equal(["CYLINDER", "CYLINDER", "CUTTER", "POINT"], tool.Geometry.Segments.Select(segment => segment.Kind));
        Assert.Equal(120, tool.Geometry.Segments[0].Height);
        Assert.True(redraws >= 4);
        Assert.Same(component, tool.SelectedComponent);
        Assert.True(viewModel.RemoveComponentCommand.CanExecute(null));

        tool.RemoveComponent(component);

        Assert.Equal(before + 1, tool.Geometry.Segments.Count);
        Assert.Null(tool.SelectedComponent);
        Assert.Equal(0, tool.ComponentCount);
    }

    [Fact]
    public void Turning_machines_explain_the_measurements_as_x_and_z_offsets()
    {
        var api = new PreparationQueueViewModelTests.FakeApiClient([])
        {
            ToolPreparation = Preparation() with { ProcessType = "turning", ToolDiameterOffsetKind = "DIAMETER" }
        };
        var viewModel = new ToolPreparationViewModel(api, "client-1", "tool-room-1", api.ToolPreparation);

        Assert.True(viewModel.IsTurning);
        Assert.Contains("X offset", viewModel.MeasurementHint, StringComparison.Ordinal);
        Assert.Contains("diameters", viewModel.OffsetKindText, StringComparison.Ordinal);
    }

    internal static PlannerToolPreparation Preparation(int version = 0) => new(
        "operation-1", "machine-1", "M01", "Mill", "mill", "HAAS_NGC", "RADIUS", "tools-1", 3, "tools.txt",
        version,
        version == 0 ? null : $"prep-{version}",
        version == 0 ? null : DateTimeOffset.Parse("2026-09-20T08:00:00Z"),
        version == 0 ? null : "tool-room-1",
        null, null,
        version == 0 ? null : "tools-1",
        [
            new(1, "T1", "End mill 10", true, "1", null, null, null, "OTHER",
                new Dictionary<string, double>(), null, []),
            new(2, "T2", "Drill 8.5", true, "2", 2, 95.5, 8.5, "DRILL",
                new Dictionary<string, double> { ["cuttingDiameter"] = 8.5, ["pointAngle"] = 118 }, "regrind",
                [new(1, "HOLDER", "BT40 ER32", "BT40-ER32-70", 70, 63, null)]),
            new(3, "T3", "Probe", false, null, null, null, null, "PROBE",
                new Dictionary<string, double>(), null, [])
        ]);
}
