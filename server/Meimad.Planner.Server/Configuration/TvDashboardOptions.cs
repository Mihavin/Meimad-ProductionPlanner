namespace Meimad.Planner.Server.Configuration;

public sealed class TvDashboardOptions
{
    public const string SectionName = "TvDashboard";

    public int RefreshAfterSeconds { get; init; } = 15;

    public int UrgentWithinHours { get; init; } = 48;

    public int CalculationHorizonDays { get; init; } = 7;

    public static TvDashboardOptions FromConfiguration(IConfiguration configuration)
    {
        var options = configuration.GetSection(SectionName).Get<TvDashboardOptions>()
            ?? new TvDashboardOptions();
        if (options.RefreshAfterSeconds is < 5 or > 300)
        {
            throw new InvalidOperationException(
                "TvDashboard:RefreshAfterSeconds must be between 5 and 300.");
        }

        if (options.UrgentWithinHours is < 1 or > 720)
        {
            throw new InvalidOperationException(
                "TvDashboard:UrgentWithinHours must be between 1 and 720.");
        }

        if (options.CalculationHorizonDays is < 1 or > 31)
        {
            throw new InvalidOperationException(
                "TvDashboard:CalculationHorizonDays must be between 1 and 31.");
        }

        return options;
    }
}
