namespace Meimad.Planner.Server.Domain.GCode;

internal static class NcCycleTimeEstimator
{
    internal static NcMachineCycleEstimate Evaluate(
        string releaseId,
        NcProgramAnalysis analysis,
        NcMachineTiming machine,
        DateTimeOffset calculatedAt)
    {
        var warnings = analysis.Warnings.ToList();
        if (analysis.Status == NcAnalysisStatus.Unavailable)
        {
            return Unavailable(releaseId, analysis, machine, calculatedAt, warnings);
        }

        if (analysis.RapidDistanceMillimeters > 0
            && (machine.RapidRateMillimetersPerMinute is null or <= 0))
        {
            warnings.Add("Estimate unavailable: Machine Rapid Rate is not configured.");
        }

        if (analysis.ToolChangeCount > 0
            && (machine.ToolChangeTimeSeconds is null or < 0))
        {
            warnings.Add("Estimate unavailable: Machine Tool Change Time is not configured.");
        }

        if (machine.MachineTimeFactor <= 0 || !double.IsFinite(machine.MachineTimeFactor))
        {
            warnings.Add("Estimate unavailable: Machine Time Factor is invalid.");
        }

        if (warnings.Any(value => value.StartsWith("Estimate unavailable:", StringComparison.Ordinal)))
        {
            return Unavailable(releaseId, analysis, machine, calculatedAt, warnings);
        }

        var rapidSeconds = analysis.RapidDistanceMillimeters == 0
            ? 0
            : analysis.RapidDistanceMillimeters
                / machine.RapidRateMillimetersPerMinute!.Value * 60d;
        var toolChangeSeconds = analysis.ToolChangeCount == 0
            ? 0
            : analysis.ToolChangeCount * machine.ToolChangeTimeSeconds!.Value;
        var rawSeconds = analysis.FeedMotionSeconds + rapidSeconds
            + toolChangeSeconds + analysis.DwellSeconds;
        var estimatedSeconds = rawSeconds * machine.MachineTimeFactor;
        if (!double.IsFinite(rawSeconds) || !double.IsFinite(estimatedSeconds)
            || rawSeconds < 0 || estimatedSeconds < 0)
        {
            warnings.Add("Estimate unavailable: calculated duration is outside the supported range.");
            return Unavailable(releaseId, analysis, machine, calculatedAt, warnings);
        }

        return new NcMachineCycleEstimate(
            releaseId, machine.MachineId, analysis.ParserVersion,
            analysis.FeedMotionSeconds, analysis.RapidDistanceMillimeters,
            rapidSeconds, analysis.ToolChangeCount, toolChangeSeconds,
            analysis.DwellSeconds, machine.RapidRateMillimetersPerMinute,
            machine.ToolChangeTimeSeconds, machine.MachineTimeFactor,
            rawSeconds, estimatedSeconds, warnings.Distinct(StringComparer.Ordinal).ToArray(),
            analysis.Confidence, calculatedAt);
    }

    private static NcMachineCycleEstimate Unavailable(
        string releaseId,
        NcProgramAnalysis analysis,
        NcMachineTiming machine,
        DateTimeOffset calculatedAt,
        IReadOnlyList<string> warnings) => new(
            releaseId, machine.MachineId, analysis.ParserVersion,
            analysis.FeedMotionSeconds, analysis.RapidDistanceMillimeters, null,
            analysis.ToolChangeCount, null, analysis.DwellSeconds,
            machine.RapidRateMillimetersPerMinute, machine.ToolChangeTimeSeconds,
            machine.MachineTimeFactor, null, null,
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            NcEstimateConfidence.Unavailable, calculatedAt);
}
