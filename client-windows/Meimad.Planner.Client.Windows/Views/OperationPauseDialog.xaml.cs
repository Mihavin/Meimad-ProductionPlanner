using System.Windows;
using System.Windows.Controls;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Views;

public partial class OperationPauseDialog : Window
{
    public OperationPauseDialog() => InitializeComponent();
    internal OperationPauseRequest? Value { get; private set; }

    private string ReasonType => (Reason.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
    private void Reason_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProblemPanel is null) return;
        ProblemPanel.Visibility = ReasonType == "additional_qa" ? Visibility.Visible : Visibility.Collapsed;
        ToolingPanel.Visibility = ReasonType == "tooling_problem" ? Visibility.Visible : Visibility.Collapsed;
        ContactPanel.Visibility = RequestPanel.Visibility = ReasonType == "customer_request" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        string? required = ReasonType switch
        {
            "additional_qa" when string.IsNullOrWhiteSpace(Problem.Text) => "Enter the QA problem description.",
            "tooling_problem" when string.IsNullOrWhiteSpace(Tooling.Text) => "Enter the missing or problematic tooling item.",
            "customer_request" when string.IsNullOrWhiteSpace(Contact.Text) => "Enter the customer contact name.",
            "customer_request" when string.IsNullOrWhiteSpace(Request.Text) => "Enter the customer request description.",
            "other" when string.IsNullOrWhiteSpace(Comment.Text) => "Enter a comment.",
            _ => null
        };
        if (required is not null) { Validation.Text = required; return; }
        static string? Clean(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        Value = new OperationPauseRequest(ReasonType, Clean(Problem.Text), Clean(Tooling.Text),
            Clean(Contact.Text), Clean(Request.Text), Clean(Comment.Text));
        DialogResult = true;
    }
}
