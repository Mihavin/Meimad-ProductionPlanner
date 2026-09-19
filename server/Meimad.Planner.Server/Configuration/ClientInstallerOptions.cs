namespace Meimad.Planner.Server.Configuration;

/// <summary>
/// Where the Server keeps the Windows client MSI it distributes to clients whose version does
/// not match its own. The Server installer places the matching MSI and its JSON manifest
/// (<c>Meimad-Planner-Client-Setup.json</c>: version, SHA-256, byte length) in the
/// <c>client-installer</c> folder next to the Server executable.
/// </summary>
public sealed class ClientInstallerOptions
{
    public const string SectionName = "ClientInstaller";

    /// <summary>Folder holding the client MSI and its manifest; relative paths resolve under the Server content root.</summary>
    public string Folder { get; init; } = "client-installer";

    /// <summary>Bare file name of the client MSI inside <see cref="Folder"/>.</summary>
    public string FileName { get; init; } = "Meimad-Planner-Client-Setup.msi";

    internal string ResolvedFolder { get; private init; } = string.Empty;

    internal string InstallerPath => Path.Combine(ResolvedFolder, FileName);

    internal string ManifestPath => Path.Combine(ResolvedFolder, Path.ChangeExtension(FileName, ".json"));

    public static ClientInstallerOptions FromConfiguration(
        IConfiguration configuration,
        string contentRootPath)
    {
        var configured = configuration.GetSection(SectionName).Get<ClientInstallerOptions>()
            ?? new ClientInstallerOptions();
        if (string.IsNullOrWhiteSpace(configured.Folder))
        {
            throw new InvalidOperationException("ClientInstaller:Folder is required.");
        }

        if (string.IsNullOrWhiteSpace(configured.FileName)
            || !string.Equals(configured.FileName, Path.GetFileName(configured.FileName), StringComparison.Ordinal)
            || !configured.FileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ClientInstaller:FileName must be a bare .msi file name.");
        }

        return new ClientInstallerOptions
        {
            Folder = configured.Folder,
            FileName = configured.FileName,
            // Deliberately NOT ServerStoragePathResolver.Resolve(): that helper redirects a
            // relative path from a Program Files content root to ProgramData, which is right
            // for writable runtime data (backups, E-Ink packages, GCode releases) but wrong
            // here. The client MSI and its manifest are a read-only payload the installer
            // places directly next to the Server executable (build-installers.ps1 harvests
            // client-installer\ into INSTALLFOLDER), so this must resolve next to the exe,
            // not into ProgramData where nothing was ever placed.
            ResolvedFolder = Path.IsPathFullyQualified(configured.Folder)
                ? Path.GetFullPath(configured.Folder)
                : Path.GetFullPath(Path.Combine(contentRootPath, configured.Folder))
        };
    }
}
