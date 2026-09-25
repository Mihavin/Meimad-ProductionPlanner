using Meimad.Planner.Client.Windows.Presentation.ToolPreparation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class ToolShapeGeometryTests
{
    [Fact]
    public void Drill_gets_shank_flutes_and_point_and_the_measured_length_gap_without_a_holder()
    {
        var geometry = ToolShapeBuilder.Build(
            "DRILL",
            new Dictionary<string, double>
            {
                ["cuttingDiameter"] = 10, ["fluteLength"] = 50, ["overallLength"] = 90, ["pointAngle"] = 118
            },
            [], measuredLength: 120, measuredDiameter: 10);

        Assert.Equal(["GAP", "CYLINDER", "CUTTER", "POINT"], geometry.Segments.Select(segment => segment.Kind));
        // No component was described: the cutter's tip sits at the measured length and the
        // undescribed 30 mm above it are an empty axis, not an invented holder.
        Assert.True(geometry.Segments[0].IsDefault);
        Assert.Equal(0, geometry.Segments[0].Top);
        Assert.Equal(30, geometry.Segments[0].Height);
        Assert.Equal(30, geometry.Segments[1].Top);
        Assert.Equal(40, geometry.Segments[1].Height);
        Assert.Equal(10, geometry.Segments[2].Diameter);
        Assert.Equal(3.0, geometry.Segments[3].Height);
        Assert.Equal(118, geometry.Segments[3].TipAngle);
        Assert.Equal(120, geometry.TotalLength);
        Assert.Equal(10, geometry.MaximumDiameter);
        Assert.Equal(120, geometry.MeasuredLength);
        Assert.Equal(10, geometry.MeasuredDiameter);
        Assert.All(geometry.Segments.Skip(1), segment => Assert.False(segment.IsDefault));
    }

    [Fact]
    public void Turning_grooving_threading_and_the_new_milling_types_draw_their_own_parts()
    {
        var grooving = ToolShapeBuilder.Build("EXTERNAL_GROOVING",
            new Dictionary<string, double> { ["cuttingWidth"] = 3, ["maxDepth"] = 12, ["shankWidth"] = 25, ["overallLength"] = 150 }, [], null, null);
        Assert.Equal(["CYLINDER", "BLADE"], grooving.Segments.Select(segment => segment.Kind));
        Assert.Equal(25, grooving.Segments[0].Diameter);
        Assert.Equal(3, grooving.Segments[1].Diameter);
        Assert.Equal(12, grooving.Segments[1].Height);
        Assert.Equal(162, grooving.TotalLength);
        Assert.All(grooving.Segments, segment => Assert.False(segment.IsDefault));

        var threading = ToolShapeBuilder.Build("INTERNAL_THREADING",
            new Dictionary<string, double> { ["pitch"] = 1.5, ["shankDiameter"] = 16, ["overallLength"] = 120 }, [], null, null);
        Assert.Equal(["CYLINDER", "CONE"], threading.Segments.Select(segment => segment.Kind));
        Assert.Equal(60, threading.Segments[1].TipAngle);
        Assert.Equal(120, threading.Segments[0].Height);

        var boringBar = ToolShapeBuilder.Build("BORING_BAR",
            new Dictionary<string, double> { ["shankDiameter"] = 20, ["overallLength"] = 200, ["cornerRadius"] = 0.4, ["insertEdgeLength"] = 9 }, [], null, null);
        Assert.Equal(["CYLINDER", "INSERT"], boringBar.Segments.Select(segment => segment.Kind));
        Assert.Equal(200, boringBar.Segments[0].Height);
        Assert.Equal(9, boringBar.Segments[1].Height);
        Assert.False(boringBar.Segments[1].IsDefault);

        var tSlot = ToolShapeBuilder.Build("T_SLOT_MILL",
            new Dictionary<string, double> { ["cuttingDiameter"] = 20, ["cuttingWidth"] = 6, ["neckDiameter"] = 8, ["neckLength"] = 15, ["overallLength"] = 80, ["shankDiameter"] = 12 }, [], null, null);
        Assert.Equal(["CYLINDER", "CYLINDER", "DISC"], tSlot.Segments.Select(segment => segment.Kind));
        Assert.Equal(59, tSlot.Segments[0].Height);
        Assert.Equal(8, tSlot.Segments[1].Diameter);
        Assert.Equal(6, tSlot.Segments[2].Height);
        Assert.Equal(20, tSlot.Segments[2].Diameter);

        var lollipop = ToolShapeBuilder.Build("LOLLIPOP_MILL",
            new Dictionary<string, double> { ["cuttingDiameter"] = 8, ["neckDiameter"] = 5, ["neckLength"] = 20, ["overallLength"] = 70, ["shankDiameter"] = 8 }, [], null, null);
        Assert.Equal(["CYLINDER", "CYLINDER", "SPHERE"], lollipop.Segments.Select(segment => segment.Kind));
        Assert.Equal(42, lollipop.Segments[0].Height);
        Assert.Equal(8, lollipop.Segments[2].Diameter);

        var dovetail = ToolShapeBuilder.Build("DOVETAIL_MILL",
            new Dictionary<string, double> { ["cuttingDiameter"] = 16, ["fluteLength"] = 8, ["overallLength"] = 60, ["shankDiameter"] = 12, ["taperAngle"] = 60 }, [], null, null);
        Assert.Equal("DOVETAIL", dovetail.Segments[^1].Kind);
        Assert.Equal(60, dovetail.Segments[^1].TipAngle);
        Assert.Equal(16, dovetail.Segments[^1].Diameter);

        var countersink = ToolShapeBuilder.Build("COUNTERSINK",
            new Dictionary<string, double> { ["cuttingDiameter"] = 12, ["pointAngle"] = 90, ["fluteLength"] = 10, ["overallLength"] = 60, ["shankDiameter"] = 8 }, [], null, null);
        Assert.Equal("POINT", countersink.Segments[^1].Kind);
        Assert.Equal(90, countersink.Segments[^1].TipAngle);
        Assert.Equal(6, countersink.Segments[^1].Height);
    }

    [Fact]
    public void Components_stack_in_order_and_an_explicit_holder_replaces_the_default()
    {
        var geometry = ToolShapeBuilder.Build(
            "END_MILL",
            new Dictionary<string, double> { ["cuttingDiameter"] = 10, ["fluteLength"] = 22, ["overallLength"] = 72 },
            [
                new("HOLDER", "BT40 ER32", 70, 63),
                new("EXTENSION", "Ext 25x100", 100, 25),
                new("CUTTER", "EM10", null, null)
            ],
            null, null);

        Assert.Equal(["HOLDER", "CYLINDER", "CYLINDER", "CUTTER"], geometry.Segments.Select(segment => segment.Kind));
        Assert.Equal(["BT40 ER32", "Ext 25x100", "EM10", "End mill"], geometry.Segments.Select(segment => segment.Label));
        Assert.Equal([0, 70, 170, 220], geometry.Segments.Select(segment => segment.Top));
        Assert.Equal(242, geometry.TotalLength);
        Assert.All(geometry.Segments, segment => Assert.False(segment.IsDefault));
        Assert.Null(geometry.MeasuredLength);
    }

    [Fact]
    public void Missing_dimensions_are_drawn_with_marked_defaults()
    {
        var geometry = ToolShapeBuilder.Build("END_MILL", new Dictionary<string, double>(), [], null, null);

        Assert.Equal(["CYLINDER", "CUTTER"], geometry.Segments.Select(segment => segment.Kind));
        Assert.All(geometry.Segments, segment => Assert.True(segment.IsDefault));
        Assert.Equal(10, geometry.Segments[^1].Diameter);
        Assert.True(geometry.TotalLength > 0);
    }

    [Fact]
    public void Without_components_the_cutter_hangs_from_the_gauge_line()
    {
        var shape = new Dictionary<string, double> { ["cuttingDiameter"] = 10, ["fluteLength"] = 22, ["overallLength"] = 72 };

        var shorter = ToolShapeBuilder.Build("END_MILL", shape, [], measuredLength: 70, measuredDiameter: 10);
        Assert.Equal(["CYLINDER", "CUTTER"], shorter.Segments.Select(segment => segment.Kind));
        Assert.Equal(0, shorter.Segments[0].Top);
        Assert.Equal(72, shorter.TotalLength);

        var longer = ToolShapeBuilder.Build("END_MILL", shape, [], measuredLength: 100, measuredDiameter: 10);
        Assert.Equal(["GAP", "CYLINDER", "CUTTER"], longer.Segments.Select(segment => segment.Kind));
        Assert.Equal(28, longer.Segments[0].Height);
        Assert.Equal(100, longer.TotalLength);

        // A described holder is drawn instead of the gap.
        var assembled = ToolShapeBuilder.Build("END_MILL", shape, [new("HOLDER", "BT40", 28, 63)], 100, 10);
        Assert.Equal(["HOLDER", "CYLINDER", "CUTTER"], assembled.Segments.Select(segment => segment.Kind));
        Assert.Equal(100, assembled.TotalLength);
    }

    [Fact]
    public void Measured_diameter_sizes_the_cutter_when_no_shape_diameter_was_entered()
    {
        var geometry = ToolShapeBuilder.Build("END_MILL", new Dictionary<string, double>(), [], 95, 16);

        Assert.Equal(16, geometry.Segments[^1].Diameter);
        Assert.False(geometry.Segments[^1].IsDefault);
    }

    [Fact]
    public void Special_shapes_end_in_their_own_tip_segment()
    {
        var shape = new Dictionary<string, double> { ["cuttingDiameter"] = 10 };

        Assert.Equal("BALL", ToolShapeBuilder.Build("BALL_END_MILL", shape, [], null, null).Segments[^1].Kind);
        Assert.Equal(5, ToolShapeBuilder.Build("BALL_END_MILL", shape, [], null, null).Segments[^1].Height);
        Assert.Equal("DISC", ToolShapeBuilder.Build("FACE_MILL", shape, [], null, null).Segments[^1].Kind);
        Assert.Equal("CONE", ToolShapeBuilder.Build("CHAMFER_MILL", shape, [], null, null).Segments[^1].Kind);
        Assert.Equal("INSERT", ToolShapeBuilder.Build("TURNING_TOOL", shape, [], null, null).Segments[^1].Kind);
        Assert.Equal("SPHERE", ToolShapeBuilder.Build("PROBE", shape, [], null, null).Segments[^1].Kind);
        Assert.True(ToolShapeBuilder.Build("BULL_NOSE_END_MILL", shape, [], null, null).Segments[^1].CornerRadius > 0);
    }

    [Theory]
    [InlineData(10, 118, 3.0)]
    [InlineData(10, 90, 5.0)]
    [InlineData(20, 140, 3.64)]
    public void Point_height_follows_the_included_angle(double diameter, double angle, double expected)
    {
        Assert.Equal(expected, ToolShapeBuilder.PointHeight(diameter, angle));
    }
}
