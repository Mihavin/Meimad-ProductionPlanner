using Meimad.Planner.Server.Domain.ProductionRuns;

namespace Meimad.Planner.Server.Tests.ProductionRuns;

public sealed class ProductionRunCyclePlannerTests
{
    private readonly ProductionRunCyclePlanner planner = new();

    [Fact]
    public void One_program_one_output_calculates_ten_exact_cycles()
    {
        var result = planner.Calculate(Input(0, Program("A", 0, 6, 0, Output("A", 1, 10, 10))));
        Assert.Equal(10, result.TotalProgramExecutions);
        Assert.Equal(TimeSpan.FromSeconds(60), result.TotalDuration);
        Assert.Equal(10, result.Programs[0].TargetCycles);
    }

    [Fact]
    public void Coupled_two_a_and_one_b_for_twenty_and_ten_is_ten_cycles()
    {
        var result = planner.Calculate(Input(30,
            Program("combined", 0, 5, 0,
                Output("A", 2, 20, 20), Output("B", 1, 10, 10))));
        Assert.Equal(10, result.Programs[0].TargetCycles);
        Assert.Equal(TimeSpan.FromSeconds(80), result.TotalDuration);
        Assert.All(result.Programs[0].Outputs,
            value => Assert.Equal(TimeSpan.FromSeconds(80), value.ForecastCompletionOffset));
    }

    [Fact]
    public void Coupled_outputs_with_unequal_required_cycles_are_rejected()
    {
        var error = Assert.Throws<ProductionRunCycleValidationException>(() => planner.Calculate(
            Input(0, Program("combined", 0, 1, 0,
                Output("A", 2, 20, 20), Output("B", 1, 9, 9)))));
        Assert.Equal("unequal_required_cycles", error.Code);
    }

    [Fact]
    public void Non_divisible_target_is_rejected_without_rounding()
    {
        var error = Assert.Throws<ProductionRunCycleValidationException>(() => planner.Calculate(
            Input(0, Program("combined", 0, 1, 0, Output("A", 2, 19, 19)))));
        Assert.Equal("not_divisible", error.Code);
    }

    [Fact]
    public void Two_programs_have_independent_ten_and_four_cycle_targets()
    {
        var result = planner.Calculate(Input(0,
            Program("A", 0, 10, 0, Output("A", 1, 10, 10)),
            Program("B", 1, 20, 0, Output("B", 1, 4, 4))));
        Assert.Equal(new long[] { 10, 4 }, result.Programs.Select(value => value.TargetCycles));
        Assert.Equal(14, result.TotalProgramExecutions);
    }

    [Fact]
    public void Shorter_program_completes_after_round_four_and_is_skipped_later()
    {
        var result = planner.Calculate(Input(0,
            Program("A", 0, 10, 0, Output("A", 1, 10, 10)),
            Program("B", 1, 20, 0, Output("B", 1, 4, 4))));
        Assert.Equal(TimeSpan.FromSeconds(120), result.Programs.Single(value => value.ProgramId == "B").ForecastCompletionOffset);
        Assert.Equal(TimeSpan.FromSeconds(180), result.Programs.Single(value => value.ProgramId == "A").ForecastCompletionOffset);
    }

    [Fact]
    public void Three_programs_produce_deterministic_completion_offsets()
    {
        var result = planner.Calculate(Input(15,
            Program("A", 0, 2, 0, Output("A", 1, 3, 3)),
            Program("B", 1, 3, 0, Output("B", 1, 2, 2)),
            Program("C", 2, 5, 0, Output("C", 1, 1, 1))));
        Assert.Equal(TimeSpan.FromSeconds(25), result.Programs[2].ForecastCompletionOffset);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Programs[1].ForecastCompletionOffset);
        Assert.Equal(TimeSpan.FromSeconds(32), result.Programs[0].ForecastCompletionOffset);
        Assert.Equal(TimeSpan.FromSeconds(32), result.TotalDuration);
    }

    [Fact]
    public void Partial_progress_calculates_remaining_duration_and_offsets()
    {
        var result = planner.Calculate(new ProductionRunCyclePlanInput(50, true,
        [
            Program("A", 0, 10, 3, Output("A", 1, 10, 10)),
            Program("B", 1, 20, 3, Output("B", 1, 4, 4))
        ]));
        Assert.Equal(8, result.RemainingProgramExecutions);
        Assert.Equal(TimeSpan.FromSeconds(90), result.RemainingDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Programs[1].ForecastCompletionOffset);
        Assert.Equal(TimeSpan.FromSeconds(90), result.Programs[0].ForecastCompletionOffset);
    }

    [Fact]
    public void Attempted_cycle_beyond_target_is_rejected()
    {
        var program = planner.Calculate(Input(0,
            Program("A", 0, 1, 1, Output("A", 1, 1, 1)))).Programs[0];
        var error = Assert.Throws<ProductionRunCycleValidationException>(() =>
            planner.CompleteOneCycle(program));
        Assert.Equal("cycle_target_reached", error.Code);
    }

    [Fact]
    public void Large_cycle_counts_use_arithmetic_without_occurrence_materialization()
    {
        var result = planner.Calculate(Input(0,
            Program("A", 0, 1, 0, Output("A", 1, 1_000_000_000, 1_000_000_000))));
        Assert.Equal(1_000_000_000, result.TotalProgramExecutions);
        Assert.Equal(TimeSpan.FromSeconds(1_000_000_000), result.TotalDuration);
        Assert.Single(result.Programs);
    }

    [Fact]
    public void Stable_sequence_order_controls_same_round_completion()
    {
        var result = planner.Calculate(Input(0,
            Program("second", 1, 7, 0, Output("B", 1, 1, 1)),
            Program("first", 0, 5, 0, Output("A", 1, 1, 1))));
        Assert.Equal(new[] { "first", "second" }, result.Programs.Select(value => value.ProgramId));
        Assert.Equal(TimeSpan.FromSeconds(5), result.Programs[0].ForecastCompletionOffset);
        Assert.Equal(TimeSpan.FromSeconds(12), result.Programs[1].ForecastCompletionOffset);
    }

    [Fact]
    public void One_cycle_advances_all_coupled_outputs_atomically_with_integer_quantities()
    {
        var program = planner.Calculate(Input(0,
            Program("combined", 0, 1, 4,
                Output("A", 2, 20, 20), Output("B", 1, 10, 10)))).Programs[0];
        var completion = planner.CompleteOneCycle(program);
        Assert.Equal(5, completion.CompletedCycles);
        Assert.False(completion.IsProgramComplete);
        Assert.Collection(completion.Outputs,
            a => { Assert.Equal(2, a.ProducedThisCycle); Assert.Equal(10, a.ProducedTotal); },
            b => { Assert.Equal(1, b.ProducedThisCycle); Assert.Equal(5, b.ProducedTotal); });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_quantity_per_cycle_is_rejected(long quantity)
    {
        var error = Assert.Throws<ProductionRunCycleValidationException>(() => planner.Calculate(
            Input(0, Program("A", 0, 1, 0, Output("A", quantity, 10, 10)))));
        Assert.Equal("positive_required", error.Code);
    }

    [Fact]
    public void Over_allocation_and_duplicate_concrete_output_are_rejected()
    {
        var over = Assert.Throws<ProductionRunCycleValidationException>(() => planner.Calculate(
            Input(0, Program("A", 0, 1, 0, Output("A", 1, 11, 10)))));
        Assert.Equal("over_allocation", over.Code);

        var duplicate = Assert.Throws<ProductionRunCycleValidationException>(() => planner.Calculate(
            Input(0, Program("A", 0, 1, 0,
                Output("same", 1, 2, 2), Output("same", 1, 2, 2)))));
        Assert.Equal("duplicate_output", duplicate.Code);
    }

    private static ProductionRunCyclePlanInput Input(
        int setup, params ProductionRunProgramCycleInput[] programs) => new(setup, false, programs);

    private static ProductionRunProgramCycleInput Program(
        string id, int sequence, decimal cycleSeconds, long completed,
        params ProductionRunOutputCycleInput[] outputs) =>
        new(id, sequence, cycleSeconds, completed, outputs);

    private static ProductionRunOutputCycleInput Output(
        string id, long quantityPerCycle, long target, long remaining) =>
        new(id, quantityPerCycle, target, remaining);
}
