using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Meimad.Planner.Client.Windows.Localization;
using Meimad.Planner.Client.Windows.Presentation.ToolCatalog;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>The tool catalog page: search and edit the factory's tool definitions.</summary>
public partial class ToolCatalogView : UserControl
{
    public ToolCatalogView() => InitializeComponent();

    private async void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not ToolCatalogViewModel viewModel) return;
        e.Handled = true;
        await viewModel.RefreshAsync();
    }

    private async void DeleteTool_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ToolCatalogViewModel { Selected: { } tool } viewModel) return;
        var answer = LocalizedMessageBox.Show(Window.GetWindow(this),
            $"Delete catalog tool {tool.InternalCode} {tool.Name}? A tool that a preparation refers to is protected by the Server.",
            "Delete catalog tool", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) await viewModel.DeleteSelectedAsync();
    }
}
