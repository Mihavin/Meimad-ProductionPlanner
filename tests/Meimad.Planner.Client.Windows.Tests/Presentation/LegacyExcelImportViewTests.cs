namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class LegacyExcelImportViewTests
{
    [Fact]
    public void Setup_import_tab_exposes_explicit_source_and_entity_mapping_controls()
    {
        var xaml = File.ReadAllText(FindSetupView());

        Assert.Contains("Header=\"Excel Import Wizard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WizardStepTitle", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedIndex=\"{Binding LegacyImport.WizardStep, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ImportPoolBatches", xaml, StringComparison.Ordinal);
        Assert.Contains("ImportOrders", xaml, StringComparison.Ordinal);
        Assert.Contains("ImportMachineAssignments", xaml, StringComparison.Ordinal);
        Assert.Contains("ApplySelectedPatternToSimilarCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ApplySelectedPatternToAllCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("AcceptClearMachineSuggestionsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Create Batches in Pool", xaml, StringComparison.Ordinal);
        Assert.Contains("ImportOrderGrid", xaml, StringComparison.Ordinal);
        Assert.Contains("ImportAllocationsSection", xaml, StringComparison.Ordinal);
        Assert.Contains("ImportCaseSection", xaml, StringComparison.Ordinal);
        Assert.Contains("IncludedMappings", xaml, StringComparison.Ordinal);
        Assert.Contains("IncludedMachineMappings", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowsMachineMappings", xaml, StringComparison.Ordinal);
        Assert.Contains("CanCommitNow", xaml, StringComparison.Ordinal);
        Assert.Contains("ReviewRows", xaml, StringComparison.Ordinal);
        Assert.Contains("DecisionDisplayName", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionReason", xaml, StringComparison.Ordinal);
        Assert.Contains("WizardStepTabItem", xaml, StringComparison.Ordinal);
        #if false
        Assert.Contains("Content=\"Browse…\"", xaml, StringComparison.Ordinal);
        #endif
        Assert.Contains("Content=\"Browse", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy layout", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Style=\"{StaticResource ImportOrderSection}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedWizardRow", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedValuePath=\"Value\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DisplayMemberPath=\"DisplayName\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ColumnChoices}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SourceColumn, UpdateSourceTrigger=PropertyChanged}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding IsRequired, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedItem=\"{Binding TargetField", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding LegacyImport.IncludedMachineMappings}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedMachineCandidate", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding SourceSummary", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding SelectedExistingOperationCandidate", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding SelectedRouteOperationCandidate", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding AvailableRouteOperationCandidates}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CompatibilityReviewText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding RequiresCompatibilityOverride}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding CompatibilityOverrideReason", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding Allocations", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding OrderWorkFinishDate", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding OrderNotes", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding NewCaseRevision", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding NewCaseCustomerReference", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding NewCaseNotes", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"0,5,0,0\" Visibility=\"Collapsed\"", xaml, StringComparison.Ordinal);
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
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate Views/SetupView.xaml from the test output directory.");
    }
}
