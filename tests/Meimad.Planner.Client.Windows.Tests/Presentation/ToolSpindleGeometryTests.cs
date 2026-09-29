using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;
using Meimad.Planner.Client.Windows.Presentation.ToolPreparation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>The spindle side of a milling tool in the Tool Room drawing (owner decisions 2026-09-29, schema v91).</summary>
public sealed class ToolSpindleGeometryTests
{
    private static readonly PlannerSpindleAdaptor Bt40 = new("bt40", "BT40", 65.4, 44.45, 25.375, 63, 25, null, true, 1);
    private static readonly PlannerPullStud Haas = new("haas", "HAAS BT40 45° M16", "M16x2.0", 45, 59.94, 27.94, 14.96, 9.96, 16.99, null, true, 1);

    [Fact]
    public void The_owner_example_draws_the_pull_stud_and_taper_above_the_gauge_line_and_drives_the_holder_length()
    {
        // Flat mill D10, CL 22, SD 10, OL 80, OHL 50 in a Ø40 holder on BT40 (TCD 63, TCL 25), measured 120.
        var geometry = ToolShapeBuilder.Build(
            "END_MILL",
            new Dictionary<string, double>
            {
                ["cuttingDiameter"] = 10, ["fluteLength"] = 22, ["shankDiameter"] = 10, ["overallLength"] = 80, ["outsideHolderLength"] = 50
            },
            [new ToolShapeComponent("HOLDER", "ER32 holder", null, 40), new ToolShapeComponent("COLLET", "ER32 10", null, 10)],
            measuredLength: 120, measuredDiameter: 10,
            ToolSpindleShape.From(Bt40, Haas));

        Assert.Equal(["CYLINDER", "CYLINDER", "CYLINDER", "TAPER", "CYLINDER", "CYLINDER", "CYLINDER", "CUTTER"],
            geometry.Segments.Select(segment => segment.Kind));
        // Above the gauge line: the pull stud (27.94) on the taper (65.4, Ø44.45 down to Ø25.375).
        Assert.Equal(65.4 + 27.94, geometry.AboveGaugeLength, 6);
        Assert.StartsWith("Pull stud HAAS BT40", geometry.Segments[0].Label, StringComparison.Ordinal);
        Assert.Equal(-(65.4 + 27.94), geometry.Segments[0].Top, 6);
        var taper = geometry.Segments[3];
        Assert.Equal((-65.4, 65.4, 44.45, 25.375), (taper.Top, taper.Height, taper.Diameter, taper.TopDiameter));
        // Below it: the tool-changer flange, the holder with HL = 120 − 50 − 25 = 45, then OHL 50 of the tool.
        Assert.Equal((0d, 25d, 63d), (geometry.Segments[4].Top, geometry.Segments[4].Height, geometry.Segments[4].Diameter));
        var holder = geometry.Segments[5];
        Assert.Equal((25d, 45d, 40d, "ER32 holder (HL)", false), (holder.Top, holder.Height, holder.Diameter, holder.Label, holder.IsDefault));
        Assert.Equal((70d, 28d, 10d), (geometry.Segments[6].Top, geometry.Segments[6].Height, geometry.Segments[6].Diameter));
        Assert.Equal((98d, 22d, 10d), (geometry.Segments[7].Top, geometry.Segments[7].Height, geometry.Segments[7].Diameter));
        // The tip lands exactly on the measured length.
        Assert.Equal(120, geometry.TotalLength, 6);
    }

    [Fact]
    public void Without_a_measured_length_the_holder_keeps_its_own_length_and_is_marked_unspecified()
    {
        var geometry = ToolShapeBuilder.Build(
            "DRILL",
            new Dictionary<string, double> { ["cuttingDiameter"] = 8, ["fluteLength"] = 40, ["outsideHolderLength"] = 60 },
            [new ToolShapeComponent("HOLDER", "Drill chuck", 70, 50)],
            measuredLength: null, measuredDiameter: null,
            ToolSpindleShape.From(Bt40, null));

        Assert.Equal(65.4, geometry.AboveGaugeLength, 6);
        Assert.DoesNotContain(geometry.Segments, segment => segment.Label.StartsWith("Pull stud", StringComparison.Ordinal));
        var holder = geometry.Segments.Single(segment => segment.Label == "Drill chuck");
        Assert.Equal((25d, 70d, true), (holder.Top, holder.Height, holder.IsDefault));
    }

    [Fact]
    public void Turning_tools_and_tools_without_an_adaptor_are_drawn_as_before()
    {
        var shape = new Dictionary<string, double> { ["cornerRadius"] = 0.4, ["overallLength"] = 100 };
        var withSpindle = ToolShapeBuilder.Build("TURNING_TOOL", shape, [], 120, 20, ToolSpindleShape.From(Bt40, Haas));
        var without = ToolShapeBuilder.Build("TURNING_TOOL", shape, [], 120, 20);
        Assert.Equal(without.Segments, withSpindle.Segments);
        Assert.Equal(0, withSpindle.AboveGaugeLength);
        Assert.Null(ToolSpindleShape.From(null, Haas));
    }

    [Fact]
    public void A_tool_uses_its_own_choice_else_the_machine_default_in_the_tool_room_and_the_nc_viewer()
    {
        var studB = Haas with { PullStudId = "stud-b", Name = "MAS P40T-I" };
        var context = new ToolSpindleContext([Bt40], [Haas, studB], "bt40", "haas");
        Assert.Equal("Machine default (BT40)", context.AdaptorChoices(null)[0].Name);
        Assert.Equal("HAAS BT40 45° M16", context.Resolve(null, null)!.PullStudName);
        Assert.Equal("MAS P40T-I", context.Resolve(null, "stud-b")!.PullStudName);

        var preparation = new PlannerToolPreparation(
            "operation-1", "machine-1", "M01", "Mill", "mill", "HAAS_NGC", "RADIUS", "tools-1", 1, "tools.csv",
            1, "prep-1", DateTimeOffset.Parse("2026-09-29T09:00:00Z"), "tool-room", null, null, "tools-1",
            [
                new(1, "T1", "Flat D10", true, "1", 1, 120, 10, "END_MILL",
                    new Dictionary<string, double> { ["cuttingDiameter"] = 10, ["fluteLength"] = 22, ["outsideHolderLength"] = 50 }, null,
                    [new(1, "HOLDER", "ER32 holder", null, null, 40, null)], PullStudId: "stud-b")
            ],
            "bt40", "haas", [Bt40], [Haas, studB]);
        var assembly = Assert.Single(NcViewerOperationToolTable.From(preparation)!.Assemblies!);
        Assert.Contains(assembly.Segments, segment => segment.Kind == "TAPER" && segment.TopDiameter == 25.375);
        Assert.Contains(assembly.Segments, segment => segment.Label == "Pull stud MAS P40T-I");
        Assert.Equal(65.4 + 27.94, assembly.AboveGaugeLength, 3);
        Assert.Equal(120, assembly.TotalLength, 3);
    }
}
