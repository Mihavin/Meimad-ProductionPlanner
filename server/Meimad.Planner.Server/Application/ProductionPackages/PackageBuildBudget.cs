namespace Meimad.Planner.Server.Application.ProductionPackages;

internal sealed class PackageBuildBudget(long maximumBytes)
{
    private long input;
    private long output;
    internal void AddInput(long bytes) => Add(ref input, bytes);
    internal void AddOutput(long bytes) => Add(ref output, bytes);
    private void Add(ref long total, long bytes)
    {
        if (bytes < 0 || bytes > maximumBytes - total)
            throw new ProductionPackageBuildException("production_package_total_size_exceeded",
                "The combined package source or generated artifacts exceed ProductionPackages:MaximumPackageBytes. No package was activated.");
        total += bytes;
    }
}
