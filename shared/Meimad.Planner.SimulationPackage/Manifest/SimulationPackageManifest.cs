using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meimad.Planner.SimulationPackage.Manifest;

/// <summary>
/// manifest.json of a Meimad simulation package, schemaVersion 1 (Cimatron exporter definition §8).
/// The JSON contract is <c>Schemas/simulation-package.schema.json</c>; property names are camelCase.
/// </summary>
public sealed record SimulationPackageManifest
{
    public int SchemaVersion { get; init; }
    public string? PackageId { get; init; }
    public ExporterInfo? Exporter { get; init; }
    public SourceInfo? Source { get; init; }

    /// <summary>The Meimad import this package was made for (Case, Operation, the data version it was based on).</summary>
    public MeimadImportContext? Meimad { get; init; }
    public PartInfo? Part { get; init; }
    public OperationInfo? Operation { get; init; }
    public MachineInfo? Machine { get; init; }
    public string? Units { get; init; }
    public CoordinateSystemDefinition? CoordinateSystem { get; init; }
    public PackageFiles? Files { get; init; }

    /// <summary>Every file the manifest references, flattened with its role.</summary>
    public IEnumerable<PackageFileEntry> EnumerateFiles()
    {
        if (Files is null) yield break;
        if (Files.Nc is { } nc) yield return new(PackageFileRole.Nc, nc.Path, nc.Sha256, nc.SizeBytes);
        if (Files.ToolTable is { } toolTable) yield return new(PackageFileRole.ToolTable, toolTable.Path, toolTable.Sha256, toolTable.SizeBytes);
        if (Files.ToolReport is { } toolReport) yield return new(PackageFileRole.ToolReport, toolReport.Path, toolReport.Sha256, toolReport.SizeBytes);
        if (Files.Cutters is { } cutters) yield return new(PackageFileRole.Cutters, cutters.Path, cutters.Sha256, cutters.SizeBytes);
        if (Files.Holders is { } holders) yield return new(PackageFileRole.Holders, holders.Path, holders.Sha256, holders.SizeBytes);
        if (Files.Materials is { } materials) yield return new(PackageFileRole.Materials, materials.Path, materials.Sha256, materials.SizeBytes);
        if (Files.JobsXml is { } jobs) yield return new(PackageFileRole.JobsXml, jobs.Path, jobs.Sha256, jobs.SizeBytes);
        if (Files.CoordinateSystems is { } systems) yield return new(PackageFileRole.CoordinateSystems, systems.Path, systems.Sha256, systems.SizeBytes);
        foreach (var stock in Files.Stock ?? [])
        {
            yield return new(PackageFileRoles.ForStockKind(stock.Kind), stock.Path, stock.Sha256, stock.SizeBytes,
                StockKind: stock.Kind, SourceOperation: stock.SourceOperation, Name: stock.Name, FoundBy: stock.FoundBy);
        }

        foreach (var fixture in Files.Fixtures ?? [])
        {
            yield return new(PackageFileRole.Fixture, fixture.Path, fixture.Sha256, fixture.SizeBytes,
                Name: fixture.Name, Collision: fixture.Collision ?? true);
        }

        if (Files.Part is { } part) yield return new(PackageFileRole.Part, part.Path, part.Sha256, part.SizeBytes, Name: part.Name);
        foreach (var scene in Files.MachineScene ?? [])
            yield return new(PackageFileRole.MachineScene, scene.Path, scene.Sha256, scene.SizeBytes);
    }
}

public sealed record ExporterInfo
{
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? CimatronVersion { get; init; }
    public string? AdapterVersion { get; init; }
    public double? MeshToleranceMm { get; init; }
    public bool? UsedDefaultMeshTolerance { get; init; }
}

public sealed record SourceInfo
{
    public string? Application { get; init; }
    public string? Project { get; init; }
    public string? NcFileName { get; init; }
    public string? ExportedAtUtc { get; init; }
    public string? Host { get; init; }
    public string? User { get; init; }

    /// <summary>The CAM document's file path as Cimatron reports it.</summary>
    public string? DocumentPath { get; init; }

    /// <summary>Cimatron's document identity (ICimDocument.PID), when it reports one.</summary>
    public string? DocumentId { get; init; }

    /// <summary>The Cimatron setup the selected CAM operations belong to.</summary>
    public string? Setup { get; init; }

    /// <summary>The CAM operations the package was made from, by Cimatron id (names are labels only).</summary>
    public IReadOnlyList<CamOperationReference>? Operations { get; init; }
}

/// <summary>One selected CAM operation: a whole toolpath, or one procedure of a toolpath.</summary>
public sealed record CamOperationReference
{
    /// <summary><c>toolpath</c> or <c>procedure</c>.</summary>
    public string Kind { get; init; } = "procedure";
    public int Id { get; init; }
    public string? Name { get; init; }
    public string? Toolpath { get; init; }
}

/// <summary>The Meimad side of an import started from a Case Operation.</summary>
public sealed record MeimadImportContext
{
    public string? ImportId { get; init; }
    public string? CaseId { get; init; }
    public string? CaseOperationId { get; init; }
    public string? BasedOnVersion { get; init; }
}

public sealed record PartInfo
{
    public string? Number { get; init; }
    public string? Name { get; init; }
}

public sealed record OperationInfo
{
    public string? Number { get; init; }
    public string? Name { get; init; }
    public int? ProcedureId { get; init; }
    public string? ProcedureName { get; init; }
    public IReadOnlyList<int>? ProcedureIds { get; init; }
}

public sealed record MachineInfo
{
    public string? ExternalId { get; init; }
    public string? Name { get; init; }
    public string? Postprocessor { get; init; }
    public string? AxisConfiguration { get; init; }
}

/// <summary>An X/Y/Z triple in the manifest (millimetres or degrees by context).</summary>
public sealed record ManifestVector(double X, double Y, double Z);

/// <summary>
/// The package coordinate system (exporter definition §5): mode A <c>nc-procedure-ucs</c> (files already
/// in NC work coordinates, identity) or mode B <c>part-model-coordinates</c> (files in part-model
/// coordinates, the UCS frame given by origin + rotation + order and/or a row-major 4×4 matrix).
/// </summary>
public sealed record CoordinateSystemDefinition
{
    public string? Type { get; init; }
    public string? Name { get; init; }
    public string? WorkOffset { get; init; }
    public string? FilesAreIn { get; init; }
    public string? Transform { get; init; }
    public ManifestVector? OriginMm { get; init; }
    public ManifestVector? RotationDeg { get; init; }
    public string? RotationOrder { get; init; }
    public IReadOnlyList<double>? Matrix { get; init; }
    public IReadOnlyList<int>? Procedures { get; init; }
}

public static class CoordinateSystemTypes
{
    /// <summary>Mode A: geometry already in NC procedure UCS / work coordinates.</summary>
    public const string NcProcedureUcs = "nc-procedure-ucs";

    /// <summary>Mode B: geometry in Cimatron part-model coordinates plus the UCS transformation.</summary>
    public const string PartModelCoordinates = "part-model-coordinates";

    public const string FilesInNcWorkCoordinates = "nc-work-coordinates";
    public const string FilesInPartModelCoordinates = "part-model-coordinates";
    public const string IdentityTransform = "identity";
}

public record PackageFileReference
{
    public string Path { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long? SizeBytes { get; init; }
}

public sealed record StockFileReference : PackageFileReference
{
    public string? Kind { get; init; }
    public string? SourceOperation { get; init; }
    public string? Name { get; init; }
    public string? FoundBy { get; init; }
}

public sealed record FixtureFileReference : PackageFileReference
{
    public string? Name { get; init; }
    public bool? Collision { get; init; }
}

public sealed record PartFileReference : PackageFileReference
{
    public string? Role { get; init; }
    public string? Name { get; init; }
}

public sealed record PackageFiles
{
    public PackageFileReference? Nc { get; init; }
    public PackageFileReference? ToolTable { get; init; }
    public PackageFileReference? ToolReport { get; init; }
    public PackageFileReference? Cutters { get; init; }
    public PackageFileReference? Holders { get; init; }
    public PackageFileReference? Materials { get; init; }
    public PackageFileReference? JobsXml { get; init; }
    public PackageFileReference? CoordinateSystems { get; init; }
    public IReadOnlyList<StockFileReference>? Stock { get; init; }
    public IReadOnlyList<FixtureFileReference>? Fixtures { get; init; }
    public PartFileReference? Part { get; init; }
    public IReadOnlyList<PackageFileReference>? MachineScene { get; init; }
}

/// <summary>
/// File roles of a package. The geometry/program roles are the Stage 0 enum
/// (<c>nc</c>, <c>initialStock</c>, <c>remainingStock</c>, <c>fixture</c>, <c>part</c>, <c>machineScene</c>)
/// plus <c>machinedStock</c> for a simulation result; the data roles carry the tool and job exports.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PackageFileRole>))]
public enum PackageFileRole
{
    [JsonStringEnumMemberName("nc")] Nc,
    [JsonStringEnumMemberName("initialStock")] InitialStock,
    [JsonStringEnumMemberName("remainingStock")] RemainingStock,
    [JsonStringEnumMemberName("machinedStock")] MachinedStock,
    [JsonStringEnumMemberName("fixture")] Fixture,
    [JsonStringEnumMemberName("part")] Part,
    [JsonStringEnumMemberName("machineScene")] MachineScene,
    [JsonStringEnumMemberName("toolTable")] ToolTable,
    [JsonStringEnumMemberName("toolReport")] ToolReport,
    [JsonStringEnumMemberName("cutters")] Cutters,
    [JsonStringEnumMemberName("holders")] Holders,
    [JsonStringEnumMemberName("materials")] Materials,
    [JsonStringEnumMemberName("jobsXml")] JobsXml,
    [JsonStringEnumMemberName("coordinateSystems")] CoordinateSystems,
    /// <summary>A stock file whose kind is not one of initial/remaining/machined.</summary>
    [JsonStringEnumMemberName("unknownStock")] UnknownStock
}

public static class PackageFileRoles
{
    public static PackageFileRole ForStockKind(string? kind) => kind switch
    {
        StockKinds.Initial => PackageFileRole.InitialStock,
        StockKinds.Remaining => PackageFileRole.RemainingStock,
        StockKinds.Machined => PackageFileRole.MachinedStock,
        _ => PackageFileRole.UnknownStock
    };

    public static string Name(PackageFileRole role) => JsonSerializer.Serialize(role, ManifestJson.Options).Trim('"');

    public static bool IsStock(PackageFileRole role) =>
        role is PackageFileRole.InitialStock or PackageFileRole.RemainingStock
            or PackageFileRole.MachinedStock or PackageFileRole.UnknownStock;

    public static bool IsGeometry(PackageFileRole role) =>
        IsStock(role) || role is PackageFileRole.Fixture or PackageFileRole.Part or PackageFileRole.MachineScene;
}

/// <summary>Stock kinds (exporter definition §9).</summary>
public static class StockKinds
{
    /// <summary>The initial blank of this Operation.</summary>
    public const string Initial = "initial";

    /// <summary>Rest material after the previous Operation (<c>sourceOperation</c> names it).</summary>
    public const string Remaining = "remaining";

    /// <summary>The result of a simulation of this Operation.</summary>
    public const string Machined = "machined";

    public static readonly IReadOnlyList<string> All = [Initial, Remaining, Machined];
}

/// <summary>Units of the package files. Meimad works in millimetres internally.</summary>
public static class PackageUnits
{
    public const string Millimetres = "mm";
    public const string Inches = "inch";

    /// <summary>The factor that converts the unit into millimetres; null when the unit is unknown.</summary>
    public static double? ToMillimetres(string? units) => units?.Trim().ToLowerInvariant() switch
    {
        "mm" => 1.0,
        "inch" or "in" => 25.4,
        _ => null
    };
}

/// <summary>One referenced file, flattened.</summary>
public sealed record PackageFileEntry(
    PackageFileRole Role,
    string Path,
    string Sha256,
    long? SizeBytes,
    string? StockKind = null,
    string? SourceOperation = null,
    string? Name = null,
    bool? Collision = null,
    string? FoundBy = null);

/// <summary>Fixed names and serializer options of the package format.</summary>
public static class ManifestJson
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "manifest.json";
    public const string PackageFileName = "package.json";
    public const string CompleteMarkerFileName = "COMPLETE";
    public const string ExportLogPath = "metadata/export-log.json";

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static SimulationPackageManifest Deserialize(string json) =>
        JsonSerializer.Deserialize<SimulationPackageManifest>(json, Options)
        ?? throw new JsonException("The manifest is empty.");

    public static string Serialize(SimulationPackageManifest manifest) => JsonSerializer.Serialize(manifest, Options);
}
