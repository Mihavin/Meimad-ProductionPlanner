namespace Meimad.Planner.Server.Configuration;

public sealed class SetupEstimationOptions
{
    public const string SectionName = "SetupEstimation";

    public double DefaultToolLoadTimePerToolSeconds { get; init; } = 60;

    public double DefaultFirstPieceFactor { get; init; } = 1.5;

    public static SetupEstimationOptions FromConfiguration(IConfiguration configuration)
    {
        var options = configuration.GetSection(SectionName).Get<SetupEstimationOptions>() ?? new();
        if (!double.IsFinite(options.DefaultToolLoadTimePerToolSeconds)
            || options.DefaultToolLoadTimePerToolSeconds < 0)
        {
            throw new InvalidOperationException(
                "SetupEstimation:DefaultToolLoadTimePerToolSeconds must be a finite non-negative number.");
        }

        if (!double.IsFinite(options.DefaultFirstPieceFactor)
            || options.DefaultFirstPieceFactor < 1)
        {
            throw new InvalidOperationException(
                "SetupEstimation:DefaultFirstPieceFactor must be a finite number greater than or equal to 1.0.");
        }

        return options;
    }
}
