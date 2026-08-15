namespace Meimad.Planner.Server.Configuration;

internal sealed record LegacyImportOptions(TimeSpan PreviewLifetime)
{
    internal static LegacyImportOptions FromConfiguration(IConfiguration configuration)
    {
        var minutes = configuration.GetValue<int?>("LegacyImport:PreviewLifetimeMinutes") ?? 120;
        if (minutes is < 5 or > 1440)
        {
            throw new InvalidOperationException(
                "LegacyImport:PreviewLifetimeMinutes must be between 5 and 1440.");
        }
        return new LegacyImportOptions(TimeSpan.FromMinutes(minutes));
    }
}
