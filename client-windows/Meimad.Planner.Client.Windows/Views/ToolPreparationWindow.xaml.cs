using System.Windows;
using Meimad.Planner.Client.Windows.Localization;
using Meimad.Planner.Client.Windows.Presentation.ToolPreparation;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>The Tool Room's editable tool table with a shape preview, opened from the Tool Room queue.</summary>
public partial class ToolPreparationWindow : Window
{
    internal ToolPreparationWindow(ToolPreparationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += (_, e) =>
        {
            if (!viewModel.IsDirty) return;
            var answer = LocalizedMessageBox.Show(this,
                "The tool table has unsaved measurements. Close without saving?",
                "Tool table", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            e.Cancel = answer != MessageBoxResult.Yes;
        };
    }

    internal ToolPreparationViewModel ViewModel => (ToolPreparationViewModel)DataContext;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Takes type, hand and dimensions from a catalog tool and keeps the link on the prepared tool.</summary>
    private void PickCatalogTool_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTool is not { } tool) return;
        var picked = ToolCatalogPickerWindow.Pick(this, ViewModel.Api, tool.Description);
        if (picked is not null) tool.ApplyCatalogTool(picked);
    }

    private void ClearCatalogTool_Click(object sender, RoutedEventArgs e) => ViewModel.SelectedTool?.ClearCatalogTool();
}
