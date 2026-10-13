using System.Windows;
using Meimad.Planner.Client.Windows.Presentation.CimatronImport;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>
/// Import from Cimatron: the one window of the import (specification of 2026-10-13, §3) — the CAM file, the target Meimad
/// Operation, the CAM operations grouped by setup, then progress, result and errors in the same window.
/// </summary>
public partial class CimatronImportWindow : Window
{
    private readonly CimatronImportViewModel viewModel;

    internal CimatronImportWindow(CimatronImportViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadAsync();
        Closing += (_, args) =>
        {
            // An export in progress is cancelled first; registration is never interrupted.
            if (viewModel.CancelCommand.CanExecute(null)) viewModel.CancelCommand.Execute(null);
            else if (viewModel.IsBusy) args.Cancel = true;
        };
    }

    /// <summary>True when the user chose Open simulation after a successful import.</summary>
    internal bool OpenSimulationRequested { get; private set; }

    /// <summary>Shows the window; returns it after it closed, with the import result on its view model.</summary>
    internal static CimatronImportWindow Show(Window? owner, CimatronImportViewModel viewModel)
    {
        var window = new CimatronImportWindow(viewModel) { Owner = owner };
        window.ShowDialog();
        return window;
    }

    private void OpenSimulation_Click(object sender, RoutedEventArgs e)
    {
        OpenSimulationRequested = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
