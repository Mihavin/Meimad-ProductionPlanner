namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class HaasSetupViewTests
{
    [Fact]
    public void Setup_view_exposes_protocol_registry_Haas_configuration_and_diagnostics()
    {
        var xaml = File.ReadAllText(FindSetupView());

        Assert.Contains("Header=\"CNC Connection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding CncAdapters}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ReconnectCncCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("TestHaasMdcCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("TestHaasNetShareCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ReadHaasVariableCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("HaasDiagnostics", xaml, StringComparison.Ordinal);
        Assert.Contains("HaasTimeline", xaml, StringComparison.Ordinal);
    }

    private static string FindSetupView()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "client-windows",
                "Meimad.Planner.Client.Windows",
                "Views",
                "SetupView.xaml");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate Views/SetupView.xaml from the test output directory.");
    }
}
