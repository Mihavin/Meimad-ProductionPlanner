using System.Text.Json;

namespace Meimad.Planner.NcEngine;

/// <summary>One NC viewer machine definition (vendored or Meimad) as listed for selection.</summary>
public sealed record NcEngineMachineDefinition(
    string Id,
    string Name,
    string Type,
    string Control,
    string? Translation,
    bool BuiltIn);

/// <summary>
/// The machine definitions the NC engine can be asked for, read from the installed
/// <c>nc-engine\machines</c> (vendored) and <c>nc-engine\meimad\machines</c> (Meimad) folders
/// without starting V8. The Server uses it to validate a Machine's <c>ncViewerMachine</c>; the
/// Windows client uses it to fill the Setup list. Both run the same installed engine version, so
/// the lists agree.
/// </summary>
public sealed class NcEngineMachineCatalog
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly Dictionary<string, NcEngineMachineDefinition> machines;

    private NcEngineMachineCatalog(IEnumerable<NcEngineMachineDefinition> definitions)
    {
        machines = new Dictionary<string, NcEngineMachineDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            // A Meimad definition with a vendored id replaces it, as the engine registry does.
            machines[definition.Id] = definition;
        }
        Machines = machines.Values.OrderBy(machine => machine.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Machines sorted by name.</summary>
    public IReadOnlyList<NcEngineMachineDefinition> Machines { get; }

    public bool Contains(string? id) => id is not null && machines.ContainsKey(id);

    public NcEngineMachineDefinition? Find(string? id) =>
        id is not null && machines.TryGetValue(id, out var machine) ? machine : null;

    /// <summary>Reads the installed definitions; a missing engine folder gives an empty catalog.</summary>
    public static NcEngineMachineCatalog Load(string? engineRoot = null)
    {
        var root = Path.GetFullPath(engineRoot ?? NcEngineRuntime.DefaultEngineRoot);
        var controls = new Dictionary<string, (string Name, string? Translation)>(StringComparer.Ordinal);
        foreach (var folder in new[] { Path.Combine(root, "controls"), Path.Combine(root, "meimad", "controls") })
        {
            foreach (var (element, _) in Documents(folder))
            {
                var id = Text(element, "id");
                if (id is null) continue;
                controls[id] = (Text(element, "name") ?? id, Text(element, "translation"));
            }
        }

        var definitions = new List<NcEngineMachineDefinition>();
        foreach (var (folder, builtIn) in new[] { (Path.Combine(root, "machines"), true), (Path.Combine(root, "meimad", "machines"), false) })
        {
            foreach (var (element, _) in Documents(folder))
            {
                var id = Text(element, "id");
                var controlId = Text(element, "control");
                if (id is null || controlId is null || !IsIdentifier(id)) continue;
                var control = controls.GetValueOrDefault(controlId, (controlId, null));
                definitions.Add(new NcEngineMachineDefinition(
                    id,
                    Text(element, "name") ?? id,
                    Text(element, "type") ?? "mill",
                    control.Name,
                    control.Translation,
                    builtIn));
            }
        }
        return new NcEngineMachineCatalog(definitions);
    }

    /// <summary>Lower-case letters, digits and hyphens, as the engine registry requires.</summary>
    public static bool IsIdentifier(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 100
        && (char.IsAsciiLetterLower(value[0]) || char.IsAsciiDigit(value[0]))
        && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');

    private static IEnumerable<(JsonElement Element, string Path)> Documents(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllText(file), DocumentOptions);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }
            using (document)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    yield return (document.RootElement.Clone(), file);
            }
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
