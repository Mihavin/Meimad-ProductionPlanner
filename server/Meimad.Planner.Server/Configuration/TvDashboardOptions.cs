namespace Meimad.Planner.Server.Configuration;

public sealed class TvDashboardOptions
{
    public const string SectionName = "TvDashboard";

    public int RefreshAfterSeconds { get; init; } = 15;

    public int UrgentWithinHours { get; init; } = 48;

    public int CalculationHorizonDays { get; init; } = 7;

    /// <summary>
    /// When a Machine has no current Operation, show the picture and Part Number of the part it worked last next to
    /// IDLE (owner request 2026-10-12). Off by default: an idle row shows only IDLE.
    /// </summary>
    public bool ShowLastPartImageWhenIdle { get; init; }

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
