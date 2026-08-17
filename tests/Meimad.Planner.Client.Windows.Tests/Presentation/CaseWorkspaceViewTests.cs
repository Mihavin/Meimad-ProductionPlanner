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
