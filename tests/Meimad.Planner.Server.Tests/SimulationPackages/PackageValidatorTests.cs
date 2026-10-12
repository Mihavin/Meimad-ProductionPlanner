using System.Text.Json;
using System.Text.Json.Nodes;
using Meimad.Planner.SimulationPackage.Diagnostics;
using Meimad.Planner.SimulationPackage.Geometry;
using Meimad.Planner.SimulationPackage.Manifest;
using Meimad.Planner.SimulationPackage.Schema;
using Meimad.Planner.SimulationPackage.Validation;
using static Meimad.Planner.SimulationPackage.Diagnostics.PackageDiagnosticCodes;

namespace Meimad.Planner.Server.Tests.SimulationPackages;

/// <summary>
/// Stage 0 contract checks (schemas and golden packages) and the §17 package tests: complete package,
/// missing file, invalid hash, interrupted export without COMPLETE, old schema version, multiple stock
/// candidates, multiple fixtures, path traversal, approved roots, tool table vs NC, missing UCS.
/// </summary>
public sealed class PackageValidatorTests : IDisposable
{
    private readonly GoldenPackages golden = new();

    public void Dispose() => golden.Dispose();

    private SimulationPackageValidator Validator(int maxFixtures = 32) => new(new PackageValidationOptions
    {
        ApprovedRoots = [golden.Root],
        MaxFixtureCount = maxFixtures,
        TestedCimatronVersions = ["2026"]
    });

    private static string[] Codes(PackageValidationReport report, DiagnosticSeverity severity) =>
        report.Diagnostics.Where(item => item.Severity == severity).Select(item => item.Code).ToArray();

    [Fact]
    public void Shipped_schemas_load_with_the_supported_keyword_subset()
    {
        var schemas = JsonSchemaSubsetValidator.Default;
        Assert.Equal(3, schemas.SchemaNames.Count);
        foreach (var name in schemas.SchemaNames)
        {
            using var document = JsonDocument.Parse(JsonSchemaSubsetValidator.ReadEmbeddedSchemaText(name));
            Assert.Equal(name, document.RootElement.GetProperty("$id").GetString());
        }

        // The schema files are also shipped beside the assembly for exporters and other tools.
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "simulation-package-schemas", "simulation-package.schema.json")));
        Assert.Throws<InvalidOperationException>(() => JsonSchemaSubsetValidator.FromTexts(new Dictionary<string, string>
        {
            ["x.schema.json"] = """{"type":"object","patternProperties":{"^x":{}}}"""
        }));
    }

    [Theory]
    [InlineData("3-axis")]
    [InlineData("3plus2")]
    [InlineData("5-axis")]
    public void Golden_manifests_and_tool_tables_satisfy_the_schemas(string name)
    {
        var folder = Path.Combine(GoldenPackages.Source, name);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        Assert.Empty(JsonSchemaSubsetValidator.Default.Validate(JsonSchemaSubsetValidator.SimulationPackageSchema, manifest.RootElement));
        using var tools = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "tools", "tool-table.json")));
        Assert.Empty(JsonSchemaSubsetValidator.Default.Validate(JsonSchemaSubsetValidator.ToolTableSchema, tools.RootElement));
        using var systems = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            coordinateSystems = new[] { manifest.RootElement.GetProperty("coordinateSystem") }
        }));
        Assert.Empty(JsonSchemaSubsetValidator.Default.Validate(JsonSchemaSubsetValidator.CoordinateSystemsSchema, systems.RootElement));
    }

    [Fact]
    public void Schema_rejects_unknown_properties_bad_hashes_absolute_paths_and_mixed_modes()
    {
        var text = File.ReadAllText(Path.Combine(GoldenPackages.Source, "3-axis", "manifest.json"));
        JsonElement Mutate(Action<JsonObject> edit)
        {
            var node = JsonNode.Parse(text)!.AsObject();
            edit(node);
            return JsonDocument.Parse(node.ToJsonString()).RootElement;
        }

        var schema = JsonSchemaSubsetValidator.Default;
        Assert.Contains(schema.Validate(JsonSchemaSubsetValidator.SimulationPackageSchema, Mutate(n => n["surprise"] = 1)),
            item => item.Keyword == "additionalProperties" && item.InstancePointer == "/surprise");
        Assert.Contains(schema.Validate(JsonSchemaSubsetValidator.SimulationPackageSchema, Mutate(n => n["files"]!["nc"]!["sha256"] = "ABC")),
            item => item.InstancePointer == "/files/nc/sha256" && item.Keyword == "pattern");
        foreach (var bad in new[] { "../outside.nc", "C:/O1001.nc", "/nc/O1001.nc", "\\\\server\\share\\O1001.nc", "nc/../../x.nc" })
        {
            Assert.Contains(schema.Validate(JsonSchemaSubsetValidator.SimulationPackageSchema, Mutate(n => n["files"]!["nc"]!["path"] = bad)),
                item => item.InstancePointer == "/files/nc/path");
        }

        Assert.NotEmpty(schema.Validate(JsonSchemaSubsetValidator.SimulationPackageSchema,
            Mutate(n => n["coordinateSystem"]!["rotationOrder"] = "RzRyRx")));
    }

    [Theory]
    [InlineData("3-axis", 2)]
    [InlineData("3plus2", 1)]
    [InlineData("5-axis", 1)]
    public async Task Complete_golden_packages_validate_without_blocking_errors(string name, int fixtures)
    {
        var package = golden.Copy(name);
        var report = await Validator().ValidateAsync(package);
        Assert.True(report.CompleteMarkerPresent);
        Assert.False(report.HasBlockingErrors, string.Join("; ", report.Errors.Select(item => $"{item.Code}: {item.Message}")));
        Assert.Empty(Codes(report, DiagnosticSeverity.Warning));
        Assert.Equal(PackageHashing.Sha256OfFile(Path.Combine(package, "manifest.json")), report.ManifestSha256);
        Assert.Equal(fixtures, report.Files.Count(file => file.Role == PackageFileRole.Fixture));
        Assert.All(report.Files, file => Assert.Equal(file.DeclaredSha256, file.ActualSha256));
        Assert.All(report.Files.Where(file => PackageFileRoles.IsGeometry(file.Role)), file => Assert.Equal(12, file.TriangleCount));
        Assert.Single(report.StockCandidates);
        // Every object has a role, units, the UCS, a source and a hash (Stage 0 "done when").
        Assert.Equal("mm", report.Manifest!.Units);
        Assert.False(string.IsNullOrWhiteSpace(report.Manifest.CoordinateSystem!.Name));
        Assert.Equal("Cimatron", report.Manifest.Source!.Application);
    }

    [Fact]
    public async Task Rotary_packages_report_that_alignment_is_left_to_the_simulation()
    {
        var report = await Validator().ValidateAsync(golden.Copy("5-axis"));
        Assert.Contains(report.Diagnostics, item => item.Code == ToolpathAlignmentNotChecked && item.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public async Task Interrupted_export_without_complete_is_not_read()
    {
        var report = await Validator().ValidateAsync(golden.Copy("broken/missing-complete"));
        Assert.False(report.CompleteMarkerPresent);
        Assert.Equal([PackageIncomplete], Codes(report, DiagnosticSeverity.Error));
        Assert.Null(report.Manifest);
    }

    [Fact]
    public async Task Changed_file_after_export_is_a_hash_mismatch()
    {
        var report = await Validator().ValidateAsync(golden.Copy("broken/hash-mismatch"));
        var mismatch = Assert.Single(report.Errors, item => item.Code == FileHashMismatch && item.Context["path"] == "nc/O1001.nc" && item.Context.ContainsKey("actualSha256"));
        Assert.Equal("nc", mismatch.Context["role"]);
    }

    [Fact]
    public async Task Missing_file_blocks()
    {
        var report = await Validator().ValidateAsync(golden.Copy("broken/missing-file"));
        var missing = Assert.Single(report.Errors);
        Assert.Equal(FileMissing, missing.Code);
        Assert.Equal("geometry/fixture-002.stl", missing.Context["path"]);
        Assert.Equal("fixture", missing.Context["role"]);
    }

    [Fact]
    public async Task Old_schema_version_is_refused_with_a_stable_code()
    {
        var report = await Validator().ValidateAsync(golden.Copy("broken/old-schema"));
        var error = Assert.Single(report.Errors);
        Assert.Equal(SchemaVersionUnsupported, error.Code);
        Assert.Equal("0", error.Context["schemaVersion"]);
        Assert.Equal("1", error.Context["supported"]);
    }

    [Fact]
    public async Task Path_traversal_in_the_manifest_blocks()
    {
        var report = await Validator().ValidateAsync(golden.Copy("broken/path-traversal"));
        Assert.Contains(report.Errors, item => item.Code == FilePathTraversal && item.Context["path"] == "../part.stl");
    }

    [Fact]
    public async Task Tool_table_must_cover_every_nc_tool()
    {
        var report = await Validator().ValidateAsync(golden.Copy("broken/tool-mismatch"));
        var error = Assert.Single(report.Errors);
        Assert.Equal(ToolTableMismatch, error.Code);
        Assert.Equal("2", error.Context["missingTools"]);
    }

    [Fact]
    public async Task Missing_ucs_blocks_and_is_not_guessed()
    {
        var report = await Validator().ValidateAsync(golden.Copy("broken/missing-ucs"));
        Assert.Contains(UcsMissing, Codes(report, DiagnosticSeverity.Error));
        Assert.DoesNotContain(ManifestSchemaViolation, Codes(report, DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task Package_outside_the_approved_roots_is_refused_before_reading()
    {
        var package = golden.Copy("3-axis");
        var validator = new SimulationPackageValidator(new PackageValidationOptions { ApprovedRoots = [Path.Combine(golden.Root, "elsewhere")] });
        var report = await validator.ValidateAsync(package);
        Assert.Equal([PackageRootNotApproved], Codes(report, DiagnosticSeverity.Error));
        Assert.Equal([PackagePathInvalid], Codes(await validator.ValidateAsync("relative\\folder"), DiagnosticSeverity.Error));
        Assert.Equal([FilePathTraversal], Codes(await Validator().ValidateAsync(Path.Combine(golden.Root, "..", "x")), DiagnosticSeverity.Error));
        Assert.Equal([PackageNotFound], Codes(await Validator().ValidateAsync(Path.Combine(golden.Root, "nothing")), DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task Multiple_stock_candidates_are_listed_not_chosen()
    {
        var package = golden.Copy("3-axis");
        File.Copy(Path.Combine(package, "geometry", "stock.stl"), Path.Combine(package, "geometry", "stock-remaining.stl"));
        GoldenPackages.EditManifest(package, node => node["files"]!["stock"]!.AsArray().Add(new JsonObject
        {
            ["path"] = "geometry/stock-remaining.stl", ["kind"] = "remaining", ["sourceOperation"] = "005", ["foundBy"] = "fallback",
            ["sha256"] = new string('0', 64)
        }), rehash: true);
        var report = await Validator().ValidateAsync(package);
        Assert.False(report.HasBlockingErrors, string.Join(";", report.Errors.Select(item => item.Code)));
        Assert.Equal(2, report.StockCandidates.Count);
        Assert.Contains(StockMultipleCandidates, Codes(report, DiagnosticSeverity.Warning));
        Assert.Contains(StockFallbackRule, Codes(report, DiagnosticSeverity.Warning));
        Assert.Contains(report.StockCandidates, file => file.Role == PackageFileRole.RemainingStock && file.SourceOperation == "005");
    }

    [Fact]
    public async Task Multiple_fixtures_are_kept_separate_and_limited()
    {
        var package = golden.Copy("3-axis");
        var report = await Validator().ValidateAsync(package);
        Assert.Equal(["Base plate", "Vise jaw"], report.Files.Where(file => file.Role == PackageFileRole.Fixture).Select(file => file.Name));
        var limited = await Validator(maxFixtures: 1).ValidateAsync(package);
        Assert.Contains(FixtureCountExceeded, Codes(limited, DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task Warnings_follow_section_14()
    {
        var package = golden.Copy("3-axis");
        File.WriteAllText(Path.Combine(package, "geometry", "part.stl"), "not an stl at all");
        GoldenPackages.EditManifest(package, node =>
        {
            node["files"]!.AsObject().Remove("fixtures");
            node["exporter"]!["cimatronVersion"] = "2023.1";
            node["exporter"]!["usedDefaultMeshTolerance"] = true;
        }, rehash: true);
        var tools = Path.Combine(package, "tools", "tool-table.json");
        File.WriteAllText(tools, File.ReadAllText(tools).Replace("\"lengthMm\": 60", "\"lengthMm\": 0"));
        GoldenPackages.EditManifest(package, _ => { }, rehash: true);

        var report = await Validator().ValidateAsync(package);
        Assert.False(report.HasBlockingErrors, string.Join(";", report.Errors.Select(item => item.Code)));
        var warnings = Codes(report, DiagnosticSeverity.Warning);
        Assert.Contains(PartUnreadable, warnings);
        Assert.Contains(FixtureMissing, warnings);
        Assert.Contains(CimatronVersionUntested, warnings);
        Assert.Contains(DefaultMeshTolerance, warnings);
        Assert.Contains(HolderDimensionsIncomplete, warnings);
    }

    [Fact]
    public async Task Blocking_errors_follow_section_14()
    {
        var package = golden.Copy("3-axis");
        File.WriteAllBytes(Path.Combine(package, "geometry", "fixture-001.stl"), [1, 2, 3, 4, 5]);
        GoldenPackages.EditManifest(package, node =>
        {
            node["units"] = "cubits";
            node["operation"]!.AsObject().Remove("procedureId");
        }, rehash: true);
        var report = await Validator().ValidateAsync(package);
        var errors = Codes(report, DiagnosticSeverity.Error);
        Assert.Contains(GeometryUnreadable, errors);
        Assert.Contains(UnitsUnknown, errors);
        Assert.Contains(ProcedureIdMissing, errors);
        Assert.All(report.Diagnostics, item => Assert.Matches("^[a-z0-9_]+$", item.Code));
    }

    [Fact]
    public async Task Invalid_json_and_missing_nc_and_measured_offsets_block()
    {
        var invalid = golden.Copy("3-axis", "invalid-json");
        File.WriteAllText(Path.Combine(invalid, "manifest.json"), "{ \"schemaVersion\": 1, ");
        Assert.Equal([ManifestInvalidJson], Codes(await Validator().ValidateAsync(invalid), DiagnosticSeverity.Error));

        var noNc = golden.Copy("3-axis", "no-nc");
        GoldenPackages.EditManifest(noNc, node => node["files"]!.AsObject().Remove("nc"));
        Assert.Contains(NcMissing, Codes(await Validator().ValidateAsync(noNc), DiagnosticSeverity.Error));

        var measured = golden.Copy("3-axis", "measured");
        GoldenPackages.EditManifest(measured, node => node["coordinateSystem"]!["measuredG54"] = new JsonObject { ["x"] = 1 });
        Assert.Contains(ManifestContainsMeasuredOffset, Codes(await Validator().ValidateAsync(measured), DiagnosticSeverity.Error));

        var noManifest = golden.Copy("3-axis", "no-manifest");
        File.Delete(Path.Combine(noManifest, "manifest.json"));
        Assert.Equal([ManifestMissing], Codes(await Validator().ValidateAsync(noManifest), DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task Misplaced_stock_on_a_3_axis_program_warns_about_the_coordinates()
    {
        var package = golden.Copy("3-axis");
        File.WriteAllBytes(Path.Combine(package, "geometry", "stock.stl"),
            MeshFiles.WriteBinaryStl(MeshFiles.Box(new(500, 500, 500), new(600, 560, 530))));
        GoldenPackages.EditManifest(package, _ => { }, rehash: true);
        var report = await Validator().ValidateAsync(package);
        Assert.Contains(ToolpathStockMisaligned, Codes(report, DiagnosticSeverity.Warning));
    }

    [Fact]
    public async Task Ascii_stl_is_readable_and_the_mht_reader_is_pluggable()
    {
        var ascii = MeshFiles.ReadStl(System.Text.Encoding.ASCII.GetBytes("""
            solid t
              facet normal 0 0 1
                outer loop
                  vertex 0 0 0
                  vertex 10 0 0
                  vertex 0 5 -2
                endloop
              endfacet
            endsolid t
            """));
        Assert.True(ascii.Readable);
        Assert.Equal(1, ascii.TriangleCount);
        Assert.Equal(-2, ascii.Bounds!.Value.Min.Z);

        var package = golden.Copy("3-axis");
        File.WriteAllText(Path.Combine(package, "tools", "TP_MODEL.TOOLS.mht"), "fake report");
        GoldenPackages.EditManifest(package, node => node["files"]!["toolTable"] = new JsonObject
        {
            ["path"] = "tools/TP_MODEL.TOOLS.mht", ["sha256"] = new string('0', 64)
        }, rehash: true);
        var withoutReader = await Validator().ValidateAsync(package);
        Assert.Contains(ToolTableUnreadable, Codes(withoutReader, DiagnosticSeverity.Error));
        var withReader = await new SimulationPackageValidator(new PackageValidationOptions
        {
            ApprovedRoots = [golden.Root],
            ToolTableReader = new FixedToolReader([1, 2])
        }).ValidateAsync(package);
        Assert.False(withReader.HasBlockingErrors, string.Join(";", withReader.Errors.Select(item => item.Code)));
    }

    private sealed class FixedToolReader(int[] numbers) : Meimad.Planner.SimulationPackage.Tools.IToolTableReader
    {
        public Task<Meimad.Planner.SimulationPackage.Tools.ToolTableFacts?> ReadAsync(string absolutePath, CancellationToken cancellationToken) =>
            Task.FromResult<Meimad.Planner.SimulationPackage.Tools.ToolTableFacts?>(new(numbers, []));
    }
}
