using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Meimad.Planner.Client.Windows.Presentation;
using Microsoft.Win32;
using Microsoft.VisualBasic;

namespace Meimad.Planner.Client.Windows.Views;

public partial class MachinePlanningBoardView : UserControl
{
    private Point dragStart;
    private bool dragInProgress;

    public MachinePlanningBoardView()
    {
        InitializeComponent();
    }

    private async void CreateProductionRun_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MachinePlanningBoardViewModel viewModel || !viewModel.CanDrag) return;
        var operations = OperationPool.SelectedItems.Cast<PlanningOperationViewModel>().ToArray();
        if (operations.Length == 0)
        {
            MessageBox.Show("Select one or more unallocated operations first.", "Create Production Run", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new ProductionRunDialog(new ProductionRunDialogViewModel(operations)) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await viewModel.CreateProductionRunAsync(dialog.ViewModel.CreateRequest());
    }

    private void BrowseMachinePicture_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MachinePlanningBoardViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select the Machine picture",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.SetMachinePictureSelection(dialog.FileName);
        }
    }

    private async void EditMachine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PlanningMachineColumnViewModel machine }
            && DataContext is MachinePlanningBoardViewModel viewModel)
            await viewModel.BeginEditMachineAsync(machine);
    }

    private async void DeleteMachine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PlanningMachineColumnViewModel machine }
            && DataContext is MachinePlanningBoardViewModel viewModel
            && MessageBox.Show(
                $"Delete Machine {machine.DisplayName}? Its backlog, downtime, device binding, and official package references must be empty.",
                "Delete Machine", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes)
            await viewModel.DeleteMachineAsync(machine);
    }

    private async void StartOperation_Click(object sender, RoutedEventArgs e) =>
        await ChangeOperationExecutionAsync(sender, "start");

    private async void SuspendOperation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PlanningOperationViewModel operation }
            || DataContext is not MachinePlanningBoardViewModel viewModel) return;
        var dialog = new OperationPauseDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Value is not null)
            await viewModel.ChangeExecutionStatusAsync(operation, "suspend", dialog.Value);
    }

    private async void FinishOperation_Click(object sender, RoutedEventArgs e) =>
        await ChangeOperationExecutionAsync(sender, "finish");

    private async void ResetOperation_Click(object sender, RoutedEventArgs e) =>
        await ChangeOperationExecutionAsync(sender, "reset");

    private async void ManualReport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item ||
            item.Parent is not ContextMenu { PlacementTarget: FrameworkElement target } ||
            target.DataContext is not PlanningOperationViewModel operation ||
            DataContext is not MachinePlanningBoardViewModel viewModel) return;
        var reportType = item.Tag?.ToString() ?? string.Empty;
        int? seconds = null;
        if (reportType == "partTimeUpdate")
        {
            var value = Interaction.InputBox("Enter manual part time in seconds:", "Manual part time update", "0");
            if (!int.TryParse(value, out var parsed) || parsed <= 0) return;
            seconds = parsed;
        }
        await viewModel.RecordManualReportAsync(operation, reportType, seconds);
    }

    private async void ScheduleBackward_Click(object sender, RoutedEventArgs e) =>
        await ChangePlanningModeAsync(sender, "backward");

    private async void ScheduleForward_Click(object sender, RoutedEventArgs e) =>
        await ChangePlanningModeAsync(sender, "forward");

    private async void SetManualMode_Click(object sender, RoutedEventArgs e) =>
        await ChangePlanningModeAsync(sender, "manual");

    private async void ProductionReadinessText_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlanningOperationViewModel operation })
        {
            await ShowProductionReadinessAsync(operation);
            e.Handled = true;
        }
    }

    private async void ProductionReadinessMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem
            && ItemsControl.ItemsControlFromItemContainer(menuItem) is ContextMenu contextMenu
            && contextMenu.PlacementTarget is FrameworkElement
            {
                DataContext: PlanningOperationViewModel operation
            })
        {
            await ShowProductionReadinessAsync(operation);
        }
    }

    private async Task ShowProductionReadinessAsync(PlanningOperationViewModel operation)
    {
        if (!operation.CanEditReadiness
            || DataContext is not MachinePlanningBoardViewModel viewModel) return;
        var readiness = await viewModel.ReadProductionReadinessAsync(operation);
        if (readiness is null) return;
        var dialog = new ProductionReadinessDialog(operation.DisplayTitle, readiness)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true && dialog.Value is not null)
        {
            await viewModel.UpdateProductionReadinessAsync(operation, dialog.Value);
        }
    }

    private async Task ChangePlanningModeAsync(object sender, string planningMode)
    {
        if (sender is not MenuItem menuItem
            || ItemsControl.ItemsControlFromItemContainer(menuItem) is not ContextMenu contextMenu
            || contextMenu.PlacementTarget is not FrameworkElement
            {
                DataContext: PlanningOperationViewModel operation
            }
            || DataContext is not MachinePlanningBoardViewModel viewModel)
        {
            return;
        }

        await viewModel.ChangePlanningModeAsync(operation, planningMode);
    }

    private async Task ChangeOperationExecutionAsync(object sender, string action)
    {
        if (sender is Button { DataContext: PlanningOperationViewModel operation }
            && DataContext is MachinePlanningBoardViewModel viewModel)
        {
            if (action == "finish"
                && MessageBox.Show(
                    $"Finish {operation.DisplayTitle}? This removes it from the active Machine backlog.",
                    "Finish operation",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            if (action == "reset"
                && MessageBox.Show(
                    $"Reset {operation.DisplayTitle} to Not started? Its machine assignment and backlog position will be kept.",
                    "Reset operation",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            await viewModel.ChangeExecutionStatusAsync(operation, action);
        }
    }

    private void DragSource_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(this);
    }

    private void DragSource_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (dragInProgress
            || e.LeftButton != MouseButtonState.Pressed
            || DataContext is not MachinePlanningBoardViewModel { CanDrag: true })
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not PlanningOperationViewModel operation)
        {
            return;
        }

        if (!operation.CanMove)
        {
            return;
        }

        try
        {
            dragInProgress = true;
            DragDrop.DoDragDrop(item, new BoardDragPayload(operation), DragDropEffects.Move);
        }
        catch (Exception exception)
        {
            if (DataContext is MachinePlanningBoardViewModel viewModel)
            {
                viewModel.ReportMoveFailure(exception);
            }
        }
        finally
        {
            dragInProgress = false;
            dragStart = e.GetPosition(this);
        }
    }

    private void MachineBacklog_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasPayload(e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void MachineBacklog_Drop(object sender, DragEventArgs e)
    {
        if (!TryReadPayload(e, out var payload)
            || sender is not ListBox { DataContext: PlanningMachineColumnViewModel machine }
            || DataContext is not MachinePlanningBoardViewModel viewModel)
        {
            return;
        }

        var position = machine.Backlog.Count;
        var targetContainer = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (targetContainer?.DataContext is PlanningOperationViewModel targetOperation)
        {
            position = machine.Backlog.IndexOf(targetOperation);
            if (e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2)
            {
                position++;
            }
        }

        try
        {
            await viewModel.AssignOrMoveAsync(payload.Operation, machine, position);
        }
        catch (Exception exception)
        {
            viewModel.ReportMoveFailure(exception);
        }
        finally
        {
            e.Handled = true;
        }
    }

    private void Pool_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryReadPayload(e, out var payload) && payload.Operation.MachineId is not null
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Pool_Drop(object sender, DragEventArgs e)
    {
        if (TryReadPayload(e, out var payload)
            && payload.Operation.MachineId is not null
            && DataContext is MachinePlanningBoardViewModel viewModel)
        {
            try
            {
                await viewModel.UnassignAsync(payload.Operation);
            }
            catch (Exception exception)
            {
                viewModel.ReportMoveFailure(exception);
            }
        }

        e.Handled = true;
    }

    private static bool HasPayload(DragEventArgs e) =>
        e.Data.GetDataPresent(typeof(BoardDragPayload));

    private static bool TryReadPayload(DragEventArgs e, out BoardDragPayload payload)
    {
        payload = e.Data.GetData(typeof(BoardDragPayload)) as BoardDragPayload
            ?? new BoardDragPayload(null!);
        return payload.Operation is not null;
    }

    internal static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current switch
            {
                Visual or Visual3D => VisualTreeHelper.GetParent(current),
                FrameworkContentElement content => content.Parent,
                ContentElement content => ContentOperations.GetParent(content),
                _ => LogicalTreeHelper.GetParent(current)
            };
        }

        return null;
    }

    private sealed record BoardDragPayload(PlanningOperationViewModel Operation);
}
