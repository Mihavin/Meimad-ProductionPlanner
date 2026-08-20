using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Domain.GCode;

namespace Meimad.Planner.Server.Tests.GCode;

public sealed class NcCycleTimeEstimatorTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-20T10:00:00Z");

    [Fact]
    public void Metric_absolute_incremental_and_multiple_feeds_are_modal()
    {
        var analysis = Parse(
            "(roughing)",
            "N10 G21 G90",
            "N20 G1 X60 F600 ; first feed",
            "N30 G91",
            "N40 G1 X60 F1200",
            "N50 G90 G1 X180");

        Assert.Equal("MILLIMETER", analysis.DetectedUnits);
        Assert.Equal(12d, analysis.FeedMotionSeconds, 6);
        Assert.Equal(NcEstimateConfidence.High, analysis.Confidence);
    }

    [Fact]
    public void Inch_program_converts_distance_and_feed_to_millimeters()
    {
        var analysis = Parse("G20 G90", "G1 X1 F60");

        Assert.Equal("INCH", analysis.DetectedUnits);
        Assert.Equal(1d, analysis.FeedMotionSeconds, 6);
    }

    [Fact]
    public void Rapid_arc_tool_changes_and_dwell_preserve_raw_metrics()
    {
        var analysis = Parse(
            "G21 G90",
            "G0 X100 Y0",
            "G1 X120 F600",
            "G3 X140 Y0 I10 J0",
            "T1 M6",
            "T2 M06",
            "G4 P2000");

        Assert.Equal(100d, analysis.RapidDistanceMillimeters, 6);
        Assert.Equal(2d + Math.PI, analysis.FeedMotionSeconds, 6);
        Assert.Equal(2, analysis.ToolChangeCount);
        Assert.Equal(2d, analysis.DwellSeconds, 6);
    }

    [Fact]
    public void R_arc_and_helical_arc_are_supported_when_geometry_is_defined()
    {
        var radiusArc = Parse("G21 G90 G1 X0 Y0 F600", "G2 X20 Y0 R10");
        var helicalArc = Parse("G21 G90 G1 X0 Y0 Z0 F600", "G3 X20 Y0 Z10 I10 J0");

        Assert.Equal(Math.PI, radiusArc.FeedMotionSeconds, 6);
        Assert.Equal(Math.Sqrt(Math.Pow(Math.PI * 10, 2) + 100) / 10,
            helicalArc.FeedMotionSeconds, 6);
    }

    [Fact]
    public void Unsupported_and_malformed_content_warns_without_throwing()
    {
        var analysis = Parse(
            "G21 G90",
            "G81 X10 Y10 Z-5 F100",
            "G1 X#101 F200",
            "G93 G1 X20 F2",
            "M98 P1000",
            "THIS IS NOT AN NC BLOCK");

        Assert.Equal(NcAnalysisStatus.Partial, analysis.Status);
        Assert.Equal(NcEstimateConfidence.Low, analysis.Confidence);
        Assert.Contains("G81", analysis.UnsupportedConstructs);
        Assert.Contains("MACRO_VARIABLE", analysis.UnsupportedConstructs);
        Assert.Contains("G93", analysis.UnsupportedConstructs);
        Assert.Contains("M98", analysis.UnsupportedConstructs);
        Assert.Contains(analysis.Warnings, value => value.Contains("malformed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Same_analysis_evaluates_differently_for_two_machines()
    {
        var analysis = Parse("G21 G90", "G0 X6000", "T1 M6", "G4 X2");

        var first = NcCycleTimeEstimator.Evaluate("release-1", analysis,
            new NcMachineTiming("machine-a", 6000, 10, 1), At);
        var second = NcCycleTimeEstimator.Evaluate("release-1", analysis,
            new NcMachineTiming("machine-b", 12000, 4, 1.2), At);

        Assert.Equal(72d, first.EstimatedCycleSeconds!.Value, 6);
        Assert.Equal(43.2d, second.EstimatedCycleSeconds!.Value, 6);
        Assert.Equal(60d, first.RapidSeconds!.Value, 6);
        Assert.Equal(30d, second.RapidSeconds!.Value, 6);
    }

    [Fact]
    public void Missing_machine_timing_makes_estimate_unavailable_not_gcode_invalid()
    {
        var analysis = Parse("G21 G90", "G0 X10", "T1 M6", "G1 X20 F100");

        var estimate = NcCycleTimeEstimator.Evaluate("release-1", analysis,
            new NcMachineTiming("machine-a", null, null, 1), At);

        Assert.Equal(NcEstimateConfidence.Unavailable, estimate.Confidence);
        Assert.Null(estimate.EstimatedCycleSeconds);
        Assert.Equal(NcAnalysisStatus.Complete, analysis.Status);
    }

    private static NcProgramAnalysis Parse(params string[] lines) =>
        NcProgramParser.Parse(lines, At);
}
