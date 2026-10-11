using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Localization;
using Meimad.Planner.Client.Windows.Presentation.MachineDesigner;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>
/// The Machine Designer: the component tree and inspector in WPF, the machine drawn in WebView2 from the
/// installed NcViewer folder (machine-designer.html) under a private https origin. The page receives the
/// definition as web messages and may load only the mesh files this window issued tokens for; it has no host
/// objects, file or network access.
/// </summary>
public partial class MachineDesignerWindow : Window, IMachineDesignerHost
{
    private const string Origin = "https://machinedesigner.meimad.local";
    private const string PagePath = "/desktop/machine-designer.html";
    private static readonly string AssetRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "NcViewer"))
        + Path.DirectorySeparatorChar;

    private readonly MachineDesignerViewModel viewModel;
    private readonly Dictionary<string, string> meshPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> meshTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer sceneTimer;
    private bool pageReady;
    private bool closeConfirmed;

    internal MachineDesignerWindow(IPlannerApiClient api, string clientId, PlannerMachine machine, bool canEdit)
    {
        InitializeComponent();
        viewModel = new MachineDesignerViewModel(api, clientId, machine, canEdit, this);
        DataContext = viewModel;
        sceneTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        sceneTimer.Tick += (_, _) =>
        {
            sceneTimer.Stop();
            Post(viewModel.SceneJson());
        };
        viewModel.SceneChanged += (_, _) => { sceneTimer.Stop(); sceneTimer.Start(); };
        viewModel.PoseChanged += (_, _) => Post(viewModel.PoseJson());
        Loaded += async (_, _) =>
        {
            await InitializeBrowserAsync();
            await viewModel.LoadAsync();
        };
        Closing += OnClosing;
        Themes.WorkbenchTheme.Changed += WorkbenchThemeChanged;
        LocalizationService.Current.LanguageChanged += LanguageChanged;
        Closed += (_, _) =>
        {
            Themes.WorkbenchTheme.Changed -= WorkbenchThemeChanged;
            LocalizationService.Current.LanguageChanged -= LanguageChanged;
            sceneTimer.Stop();
            Browser.Dispose();
        };
    }

    /// <summary>Opens a Machine's designer; <paramref name="canEdit"/> false shows it read-only.</summary>
    internal static MachineDesignerWindow Open(IPlannerApiClient api, string clientId, PlannerMachine machine, bool canEdit, Window? owner)
    {
        var window = new MachineDesignerWindow(api, clientId, machine, canEdit) { Owner = owner };
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
                null, userData, new CoreWebView2EnvironmentOptions(NcViewerWindow.BrowserArguments()));
            await Browser.EnsureCoreWebView2Async(environment);
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or COMException
            or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Browser.Visibility = Visibility.Collapsed;
            FallbackMessage.Visibility = Visibility.Visible;
            FallbackMessage.Text = exception is WebView2RuntimeNotFoundException
                ? "The Microsoft Edge WebView2 Runtime is not installed on this PC, so the 3D view cannot start. The definition can still be edited."
                : $"The 3D view could not start ({exception.Message}). The definition can still be edited.";
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
            HandleMessage(e.WebMessageAsJson);
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ThemeScript());
        core.Navigate(Origin + PagePath);
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)) return;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            switch (type.GetString())
            {
                case "ready":
                    pageReady = true;
                    PostLabels();
                    Post(viewModel.SceneJson());
                    break;
                case "select":
                    viewModel.SelectFromPage(Text("id"));
                    break;
                case "meshError":
                    viewModel.ReportMeshProblem(Text("node"), Text("mesh"), Text("message"));
                    break;
            }
        }
        catch (JsonException)
        {
            // Not a message of the page.
        }
    }

    /// <summary>The page's interface texts, translated with the client catalog (the page is outside the WPF tree).</summary>
    internal static readonly IReadOnlyList<string> PageTexts =
    [
        "Machine collisions not checked.", "Iso", "Front", "Right", "Top", "Fit", "Meshes", "Boxes",
        "Isometric view", "View along +Y", "View along −X", "View along −Z", "Fit the machine in the view (F)",
        "Click a component to select it · drag to rotate · right-drag to pan · wheel to zoom · ▲ tool mount · XYZ workpiece mount",
        "mesh not drawn", "No axes", "Unverified: positions are not yet checked on the real machine."
    ];

    private void PostLabels()
    {
        var service = LocalizationService.Current;
        Post(JsonSerializer.Serialize(new
        {
            type = "labels",
            items = PageTexts.Distinct().ToDictionary(text => text, text => service.Translate(text))
        }));
    }

    private void Post(string json)
    {
        if (!pageReady || Browser.CoreWebView2 is null) return;
        try
        {
            Browser.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException or ObjectDisposedException)
        {
            Trace.WriteLine($"Machine designer message was not delivered: {exception.Message}");
        }
    }

    // ----- served files -------------------------------------------------------------------------

    string IMachineDesignerHost.MeshUrl(string path)
    {
        if (!meshTokens.TryGetValue(path, out var token))
        {
            token = Guid.NewGuid().ToString("N");
            meshTokens[path] = token;
            meshPaths[token] = path;
        }
        return Origin + "/__mesh/" + token;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var environment = Browser.CoreWebView2.Environment;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri))
        {
            e.Response = environment.CreateWebResourceResponse(null, 400, "Bad Request", string.Empty);
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
        var contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            _ => "application/octet-stream"
        };
        e.Response = environment.CreateWebResourceResponse(
            new MemoryStream(File.ReadAllBytes(path), writable: false), 200, "OK", $"Content-Type: {contentType}\r\nCache-Control: no-cache");
    }

    /// <summary>A mesh as binary STL (GLB converted, STEP tessellated) off the UI thread; only issued tokens are served.</summary>
    private void ServeMesh(CoreWebView2WebResourceRequestedEventArgs e, string token)
    {
        var environment = Browser.CoreWebView2.Environment;
        if (!meshPaths.TryGetValue(token, out var path))
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                or InvalidOperationException or JsonException or DllNotFoundException or BadImageFormatException
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
                        new MemoryStream(System.Text.Encoding.UTF8.GetBytes(error ?? "The mesh could not be read.")), 422,
                        "Unprocessable", "Content-Type: text/plain; charset=utf-8")
                    : environment.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), 200, "OK",
                        "Content-Type: model/stl\r\nCache-Control: no-store");
            }
            catch (Exception exception) when (exception is InvalidOperationException or COMException or ObjectDisposedException)
            {
                Trace.WriteLine($"Machine designer mesh was not served: {exception.Message}");
            }
            finally
            {
                try
                {
                    deferral.Complete();
                }
                catch (Exception exception) when (exception is InvalidOperationException or COMException or ObjectDisposedException)
                {
                    // The window closed while the mesh was read.
                }
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ----- host -----------------------------------------------------------------------------------

    IReadOnlyList<string> IMachineDesignerHost.ChooseMeshFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import machine meshes",
            Filter = "3D meshes (GLB, STL, STEP)|*.glb;*.stl;*.stp;*.step|All files|*.*",
            CheckFileExists = true,
            Multiselect = true
        }.Localized();
        return dialog.ShowDialog(this) == true ? dialog.FileNames : [];
    }

    bool IMachineDesignerHost.Confirm(string message, string title) =>
        LocalizedMessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel)
            == MessageBoxResult.OK;

    private void NodeTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is DesignerNode node) viewModel.SelectedNode = node;
    }

    private void IssueList_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        viewModel.SelectIssue(IssueList.SelectedItem as MachineSimulationIssueDto);

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (closeConfirmed || !viewModel.IsDirty || !viewModel.CanEdit) return;
        if (!((IMachineDesignerHost)this).Confirm("Close the Machine Designer without saving your changes?", "Unsaved changes"))
        {
            e.Cancel = true;
            return;
        }
        closeConfirmed = true;
    }

    private void LanguageChanged(object? sender, EventArgs e) => PostLabels();

    private void WorkbenchThemeChanged(object? sender, EventArgs e)
    {
        if (Browser.CoreWebView2 is null) return;
        _ = Browser.CoreWebView2.ExecuteScriptAsync(ThemeScript());
        Post("{\"type\":\"theme\"}");
    }

    /// <summary>Only these two application-owned constants enter the script.</summary>
    private static string ThemeScript()
    {
        var theme = Themes.WorkbenchTheme.Current == "light" ? "light" : "graphite";
        return $"document.documentElement.dataset.workbenchTheme = '{theme}';";
    }
}
