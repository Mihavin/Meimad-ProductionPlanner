namespace Meimad.Planner.Server.Domain.Timeline;

internal sealed record SetupOccupancyInput(
    int ProductionQuantity,
    int? RequiredToolCount,
    double? FixtureSetupSeconds,
    double? ManagerCycleOverrideSeconds,
    double? NcEstimatedCycleSeconds,
    double? ManualCycleSeconds,
    double ToolLoadTimePerToolSeconds,
    double FirstPieceFactor);

internal sealed record SetupOccupancyEstimate(
    double? SelectedCycleSeconds,
    string PlanningCycleSource,
    double ToolLoadingSeconds,
    double? FixtureSetupSeconds,
    double? FirstPieceProveOutSeconds,
    double? TotalSetupSeconds,
    int RemainingProductionQuantity,
    double? RemainingProductionSeconds,
    double? TotalPlannedMachineSeconds,
    IReadOnlyList<string> Warnings)
{
    internal bool IsAvailable => TotalPlannedMachineSeconds.HasValue;
}

internal static class SetupOccupancyEstimator
{
    internal static SetupOccupancyEstimate Evaluate(SetupOccupancyInput input)
    {
        if (input.ProductionQuantity < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input), "Production quantity cannot be negative.");
        }

        ValidateNonNegative(input.RequiredToolCount, nameof(input.RequiredToolCount));
        ValidateFiniteNonNegative(input.FixtureSetupSeconds, nameof(input.FixtureSetupSeconds));
        ValidateFiniteNonNegative(input.ManagerCycleOverrideSeconds, nameof(input.ManagerCycleOverrideSeconds));
        ValidateFiniteNonNegative(input.NcEstimatedCycleSeconds, nameof(input.NcEstimatedCycleSeconds));
        ValidateFiniteNonNegative(input.ManualCycleSeconds, nameof(input.ManualCycleSeconds));
        ValidateFiniteNonNegative(input.ToolLoadTimePerToolSeconds, nameof(input.ToolLoadTimePerToolSeconds));
        if (!double.IsFinite(input.FirstPieceFactor) || input.FirstPieceFactor < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input), "First-piece factor must be finite and at least 1.0.");
        }

        var warnings = new List<string>();
        var cycle = input.ManagerCycleOverrideSeconds
            ?? input.NcEstimatedCycleSeconds
            ?? input.ManualCycleSeconds;
        var source = input.ManagerCycleOverrideSeconds.HasValue
            ? "manager_override"
            : input.NcEstimatedCycleSeconds.HasValue
                ? "nc_estimate"
                : input.ManualCycleSeconds.HasValue ? "manual" : "unavailable";

        if (input.ProductionQuantity == 0)
        {
            return new SetupOccupancyEstimate(
                cycle, source, 0, input.FixtureSetupSeconds, 0, 0,
                0, 0, 0, warnings);
        }

        var toolCount = input.RequiredToolCount ?? 0;
        if (!input.RequiredToolCount.HasValue)
        {
            warnings.Add(
                "Required tool count is unavailable; prepared-tool magazine loading contributes 0 seconds.");
        }

        var toolLoading = toolCount * input.ToolLoadTimePerToolSeconds;
        if (!double.IsFinite(toolLoading))
        {
            throw new OverflowException("Tool-loading duration exceeds the supported range.");
        }

        if (!input.FixtureSetupSeconds.HasValue)
        {
            warnings.Add("Fixture setup time is missing.");
        }
        if (!cycle.HasValue)
        {
            warnings.Add("No manager, NC-based, or manual cycle estimate is available.");
        }
        if (!input.FixtureSetupSeconds.HasValue || !cycle.HasValue)
        {
            return new SetupOccupancyEstimate(
                cycle, source, toolLoading, input.FixtureSetupSeconds, null, null,
                Math.Max(input.ProductionQuantity - 1, 0), null, null, warnings);
        }

        var firstPiece = cycle.Value * input.FirstPieceFactor;
        var totalSetup = toolLoading + input.FixtureSetupSeconds.Value + firstPiece;
        var remainingQuantity = Math.Max(input.ProductionQuantity - 1, 0);
        var remaining = remainingQuantity * cycle.Value;
        var total = totalSetup + remaining;
        if (!double.IsFinite(firstPiece) || !double.IsFinite(totalSetup)
            || !double.IsFinite(remaining) || !double.IsFinite(total))
        {
            throw new OverflowException("Setup occupancy duration exceeds the supported range.");
        }

        return new SetupOccupancyEstimate(
            cycle, source, toolLoading, input.FixtureSetupSeconds, firstPiece,
            totalSetup, remainingQuantity, remaining, total, warnings);
    }

    private static void ValidateNonNegative(int? value, string field)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(field);
    }

    private static void ValidateFiniteNonNegative(double? value, string field)
    {
        if (value.HasValue && (!double.IsFinite(value.Value) || value.Value < 0))
        {
            throw new ArgumentOutOfRangeException(field);
        }
    }
}
