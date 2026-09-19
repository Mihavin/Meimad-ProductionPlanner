using Meimad.Planner.Server.Configuration;
using Microsoft.Extensions.Configuration;

namespace Meimad.Planner.Server.Tests.ClientInstaller;

/// <summary>
/// Regression coverage for a real deployment bug: the default relative
/// ClientInstaller:Folder ("client-installer") must resolve next to the
/// Server executable, because that is exactly where build-installers.ps1
/// places the bundled client MSI and its manifest (INSTALLFOLDER\client-installer\).
/// It must NOT go through ServerStoragePathResolver's Program-Files-to-
/// ProgramData redirection, which is correct for writable runtime data
/// (backups, E-Ink packages, GCode releases) but silently pointed this
/// read-only, installer-placed payload at an empty ProgramData folder on
/// the live 0.1.118 install, making GET /api/v1/client-installer always
/// report installerAvailable: false.
/// </summary>
public sealed class ClientInstallerOptionsTests
{
    [Fact]
    public void Default_folder_resolves_next_to_the_executable_even_under_Program_Files()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var contentRoot = Path.Combine(programFiles, "Meimad Production Planner Server");

        var options = ClientInstallerOptions.FromConfiguration(new ConfigurationBuilder().Build(), contentRoot);

        Assert.StartsWith(contentRoot, options.InstallerPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProgramData", options.InstallerPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(contentRoot, "client-installer", "Meimad-Planner-Client-Setup.msi")),
            Path.GetFullPath(options.InstallerPath));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(contentRoot, "client-installer", "Meimad-Planner-Client-Setup.json")),
            Path.GetFullPath(options.ManifestPath));
    }

    [Fact]
    public void A_fully_qualified_folder_is_used_as_is()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "MeimadPlanner.ClientInstallerOptions.Tests", Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ClientInstaller:Folder"] = absolute })
            .Build();

        var options = ClientInstallerOptions.FromConfiguration(config, Path.GetTempPath());

        Assert.Equal(Path.GetFullPath(Path.Combine(absolute, "Meimad-Planner-Client-Setup.msi")), Path.GetFullPath(options.InstallerPath));
    }

    [Fact]
    public void Blank_folder_or_a_file_name_with_a_path_separator_is_rejected()
    {
        var blank = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ClientInstaller:Folder"] = " " }).Build();
        Assert.Throws<InvalidOperationException>(() => ClientInstallerOptions.FromConfiguration(blank, Path.GetTempPath()));

        var pathyFileName = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ClientInstaller:FileName"] = "sub/evil.msi" }).Build();
        Assert.Throws<InvalidOperationException>(() => ClientInstallerOptions.FromConfiguration(pathyFileName, Path.GetTempPath()));

        var notMsi = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ClientInstaller:FileName"] = "client.exe" }).Build();
        Assert.Throws<InvalidOperationException>(() => ClientInstallerOptions.FromConfiguration(notMsi, Path.GetTempPath()));
    }
}
