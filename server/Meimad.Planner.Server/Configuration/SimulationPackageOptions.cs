namespace Meimad.Planner.Server.Configuration;

/// <summary>
/// <c>SimulationPackages</c> settings (Cimatron simulation package import). A package is read only from
/// an approved root: the Setup network folder (and its aliases), the Case working folder of the import,
/// or one of <see cref="ApprovedRoots"/>. The folder watcher is off by default and only scans
/// <see cref="WatcherOptions.Roots"/> that are themselves approved.
/// </summary>
public sealed class SimulationPackageOptions
{
    public const string SectionName = "SimulationPackages";

    public string[] ApprovedRoots { get; init; } = [];

    public string[] TestedCimatronVersions { get; init; } = ["2026"];

    public long MaximumFileBytes { get; init; } = 512L * 1024 * 1024;

    public int MaximumFixtureCount { get; init; } = 32;

    public WatcherOptions Watcher { get; init; } = new();

    public sealed class WatcherOptions
    {
        public bool Enabled { get; init; }

        public string[] Roots { get; init; } = [];

        public int PollIntervalSeconds { get; init; } = 120;

        public int MaximumDepth { get; init; } = 6;
    }

    public static SimulationPackageOptions FromConfiguration(IConfiguration configuration)
    {
        var configured = configuration.GetSection(SectionName).Get<SimulationPackageOptions>() ?? new SimulationPackageOptions();
        if (configured.MaximumFileBytes is < 1024 or > 4L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("SimulationPackages:MaximumFileBytes is outside supported bounds.");
        if (configured.MaximumFixtureCount is < 1 or > 256)
            throw new InvalidOperationException("SimulationPackages:MaximumFixtureCount must be between 1 and 256.");
        if (configured.Watcher.PollIntervalSeconds is < 10 or > 86400 || configured.Watcher.MaximumDepth is < 1 or > 12)
            throw new InvalidOperationException("SimulationPackages:Watcher settings are outside supported bounds.");
        if (configured.ApprovedRoots.Concat(configured.Watcher.Roots).Any(root => !Path.IsPathFullyQualified(root ?? "")))
            throw new InvalidOperationException("SimulationPackages roots must be full paths.");
        return configured;
    }
}
