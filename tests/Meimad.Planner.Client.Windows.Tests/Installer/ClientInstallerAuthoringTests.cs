using System.Xml.Linq;

namespace Meimad.Planner.Client.Windows.Tests.Installer;

public sealed class ClientInstallerAuthoringTests
{
    [Fact]
    public void Start_menu_shortcut_is_advertised_by_its_executable_component()
    {
        var document = XDocument.Load(FindPackageAuthoring());
        var wix = XNamespace.Get("http://wixtoolset.org/schemas/v4/wxs");
        var component = Assert.Single(
            document.Descendants(wix + "Component"),
            value => (string?)value.Attribute("Id") == "ClientExecutableComponent");
        var shortcut = Assert.Single(component.Elements(wix + "Shortcut"));

        Assert.Equal("ClientStartMenuShortcut", (string?)shortcut.Attribute("Id"));
        Assert.Equal("yes", (string?)shortcut.Attribute("Advertise"));
        Assert.Null(shortcut.Attribute("Target"));
        Assert.Contains(document.Descendants(wix + "ComponentRef"),
            value => (string?)value.Attribute("Id") == "ClientExecutableComponent");
        Assert.DoesNotContain(document.Descendants(wix + "Component"),
            value => (string?)value.Attribute("Id") == "ClientShortcutComponent");
        Assert.DoesNotContain(document.Descendants(wix + "RegistryValue"),
            value => (string?)value.Attribute("Name") == "ClientShortcut");
    }

    private static string FindPackageAuthoring()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "installer", "client", "Package.wxs");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate installer/client/Package.wxs from the test output directory.");
    }
}
