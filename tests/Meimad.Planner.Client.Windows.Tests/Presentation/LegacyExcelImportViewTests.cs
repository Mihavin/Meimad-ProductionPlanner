namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class LegacyExcelImportViewTests
{
    [Fact]
    public void Choosing_a_workbook_previews_and_prepares_the_defined_import_stages()
    {
        var codeBehind = File.ReadAllText(FindSetupView() + ".cs");

        Assert.Contains("private async void BrowseLegacyWorkbook_Click", codeBehind, StringComparison.Ordinal);
        Assert.Contains("viewModel.LegacyImport.SetWorkbookSelection(dialog.FileName);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("await viewModel.LegacyImport.PreviewDefinedImportAsync();", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_import_tab_shows_one_sheet_fixed_case_order_preview_and_import()
    {
        var xaml = File.ReadAllText(FindSetupView());

        Assert.Contains("Text=\"Excel Case + Order Import\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Simple fixed-mapping Case and Order import", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.FixedMappingSummary", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.ImportSheetName", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.PreviewDefinedImportCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Preview data\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding LegacyImport.SimpleCaseOrderRows}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Case / Order result\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Import Cases and Orders\"", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.ImportCasesAndOrdersCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.CaseOrderImportAvailabilityText", xaml, StringComparison.Ordinal);
        Assert.Contains("never creates Batches, Operations, Machine assignments", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_mapping_tabs_are_hidden_from_the_normal_import_page()
    {
        var xaml = File.ReadAllText(FindSetupView());

        Assert.Contains(
            "<TabControl Grid.Row=\"4\" Visibility=\"Collapsed\" SelectedIndex=\"{Binding LegacyImport.WizardStep, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("<Border Grid.Row=\"6\" Visibility=\"Collapsed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Fixed mapping", xaml, StringComparison.Ordinal);
        Assert.Contains("Import is the only write action.", xaml, StringComparison.Ordinal);
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
