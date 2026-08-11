using System.Windows.Controls;
using System.Windows;
using Meimad.Planner.Client.Windows.Presentation;
using Microsoft.Win32;

namespace Meimad.Planner.Client.Windows.Views;

public partial class CaseWorkspaceView : UserControl
{
    public CaseWorkspaceView()
    {
        InitializeComponent();
    }

    private void BrowseFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select the external Case Working Folder",
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.SetWorkingFolderSelection(dialog.FolderName);
        }
    }

    private void BrowsePicture_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not CaseWorkspaceViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select the Case picture",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.SetPreviewSelection(dialog.FileName);
        }
    }

    private async void DeleteCase_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaseWorkspaceViewModel viewModel
            && Confirm("Delete the selected Case? It must have no Operations, Orders, or Production Batches."))
            await viewModel.DeleteSelectedCaseAsync();
    }

    private async void DeleteOperation_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaseWorkspaceViewModel viewModel
            && Confirm("Delete the selected Case Operation? Referenced or instantiated Operations cannot be deleted."))
            await viewModel.DeleteSelectedOperationAsync();
    }

    private async void DeleteOrder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaseWorkspaceViewModel viewModel
            && Confirm("Delete the selected Order? Orders allocated to a Production Batch cannot be deleted."))
            await viewModel.DeleteSelectedOrderAsync();
    }

    private async void DeleteBatch_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaseWorkspaceViewModel viewModel
            && Confirm("Delete the selected Production Batch and its unassigned Operations and allocations?"))
            await viewModel.DeleteSelectedBatchAsync();
    }

    private static bool Confirm(string message) => MessageBox.Show(
        message, "Confirm deletion", MessageBoxButton.YesNo,
        MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}
