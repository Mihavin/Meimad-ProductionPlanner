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
        ApplyBrowserBackground();
        Themes.WorkbenchTheme.Changed += WorkbenchThemeChanged;
        Closed += (_, _) =>
        {
            Themes.WorkbenchTheme.Changed -= WorkbenchThemeChanged;
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
            var environment = await CoreWebView2Environment.CreateAsync(
                null, userData, new CoreWebView2EnvironmentOptions(BrowserArguments()));
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
        core.NavigationCompleted += (_, _) => ApplyWorkbenchTheme();
        // The theme attribute is present before the first paint, so a Light workshop viewer never
        // flashes the dark chrome while the page loads.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(WorkbenchThemeScript());
        core.Navigate(Origin + PagePath);
    }

    /// <summary>
    /// GPU acceleration for the toolpath view. Chromium refuses WebGL hardware acceleration on
    /// GPU drivers it blocklists and then draws through a slow software rasterizer, which is common
    /// on older shop-floor PCs, so the blocklist is ignored and rasterization runs on the GPU.
    /// MEIMAD_NC_VIEWER_GPU=off forces software rendering if a driver misbehaves. Every NC viewer
    /// window shares this WebView2 user data folder and therefore these arguments.
    /// </summary>
    internal static string BrowserArguments() =>
        string.Equals(Environment.GetEnvironmentVariable("MEIMAD_NC_VIEWER_GPU"), "off", StringComparison.OrdinalIgnoreCase)
            ? "--disable-gpu"
            : "--ignore-gpu-blocklist --enable-gpu-rasterization --enable-zero-copy";

    private void WorkbenchThemeChanged(object? sender, EventArgs e)
    {
        ApplyBrowserBackground();
        ApplyWorkbenchTheme();
    }

    /// <summary>Only these two application-owned constants enter the script; no document text.</summary>
    private static string WorkbenchThemeScript()
    {
        var theme = Themes.WorkbenchTheme.Current == "light" ? "light" : "graphite";
        return $"document.documentElement.dataset.workbenchTheme = '{theme}';";
    }

    /// <summary>The WebView fill behind the page follows the workbench canvas of the current theme.</summary>
    private void ApplyBrowserBackground() =>
        Browser.DefaultBackgroundColor = Themes.WorkbenchTheme.Current == "light"
            ? System.Drawing.Color.FromArgb(0xED, 0xF0, 0xF1)
            : System.Drawing.Color.FromArgb(0x0F, 0x17, 0x20);

    private async void ApplyWorkbenchTheme()
    {
        if (Browser.CoreWebView2 is null) return;
        try
        {
            await Browser.CoreWebView2.ExecuteScriptAsync(WorkbenchThemeScript());
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ObjectDisposedException)
        {
            Trace.WriteLine($"NC viewer theme was not applied: {ex.Message}");
        }
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

        if (uri.AbsolutePath.StartsWith("/__mesh/", StringComparison.Ordinal))
        {
            ServeMesh(e, uri.AbsolutePath["/__mesh/".Length..]);
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

    /// <summary>
    /// A machine, fixture or rest-material model as binary STL. Only paths the session issued a
    /// token for are served; STEP tessellation runs off the UI thread under a request deferral.
    /// </summary>
    private void ServeMesh(CoreWebView2WebResourceRequestedEventArgs e, string token)
    {
        var environment = Browser.CoreWebView2.Environment;
        var path = session.MeshPath(token);
        if (path is null)
        {
            e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
            return;
        }
        var deferral = e.GetDeferral();
        _ = Task.Run(() =>
        {
            try
            {
                return (Bytes: NcViewerMeshes.ReadAsStl(path), Error: (string?)null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or InvalidOperationException or DllNotFoundException or BadImageFormatException
                or TypeInitializationException or SEHException)
            {
                return (Bytes: (byte[]?)null, Error: (string?)exception.Message);
            }
        }).ContinueWith(task =>
        {
            try
            {
                if (Browser.CoreWebView2 is null) return;
                var (bytes, error) = task.Result;
                e.Response = bytes is null
                    ? environment.CreateWebResourceResponse(
                        new MemoryStream(System.Text.Encoding.UTF8.GetBytes(error ?? "The model could not be read.")), 422,
                        "Unprocessable", "Content-Type: text/plain; charset=utf-8")
                    : environment.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), 200, "OK",
                        "Content-Type: model/stl\r\nCache-Control: no-store");
            }
            catch (Exception exception) when (exception is InvalidOperationException or COMException or ObjectDisposedException)
            {
                Trace.WriteLine($"NC viewer model was not served: {exception.Message}");
            }
            finally
            {
                try
                {
                    deferral.Complete();
                }
                catch (Exception exception) when (exception is InvalidOperationException or COMException or ObjectDisposedException)
                {
                    // The window closed while the model was read.
                }
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
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
            Filter = "NC programs|*.nc;*.cnc;*.tap;*.min;*.eia;*.mpf;*.spf;*.txt|All files|*.*",
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
            Filter = "NC programs|*.nc;*.cnc;*.tap;*.min;*.eia;*.mpf;*.spf;*.txt|All files|*.*",
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

    IReadOnlyList<string> INcViewerHostUi.ChooseModelFiles(string? initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import fixtures and rest material exported from Cimatron",
            Filter = "3D models (STL, STEP)|*.stl;*.stp;*.step|All files|*.*",
            CheckFileExists = true,
            Multiselect = true
        }.Localized();
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog(this) == true ? dialog.FileNames : [];
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

    IReadOnlyList<string> INcViewerHostUi.ChooseSubprogramFiles(string? initialDirectory)
    {
        // Same file types as the Release G-code form's "Add subprogram files…".
        var dialog = new OpenFileDialog
        {
            Title = "Add subprogram files",
            Filter = "NC programs|*.nc;*.tap;*.cnc;*.txt;*.gcode;*.iso;*.mpf;*.spf;*.min;*.ngc;*.eia|All files|*.*",
            CheckFileExists = true,
            Multiselect = true
        }.Localized();
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog(this) == true ? dialog.FileNames : [];
    }

    public bool ConfirmDiscardChanges(string documentName) =>
        LocalizedMessageBox.Show(this,
            $"{documentName} has unsaved changes. Discard them?",
            "NC Viewer", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    bool INcViewerHostUi.ConfirmReplaceFile(string path) =>
        LocalizedMessageBox.Show(this,
            $"The revision folder already has a different file with this name:{Environment.NewLine}{path}{Environment.NewLine}Replace it?",
            "NC Viewer", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private MacroVariablesWindow? macroVariables;

    void INcViewerHostUi.ShowMacroVariables(NcViewerMacroVariables variables)
    {
        if (macroVariables is null)
        {
            if (!variables.Open) return;
            macroVariables = new MacroVariablesWindow { Owner = this, Left = Left + Math.Max(0, ActualWidth - 560), Top = Top + 80 };
            macroVariables.Closed += (_, _) =>
            {
                macroVariables = null;
                ((INcViewerHostUi)this).PostToPage("{\"kind\":\"event\",\"channel\":\"meimad:variables-closed\",\"payload\":null}");
            };
            macroVariables.Show();
        }
        else if (variables.Open)
        {
            macroVariables.Activate();
        }
        macroVariables.Display(variables);
    }

    public void UpdateTitle(string documentName, bool dirty) =>
        Title = $"{documentName}{(dirty ? " *" : string.Empty)} - NC Viewer - {request.ContextTitle}";
}
