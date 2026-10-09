namespace Meimad.Planner.Server.Configuration;

public sealed class ProductionPackageOptions
{
    public const string SectionName = "ProductionPackages";

    public string PackageRoot { get; init; } = "production-packages";
    public long MaximumArtifactBytes { get; init; } = 100 * 1024 * 1024;
    public long MaximumPackageBytes { get; init; } = 256 * 1024 * 1024;
    public int MaximumConcurrentBuilds { get; init; } = 2;
    // Owner-approved 2026-10-08. Zero/null disables collection; referenced artifacts are never eligible.
    public int? OrphanGraceDays { get; init; } = 7;
    internal string ResolvedPackageRoot { get; private init; } = string.Empty;

    public static ProductionPackageOptions FromConfiguration(
        IConfiguration configuration,
        string contentRootPath)
    {
        var configured = configuration.GetSection(SectionName).Get<ProductionPackageOptions>()
            ?? new ProductionPackageOptions();
        if (string.IsNullOrWhiteSpace(configured.PackageRoot))
            throw new InvalidOperationException("ProductionPackages:PackageRoot is required.");
        if (configured.MaximumArtifactBytes is < 1 or > 1_073_741_824)
            throw new InvalidOperationException("Production Package artifact size is outside supported bounds.");
        if (configured.MaximumPackageBytes is < 1 or > 1_073_741_824 || configured.MaximumConcurrentBuilds is < 1 or > 8)
            throw new InvalidOperationException("Production Package aggregate bytes must be 1 to 1073741824 and concurrent builds 1 to 8.");
        if (configured.OrphanGraceDays is < 0 or > 3650)
            throw new InvalidOperationException("ProductionPackages:OrphanGraceDays must be 1 to 3650, or zero to disable collection.");
        var root = ServerStoragePathResolver.Resolve(configured.PackageRoot, contentRootPath);
        if (root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("Production Package storage must be server-local.");
        return new()
        {
            PackageRoot = configured.PackageRoot,
            MaximumArtifactBytes = configured.MaximumArtifactBytes,
            MaximumPackageBytes = configured.MaximumPackageBytes,
            MaximumConcurrentBuilds = configured.MaximumConcurrentBuilds,
            OrphanGraceDays = configured.OrphanGraceDays is 0 ? null : configured.OrphanGraceDays,
            ResolvedPackageRoot = root
        };
    }
}
