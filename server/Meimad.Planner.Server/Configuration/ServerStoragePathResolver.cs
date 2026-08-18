namespace Meimad.Planner.Server.Configuration;

internal static class ServerStoragePathResolver
{
    private const string ProductDataFolder = "MeimadPlanner";
    private const string ServerDataFolder = "Server";

    internal static string Resolve(string path, string contentRootPath)
    {
        if (Path.IsPathFullyQualified(path))
        {
            return Path.GetFullPath(path);
        }

        var storageRoot = InstalledStorageRoot(contentRootPath) ?? contentRootPath;
        return Path.GetFullPath(Path.Combine(storageRoot, path));
    }

    internal static string? InstalledStorageRoot(
        string contentRootPath,
        string? programFilesPath = null,
        string? programFilesX86Path = null,
        string? commonApplicationDataPath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        programFilesPath ??= Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        programFilesX86Path ??= Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        commonApplicationDataPath ??= Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonApplicationDataPath)
            || (!IsWithin(contentRootPath, programFilesPath)
                && !IsWithin(contentRootPath, programFilesX86Path)))
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(commonApplicationDataPath, ProductDataFolder, ServerDataFolder));
    }

    private static bool IsWithin(string candidatePath, string? parentPath)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return false;
        }

        var candidate = Path.GetFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFullPath(parentPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(parent, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
