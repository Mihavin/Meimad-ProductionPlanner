using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>Picks one active catalog tool for a prepared tool (the Tool Room window).</summary>
public partial class ToolCatalogPickerWindow : Window
{
    private readonly IPlannerApiClient api;

    internal ToolCatalogPickerWindow(IPlannerApiClient api, string? initialQuery)
    {
        this.api = api;
        InitializeComponent();
        SearchBox.Text = initialQuery ?? string.Empty;
        Loaded += async (_, _) => await SearchAsync();
    }

    internal PlannerCatalogTool? Picked { get; private set; }

    /// <summary>Opens the picker over <paramref name="owner"/>; null when nothing was picked.</summary>
    internal static PlannerCatalogTool? Pick(Window owner, IPlannerApiClient api, string? initialQuery)
    {
        var window = new ToolCatalogPickerWindow(api, initialQuery) { Owner = owner };
        return window.ShowDialog() == true ? window.Picked : null;
    }

    private async Task SearchAsync()
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim();
            var tools = await api.ListCatalogToolsAsync(query, null, includeInactive: false);
            // A description that matches nothing shows the whole catalog instead of an empty list.
            if (tools.Count == 0 && query is not null)
            {
                tools = await api.ListCatalogToolsAsync(null, null, includeInactive: false);
                StatusText.Text = LocalizationService.Current.Translate("No tool matches the search; the whole catalog is shown.");
            }
            else
            {
                StatusText.Text = tools.Count == 0
                    ? LocalizationService.Current.Translate("The catalog is empty.")
                    : string.Empty;
            }
            ToolsGrid.ItemsSource = tools;
            if (tools.Count > 0) ToolsGrid.SelectedIndex = 0;
        }
        catch (Exception exception) when (exception is PlannerApiException or HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAsync();
    }

    private void ToolsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Use();

    private void Use_Click(object sender, RoutedEventArgs e) => Use();

    private void Use()
    {
        if (ToolsGrid.SelectedItem is not PlannerCatalogTool tool) return;
        Picked = tool;
        DialogResult = true;
    }
}
