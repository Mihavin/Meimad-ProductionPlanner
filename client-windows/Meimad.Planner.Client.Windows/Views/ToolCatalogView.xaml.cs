using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Meimad.Planner.Client.Windows.Localization;
using Meimad.Planner.Client.Windows.Presentation.ToolCatalog;
using Microsoft.Win32;

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

    private async void ImportCimatron_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ToolCatalogViewModel viewModel) return;
        var dialog = new OpenFileDialog
        {
            Title = "Import tools from Cimatron",
            Filter = "Cimatron cutter workbooks|*.xlsm;*.xlsx|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        }.Localized();
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var preview = await viewModel.PreviewCimatronImportAsync(dialog.FileName);
        if (preview is null) return;
        if (preview.Created + preview.Updated == 0)
        {
            LocalizedMessageBox.Show(Window.GetWindow(this),
                $"{preview.FileName}: {ToolCatalogViewModel.CimatronSummary(preview)} There is nothing to import.",
                "Import tools from Cimatron", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var skipped = preview.Rows.Where(row => row.Action == "SKIP").Take(8)
            .Select(row => $"{row.CutterName}: {row.Message}").ToList();
        var details = skipped.Count == 0 ? string.Empty : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, skipped);
        var answer = LocalizedMessageBox.Show(Window.GetWindow(this),
            $"{preview.FileName}: {ToolCatalogViewModel.CimatronSummary(preview)} Import the new and updated tools now?{details}",
            "Import tools from Cimatron", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Yes) await viewModel.ApplyCimatronImportAsync(dialog.FileName);
    }

    private async void ExportCimatron_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ToolCatalogViewModel viewModel) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export tools to Cimatron",
            Filter = "Cimatron cutter workbooks|*.xlsm|All files|*.*",
            FileName = "Meimad-Tool-Catalog-Cimatron.xlsm",
            AddExtension = true,
            DefaultExt = ".xlsm",
            OverwritePrompt = true
        }.Localized();
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await viewModel.ExportCimatronAsync(dialog.FileName);
    }
}
