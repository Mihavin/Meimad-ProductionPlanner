using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>
/// Signs a person in to the Meimad Planner (owner decision 2026-09-27: everyone signs in with a user
/// name and password). On an empty Server it creates the first administrator, and when an
/// administrator set a temporary password it asks for the person's own password before continuing.
/// </summary>
internal sealed class SignInWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly TextBlock heading = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock explanation = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBox userName = new() { Padding = new Thickness(4) };
    private readonly TextBox displayName = new() { Padding = new Thickness(4) };
    private readonly PasswordBox password = new() { Padding = new Thickness(4) };
    private readonly PasswordBox newPassword = new() { Padding = new Thickness(4) };
    private readonly PasswordBox confirmPassword = new() { Padding = new Thickness(4) };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button submit = new() { IsDefault = true, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
    private readonly StackPanel userNameRow;
    private readonly StackPanel displayNameRow;
    private readonly StackPanel passwordRow;
    private readonly StackPanel newPasswordRow;
    private readonly StackPanel confirmRow;
    private Mode mode;
    private string signInPassword = string.Empty;

    private enum Mode
    {
        SignIn,
        FirstAdministrator,
        ChangePassword
    }

    private SignInWindow(MainWindowViewModel viewModel, bool changePassword)
    {
        this.viewModel = viewModel;
        error.SetResourceReference(TextBlock.ForegroundProperty, "ErrorTextBrush");
        submit.SetResourceReference(StyleProperty, "PrimaryButton");
        Title = "Sign in — Meimad Production Planner";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        userNameRow = Field("User name", userName);
        displayNameRow = Field("Full name", displayName);
        passwordRow = Field("Password", password);
        newPasswordRow = Field("New password (at least 6 characters)", newPassword);
        confirmRow = Field("Repeat the new password", confirmPassword);

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(heading);
        root.Children.Add(explanation);
        root.Children.Add(userNameRow);
        root.Children.Add(displayNameRow);
        root.Children.Add(passwordRow);
        root.Children.Add(newPasswordRow);
        root.Children.Add(confirmRow);
        root.Children.Add(error);

        submit.Click += async (_, _) => await SubmitAsync();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        buttons.Children.Add(submit);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        userName.Text = viewModel.LastUserName;
        SetMode(changePassword || viewModel.Account is { MustChangePassword: true }
            ? Mode.ChangePassword
            : viewModel.NeedsFirstAdministrator ? Mode.FirstAdministrator : Mode.SignIn);
        Loaded += (_, _) => FocusFirstEmpty();
    }

    /// <summary>Shows the sign-in; the view model knows the result through its account.</summary>
    internal static void Show(Window owner, MainWindowViewModel viewModel)
    {
        new SignInWindow(viewModel, changePassword: false) { Owner = owner }.ShowDialog();
    }

    /// <summary>Lets the signed-in person replace their password.</summary>
    internal static void ShowChangePassword(Window owner, MainWindowViewModel viewModel)
    {
        new SignInWindow(viewModel, changePassword: true) { Owner = owner, Title = "Change password — Meimad Production Planner" }.ShowDialog();
    }

    private static StackPanel Field(string label, Control input)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) });
        AutomationProperties.SetName(input, label);
        row.Children.Add(input);
        return row;
    }

    private void SetMode(Mode next)
    {
        mode = next;
        error.Text = string.Empty;
        switch (mode)
        {
            case Mode.FirstAdministrator:
                heading.Text = "Create the first administrator";
                explanation.Text = "This Server has no accounts yet. The first account is an administrator: it may do everything and creates the other users in the Users tab.";
                submit.Content = "Create and sign in";
                break;
            case Mode.ChangePassword:
                heading.Text = "Choose your password";
                explanation.Text = viewModel.Account is { MustChangePassword: true }
                    ? "An administrator gave you a temporary password. Choose your own password to continue; only you will know it."
                    : "Enter your current password and the new one. Your other open sessions stay signed in.";
                submit.Content = "Save password";
                break;
            default:
                heading.Text = "Sign in";
                explanation.Text = "Sign in with the user name and password your administrator gave you. Everyone may view; what you may change depends on your user types.";
                submit.Content = "Sign in";
                break;
        }

        userNameRow.Visibility = mode == Mode.ChangePassword ? Visibility.Collapsed : Visibility.Visible;
        displayNameRow.Visibility = mode == Mode.FirstAdministrator ? Visibility.Visible : Visibility.Collapsed;
        passwordRow.Visibility = mode == Mode.SignIn || (mode == Mode.ChangePassword && signInPassword.Length == 0)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ((TextBlock)passwordRow.Children[0]).Text = mode != Mode.ChangePassword
            ? "Password"
            : viewModel.Account is { MustChangePassword: true } ? "Temporary password" : "Current password";
        newPasswordRow.Visibility = mode == Mode.SignIn ? Visibility.Collapsed : Visibility.Visible;
        confirmRow.Visibility = mode == Mode.SignIn ? Visibility.Collapsed : Visibility.Visible;
        FocusFirstEmpty();
    }

    private void FocusFirstEmpty()
    {
        if (userNameRow.IsVisible && string.IsNullOrWhiteSpace(userName.Text)) userName.Focus();
        else if (displayNameRow.IsVisible && string.IsNullOrWhiteSpace(displayName.Text)) displayName.Focus();
        else if (passwordRow.IsVisible) password.Focus();
        else newPassword.Focus();
    }

    private async Task SubmitAsync()
    {
        error.Text = string.Empty;
        if (mode != Mode.SignIn && newPassword.Password != confirmPassword.Password)
        {
            error.Text = "The two new passwords are different. Type the same password twice.";
            return;
        }

        submit.IsEnabled = false;
        try
        {
            string? failure;
            switch (mode)
            {
                case Mode.SignIn:
                    failure = await viewModel.SignInAsync(userName.Text, password.Password);
                    if (failure is null && viewModel.Account is { MustChangePassword: true })
                    {
                        signInPassword = password.Password;
                        SetMode(Mode.ChangePassword);
                        return;
                    }
                    break;
                case Mode.FirstAdministrator:
                    failure = await viewModel.CreateFirstAdministratorAsync(
                        userName.Text, string.IsNullOrWhiteSpace(displayName.Text) ? userName.Text : displayName.Text,
                        newPassword.Password);
                    break;
                default:
                    var current = signInPassword.Length > 0 ? signInPassword : password.Password;
                    failure = await viewModel.ChangePasswordAsync(current, newPassword.Password);
                    break;
            }

            if (failure is not null)
            {
                error.Text = failure;
                return;
            }

            DialogResult = true;
        }
        finally
        {
            submit.IsEnabled = true;
        }
    }
}
