using System.Windows;
using Microsoft.Win32;

namespace Meimad.Planner.Client.Windows.Localization;

/// <summary>
/// Message boxes are native Win32 dialogs outside the WPF visual tree, so
/// <see cref="LocalizationBehavior"/> never sees them. Every client message box goes
/// through this helper: text and caption are translated line by line, and Hebrew uses
/// right-to-left reading order.
/// </summary>
internal static class LocalizedMessageBox
{
    internal static MessageBoxResult Show(string messageBoxText) =>
        Show(null, messageBoxText, string.Empty, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

    internal static MessageBoxResult Show(string messageBoxText, string caption) =>
        Show(null, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

    internal static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button) =>
        Show(null, messageBoxText, caption, button, MessageBoxImage.None, MessageBoxResult.None);

    internal static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon) =>
        Show(null, messageBoxText, caption, button, icon, MessageBoxResult.None);

    internal static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult) =>
        Show(null, messageBoxText, caption, button, icon, defaultResult);

    internal static MessageBoxResult Show(Window? owner, string messageBoxText, string caption) =>
        Show(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

    internal static MessageBoxResult Show(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon) =>
        Show(owner, messageBoxText, caption, button, icon, MessageBoxResult.None);

    internal static MessageBoxResult Show(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult)
    {
        var text = LocalizedText.Translate(messageBoxText);
        var title = LocalizedText.Translate(caption);
        var options = LocalizedText.IsRightToLeft
            ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign
            : MessageBoxOptions.None;
        return owner is null
            ? MessageBox.Show(text, title, button, icon, defaultResult, options)
            : MessageBox.Show(owner, text, title, button, icon, defaultResult, options);
    }
}

internal static class LocalizedFileDialogs
{
    /// <summary>Translates a common dialog's title and, for file dialogs, the filter descriptions.</summary>
    internal static T Localized<T>(this T dialog)
        where T : CommonItemDialog
    {
        dialog.Title = LocalizedText.Translate(dialog.Title);
        if (dialog is FileDialog fileDialog)
        {
            fileDialog.Filter = TranslateFilter(fileDialog.Filter);
        }
        return dialog;
    }

    /// <summary>
    /// A filter is "description|patterns|description|patterns". Known complete filters come
    /// from the catalog as one entry; otherwise each description is translated on its own and
    /// the patterns are never touched.
    /// </summary>
    internal static string TranslateFilter(string? filter, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return filter ?? string.Empty;
        }

        if (LocalizedText.TryTranslateExact(filter, language, out var translatedFilter))
        {
            return translatedFilter;
        }

        var parts = filter.Split('|');
        for (var index = 0; index < parts.Length; index += 2)
        {
            if (LocalizedText.TryTranslateExact(parts[index], language, out var description)
                && !description.Contains('|', StringComparison.Ordinal))
            {
                parts[index] = description;
            }
        }
        return string.Join('|', parts);
    }
}

/// <summary>Failure-tolerant access for dialogs that may be shown while the client is failing.</summary>
internal static class LocalizedText
{
    internal static bool IsRightToLeft
    {
        get
        {
            try
            {
                return LocalizationService.Current.IsRightToLeft;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    internal static string Translate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        try
        {
            return LocalizationService.Current.Translate(value);
        }
        catch (Exception)
        {
            // A startup or crash message must still be shown when localization itself failed.
            return value;
        }
    }

    internal static bool TryTranslateExact(string value, string? language, out string translation)
    {
        try
        {
            var service = LocalizationService.Current;
            return service.TryTranslateExact(language ?? service.CurrentLanguage, value, out translation);
        }
        catch (Exception)
        {
            translation = value;
            return false;
        }
    }
}
