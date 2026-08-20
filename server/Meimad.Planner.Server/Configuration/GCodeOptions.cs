namespace Meimad.Planner.Server.Configuration;

public sealed class GCodeOptions
{
    public const string SectionName = "GCode";

    public string ReleaseRoot { get; init; } = "gcode-releases";

    public long MaximumGCodeFileBytes { get; init; } = 100 * 1024 * 1024;

    public long MaximumToolTableFileBytes { get; init; } = 25 * 1024 * 1024;

    internal string ResolvedReleaseRoot { get; private init; } = string.Empty;

    public static GCodeOptions FromConfiguration(
        IConfiguration configuration,
        string contentRootPath)
    {
        var configured = configuration.GetSection(SectionName).Get<GCodeOptions>()
            ?? new GCodeOptions();
        if (string.IsNullOrWhiteSpace(configured.ReleaseRoot))
        {
            throw new InvalidOperationException("GCode:ReleaseRoot is required.");
        }

        if (configured.MaximumGCodeFileBytes is < 1 or > 1_073_741_824
            || configured.MaximumToolTableFileBytes is < 1 or > 268_435_456)
        {
            throw new InvalidOperationException("G-code release size limits are outside supported bounds.");
        }

        return new GCodeOptions
        {
            ReleaseRoot = configured.ReleaseRoot,
            MaximumGCodeFileBytes = configured.MaximumGCodeFileBytes,
            MaximumToolTableFileBytes = configured.MaximumToolTableFileBytes,
            ResolvedReleaseRoot = ServerStoragePathResolver.Resolve(
                configured.ReleaseRoot,
                contentRootPath)
        };
    }
}
