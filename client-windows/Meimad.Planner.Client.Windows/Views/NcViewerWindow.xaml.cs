using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>
/// Hosts the vendored Chevalier NC viewer page in WebView2. The page and every asset are served
/// from the installed NcViewer folder under a private https origin; packed toolpath models are
/// served from memory. The page can only talk to <see cref="NcViewerSession"/> through web
/// messages; it has no host objects, file access or network access.
/// </summary>
public partial class NcViewerWindow : Window, INcViewerHostUi
{
    private const string Origin = "https://ncviewer.meimad.local";
    private const string PagePath = "/desktop/meimad-viewer.html";
    private const int RetainedModels = 2;
    private static readonly string AssetRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "NcViewer"))
        + Path.DirectorySeparatorChar;

    private readonly NcViewerOpenRequest request;
    private readonly NcViewerSession session;
    private readonly List<(string Token, byte[] Bytes)> models = [];
    private bool closeConfirmed;

    internal NcViewerWindow(NcViewerOpenRequest request)
    {
        this.request = request;
        InitializeComponent();
        session = new NcViewerSession(request, this);
        UpdateTitle(request.DocumentName, dirty: false);
        Loaded += async (_, _) => await InitializeBrowserAsync();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            session.Dispose();
            Browser.Dispose();
            models.Clear();
        };
    }

    /// <summary>
    /// Opens a new, independent viewer window for <paramref name="request"/> (not owned by the main
    /// window, so a large editor can sit on another screen or behind the Planner).
    /// </summary>
    internal static NcViewerWindow Open(NcViewerOpenRequest request)
    {
        var window = new NcViewerWindow(request);
        window.Show();
        window.Activate();
        return window;
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var userData = Path.Combine(NcViewerSettingsStore.DataFolder, "WebView2");
            Directory.CreateDirectory(userData);
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            await Browser.EnsureCoreWebView2Async(environment);
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or COMException
            or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowFallback(exception is WebView2RuntimeNotFoundException
                ? "The Microsoft Edge WebView2 Runtime is not installed on this PC, so the 3D NC viewer cannot start. The program is shown as read-only text. Install the WebView2 Runtime (Evergreen) to use the viewer."
                : $"The 3D NC viewer could not start ({exception.Message}). The program is shown as read-only text.");
            return;
        }

        var core = Browser.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached
            || Environment.GetEnvironmentVariable("MEIMAD_NC_VIEWER_DEVTOOLS") == "1";
        core.AddWebResourceRequestedFilter($"{Origin}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += (_, e) =>
        {
            if (!e.Source.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase)) return;
            session.HandleMessage(e.WebMessageAsJson);
        };
        core.Navigate(Origin + PagePath);
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var environment = Browser.CoreWebView2.Environment;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri))
        {
            e.Response = environment.CreateWebResourceResponse(null, 400, "Bad Request", string.Empty);
            return;
        }

        if (uri.AbsolutePath.StartsWith("/__model/", StringComparison.Ordinal))
        {
            var token = uri.AbsolutePath["/__model/".Length..];
            var model = models.FirstOrDefault(value => value.Token == token);
            e.Response = model.Bytes is null
                ? environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty)
                : environment.CreateWebResourceResponse(new MemoryStream(model.Bytes, writable: false), 200, "OK",
                    "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store");
            return;
        }

        var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(AssetRoot, relative));
        if (!path.StartsWith(AssetRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
            return;
        }
        e.Response = environment.CreateWebResourceResponse(
            new MemoryStream(File.ReadAllBytes(path), writable: false), 200, "OK",
            $"Content-Type: {ContentType(path)}\r\nCache-Control: no-cache");
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        _ => "application/octet-stream"
    };

    private void ShowFallback(string message)
    {
        Browser.Visibility = Visibility.Collapsed;
        FallbackPanel.Visibility = Visibility.Visible;
        FallbackMessage.Text = message;
        FallbackText.Text = request.Document.Text.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (closeConfirmed || !session.IsDirty) return;
        if (!ConfirmDiscardChanges(session.DocumentName))
        {
            e.Cancel = true;
            return;
        }
        closeConfirmed = true;
    }

    // ----- INcViewerHostUi ------------------------------------------------------------------------

    void INcViewerHostUi.PostToPage(string json)
    {
        if (Browser.CoreWebView2 is { } core) core.PostWebMessageAsJson(json);
    }

    void INcViewerHostUi.PublishModel(string token, byte[] utf8Json)
    {
        models.Add((token, utf8Json));
        while (models.Count > RetainedModels) models.RemoveAt(0);
    }

    string? INcViewerHostUi.ChooseOpenFile(string? initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open NC program",
            Filter = "NC programs|*.nc;*.cnc;*.tap;*.min;*.mpf;*.spf;*.txt|All files|*.*",
            CheckFileExists = true,
            InitialDirectory = initialDirectory ?? NcViewerSession.DefaultProgramFolder()
        }.Localized();
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    string? INcViewerHostUi.ChooseSaveFile(string suggestedName, string? initialDirectory, string title)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = suggestedName,
            Filter = "NC programs|*.nc;*.cnc;*.tap;*.min;*.mpf;*.spf;*.txt|All files|*.*",
            AddExtension = true,
            DefaultExt = ".nc",
            OverwritePrompt = true,
            InitialDirectory = initialDirectory ?? NcViewerSession.DefaultProgramFolder()
        }.Localized();
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    string? INcViewerHostUi.ChooseFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false }.Localized();
        if (!string.IsNullOrWhiteSpace(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    string? INcViewerHostUi.ChooseStlFile(string? initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the stock STL file",
            Filter = "STL files|*.stl|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        }.Localized();
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    string? INcViewerHostUi.ChooseToolTableFile(string? initialDirectory)
    {
        // Same file types as the Release G-code form's tool-table Browse.
        var dialog = new OpenFileDialog
        {
            Title = "Select the exact physical tool table",
            Filter = "Tool tables|*.csv;*.json;*.mht;*.txt|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        }.Localized();
        if (!string.IsNullOrWhiteSpace(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public bool ConfirmDiscardChanges(string documentName) =>
        LocalizedMessageBox.Show(this,
            $"{documentName} has unsaved changes. Discard them?",
            "NC Viewer", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    bool INcViewerHostUi.ConfirmReplaceFile(string path) =>
        LocalizedMessageBox.Show(this,
            $"The revision folder already has a different file with this name:{Environment.NewLine}{path}{Environment.NewLine}Replace it?",
            "NC Viewer", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void UpdateTitle(string documentName, bool dirty) =>
        Title = $"{documentName}{(dirty ? " *" : string.Empty)} - NC Viewer - {request.ContextTitle}";
}
