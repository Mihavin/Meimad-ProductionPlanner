using System.Net;
using System.Net.Sockets;

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
    private readonly Func<string, IPAddress[]> resolveHost;
    private readonly Func<string, bool> fileExists;

    public ServerFilePathResolver(ServerFileAccessOptions options)
        : this(options, Dns.GetHostAddresses, File.Exists)
    {
    }

    internal ServerFilePathResolver(
        ServerFileAccessOptions options,
        Func<string, IPAddress[]> resolveHost,
        Func<string, bool> fileExists)
    {
        this.options = options;
        this.resolveHost = resolveHost;
        this.fileExists = fileExists;
    }

    internal string? ResolveExistingFile(string path, string workingFolderPath)
    {
        var absolutePath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(workingFolderPath, path);
        foreach (var candidate in Candidates(absolutePath))
        {
            if (fileExists(candidate))
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
        var mappedPath = Path.Combine(networkPath, suffix);
        yield return mappedPath;

        if (!TrySplitUnc(mappedPath, out var serverName, out var shareAndPath)
            || IPAddress.TryParse(serverName, out _))
        {
            yield break;
        }

        IPAddress[] addresses;
        try
        {
            addresses = resolveHost(serverName);
        }
        catch (SocketException)
        {
            yield break;
        }

        foreach (var address in addresses
                     .Where(value => value.AddressFamily == AddressFamily.InterNetwork)
                     .Distinct())
        {
            yield return $@"\\{address}\{shareAndPath}";
        }
    }

    private static bool TrySplitUnc(string path, out string serverName, out string shareAndPath)
    {
        serverName = string.Empty;
        shareAndPath = string.Empty;
        if (!path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return false;
        }

        var separator = path.IndexOf('\\', 2);
        if (separator <= 2 || separator == path.Length - 1)
        {
            return false;
        }

        serverName = path[2..separator];
        shareAndPath = path[(separator + 1)..];
        return true;
    }
}
