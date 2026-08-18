namespace Meimad.Planner.Server.Configuration;

internal sealed class BackupOptions
{
    internal const string SectionName = "Backup";
    internal const string DefaultRelativeFolder = "backups";
    internal const int DefaultRetentionCount = 14;

    internal BackupOptions(string backupFolder, int retentionCount)
    {
        BackupFolder = backupFolder;
        RetentionCount = retentionCount;
    }

    internal string BackupFolder { get; }

    internal int RetentionCount { get; }

    internal static BackupOptions FromConfiguration(
        IConfiguration configuration,
        string contentRootPath,
        string activeDatabasePath)
    {
        var configuredFolder = configuration[$"{SectionName}:Folder"];
        var folder = string.IsNullOrWhiteSpace(configuredFolder)
            ? DefaultRelativeFolder
            : configuredFolder.Trim();
        var fullFolder = ServerStoragePathResolver.Resolve(folder, contentRootPath);

        if (PathsEqual(fullFolder, activeDatabasePath))
        {
            throw new InvalidOperationException(
                "Backup:Folder must be a directory and cannot be the active database path.");
        }

        var retentionCount = configuration.GetValue<int?>(
            $"{SectionName}:RetentionCount") ?? DefaultRetentionCount;
        if (retentionCount is < 1 or > 3650)
        {
            throw new InvalidOperationException(
                "Backup:RetentionCount must be between 1 and 3650.");
        }

        return new BackupOptions(fullFolder, retentionCount);
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
