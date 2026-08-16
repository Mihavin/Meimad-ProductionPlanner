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
    public void Setup_import_tab_shows_one_sheet_preview_and_four_fixed_stages()
    {
        var xaml = File.ReadAllText(FindSetupView());

        Assert.Contains("Text=\"Excel Planning Import\"", xaml, StringComparison.Ordinal);
        Assert.Contains("There is no column-mapping wizard in this workflow.", xaml, StringComparison.Ordinal);
        Assert.Contains("Cases A/O/F/D, Orders B/L/E/N, and Batches P/H", xaml, StringComparison.Ordinal);
        Assert.Contains("Planned quantity is calculated from the related Orders.", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.ImportSheetName", xaml, StringComparison.Ordinal);
        Assert.Contains("LegacyImport.PreviewDefinedImportCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Preview data\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding LegacyImport.CurrentStageRows}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Proposed result\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Import reviewed data\"", xaml, StringComparison.Ordinal);

        Assert.Contains("Content=\"1  Cases\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"2  Related Orders\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"3  Batches in Pool\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"4  Assign to Machine\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowCasesStageCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowOrdersStageCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowBatchesStageCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowAssignmentsStageCommand", xaml, StringComparison.Ordinal);
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
        Assert.Contains("The predefined mapping is used automatically", xaml, StringComparison.Ordinal);
        Assert.Contains("Only the final Import button writes data.", xaml, StringComparison.Ordinal);
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
