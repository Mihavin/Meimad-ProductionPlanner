using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Meimad.Planner.Client.Windows.Presentation;
using Microsoft.Win32;

namespace Meimad.Planner.Client.Windows.Views;

public partial class MachinePlanningBoardView : UserControl
{
    private Point dragStart;

    public MachinePlanningBoardView()
    {
        InitializeComponent();
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

    private void ViewBackwardTimeline_Click(object sender, RoutedEventArgs e)
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

        viewModel.RequestBackwardTimeline(operation);
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
        if (e.LeftButton != MouseButtonState.Pressed
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

        DragDrop.DoDragDrop(item, new BoardDragPayload(operation), DragDropEffects.Move);
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

        await viewModel.AssignOrMoveAsync(payload.Operation, machine, position);
        e.Handled = true;
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
            await viewModel.UnassignAsync(payload.Operation);
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

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private sealed record BoardDragPayload(PlanningOperationViewModel Operation);
}
