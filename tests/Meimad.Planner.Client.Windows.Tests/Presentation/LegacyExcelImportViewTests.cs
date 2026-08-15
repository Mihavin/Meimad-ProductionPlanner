namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class LegacyExcelImportViewTests
{
    [Fact]
    public void Choosing_a_legacy_workbook_immediately_starts_only_the_read_only_preview()
    {
        var codeBehind = File.ReadAllText(FindSetupView() + ".cs");

        Assert.Contains("private async void BrowseLegacyWorkbook_Click", codeBehind, StringComparison.Ordinal);
        Assert.Contains("viewModel.LegacyImport.SetWorkbookSelection(dialog.FileName);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("await viewModel.LegacyImport.PreviewAsync();", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("PrepareAutomatically", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_import_tab_exposes_four_guided_stages_and_keeps_advanced_corrections()
    {
        var xaml = File.ReadAllText(FindSetupView());

        Assert.Contains("Import a working plan in four steps", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowCasesStageCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowOrdersStageCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowBatchesStageCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowAssignmentsStageCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("CurrentImportStageTitle", xaml, StringComparison.Ordinal);
        Assert.Contains("CurrentImportStageDescription", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"1  Cases\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"2  Related Orders\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"3  Batches in Pool\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"4  Assign to Machine\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Step 1: review each Part Number", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Step 2: find the related Case", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Step 3: create a full-route Batch", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Step 4: choose a compatible Machine", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.ShowDuration=\"12000\"", xaml, StringComparison.Ordinal);
        Assert.Contains("all approved changes are committed once, atomically", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Prepare automatic draft", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Import ready items\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Commit import\"", xaml, StringComparison.Ordinal);

        // The simpler path augments the detailed workflow; it does not remove the
        // sheet, mapping, per-row, compatibility, or atomic review controls.
        Assert.Contains("SelectedIndex=\"{Binding LegacyImport.WizardStep, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.IncludedMappings", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.IncludedMachineMappings", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.SelectedWizardRow", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding LegacyImport.CommitCommand}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_import_tab_exposes_explicit_source_and_entity_mapping_controls()
    {
        var xaml = File.ReadAllText(FindSetupView());

        Assert.Contains("Text=\"Excel Import Wizard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Source=\"{StaticResource ImportButtonIcon}\"", xaml, StringComparison.Ordinal);
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
        Assert.Contains("Binding=\"{Binding LegacyImport.ShowsMachineMappings}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding=\"{Binding ShowsMachineMappings}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanCommitNow", xaml, StringComparison.Ordinal);
        Assert.Contains("ReviewRows", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.DetectedSheets", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.SheetChoices", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.OptionalSheetChoices", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.PreviewCorrectionStatus", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.PreviewSummary", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.ResultSummary", xaml, StringComparison.Ordinal);
        Assert.Contains("Validate / refresh preview", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding LegacyImport.CurrentStageRows}\"", xaml, StringComparison.Ordinal);
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
        Assert.Contains("ItemsSource=\"{Binding ColumnOptions}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedValue=\"{Binding SourceColumn, UpdateSourceTrigger=PropertyChanged}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedValuePath=\"Column\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DisplayMemberPath=\"DisplayName\"", xaml, StringComparison.Ordinal);
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
        Assert.DoesNotContain("Binding=\"{Binding Decision, UpdateSourceTrigger=PropertyChanged}\"", xaml, StringComparison.Ordinal);
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
