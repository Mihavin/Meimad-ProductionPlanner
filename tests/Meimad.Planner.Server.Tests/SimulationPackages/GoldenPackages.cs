using System.Text.Json;
using System.Text.Json.Nodes;
using Meimad.Planner.SimulationPackage.Manifest;

namespace Meimad.Planner.Server.Tests.SimulationPackages;

/// <summary>
/// The golden simulation packages under tests/fixtures/simulation-packages (copied beside the tests).
/// Tests copy a package into a temporary approved root before changing anything.
/// </summary>
internal sealed class GoldenPackages : IDisposable
{
    internal static string Source => Path.Combine(AppContext.BaseDirectory, "simulation-packages");

    internal GoldenPackages()
    {
        Root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.SimulationPackages.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>The temporary approved root.</summary>
    internal string Root { get; }

    /// <summary>Copies a golden package (e.g. "3-axis" or "broken/missing-complete") to the root.</summary>
    internal string Copy(string name, string? targetName = null)
    {
        var from = Path.Combine(Source, name.Replace('/', Path.DirectorySeparatorChar));
        var to = Path.Combine(Root, targetName ?? name.Replace('/', '-'));
        foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        return to;
    }

    /// <summary>Changes the manifest JSON and (optionally) recomputes the file hashes afterwards.</summary>
    internal static void EditManifest(string package, Action<JsonObject> edit, bool rehash = false)
    {
        var path = Path.Combine(package, ManifestJson.ManifestFileName);
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(node);
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (!rehash) return;
        var manifest = ManifestJson.Deserialize(File.ReadAllText(path));
        File.WriteAllText(path, ManifestJson.Serialize(PackageManifestBuilder.WithHashes(manifest, package)));
    }

    internal static JsonDocument Expected() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Source, "expected-bounds.json")));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
    }
}
