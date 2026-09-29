using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Tests.Api;

/// <summary>The postprocessor status shows the current release's NC cycle and setup estimate per Machine.</summary>
public sealed class PostprocessorStatusTimeTests
{
    [Fact]
    public void The_current_release_shows_cycle_and_setup_per_machine_number()
    {
        var status = Status(Release(
        [
            Estimate("machine-b", "09", cycle: 81, setup: 181.5, fixture: 0),
            Estimate("machine-a", "07", cycle: 75, setup: null, fixture: null)
        ]));

        Assert.Equal(
            $"07: {Duration(75)} / part  ·  09: {Duration(81)} / part",
            status.NcCycleText);
        // A setup without the Operation's setup (fixture) time cannot be totalled and says why.
        Assert.Equal($"07: fixture time missing  ·  09: {Duration(182)}", status.SetupEstimateText);
    }

    [Fact]
    public void Without_a_current_release_or_machine_timing_the_columns_say_so()
    {
        Assert.Equal(string.Empty, Status(null).NcCycleText);
        var analysed = Release([]) with
        {
            NcAnalysis = new PlannerNcProgramAnalysis("nc-engine/1", "OK", 60, 300, 1, 2, "mm", [], [], "HIGH", DateTimeOffset.UnixEpoch)
        };
        Assert.Equal("No Machine timing", Status(analysed).NcCycleText);
        Assert.Equal("Estimate unavailable", Status(Release([])).SetupEstimateText);
    }

    private static string Duration(long seconds) => Meimad.Planner.Client.Windows.Formatting.DurationText.Format(seconds);

    private static PlannerPostprocessorReleaseStatus Status(PlannerGCodeRelease? release) =>
        new("post-a", "Haas", true, release is null ? "missing" : "current", release, null);

    private static PlannerGCodeRelease Release(IReadOnlyList<PlannerNcMachineCycleEstimate> estimates) => new(
        "release-1", "process-1", 1, "post-a", "Haas", 1, "main.nc", 100, new string('a', 64),
        DateTimeOffset.UnixEpoch, "nc-user", "NEW_PROCESS_REVISION", "Initial", "tools-1", true, true,
        MachineCycleEstimates: estimates);

    private static PlannerNcMachineCycleEstimate Estimate(string machineId, string number, double cycle, double? setup, double? fixture) => new(
        machineId, "nc-engine/1", 60, 300, 3, 1, 10, 2, 6000, 10, 1, cycle, cycle, [], "HIGH", DateTimeOffset.UnixEpoch,
        MachineNumber: number, MachineName: "Mill", RequiredToolCount: 1, ToolLoadingSeconds: 60,
        FixtureSetupSeconds: fixture, FirstPieceSeconds: cycle * 1.5, EstimatedSetupSeconds: setup);
}
