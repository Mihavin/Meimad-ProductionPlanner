using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meimad.Planner.Client.Windows.Presentation.NcViewer;

/// <summary>
/// Per-user NC viewer preferences (the upstream desktop viewer's settings minus the AI/Codex
/// ones). They only change how this PC previews programs; nothing here reaches the Server.
/// </summary>
internal sealed class NcViewerSettings
{
    internal const string DefaultInitialVariables = "#500=0,#505=0";
    internal const int MaximumTextLength = 4096;

    public string Machine { get; set; } = "auto";
    public double? G30X { get; set; }
    public double? G30Z { get; set; }
    public string InitialVariables { get; set; } = DefaultInitialVariables;
    public string MachineParametersFile { get; set; } = string.Empty;
    public string ToolTableFile { get; set; } = string.Empty;
    public Dictionary<string, string> ProgramMemory { get; set; } = new(StringComparer.Ordinal);
    public JsonElement? WorkOffsets { get; set; }

    internal NcViewerSettings Clone() => new()
    {
        Machine = Machine,
        G30X = G30X,
        G30Z = G30Z,
        InitialVariables = InitialVariables,
        MachineParametersFile = MachineParametersFile,
        ToolTableFile = ToolTableFile,
        ProgramMemory = new Dictionary<string, string>(ProgramMemory, StringComparer.Ordinal),
        WorkOffsets = WorkOffsets?.Clone()
    };
}

internal sealed class NcViewerSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly object Gate = new();

    internal NcViewerSettingsStore(string? path = null)
    {
        SettingsPath = path ?? Path.Combine(DataFolder, "settings.json");
    }

    internal static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Meimad Planner",
        "nc-viewer");

    internal string SettingsPath { get; }

    internal NcViewerSettings Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new NcViewerSettings();
                return JsonSerializer.Deserialize<NcViewerSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                    ?? new NcViewerSettings();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A damaged preferences file must not stop the viewer; defaults are used.
                return new NcViewerSettings();
            }
        }
    }

    internal void Save(NcViewerSettings settings)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
    }
}
