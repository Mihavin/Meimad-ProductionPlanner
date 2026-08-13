using System.Windows;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Views;

public partial class AssignmentOverrideDialog : Window
{
    internal AssignmentOverrideDialog(AssignmentOverridePrompt prompt)
    {
        InitializeComponent();
        WarningText.Text =
            $"{prompt.OperationDisplayName} is intended for {prompt.RequiredMachineType}. " +
            $"You selected {prompt.MachineDisplayName} ({prompt.SelectedMachineType}). " +
            "This does not change the operation route. Confirm only when this exception is intentional; the Server will audit your identity, time, both types, and reason.";
        Loaded += (_, _) => ReasonTextBox.Focus();
    }

    internal string Reason => ReasonTextBox.Text.Trim();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ReasonTextBox.Text))
        {
            ValidationText.Visibility = Visibility.Visible;
            ReasonTextBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
