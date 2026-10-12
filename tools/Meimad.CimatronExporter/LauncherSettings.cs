using System.Text.Json;

namespace Meimad.CimatronExporter;

/// <summary>What the window remembers for one Cimatron NC document.</summary>
internal sealed class DocumentMemory
{
    public string? CaseFolder { get; set; }
    public string? CaseName { get; set; }
    public string? Toolpath { get; set; }

    /// <summary>Toolpath name → Operation number.</summary>
    public Dictionary<string, string> Operations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset LastUsedUtc { get; set; }
}

/// <summary>
/// The exporter window's memory, per Windows user (<c>%APPDATA%\Meimad\CimatronExporter\settings.json</c>): the posts used
/// most recently, the UCS, the last Case folder and, per NC document, its Case and each toolpath's Operation. It only fills
/// the window; a missing or unreadable file starts empty.
/// </summary>
internal sealed class LauncherSettings
{
    private const int MaxPosts = 10;
    private const int MaxDocuments = 200;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<string> Posts { get; set; } = [];
    public string Ucs { get; set; } = "MODEL";
    public string? LastCaseFolder { get; set; }
    public Dictionary<string, DocumentMemory> Documents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Meimad", "CimatronExporter", "settings.json");

    public static LauncherSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new LauncherSettings();
            var settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), Json) ?? new LauncherSettings();
            // Keys compare without case whatever the file says.
            settings.Documents = new Dictionary<string, DocumentMemory>(settings.Documents ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var memory in settings.Documents.Values)
                memory.Operations = new Dictionary<string, string>(memory.Operations ?? [], StringComparer.OrdinalIgnoreCase);
            settings.Posts ??= [];
            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new LauncherSettings();
        }
    }

    public bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public DocumentMemory? For(string? documentKey) =>
        documentKey is not null && Documents.TryGetValue(documentKey, out var memory) ? memory : null;

    public static string? DocumentKey(string? documentPath, string? documentName) =>
        !string.IsNullOrWhiteSpace(documentPath) ? documentPath.Trim() : string.IsNullOrWhiteSpace(documentName) ? null : documentName.Trim();

    /// <summary>Remembers a form that was exported.</summary>
    public void Remember(LauncherForm form, DateTimeOffset now)
    {
        var post = form.Post.Trim();
        Posts.RemoveAll(item => item.Equals(post, StringComparison.OrdinalIgnoreCase));
        Posts.Insert(0, post);
        if (Posts.Count > MaxPosts) Posts.RemoveRange(MaxPosts, Posts.Count - MaxPosts);
        Ucs = form.Ucs.Trim();
        LastCaseFolder = form.CaseFolder.Trim();

        var key = DocumentKey(form.DocumentPath, form.DocumentName);
        if (key is null) return;
        if (!Documents.TryGetValue(key, out var memory)) Documents[key] = memory = new DocumentMemory();
        memory.CaseFolder = form.CaseFolder.Trim();
        memory.CaseName = form.CaseName.Trim();
        memory.Toolpath = form.Toolpath.Trim();
        memory.Operations[form.Toolpath.Trim()] = form.Operation.Trim();
        memory.LastUsedUtc = now;
        foreach (var old in Documents.OrderByDescending(pair => pair.Value.LastUsedUtc).Skip(MaxDocuments).Select(pair => pair.Key).ToArray())
            Documents.Remove(old);
    }
}
