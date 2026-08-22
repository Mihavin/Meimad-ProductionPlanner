namespace Meimad.Planner.Server.Configuration;

internal sealed class ServerFileAccessOptions
{
    internal IReadOnlyDictionary<string, string> DriveMappings { get; }

    private ServerFileAccessOptions(IReadOnlyDictionary<string, string> driveMappings)
    {
        DriveMappings = driveMappings;
    }

    internal static ServerFileAccessOptions FromConfiguration(IConfiguration configuration)
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in configuration.GetSection("FileAccess:DriveMappings").GetChildren())
        {
            var drive = item["Drive"]?.Trim().TrimEnd('\\', '/');
            var networkPath = item["NetworkPath"]?.Trim().TrimEnd('\\', '/');
            if (drive is null || networkPath is null
                || drive.Length != 2 || drive[1] != ':'
                || !networkPath.StartsWith("\\\\", StringComparison.Ordinal))
            {
                continue;
            }

            mappings[drive] = networkPath;
        }

        return new ServerFileAccessOptions(mappings);
    }
}

internal sealed class ServerFilePathResolver
{
    private readonly ServerFileAccessOptions options;

    public ServerFilePathResolver(ServerFileAccessOptions options)
    {
        this.options = options;
    }

    internal string? ResolveExistingFile(string path, string workingFolderPath)
    {
        var absolutePath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(workingFolderPath, path);
        foreach (var candidate in Candidates(absolutePath))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IEnumerable<string> Candidates(string path)
    {
        yield return path;
        var root = Path.GetPathRoot(path)?.TrimEnd('\\', '/');
        if (root is null || !options.DriveMappings.TryGetValue(root, out var networkPath))
        {
            yield break;
        }

        var suffix = path[root.Length..].TrimStart('\\', '/');
        yield return Path.Combine(networkPath, suffix);
    }
}
