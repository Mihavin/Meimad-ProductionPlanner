namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class CaseWorkspaceViewTests
{
    [Fact]
    public void Case_pool_exposes_server_owned_sort_options()
    {
        var caseView = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "client-windows",
            "Meimad.Planner.Client.Windows",
            "Views",
            "CaseWorkspaceView.xaml"));

        Assert.Contains("Sort Cases by", caseView, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding CaseSortOptions}\"", caseView, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding CaseSort}\"", caseView, StringComparison.Ordinal);
    }

    [Fact]
    public void Case_pool_owns_an_explicit_vertical_scrollbar_without_an_outer_page_scroller()
    {
        var repositoryRoot = FindRepositoryRoot();
        var caseView = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "client-windows",
            "Meimad.Planner.Client.Windows",
            "Views",
            "CaseWorkspaceView.xaml"));
        var mainWindow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "client-windows",
            "Meimad.Planner.Client.Windows",
            "MainWindow.xaml"));

        Assert.Contains("x:Name=\"CasePoolList\"", caseView, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.VerticalScrollBarVisibility=\"Visible\"", caseView, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.CanContentScroll=\"True\"", caseView, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<ScrollViewer VerticalScrollBarVisibility=\"Auto\" HorizontalScrollBarVisibility=\"Disabled\">\n                    <views:CaseWorkspaceView",
            mainWindow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Case_edit_form_owns_an_explicit_vertical_scrollbar()
    {
        var caseView = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "client-windows",
            "Meimad.Planner.Client.Windows",
            "Views",
            "CaseWorkspaceView.xaml"));

        Assert.Contains("x:Name=\"CaseEditScroll\"", caseView, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Visible\"", caseView, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", caseView, StringComparison.Ordinal);
        Assert.Contains("PanningMode=\"VerticalOnly\"", caseView, StringComparison.Ordinal);
    }

    [Fact]
    public void Case_detail_tabs_own_independent_vertical_scrollbars()
    {
        var repositoryRoot = FindRepositoryRoot();
        var caseView = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "client-windows",
            "Meimad.Planner.Client.Windows",
            "Views",
            "CaseWorkspaceView.xaml"));
        var stepViewer = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "client-windows",
            "Meimad.Planner.Client.Windows",
            "Views",
            "StepViewerControl.xaml.cs"));

        foreach (var name in new[] { "StepTabScroll", "OperationsTabScroll", "OrdersTabScroll", "BatchesTabScroll" })
        {
            Assert.Contains($"x:Name=\"{name}\"", caseView, StringComparison.Ordinal);
        }
        Assert.Contains("ScrollViewer.VerticalScrollBarVisibility=\"Visible\"", caseView, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true;", stepViewer, StringComparison.Ordinal);
    }

    [Fact]
    public void Batches_tab_defines_every_layout_row_used_by_its_content()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "client-windows",
            "Meimad.Planner.Client.Windows",
            "Views",
            "CaseWorkspaceView.xaml");
        var document = System.Xml.Linq.XDocument.Load(path);
        System.Xml.Linq.XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        System.Xml.Linq.XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var batchScroller = document
            .Descendants(presentation + "ScrollViewer")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "BatchesTabScroll");
        var layout = batchScroller.Elements(presentation + "Grid").Single();
        var definedRows = layout
            .Element(presentation + "Grid.RowDefinitions")!
            .Elements(presentation + "RowDefinition")
            .Count();
        var highestUsedRow = layout.Elements()
            .Select(element => (string?)element.Attribute("Grid.Row"))
            .Where(value => int.TryParse(value, out _))
            .Select(value => int.Parse(value!, System.Globalization.CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max();

        Assert.Equal(4, definedRows);
        Assert.True(highestUsedRow < definedRows);
    }

    [Fact]
    public void Operation_gcode_area_exposes_release_and_history_without_a_draft_action()
    {
        var caseView = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "client-windows",
            "Meimad.Planner.Client.Windows",
            "Views",
            "CaseWorkspaceView.xaml"));

        Assert.Contains("Content=\"Release G-code\"", caseView, StringComparison.Ordinal);
        Assert.Contains("Postprocessor status (display only)", caseView, StringComparison.Ordinal);
        Assert.Contains("Process-revision history", caseView, StringComparison.Ordinal);
        Assert.Contains("Local post-revision history", caseView, StringComparison.Ordinal);
        Assert.Contains("Header=\"Calculated cycle / part\"", caseView, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding NcCalculatedTimeSummary}\"", caseView, StringComparison.Ordinal);
        Assert.Contains("Current file", caseView, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Save Draft\"", caseView, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Content=\"Draft\"", caseView, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Operation_gcode_postprocessor_selection_updates_the_view_model_immediately()
    {
        var caseView = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "client-windows",
            "Meimad.Planner.Client.Windows",
            "Views",
            "CaseWorkspaceView.xaml"));

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            caseView,
            "SelectedItem=\"\\{Binding SelectedReleasePostprocessor, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged\\}\""));
        Assert.DoesNotContain(
            "<DataGrid ItemsSource=\"{Binding GCodePostprocessors}\" SelectedItem=",
            caseView,
            StringComparison.Ordinal);
        Assert.Contains("Postprocessor status (display only)", caseView, StringComparison.Ordinal);
        Assert.Contains("<Run Text=\"Release target: \"/>", caseView, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedReleasePostprocessor.PostprocessorName}\"", caseView, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root containing AGENTS.md was not found.");
    }
}
