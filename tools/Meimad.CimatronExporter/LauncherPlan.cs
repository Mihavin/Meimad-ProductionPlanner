using System.Text.RegularExpressions;

namespace Meimad.CimatronExporter;

/// <summary>What the exporter window holds when the user presses Export.</summary>
internal sealed record LauncherForm(
    string? DocumentName,
    string? DocumentPath,
    string CaseFolder,
    string CaseName,
    string Operation,
    string Toolpath,
    string Post,
    string Ucs,
    string? GeometryFolder);

/// <summary>
/// How the exporter was started for the window. <c>--ui</c> (or no argument) opens the window; <c>--from-post</c> is the
/// Cimatron post-processor hook, which only relaunches the window detached; <c>--auto</c> exports as soon as every field is
/// known. The other values preset the window.
/// </summary>
internal sealed record LauncherStart(
    bool FromPost,
    bool Auto,
    string? Toolpath,
    string? Post,
    string? Ucs,
    string? Operation,
    string? CaseFolder,
    string? CaseName);

/// <summary>
/// The one-click launcher's plain rules (no Cimatron, no window): which arguments open the window, where a package goes,
/// where the target/fixture STL folder is looked for, and the exporter command line the window runs.
/// </summary>
internal static partial class LauncherPlan
{
    /// <summary>The folder beside the NC document that holds the Multi STL export of the target part and fixtures.</summary>
    public const string GeometryFolderName = "Meimad geometry";

    /// <summary>The window's start, or null when the arguments are an ordinary exporter command line.</summary>
    public static LauncherStart? ParseStart(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new LauncherStart(false, false, null, null, null, null, null, null);
        var ui = args.Any(arg => arg.Equals("--ui", StringComparison.OrdinalIgnoreCase));
        var fromPost = args.Any(arg => arg.Equals("--from-post", StringComparison.OrdinalIgnoreCase));
        if (!ui && !fromPost) return null;

        string? toolpath = null, post = null, ucs = null, operation = null, caseFolder = null, caseName = null;
        var auto = false;
        for (var index = 0; index < args.Count; index++)
        {
            string? Next() => index + 1 < args.Count ? args[++index] : null;
            switch (args[index].ToLowerInvariant())
            {
                case "--toolpath": toolpath = Next(); break;
                case "--post": post = Next(); break;
                case "--ucs": ucs = Next(); break;
                case "--operation": operation = Next(); break;
                case "--case-folder": caseFolder = Next(); break;
                case "--case-name": caseName = Next(); break;
                case "--auto": auto = true; break;
            }
        }

        return new LauncherStart(fromPost, auto, Blank(toolpath), Blank(post), Blank(ucs), Blank(operation), Blank(caseFolder), Blank(caseName));
    }

    /// <summary>The detached window the post hook starts: the same presets, never <c>--from-post</c> again.</summary>
    public static IReadOnlyList<string> RelaunchArguments(LauncherStart start)
    {
        var args = new List<string> { "--ui" };
        void Add(string name, string? value)
        {
            if (value is null) return;
            args.Add(name);
            args.Add(value);
        }

        Add("--toolpath", start.Toolpath);
        Add("--post", start.Post);
        Add("--ucs", start.Ucs);
        Add("--operation", start.Operation);
        Add("--case-folder", start.CaseFolder);
        Add("--case-name", start.CaseName);
        if (start.Auto) args.Add("--auto");
        return args;
    }

    /// <summary>
    /// The Case name a document name starts with: Meimad part numbers such as "30P450172200-002" in
    /// "30P450172200-002SOFI_NC1 - Copy", else the text before the first '_' or space.
    /// </summary>
    public static string? GuessCaseName(string? documentName)
    {
        if (string.IsNullOrWhiteSpace(documentName)) return null;
        var name = Path.GetFileNameWithoutExtension(documentName.Trim());
        if (PartNumber().Match(name) is { Success: true } match) return match.Value;
        var end = name.IndexOfAny(['_', ' ']);
        return Blank(end > 0 ? name[..end] : name);
    }

    /// <summary>The Operation's package folder: &lt;Case working folder&gt;\Gcode\&lt;Case name&gt;\&lt;Operation&gt; (as the BAT launcher).</summary>
    public static string OutputFolder(string caseFolder, string caseName, string operation) =>
        Path.Combine(caseFolder.Trim(), "Gcode", Safe(caseName), Safe(operation));

    /// <summary>
    /// The target/fixture STL folder for a toolpath: "Meimad geometry\&lt;toolpath&gt;" beside the NC document, else
    /// "Meimad geometry" itself when it holds STL files; null when neither exists.
    /// </summary>
    public static string? FindGeometryFolder(string? documentPath, string? toolpath)
    {
        if (string.IsNullOrWhiteSpace(documentPath)) return null;
        var folder = Directory.Exists(documentPath) ? documentPath : Path.GetDirectoryName(documentPath);
        if (folder is null) return null;
        var root = Path.Combine(folder, GeometryFolderName);
        if (!string.IsNullOrWhiteSpace(toolpath) && HasStl(Path.Combine(root, Safe(toolpath)))) return Path.Combine(root, Safe(toolpath));
        return HasStl(root) ? root : null;
    }

    /// <summary>The exporter command line for the window's fields, validated by the same parser as the console.</summary>
    public static (ExportOptions? Options, string? Error) Options(LauncherForm form)
    {
        if (string.IsNullOrWhiteSpace(form.Toolpath)) return (null, "Choose the toolpath.");
        if (string.IsNullOrWhiteSpace(form.Operation)) return (null, "Enter the Operation number (for example 010).");
        if (string.IsNullOrWhiteSpace(form.CaseName)) return (null, "Enter the Case name (the part number).");
        if (string.IsNullOrWhiteSpace(form.CaseFolder) || !Path.IsPathFullyQualified(form.CaseFolder.Trim()))
            return (null, "Choose the Case working folder (a full path, for example \\\\server\\data\\customers files\\…).");
        if (string.IsNullOrWhiteSpace(form.Post)) return (null, "Enter the post-processor.");
        if (string.IsNullOrWhiteSpace(form.Ucs)) return (null, "Enter the UCS the NC is posted to (MODEL for part-model coordinates).");
        return ExportOptions.Parse(Arguments(form));
    }

    public static IReadOnlyList<string> Arguments(LauncherForm form)
    {
        var args = new List<string>
        {
            "--active-cimatron",
            "--output", OutputFolder(form.CaseFolder, form.CaseName, form.Operation),
            "--post", form.Post.Trim(),
            "--ucs", form.Ucs.Trim(),
            "--toolpath", form.Toolpath.Trim(),
            "--operation", form.Operation.Trim(),
            "--part-number", form.CaseName.Trim()
        };
        if (!string.IsNullOrWhiteSpace(form.GeometryFolder))
        {
            args.Add("--geometry-folder");
            args.Add(form.GeometryFolder.Trim());
        }

        return args;
    }

    private static bool HasStl(string folder) =>
        Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.stl", SearchOption.TopDirectoryOnly).Any();

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim('.', ' ');
        return safe.Length == 0 ? "_" : safe;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^[0-9][0-9A-Z]*-\d{3}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PartNumber();
}
