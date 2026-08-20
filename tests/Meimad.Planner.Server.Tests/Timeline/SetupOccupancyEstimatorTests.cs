using Meimad.Planner.Server.Domain.Timeline;

namespace Meimad.Planner.Server.Tests.Timeline;

public sealed class SetupOccupancyEstimatorTests
{
    [Fact]
    public void Quantity_zero_has_no_machine_occupancy()
    {
        var result = Estimate(quantity: 0, tools: 5, fixture: 300, ncCycle: 100);

        Assert.Equal(0, result.ToolLoadingSeconds);
        Assert.Equal(0, result.FirstPieceProveOutSeconds);
        Assert.Equal(0, result.TotalSetupSeconds);
        Assert.Equal(0, result.RemainingProductionQuantity);
        Assert.Equal(0, result.RemainingProductionSeconds);
        Assert.Equal(0, result.TotalPlannedMachineSeconds);
    }

    [Fact]
    public void Quantity_one_includes_prepared_tool_loading_fixture_and_prove_out_once()
    {
        var result = Estimate(quantity: 1, tools: 2, fixture: 300, ncCycle: 100);

        Assert.Equal(120, result.ToolLoadingSeconds);
        Assert.Equal(300, result.FixtureSetupSeconds);
        Assert.Equal(150, result.FirstPieceProveOutSeconds);
        Assert.Equal(570, result.TotalSetupSeconds);
        Assert.Equal(0, result.RemainingProductionQuantity);
        Assert.Equal(0, result.RemainingProductionSeconds);
        Assert.Equal(570, result.TotalPlannedMachineSeconds);
    }

    [Fact]
    public void Quantity_greater_than_one_does_not_count_first_part_twice()
    {
        var result = Estimate(quantity: 4, tools: 2, fixture: 300, ncCycle: 100);

        Assert.Equal(570, result.TotalSetupSeconds);
        Assert.Equal(3, result.RemainingProductionQuantity);
        Assert.Equal(300, result.RemainingProductionSeconds);
        Assert.Equal(870, result.TotalPlannedMachineSeconds);
    }

    [Fact]
    public void Manager_override_precedes_nc_and_manual_estimates()
    {
        var result = SetupOccupancyEstimator.Evaluate(new SetupOccupancyInput(
            2, 0, 10, 80, 100, 120, 60, 1.5));

        Assert.Equal("manager_override", result.PlanningCycleSource);
        Assert.Equal(80, result.SelectedCycleSeconds);
        Assert.Equal(120, result.FirstPieceProveOutSeconds);
        Assert.Equal(210, result.TotalPlannedMachineSeconds);
    }

    [Fact]
    public void Manual_estimate_is_used_when_nc_estimate_is_unavailable()
    {
        var result = SetupOccupancyEstimator.Evaluate(new SetupOccupancyInput(
            2, 1, 30, null, null, 40, 60, 2));

        Assert.Equal("manual", result.PlanningCycleSource);
        Assert.Equal(40, result.SelectedCycleSeconds);
        Assert.Equal(170, result.TotalSetupSeconds);
        Assert.Equal(210, result.TotalPlannedMachineSeconds);
    }

    [Fact]
    public void A_changed_machine_nc_estimate_changes_prove_out_and_total_duration()
    {
        var faster = Estimate(quantity: 3, tools: 1, fixture: 30, ncCycle: 50);
        var slower = Estimate(quantity: 3, tools: 1, fixture: 30, ncCycle: 80);

        Assert.Equal(265, faster.TotalPlannedMachineSeconds);
        Assert.Equal(370, slower.TotalPlannedMachineSeconds);
    }

    [Fact]
    public void Missing_fixture_or_cycle_keeps_the_estimate_unavailable()
    {
        var missingFixture = Estimate(quantity: 1, tools: 1, fixture: null, ncCycle: 10);
        var missingCycle = Estimate(quantity: 1, tools: 1, fixture: 10, ncCycle: null);

        Assert.False(missingFixture.IsAvailable);
        Assert.Contains(missingFixture.Warnings, warning => warning.Contains("Fixture", StringComparison.Ordinal));
        Assert.False(missingCycle.IsAvailable);
        Assert.Contains(missingCycle.Warnings, warning => warning.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    private static SetupOccupancyEstimate Estimate(
        int quantity,
        int? tools,
        double? fixture,
        double? ncCycle) => SetupOccupancyEstimator.Evaluate(new SetupOccupancyInput(
            quantity,
            tools,
            fixture,
            null,
            ncCycle,
            null,
            60,
            1.5));
}
