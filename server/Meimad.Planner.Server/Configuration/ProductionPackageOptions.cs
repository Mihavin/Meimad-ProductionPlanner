namespace Meimad.Planner.Server.Configuration;

public sealed class ProductionPackageOptions
{
    public const string SectionName = "ProductionPackages";

    public string PackageRoot { get; init; } = "production-packages";
    public long MaximumArtifactBytes { get; init; } = 100 * 1024 * 1024;
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
        var root = ServerStoragePathResolver.Resolve(configured.PackageRoot, contentRootPath);
        if (root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("Production Package storage must be server-local.");
        return new()
        {
            PackageRoot = configured.PackageRoot,
            MaximumArtifactBytes = configured.MaximumArtifactBytes,
            ResolvedPackageRoot = root
        };
    }
}
