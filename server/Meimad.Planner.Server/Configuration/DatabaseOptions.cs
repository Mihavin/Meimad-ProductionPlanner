namespace Meimad.Planner.Server.Configuration;

internal sealed class DatabaseOptions
{
    internal const string SectionName = "Database";
    internal const string DefaultRelativePath = "data/meimad-planner.db";

    internal DatabaseOptions(string databasePath)
    {
        DatabasePath = databasePath;
    }

    internal string DatabasePath { get; }

    internal static DatabaseOptions FromConfiguration(
        IConfiguration configuration,
        string contentRootPath)
    {
        var configuredPath = configuration[$"{SectionName}:Path"];
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? DefaultRelativePath
            : configuredPath.Trim();

        var fullPath = ServerStoragePathResolver.Resolve(path, contentRootPath);

        if (IsNetworkPath(fullPath))
        {
            throw new InvalidOperationException(
                "Database:Path must point to server-local storage, not a network share.");
        }

        return new DatabaseOptions(fullPath);
    }

    private static bool IsNetworkPath(string fullPath)
    {
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            || fullPath.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var root = System.IO.Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
