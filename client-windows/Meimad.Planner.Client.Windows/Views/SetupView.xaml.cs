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

    private async void BrowseLegacyWorkbook_Click(object sender, RoutedEventArgs e)
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
            // The simplified flow previews the workbook and prepares the four fixed
            // import stages from the approved mapping. Commit remains explicit.
            viewModel.LegacyImport.SetWorkbookSelection(dialog.FileName);
            await viewModel.LegacyImport.PreviewDefinedImportAsync();
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

    private async void DeleteSkill_Click(object sender,RoutedEventArgs e)
    {
        if(sender is FrameworkElement { DataContext: ResourceMasterDataViewModel { SelectedSkill: { } skill } vm }
            && Confirm($"Delete Skill {skill.Name}? Referenced Skills are protected by the Server.","Delete Skill"))
            await vm.DeleteSkillAsync();
    }

    private async void DeleteWorkstationType_Click(object sender,RoutedEventArgs e)
    {
        if(sender is FrameworkElement { DataContext: ResourceMasterDataViewModel { SelectedWorkstationType: { } item } vm }
            && Confirm($"Delete Workstation Type {item.Name}? Referenced types are protected by the Server.","Delete Workstation Type"))
            await vm.DeleteWorkstationTypeAsync();
    }

    private async void DeleteWorkstation_Click(object sender,RoutedEventArgs e)
    {
        if(sender is FrameworkElement { DataContext: ResourceMasterDataViewModel { SelectedWorkstation: { } item } vm }
            && Confirm($"Delete Workstation {item.Name}? Scheduled references are protected by the Server.","Delete Workstation"))
            await vm.DeleteWorkstationAsync();
    }

    private async void DeleteExternalResource_Click(object sender,RoutedEventArgs e)
    {
        if(sender is FrameworkElement { DataContext: ResourceMasterDataViewModel { SelectedExternalResource: { } item } vm }
            && Confirm($"Delete External Resource {item.Name}? Requirement references are protected by the Server.","Delete External Resource"))
            await vm.DeleteExternalResourceAsync();
    }

    private static bool Confirm(string message, string title) => MessageBox.Show(
        message,
        title,
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;
}
