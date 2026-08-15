using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Views;

public partial class SetupView : UserControl
{
    public SetupView()
    {
        InitializeComponent();
    }

    private void BrowseLegacyWorkbook_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose legacy Excel workbook",
            Filter = "Excel workbooks|*.xlsx|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && DataContext is SetupViewModel viewModel)
        {
            // File selection is intentionally the only import concern owned by the view.
            viewModel.LegacyImport.SetWorkbookSelection(dialog.FileName);
        }
    }

    private async void DeleteCalendar_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel { SelectedCalendar: { } calendar } viewModel
            && Confirm(
                $"Delete calendar {calendar.Name}? Calendars used by a Machine or as the Setup Calendar are protected by the Server.",
                "Delete calendar"))
        {
            await viewModel.DeleteSelectedCalendarAsync();
        }
    }

    private async void DeleteMachine_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel { SelectedMachine: { } machine } viewModel
            && Confirm(
                $"Delete Machine {machine.Number} - {machine.Name}? Assigned or referenced Machines are protected by the Server.",
                "Delete Machine"))
        {
            await viewModel.DeleteSelectedMachineAsync();
        }
    }

    private async void DeleteMachineType_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel { SelectedMachineType: { } machineType } viewModel
            && Confirm(
                $"Delete Machine Type {machineType.Name}? Types assigned to a Machine are protected by the Server.",
                "Delete Machine Type"))
        {
            await viewModel.DeleteSelectedMachineTypeAsync();
        }
    }

    private async void DeleteResource_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel { SelectedResource: { } resource } viewModel
            && Confirm($"Delete employee / resource {resource.Name}?", "Delete employee / resource"))
        {
            await viewModel.DeleteSelectedResourceAsync();
        }
    }

    private async void DeleteResourceException_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel { SelectedResourceException: { } exception } viewModel
            && Confirm($"Delete employee exception {exception.DisplayName}?", "Delete employee exception"))
        {
            await viewModel.DeleteSelectedResourceExceptionAsync();
        }
    }

    private async void DeleteIsraeliHoliday_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel { SelectedIsraeliHoliday: { } holiday } viewModel
            && Confirm($"Delete Israeli holiday {holiday.Name} ({holiday.Date})?", "Delete Israeli holiday"))
        {
            await viewModel.DeleteSelectedIsraeliHolidayAsync();
        }
    }

    private static bool Confirm(string message, string title) => MessageBox.Show(
        message,
        title,
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;
}
