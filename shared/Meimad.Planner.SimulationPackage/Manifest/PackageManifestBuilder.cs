using Meimad.Planner.SimulationPackage.Geometry;

namespace Meimad.Planner.SimulationPackage.Manifest;

/// <summary>
/// Fills the size and SHA-256 of every file a manifest references from the files in a folder (the
/// exporter's staging folder). Paths stay relative with forward slashes.
/// </summary>
public static class PackageManifestBuilder
{
    public static SimulationPackageManifest WithHashes(SimulationPackageManifest manifest, string folder)
    {
        if (manifest.Files is null) return manifest;
        T Hash<T>(T reference) where T : PackageFileReference
        {
            var relative = reference.Path.Replace('\\', '/');
            var full = Path.Combine(folder, Path.Combine(relative.Split('/')));
            return reference with
            {
                Path = relative,
                Sha256 = PackageHashing.Sha256OfFile(full),
                SizeBytes = new FileInfo(full).Length
            };
        }

        var files = manifest.Files;
        return manifest with
        {
            Files = files with
            {
                Nc = files.Nc is null ? null : Hash(files.Nc),
                ToolTable = files.ToolTable is null ? null : Hash(files.ToolTable),
                ToolReport = files.ToolReport is null ? null : Hash(files.ToolReport),
                Cutters = files.Cutters is null ? null : Hash(files.Cutters),
                Holders = files.Holders is null ? null : Hash(files.Holders),
                Materials = files.Materials is null ? null : Hash(files.Materials),
                JobsXml = files.JobsXml is null ? null : Hash(files.JobsXml),
                CoordinateSystems = files.CoordinateSystems is null ? null : Hash(files.CoordinateSystems),
                Stock = files.Stock?.Select(Hash).ToArray(),
                Fixtures = files.Fixtures?.Select(Hash).ToArray(),
                Part = files.Part is null ? null : Hash(files.Part),
                MachineScene = files.MachineScene?.Select(Hash).ToArray()
            }
        };
    }
}
