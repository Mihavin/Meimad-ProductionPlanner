using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Meimad.CimatronExporter;

/// <summary>
/// The one-click exporter window: started by the Cimatron toolbar button, the desktop shortcut (no argument) or the
/// post-processor hook (<c>--from-post</c>, relaunched detached). It reads the active NC document, fills every field from
/// what it remembers, and runs the same export as the console with one press of Export.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class ExporterWindow : Form
{
    private readonly LauncherStart start;
    private readonly string settingsPath;
    private readonly CimatronSession session = new();
    private readonly LauncherSettings settings;
    private CimatronSnapshot? snapshot;
    private string? packagePath;
    private bool geometryChosenByHand;

    private readonly Label document = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly ComboBox toolpath = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly TextBox operation = new() { Width = 120 };
    private readonly ComboBox post = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 420 };
    private readonly ComboBox ucs = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 200 };
    private readonly TextBox caseFolder = new() { Width = 420 };
    private readonly TextBox caseName = new() { Width = 200 };
    private readonly TextBox geometry = new() { Width = 420 };
    private readonly Label target = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Button export = new() { Text = "Export to Meimad", AutoSize = true, Enabled = false };
    private readonly Button refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Button openFolder = new() { Text = "Open package folder", AutoSize = true, Enabled = false };
    private readonly Label status = new() { AutoSize = true };
    private readonly TextBox report = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9f)
    };

    public ExporterWindow(LauncherStart start, string settingsPath)
    {
        this.start = start;
        this.settingsPath = settingsPath;
        settings = LauncherSettings.Load(settingsPath);

        Text = $"Cimatron → Meimad export {typeof(ExporterWindow).Assembly.GetName().Version?.ToString(3)}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Size = new Size(860, 680);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var fields = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void Row(string label, Control control, Control? extra = null)
        {
            fields.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 8, 3) });
            fields.Controls.Add(control);
            fields.Controls.Add(extra ?? new Label { AutoSize = true });
        }

        var browseCase = new Button { Text = "…", Width = 32 };
        var browseGeometry = new Button { Text = "…", Width = 32 };
        Row("NC document", document);
        Row("Toolpath", toolpath);
        Row("Operation", operation);
        Row("Post-processor", post);
        Row("Post relative to UCS", ucs);
        Row("Case working folder", caseFolder, browseCase);
        Row("Case name (part number)", caseName);
        Row("Target / fixture STL", geometry, browseGeometry);
        Row("Package goes to", target);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 0, 10, 6) };
        buttons.Controls.AddRange([export, refresh, openFolder, status]);
        status.Margin = new Padding(12, 8, 3, 3);

        var reportPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 10) };
        reportPanel.Controls.Add(report);
        Controls.Add(reportPanel);
        Controls.Add(buttons);
        Controls.Add(fields);
        AcceptButton = export;

        toolpath.SelectedIndexChanged += (_, _) => ToolpathChanged();
        foreach (var control in new Control[] { operation, post, ucs, caseFolder, caseName })
            control.TextChanged += (_, _) => UpdateTarget();
        geometry.TextChanged += (_, _) => UpdateTarget();
        geometry.KeyPress += (_, _) => geometryChosenByHand = true;
        browseCase.Click += (_, _) => Browse(caseFolder, "Choose the Case working folder (the Case's folder on the share).");
        browseGeometry.Click += (_, _) =>
        {
            if (Browse(geometry, "Choose the folder with the Multi STL export (file names with TARGET/PART and FIXTURE/CLAMP/VISE/JIG).")) geometryChosenByHand = true;
        };
        export.Click += async (_, _) => await ExportAsync();
        refresh.Click += async (_, _) => await ReadAsync(TimeSpan.Zero);
        openFolder.Click += (_, _) =>
        {
            if (packagePath is not null && Directory.Exists(packagePath)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{packagePath}\"") { UseShellExecute = true });
        };
        Shown += async (_, _) =>
        {
            // A start from the post-processor may find Cimatron still posting: keep trying for a minute.
            await ReadAsync(start.FromPost || start.Auto ? TimeSpan.FromSeconds(60) : TimeSpan.Zero);
            if (start.Auto && export.Enabled && LauncherPlan.Options(CurrentForm()).Options is not null) await ExportAsync();
        };
        FormClosed += (_, _) => session.Dispose();

        post.Items.AddRange(settings.Posts.Cast<object>().ToArray());
        post.Text = start.Post ?? settings.Posts.FirstOrDefault() ?? "";
        ucs.Text = start.Ucs ?? settings.Ucs;
        operation.Text = start.Operation ?? "";
        caseFolder.Text = start.CaseFolder ?? settings.LastCaseFolder ?? "";
        caseName.Text = start.CaseName ?? "";
        report.Text = "Reading the active Cimatron document…";
    }

    private async Task ReadAsync(TimeSpan wait)
    {
        SetBusy(true, "Connecting to Cimatron…");
        try
        {
            snapshot = await session.ReadAsync(wait, Environment.GetEnvironmentVariable("MEIMAD_CIMATRON_PROGID"));
            Fill(snapshot);
            report.Text = $"Cimatron {snapshot.CimatronVersion ?? "(version unknown)"}: {snapshot.Document.Name}, {snapshot.Toolpaths.Count} toolpath(s)."
                          + Environment.NewLine + "Check the fields and press Export to Meimad.";
            SetBusy(false, "Ready.");
        }
        catch (Exception exception) when (exception is CimatronApiException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            snapshot = null;
            document.Text = "(no NC document)";
            report.Text = Message(exception) + Environment.NewLine + Environment.NewLine
                          + "Open the NC document in Cimatron, click its window, then press Refresh. Start this tool normally, not with "
                          + "\"Run as administrator\".";
            SetBusy(false, "Cimatron is not connected.");
        }
    }

    private void Fill(CimatronSnapshot read)
    {
        document.Text = read.Document.Name;
        var key = LauncherSettings.DocumentKey(read.Document.FilePath, read.Document.Name);
        var memory = settings.For(key);
        if (start.CaseFolder is null && memory?.CaseFolder is { } folder) caseFolder.Text = folder;
        if (start.CaseName is null) caseName.Text = memory?.CaseName ?? LauncherPlan.GuessCaseName(read.Document.Name) ?? caseName.Text;

        var ucsText = ucs.Text;
        ucs.Items.Clear();
        ucs.Items.AddRange(read.UcsNames.Cast<object>().ToArray());
        ucs.Text = ucsText;

        toolpath.Items.Clear();
        foreach (var item in read.Toolpaths) toolpath.Items.Add(new ToolpathItem(item));
        var wanted = start.Toolpath ?? memory?.Toolpath;
        var index = read.Toolpaths.ToList().FindIndex(item => item.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        toolpath.SelectedIndex = index >= 0 ? index : toolpath.Items.Count > 0 ? 0 : -1;
        if (toolpath.Items.Count == 0) report.Text = "The NC document has no readable toolpath.";
        ToolpathChanged();
    }

    private void ToolpathChanged()
    {
        var name = (toolpath.SelectedItem as ToolpathItem)?.Toolpath.Name;
        var memory = settings.For(LauncherSettings.DocumentKey(snapshot?.Document.FilePath, snapshot?.Document.Name));
        if (name is not null && start.Operation is null && memory?.Operations.TryGetValue(name, out var remembered) == true) operation.Text = remembered;
        if (!geometryChosenByHand) geometry.Text = LauncherPlan.FindGeometryFolder(snapshot?.Document.FilePath, name) ?? "";
        UpdateTarget();
    }

    private LauncherForm CurrentForm() => new(
        snapshot?.Document.Name,
        snapshot?.Document.FilePath,
        caseFolder.Text,
        caseName.Text,
        operation.Text,
        (toolpath.SelectedItem as ToolpathItem)?.Toolpath.Name ?? "",
        post.Text,
        ucs.Text,
        geometry.Text);

    private void UpdateTarget()
    {
        var form = CurrentForm();
        target.Text = string.IsNullOrWhiteSpace(form.CaseFolder) || string.IsNullOrWhiteSpace(form.CaseName) || string.IsNullOrWhiteSpace(form.Operation)
            ? "(fill the Case folder, Case name and Operation)"
            : Path.Combine(LauncherPlan.OutputFolder(form.CaseFolder, form.CaseName, form.Operation), "cimatron-export");
        export.Enabled = snapshot is not null && toolpath.SelectedIndex >= 0;
    }

    private async Task ExportAsync()
    {
        var form = CurrentForm();
        var (options, error) = LauncherPlan.Options(form);
        if (options is null)
        {
            report.Text = error;
            status.Text = "Not exported.";
            return;
        }

        SetBusy(true, "Exporting… Cimatron posts the toolpath again; this takes about half a minute.");
        report.Text = "Exporting " + form.Toolpath + "…";
        try
        {
            Directory.CreateDirectory(options.Output!);
            var result = await session.ExportAsync(options);
            report.Text = result.Log.ToReport().Replace("\n", Environment.NewLine, StringComparison.Ordinal).Replace("\r\r", "\r", StringComparison.Ordinal);
            packagePath = result.PackagePath ?? options.Output;
            if (result.Complete)
            {
                settings.Remember(form, DateTimeOffset.UtcNow);
                settings.Save(settingsPath);
                SetBusy(false, "Exported. Import it in Meimad: Case → Operations → Cimatron setup → Import Cimatron package…");
            }
            else
            {
                SetBusy(false, "Not exported; the report says why.");
            }
        }
        catch (Exception exception) when (exception is CimatronApiException or IOException or UnauthorizedAccessException or InvalidOperationException
                                              or System.Runtime.InteropServices.COMException)
        {
            report.Text = Message(exception);
            SetBusy(false, "Not exported.");
        }

        openFolder.Enabled = packagePath is not null && Directory.Exists(packagePath);
    }

    private void SetBusy(bool busy, string text)
    {
        status.Text = text;
        UseWaitCursor = busy;
        foreach (var control in new Control[] { toolpath, operation, post, ucs, caseFolder, caseName, geometry, refresh }) control.Enabled = !busy;
        export.Enabled = !busy && snapshot is not null && toolpath.SelectedIndex >= 0;
    }

    private bool Browse(TextBox box, string description)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Directory.Exists(box.Text)) dialog.InitialDirectory = box.Text;
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        box.Text = dialog.SelectedPath;
        return true;
    }

    private static string Message(Exception exception) =>
        exception is CimatronApiException cimatron ? $"{cimatron.Code}: {cimatron.Message}" : exception.Message;

    private sealed record ToolpathItem(CimatronToolpath Toolpath)
    {
        public override string ToString() => Toolpath.SetupName is null ? Toolpath.Name : $"{Toolpath.Name}   (setup {Toolpath.SetupName})";
    }
}
