using Meimad.Planner.Client.Windows.Presentation.ToolPreparation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class ToolShapeGeometryTests
{
    [Fact]
    public void Drill_gets_shank_flutes_and_point_below_a_default_holder()
    {
        var geometry = ToolShapeBuilder.Build(
            "DRILL",
            new Dictionary<string, double>
            {
                ["cuttingDiameter"] = 10, ["fluteLength"] = 50, ["overallLength"] = 90, ["pointAngle"] = 118
            },
            [], measuredLength: 120, measuredDiameter: 10);

        Assert.Equal(["HOLDER", "CYLINDER", "CUTTER", "POINT"], geometry.Segments.Select(segment => segment.Kind));
        // No holder was described, so a marked default holder sits on the gauge line.
        Assert.True(geometry.Segments[0].IsDefault);
        Assert.Equal(0, geometry.Segments[0].Top);
        Assert.Equal(60, geometry.Segments[1].Top);
        Assert.Equal(40, geometry.Segments[1].Height);
        Assert.Equal(10, geometry.Segments[2].Diameter);
        Assert.Equal(3.0, geometry.Segments[3].Height);
        Assert.Equal(118, geometry.Segments[3].TipAngle);
        Assert.Equal(150, geometry.TotalLength);
        Assert.Equal(63, geometry.MaximumDiameter);
        Assert.Equal(120, geometry.MeasuredLength);
        Assert.Equal(10, geometry.MeasuredDiameter);
        Assert.All(geometry.Segments.Skip(1), segment => Assert.False(segment.IsDefault));
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

        Assert.Equal(["HOLDER", "CYLINDER", "CUTTER"], geometry.Segments.Select(segment => segment.Kind));
        Assert.All(geometry.Segments, segment => Assert.True(segment.IsDefault));
        Assert.Equal(10, geometry.Segments[^1].Diameter);
        Assert.True(geometry.TotalLength > 60);
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
