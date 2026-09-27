using System.IO;
using System.Windows;
using System.Windows.Controls;
using Meimad.Planner.Client.Windows.Localization;
using Meimad.Planner.Client.Windows.Presentation;
using Microsoft.Win32;

namespace Meimad.Planner.Client.Windows.Views;

public partial class ToolRequirementsView : UserControl
{
    public ToolRequirementsView() => InitializeComponent();

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ToolRequirementsViewModel viewModel) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export tool requirements",
            Filter = "Excel workbooks|*.xlsx|All files|*.*",
            FileName = $"Tool-Requirements-{viewModel.FromDate:yyyy-MM-dd}-{viewModel.ToDate:yyyy-MM-dd}.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
            OverwritePrompt = true
        }.Localized();
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var workbook = await viewModel.ExportAsync();
        if (workbook is null) return;
        try
        {
            await File.WriteAllBytesAsync(dialog.FileName, workbook);
            viewModel.ReportSaved(dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LocalizedMessageBox.Show(Window.GetWindow(this), exception.Message, "Export tool requirements",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
