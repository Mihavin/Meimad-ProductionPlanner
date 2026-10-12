using System.Windows;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Localization;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>
/// The import report of a Cimatron simulation package: what was imported or why not, every diagnostic
/// with its stable code, the package files, and — when the Server needs the planner — the stock and
/// Machine choice and the confirmation to replace a manual Work CS.
/// </summary>
public partial class SimulationPackageReportWindow : Window
{
    private readonly SimulationPackageReportViewModel model;

    internal SimulationPackageReportWindow(SimulationPackageReportViewModel model)
    {
        this.model = model;
        InitializeComponent();
        DataContext = model;
        StockChoice.Visibility = model.HasStockOptions ? Visibility.Visible : Visibility.Collapsed;
        MachineChoice.Visibility = model.HasMachineOptions ? Visibility.Visible : Visibility.Collapsed;
        OverwriteChoice.Visibility = model.NeedsManualOverwrite ? Visibility.Visible : Visibility.Collapsed;
        ChoicesPanel.Visibility = model.CanImportWithChoices ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.Visibility = model.CanImportWithChoices ? Visibility.Visible : Visibility.Collapsed;
    }

    internal SimulationPackageChoices? Choices { get; private set; }

    /// <summary>Shows the report; returns the planner's choices when they asked to import again.</summary>
    internal static SimulationPackageChoices? Show(Window? owner, SimulationPackageImportResult result, bool canManageSetup)
    {
        var window = new SimulationPackageReportWindow(new SimulationPackageReportViewModel(result, canManageSetup));
        if (owner is not null) window.Owner = owner;
        return window.ShowDialog() == true ? window.Choices : null;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var choices = model.Choices(out var missing);
        if (choices is null)
        {
            StatusText.Text = LocalizationService.Current.Translate(missing ?? string.Empty);
            return;
        }

        Choices = choices;
        DialogResult = true;
    }
}
