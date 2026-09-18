namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class HaasSetupViewTests
{
    [Fact]
    public void Setup_view_exposes_a_single_connection_type_picker_a_shared_DPRNT_section_and_diagnostics()
    {
        var xaml = File.ReadAllText(FindSetupView());

        Assert.Contains("Header=\"CNC Connection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding MachineNcDialects}\" SelectedItem=\"{Binding MachineNcDialect}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VC1–VC200", xaml, StringComparison.Ordinal);
        // One unified picker replaces the old "Adapter Type" + "Machine telemetry source" pair.
        Assert.Contains("ItemsSource=\"{Binding ConnectionTypes}\" SelectedItem=\"{Binding SelectedConnectionType}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding CncAdapters}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Adapter Type", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Machine telemetry source", xaml, StringComparison.Ordinal);
        Assert.Contains("ReconnectCncCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Test Connection\" Command=\"{Binding TestHaasConnectionCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Test MTConnect\" Command=\"{Binding TestHaasMtConnectCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Test MDC\" Command=\"{Binding TestHaasMdcCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TestHaasNetShareCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Save FOCAS configuration\" Command=\"{Binding SaveFocasConfigurationCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Test FOCAS connection\" Command=\"{Binding TestFocasConnectionCommand}\"", xaml, StringComparison.Ordinal);

        // DPRNT (shared by every connection type) appears exactly once, bound to the unified properties,
        // not duplicated per vendor panel.
        Assert.Contains("ItemsSource=\"{Binding DprntSources}\" SelectedItem=\"{Binding DprntSource}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DprntFilePath}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DprntTcpHost}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding DprntClearPolicies}\" SelectedItem=\"{Binding DprntFileClearPolicy}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Test DPRNT\" Command=\"{Binding TestHaasDprntCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(xaml, "DprntFileClearPolicy"));
        Assert.Equal(1, CountOccurrences(xaml, "DprntFilePath"));
        Assert.Equal(1, CountOccurrences(xaml, "DprntSource}"));
        Assert.DoesNotContain("Binding HaasDprntSource", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding HaasDprntFilePath", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding HaasDprntFileClearPolicy", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FocasDprnt", xaml, StringComparison.Ordinal);

        Assert.Contains("Style=\"{StaticResource FocasAdapterSection}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadHaasVariableCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Production Variable", xaml, StringComparison.Ordinal);
        Assert.Contains("direct macro writes are disabled", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HaasDiagnostics", xaml, StringComparison.Ordinal);
        Assert.Contains("HaasTimeline", xaml, StringComparison.Ordinal);
        Assert.Contains("Protected setup verification (commissioning gated)", xaml, StringComparison.Ordinal);
        Assert.Contains("LoadVerificationConfigurationCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("SaveVerificationConfigurationCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("GenerateOffsetLoaderReleaseCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("InvalidateVerificationCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("RevokeCurrentOffsetLoaderCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("no verification bypass", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Recovery reason (required)", xaml, StringComparison.Ordinal);
        Assert.Contains("Controller MAC address", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Machine secret", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VerificationSecret", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Keep verification disabled until", xaml, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
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
