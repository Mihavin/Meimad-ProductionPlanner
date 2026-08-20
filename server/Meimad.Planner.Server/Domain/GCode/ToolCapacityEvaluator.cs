namespace Meimad.Planner.Server.Domain.GCode;

internal static class ToolCapacityEvaluator
{
    internal static ToolCapacityEvaluation Evaluate(
        int? requiredToolCount,
        int? usableToolPositions)
    {
        if (!requiredToolCount.HasValue)
        {
            return new ToolCapacityEvaluation(
                "tool_requirements_unavailable",
                false,
                null,
                usableToolPositions,
                "Tool capacity cannot be validated because the active tool-table release has no structured required-tool count.");
        }

        if (requiredToolCount.Value == 0)
        {
            return new ToolCapacityEvaluation(
                "satisfied",
                true,
                0,
                usableToolPositions,
                "Tool capacity ready: the active process requires no magazine tool positions.");
        }

        if (!usableToolPositions.HasValue)
        {
            return new ToolCapacityEvaluation(
                "machine_capacity_unavailable",
                false,
                requiredToolCount,
                null,
                $"Tool capacity cannot be validated: requires {requiredToolCount.Value} tool positions; the assigned machine has no usable-capacity value.");
        }

        if (requiredToolCount.Value > usableToolPositions.Value)
        {
            return new ToolCapacityEvaluation(
                "tool_capacity_mismatch",
                false,
                requiredToolCount,
                usableToolPositions,
                $"Tool capacity mismatch: requires {requiredToolCount.Value} tool positions; assigned machine supports {usableToolPositions.Value}.");
        }

        return new ToolCapacityEvaluation(
            "satisfied",
            true,
            requiredToolCount,
            usableToolPositions,
            $"Tool capacity ready: requires {requiredToolCount.Value} tool positions; assigned machine supports {usableToolPositions.Value}.");
    }
}

internal sealed record ToolCapacityEvaluation(
    string Code,
    bool IsSatisfied,
    int? RequiredToolCount,
    int? UsableToolPositions,
    string Message);
