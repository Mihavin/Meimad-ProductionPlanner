using System.Windows;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Views;

public partial class ProductionReadinessDialog : Window
{
    private readonly PlannerProductionReadiness readiness;

    internal ProductionReadinessDialog(
        string operationDisplayName,
        PlannerProductionReadiness readiness)
    {
        InitializeComponent();
        this.readiness = readiness;
        OperationTitle.Text = operationDisplayName;
        Summary.Text = readiness.Summary;
        Components.ItemsSource = readiness.Components;
        ActionDecisions.ItemsSource = readiness.Actions?.Select(action => new
        {
            Action = action.Action switch
            {
                "Plan" => "Planning", "CreatePackage" => "Create package",
                "RecordSetupStart" => "Report setup start", "RunStart" => "Start run",
                "RecordProduction" => "Record production", _ => action.Action
            },
            State = action.IsAllowed ? "Ready" : "Blocked",
            Reasons = action.Reasons.Where(reason => reason.Classification != "SATISFIED")
                .Select(reason => new { Classification = reason.Classification == "BLOCKING" ? "Blocked" : "Needs attention", reason.Message }).ToArray()
        }).ToArray();
        Release.ItemsSource = readiness.CompatibleGCodeReleases;
        Release.DisplayMemberPath = nameof(PlannerReadinessRelease.DisplayName);
        Release.SelectedValuePath = nameof(PlannerReadinessRelease.GCodeReleaseId);
        Release.SelectedValue = readiness.EffectiveGCodeReleaseId;
        if (Release.SelectedIndex < 0 && readiness.CompatibleGCodeReleases.Count == 1)
        {
            Release.SelectedIndex = 0;
        }
        OffsetsReady.IsChecked = IsReady("toolOffsets");
    }

    internal ProductionReadinessInputUpdate? Value { get; private set; }

    private bool IsReady(string key) => readiness.Components.Any(component =>
        component.Key == key && component.State == "READY");

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var selectedReleaseId = Release.SelectedValue as string;
        if (readiness.RequiresExplicitGCodeSelection && selectedReleaseId is null)
        {
            Validation.Text = "Select the current compatible G-code release.";
            return;
        }
        static string? Clean(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        Value = new ProductionReadinessInputUpdate(
            selectedReleaseId,
            State("material"),
            null,
            OffsetsReady.IsChecked == true ? "READY" : "UNVERIFIED",
            Clean(OffsetsComment.Text));
        DialogResult = true;
    }

    private string State(string key) => readiness.Components
        .FirstOrDefault(component => component.Key == key)?.State ?? "UNVERIFIED";
}
