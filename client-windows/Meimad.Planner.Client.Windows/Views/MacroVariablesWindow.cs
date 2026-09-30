using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>
/// The NC viewer's macro variables table in its own window: every variable the program uses with its
/// value at the playback position or the selected row, as the viewer page computes it from the
/// engine's execution trace. It can be moved to another screen and stays open while playback runs.
/// </summary>
internal sealed class MacroVariablesWindow : Window
{
    private readonly TextBlock position;
    private readonly TextBox filter;
    private readonly ObservableCollection<NcViewerMacroVariable> rows = [];
    private readonly ICollectionView view;

    internal MacroVariablesWindow()
    {
        Title = "Macro variables";
        Width = 520;
        Height = 620;
        MinWidth = 360;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var root = new DockPanel { Margin = new Thickness(10) };
        position = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(position, "Position");
        DockPanel.SetDock(position, Dock.Top);
        root.Children.Add(position);

        var filterRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var filterLabel = new TextBlock { Text = "Filter", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(filterLabel, Dock.Left);
        filterRow.Children.Add(filterLabel);
        filter = new TextBox { ToolTip = "Show variables whose number, value or scope contains this text" };
        AutomationProperties.SetName(filter, "Filter");
        filter.TextChanged += (_, _) => view?.Refresh();
        filterRow.Children.Add(filter);
        DockPanel.SetDock(filterRow, Dock.Top);
        root.Children.Add(filterRow);

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Extended
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Variable", Binding = new Binding(nameof(NcViewerMacroVariable.Variable)), Width = new DataGridLength(80) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new Binding(nameof(NcViewerMacroVariable.Value)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Scope", Binding = new Binding(nameof(NcViewerMacroVariable.Scope)), Width = new DataGridLength(80) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Set at", Binding = new Binding(nameof(NcViewerMacroVariable.SetAt)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        view = CollectionViewSource.GetDefaultView(rows);
        view.Filter = item => item is NcViewerMacroVariable row && Matches(row, filter.Text);
        grid.ItemsSource = view;
        AutomationProperties.SetName(grid, "Macro variables");
        root.Children.Add(grid);
        Content = root;
    }

    /// <summary>Replaces the table; the same variables keep their rows so the view does not jump.</summary>
    internal void Display(NcViewerMacroVariables data)
    {
        position.Text = data.Position;
        if (rows.Count == data.Rows.Count && rows.Select(row => row.Variable).SequenceEqual(data.Rows.Select(row => row.Variable)))
        {
            for (var index = 0; index < rows.Count; index++)
            {
                if (rows[index] != data.Rows[index]) rows[index] = data.Rows[index];
            }
            return;
        }
        rows.Clear();
        foreach (var row in data.Rows) rows.Add(row);
    }

    internal static bool Matches(NcViewerMacroVariable row, string? text) =>
        string.IsNullOrWhiteSpace(text)
        || row.Variable.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)
        || row.Value.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)
        || row.Scope.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)
        || row.SetAt.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase);
}
