namespace Meimad.Planner.Server.Configuration;

public sealed class EditModeOptions
{
    public const string SectionName = "EditMode";

    public int TransferTimeoutSeconds { get; init; } = 30;

    public TimeSpan TransferTimeout => TimeSpan.FromSeconds(TransferTimeoutSeconds);

    public static EditModeOptions FromConfiguration(IConfiguration configuration)
    {
        var options = configuration
            .GetSection(SectionName)
            .Get<EditModeOptions>()
            ?? new EditModeOptions();

        if (options.TransferTimeoutSeconds is < 1 or > 3600)
        {
            throw new InvalidOperationException(
                "EditMode:TransferTimeoutSeconds must be between 1 and 3600.");
        }

        return options;
    }
}
