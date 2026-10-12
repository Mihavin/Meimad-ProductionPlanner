namespace Meimad.Planner.Server.Configuration;

/// <summary>
/// Limits for measured work offsets received from Machines (configuration section <c>WorkOffsets</c>).
/// A change larger than a review threshold against the current measurement is kept as a
/// REVIEW_REQUIRED candidate instead of becoming current.
/// </summary>
public sealed class WorkOffsetOptions
{
    public const string SectionName = "WorkOffsets";

    /// <summary>Largest X/Y/Z change (mm) against the current VALID measurement that is accepted without review.</summary>
    public double ReviewLinearThresholdMm { get; init; } = 1.0;

    /// <summary>Largest A/B/C change (degrees) against the current VALID measurement that is accepted without review.</summary>
    public double ReviewRotaryThresholdDeg { get; init; } = 0.5;

    /// <summary>A linear offset further from machine zero than this (mm) is refused as out of range.</summary>
    public double MaximumLinearMm { get; init; } = 10000;

    /// <summary>A rotary offset beyond this many degrees either way is refused as out of range.</summary>
    public double MaximumRotaryDeg { get; init; } = 720;

    public static WorkOffsetOptions FromConfiguration(IConfiguration configuration)
    {
        var options = configuration.GetSection(SectionName).Get<WorkOffsetOptions>() ?? new();
        if (!(options.ReviewLinearThresholdMm >= 0) || !(options.ReviewRotaryThresholdDeg >= 0)
            || !(options.MaximumLinearMm > 0) || !(options.MaximumRotaryDeg > 0)
            || !double.IsFinite(options.ReviewLinearThresholdMm) || !double.IsFinite(options.ReviewRotaryThresholdDeg)
            || !double.IsFinite(options.MaximumLinearMm) || !double.IsFinite(options.MaximumRotaryDeg))
            throw new InvalidOperationException(
                "WorkOffsets thresholds must be finite; review thresholds are zero or more and maximums are positive.");
        return options;
    }
}
