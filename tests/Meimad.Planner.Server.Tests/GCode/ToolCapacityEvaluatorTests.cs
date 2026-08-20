using Meimad.Planner.Server.Domain.GCode;

namespace Meimad.Planner.Server.Tests.GCode;

public sealed class ToolCapacityEvaluatorTests
{
    [Theory]
    [InlineData(19, 20)]
    [InlineData(20, 20)]
    public void Required_tools_at_or_below_capacity_are_satisfied(int required, int available)
    {
        var result = ToolCapacityEvaluator.Evaluate(required, available);

        Assert.True(result.IsSatisfied);
        Assert.Equal("satisfied", result.Code);
        Assert.Equal(required, result.RequiredToolCount);
        Assert.Equal(available, result.UsableToolPositions);
    }

    [Fact]
    public void Required_tools_above_capacity_return_counts_in_blocking_reason()
    {
        var result = ToolCapacityEvaluator.Evaluate(25, 20);

        Assert.False(result.IsSatisfied);
        Assert.Equal("tool_capacity_mismatch", result.Code);
        Assert.Equal(
            "Tool capacity mismatch: requires 25 tool positions; assigned machine supports 20.",
            result.Message);
    }

    [Fact]
    public void Unknown_structured_requirements_or_machine_capacity_are_not_reported_ready()
    {
        Assert.False(ToolCapacityEvaluator.Evaluate(null, 20).IsSatisfied);
        Assert.False(ToolCapacityEvaluator.Evaluate(3, null).IsSatisfied);
        Assert.True(ToolCapacityEvaluator.Evaluate(0, null).IsSatisfied);
    }
}
