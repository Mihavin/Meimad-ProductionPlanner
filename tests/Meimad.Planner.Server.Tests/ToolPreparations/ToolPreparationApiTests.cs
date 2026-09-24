using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.ToolPreparations;

public sealed class ToolPreparationApiTests
{
    private const string Route = "/api/v1/batch-operations/operation-package/tool-preparation";
    private const string PackageRoute = "/api/v1/batch-operations/operation-package/production-package";

    [Fact]
    public async Task Tool_room_reads_released_rows_and_saves_immutable_versions_with_identity()
    {
        await using var server = await TestServer.StartAsync(verificationEnabled: true);
        var client = server.Client;

        using var initial = await client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        using var initialJson = JsonDocument.Parse(await initial.Content.ReadAsStringAsync());
        var root = initialJson.RootElement;
        Assert.Equal(0, root.GetProperty("version").GetInt32());
        Assert.Equal("tools-1", root.GetProperty("toolTableReleaseId").GetString());
        Assert.Equal("RADIUS", root.GetProperty("toolDiameterOffsetKind").GetString());
        Assert.Equal("mill", root.GetProperty("processType").GetString());
        Assert.Equal("HAAS_NGC", root.GetProperty("ncDialect").GetString());
        var rows = root.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(["T1", "T2", "T3"], rows.Select(row => row.GetProperty("toolIdentifier").GetString()));
        Assert.Equal("FLAT END MILL D10", rows[0].GetProperty("description").GetString());
        Assert.True(rows[0].GetProperty("isRequired").GetBoolean());
        Assert.False(rows[2].GetProperty("isRequired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("measuredLength").ValueKind);

        // Saving needs the client identity headers, like Production Package creation.
        using var anonymous = new HttpClient(server.Application.GetTestServer().CreateHandler()) { BaseAddress = client.BaseAddress };
        using var unidentified = await anonymous.PutAsJsonAsync(Route, Update(0, MeasuredTools()));
        Assert.Equal(HttpStatusCode.PreconditionRequired, unidentified.StatusCode);

        using var wrongVersion = await client.PutAsJsonAsync(Route, Update(1, MeasuredTools()));
        Assert.Equal(HttpStatusCode.Conflict, wrongVersion.StatusCode);
        Assert.Contains("tool_preparation_version_conflict", await wrongVersion.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var unknownTool = await client.PutAsJsonAsync(Route, Update(0, [Tool("T9", 9, 50, 5)]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownTool.StatusCode);
        Assert.Contains("tool_preparation_unknown_tool", await unknownTool.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var negative = await client.PutAsJsonAsync(Route, Update(0, [Tool("T1", 1, -1, 10)]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, negative.StatusCode);
        Assert.Contains("tool_preparation_measured_length_invalid", await negative.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var staleTable = await client.PutAsJsonAsync(Route, Update(0, MeasuredTools()) with { ToolTableReleaseId = "tools-old" });
        Assert.Equal(HttpStatusCode.Conflict, staleTable.StatusCode);
        Assert.Contains("tool_preparation_tool_table_changed", await staleTable.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var saved = await client.PutAsJsonAsync(Route, Update(0, MeasuredTools()) with { Comment = "Presetter 2026-09-24" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var savedJson = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        Assert.Equal(1, savedJson.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("tool-room-user", savedJson.RootElement.GetProperty("savedBy").GetString());
        Assert.Equal("Presetter 2026-09-24", savedJson.RootElement.GetProperty("comment").GetString());
        Assert.Equal("tools-1", savedJson.RootElement.GetProperty("savedForToolTableReleaseId").GetString());
        var savedRows = savedJson.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(120.5, savedRows[0].GetProperty("measuredLength").GetDouble());
        Assert.Equal(10, savedRows[0].GetProperty("measuredDiameter").GetDouble());
        Assert.Equal("END_MILL", savedRows[0].GetProperty("shapeType").GetString());
        Assert.Equal(22, savedRows[0].GetProperty("shape").GetProperty("fluteLength").GetDouble());
        var components = savedRows[0].GetProperty("components").EnumerateArray().ToArray();
        Assert.Equal(["HOLDER", "COLLET", "CUTTER"], components.Select(component => component.GetProperty("componentType").GetString()));
        Assert.Equal("BT40 ER32 x 70", components[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, savedRows[2].GetProperty("measuredLength").ValueKind);

        // The second save appends version 2; the first version stays as it was.
        using var second = await client.PutAsJsonAsync(Route, Update(1, MeasuredTools(length: 121.0)));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var again = await client.GetAsync(Route);
        using var againJson = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
        Assert.Equal(2, againJson.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(121.0, againJson.RootElement.GetProperty("tools")[0].GetProperty("measuredLength").GetDouble());
        Assert.Equal(2L, await server.ScalarAsync("SELECT COUNT(*) FROM tool_preparations WHERE batch_operation_id='operation-package';"));
        Assert.Equal(120.5, await server.ScalarAsync(
            "SELECT tool.measured_length FROM tool_preparation_tools tool JOIN tool_preparations preparation ON preparation.id=tool.tool_preparation_id WHERE preparation.version_number=1 AND tool.tool_identifier='T1';"));
    }

    [Fact]
    public async Task Measured_package_requires_the_measurements_writes_them_into_the_offset_loader_and_goes_stale_on_a_new_save()
    {
        await using var server = await TestServer.StartAsync(verificationEnabled: true);
        var client = server.Client;

        using var refused = await client.PostAsync($"{PackageRoute}?toolOffsetMode=MEASURED", Empty());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var refusedBody = await refused.Content.ReadAsStringAsync();
        Assert.Contains("production_package_tool_measurements_missing", refusedBody, StringComparison.Ordinal);

        // T3 is optional and may stay unmeasured; T2 is required.
        using var partial = await client.PutAsJsonAsync(Route, Update(0, [Tool("T1", 1, 120.5, 10)]));
        Assert.Equal(HttpStatusCode.OK, partial.StatusCode);
        using var stillRefused = await client.PostAsync($"{PackageRoute}?toolOffsetMode=MEASURED", Empty());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stillRefused.StatusCode);
        var stillRefusedBody = await stillRefused.Content.ReadAsStringAsync();
        Assert.Contains("T2", stillRefusedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("T3", stillRefusedBody, StringComparison.Ordinal);

        using var complete = await client.PutAsJsonAsync(Route, Update(1, MeasuredTools()));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using var complete2Json = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
        var preparationId = complete2Json.RootElement.GetProperty("toolPreparationId").GetString();

        using var created = await client.PostAsync($"{PackageRoute}?toolOffsetMode=MEASURED", Empty());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var packageJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var package = packageJson.RootElement;
        Assert.Equal(preparationId, package.GetProperty("toolPreparationId").GetString());
        var artifacts = package.GetProperty("artifacts").EnumerateArray().ToArray();
        var types = artifacts.Select(value => value.GetProperty("artifactType").GetString()).ToArray();
        Assert.Contains("TOOL_OFFSETS", types);
        Assert.Contains("OFFSET_LOADER", types);
        Assert.DoesNotContain("TOOL_OFFSET_PROGRAM", types);

        var loader = artifacts.Single(value => value.GetProperty("artifactType").GetString() == "OFFSET_LOADER");
        var loaderText = Encoding.ASCII.GetString(await client.GetByteArrayAsync(
            $"{PackageRoute}/artifacts/{loader.GetProperty("artifactId").GetString()}"));
        Assert.Contains("(WRITES 2 MEASURED TOOL OFFSETS IN MM - RADIUS CUTTER VALUES)", loaderText, StringComparison.Ordinal);
        Assert.Contains("G21\r\nG10 L10 P1 R120.5 (T1 FLAT END MILL D10 LENGTH)\r\nG10 L11 P1 R0. (T1 FLAT END MILL D10 LENGTH WEAR)\r\nG10 L12 P1 R5. (T1 FLAT END MILL D10 RADIUS)\r\nG10 L13 P1 R0. (T1 FLAT END MILL D10 DIAMETER WEAR)", loaderText, StringComparison.Ordinal);
        Assert.Contains("G10 L10 P2 R98.25 (T2 DRILL 8.4 LENGTH)", loaderText, StringComparison.Ordinal);
        Assert.DoesNotContain("P3 ", loaderText, StringComparison.Ordinal);
        // The offsets are written before verification is armed.
        Assert.True(loaderText.IndexOf("G10 L10 P1", StringComparison.Ordinal) < loaderText.IndexOf("G65 P9001", StringComparison.Ordinal));

        var offsets = artifacts.Single(value => value.GetProperty("artifactType").GetString() == "TOOL_OFFSETS");
        Assert.Equal("tool-offsets/tool-offsets.json", offsets.GetProperty("logicalPath").GetString());
        Assert.Equal(preparationId, offsets.GetProperty("sourceReleaseId").GetString());
        using var offsetsJson = JsonDocument.Parse(await client.GetByteArrayAsync(
            $"{PackageRoute}/artifacts/{offsets.GetProperty("artifactId").GetString()}"));
        Assert.Equal(preparationId, offsetsJson.RootElement.GetProperty("toolPreparationId").GetString());
        Assert.True(offsetsJson.RootElement.GetProperty("offsetsLoadedByProgram").GetBoolean());
        var offsetRows = offsetsJson.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(3, offsetRows.Length);
        Assert.Equal("BT40 ER32 x 70", offsetRows[0].GetProperty("components")[0].GetProperty("name").GetString());
        Assert.False(offsetRows[2].GetProperty("writtenByProgram").GetBoolean());

        var manifest = artifacts.Single(value => value.GetProperty("artifactType").GetString() == "MANIFEST");
        using var manifestJson = JsonDocument.Parse(await client.GetByteArrayAsync(
            $"{PackageRoute}/artifacts/{manifest.GetProperty("artifactId").GetString()}"));
        Assert.Equal(preparationId, manifestJson.RootElement.GetProperty("toolPreparationId").GetString());
        Assert.Equal(2, manifestJson.RootElement.GetProperty("toolPreparationVersion").GetInt32());
        Assert.True(manifestJson.RootElement.GetProperty("toolOffsetsLoadedByProgram").GetBoolean());
        Assert.Equal("RADIUS", manifestJson.RootElement.GetProperty("toolDiameterOffsetKind").GetString());
        Assert.Equal(2, manifestJson.RootElement.GetProperty("measuredToolCount").GetInt32());

        using var current = await client.GetAsync(PackageRoute);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(["operation-package"], await server.QueueIdsAsync("SETUP_PENDING"));

        // Newer measurements make the package stale until the Tool Room rebuilds it.
        using var newer = await client.PutAsJsonAsync(Route, Update(2, MeasuredTools(length: 120.7)));
        Assert.Equal(HttpStatusCode.OK, newer.StatusCode);
        using var stale = await client.GetAsync(PackageRoute);
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        Assert.Equal(["operation-package"], await server.QueueIdsAsync("TOOL_PREPARATION_PENDING"));
        Assert.Empty(await server.QueueIdsAsync("SETUP_PENDING"));

        // The Machine's control keeps diameter values: the rebuilt package writes the diameter.
        await server.ExecuteAsync("UPDATE machines SET tool_diameter_offset_kind='DIAMETER' WHERE id='machine-package';");
        using var rebuilt = await client.PostAsync($"{PackageRoute}?toolOffsetMode=MEASURED", Empty());
        Assert.Equal(HttpStatusCode.Created, rebuilt.StatusCode);
        using var rebuiltJson = JsonDocument.Parse(await rebuilt.Content.ReadAsStringAsync());
        var rebuiltLoader = rebuiltJson.RootElement.GetProperty("artifacts").EnumerateArray()
            .Single(value => value.GetProperty("artifactType").GetString() == "OFFSET_LOADER");
        var rebuiltText = Encoding.ASCII.GetString(await client.GetByteArrayAsync(
            $"{PackageRoute}/artifacts/{rebuiltLoader.GetProperty("artifactId").GetString()}"));
        Assert.Contains("G10 L10 P1 R120.7 (T1 FLAT END MILL D10 LENGTH)", rebuiltText, StringComparison.Ordinal);
        Assert.Contains("G10 L12 P1 R10. (T1 FLAT END MILL D10 DIAMETER)", rebuiltText, StringComparison.Ordinal);
        Assert.Equal(["operation-package"], await server.QueueIdsAsync("SETUP_PENDING"));
    }

    [Fact]
    public async Task Measured_package_without_verification_writes_a_separate_offset_program()
    {
        await using var server = await TestServer.StartAsync(verificationEnabled: false);
        var client = server.Client;
        using var saved = await client.PutAsJsonAsync(Route, Update(0, MeasuredTools()));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var created = await client.PostAsync(PackageRoute, Empty());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var packageJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var artifacts = packageJson.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
        var types = artifacts.Select(value => value.GetProperty("artifactType").GetString()).ToArray();
        Assert.DoesNotContain("OFFSET_LOADER", types);
        Assert.Contains("TOOL_OFFSET_PROGRAM", types);
        Assert.Contains("TOOL_OFFSETS", types);

        var program = artifacts.Single(value => value.GetProperty("artifactType").GetString() == "TOOL_OFFSET_PROGRAM");
        Assert.Equal("tool-offsets/O01991.nc", program.GetProperty("logicalPath").GetString());
        var text = Encoding.ASCII.GetString(await client.GetByteArrayAsync(
            $"{PackageRoute}/artifacts/{program.GetProperty("artifactId").GetString()}"));
        Assert.StartsWith("%\r\nO01991 (MEIMAD MEASURED TOOL OFFSETS)\r\n(PRODUCTION PACKAGE ", text, StringComparison.Ordinal);
        Assert.Contains("G10 L10 P1 R120.5 (T1 FLAT END MILL D10 LENGTH)", text, StringComparison.Ordinal);
        Assert.EndsWith("M30\r\n%\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("G65", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manual_dummy_package_carries_no_measured_payload_even_when_measurements_exist()
    {
        await using var server = await TestServer.StartAsync(verificationEnabled: true);
        var client = server.Client;
        await server.ExecuteAsync("INSERT INTO machine_package_capabilities(machine_id,allow_manual_dummy_tool_offsets,updated_at,updated_by) VALUES('machine-package',1,'2026-09-01T08:00:00Z','test');");
        using var saved = await client.PutAsJsonAsync(Route, Update(0, MeasuredTools()));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var created = await client.PostAsync($"{PackageRoute}?toolOffsetMode=MANUAL_DUMMY", Empty());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var packageJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, packageJson.RootElement.GetProperty("toolPreparationId").ValueKind);
        var artifacts = packageJson.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.DoesNotContain(artifacts, value => value.GetProperty("artifactType").GetString() is "TOOL_OFFSETS" or "TOOL_OFFSET_PROGRAM" or "TOOL_TABLE");
        var loader = artifacts.Single(value => value.GetProperty("artifactType").GetString() == "OFFSET_LOADER");
        var text = Encoding.ASCII.GetString(await client.GetByteArrayAsync(
            $"{PackageRoute}/artifacts/{loader.GetProperty("artifactId").GetString()}"));
        Assert.DoesNotContain("G10", text, StringComparison.Ordinal);
        Assert.Contains("MANUAL DUMMY TOOL OFFSETS - VERIFICATION ONLY", text, StringComparison.Ordinal);
    }

    private static StringContent Empty() => new("{}", Encoding.UTF8, "application/json");

    private static ToolPreparationRequest Update(int expectedVersion, IReadOnlyList<ToolRequest> tools) =>
        new(expectedVersion, "tools-1", null, tools);

    private static IReadOnlyList<ToolRequest> MeasuredTools(double length = 120.5) =>
    [
        Tool("T1", 1, length, 10, "END_MILL", new Dictionary<string, double> { ["cuttingDiameter"] = 10, ["fluteLength"] = 22, ["overallLength"] = 72 },
        [
            new(1, "HOLDER", "BT40 ER32 x 70", "BT40-ER32-70", 70, 63, null),
            new(2, "COLLET", "ER32 10 mm", null, null, 10, null),
            new(3, "CUTTER", "Flat end mill D10 4 flutes", "EM-10-4", 72, 10, "carbide")
        ]),
        Tool("T2", 2, 98.25, 8.4, "DRILL", new Dictionary<string, double> { ["cuttingDiameter"] = 8.4, ["pointAngle"] = 118 })
    ];

    private static ToolRequest Tool(
        string identifier, int offset, double length, double diameter, string shape = "END_MILL",
        Dictionary<string, double>? dimensions = null, IReadOnlyList<ComponentRequest>? components = null) =>
        new(identifier, offset, length, diameter, shape, dimensions ?? [], null, components ?? []);

    private sealed record ComponentRequest(int Sequence, string ComponentType, string Name, string? CatalogNumber, double? Length, double? Diameter, string? Notes);

    private sealed record ToolRequest(
        string ToolIdentifier, int? OffsetNumber, double? MeasuredLength, double? MeasuredDiameter, string ShapeType,
        Dictionary<string, double> Shape, string? Notes, IReadOnlyList<ComponentRequest> Components);

    private sealed record ToolPreparationRequest(int ExpectedVersion, string ToolTableReleaseId, string? Comment, IReadOnlyList<ToolRequest> Tools);

    /// <summary>A Server with one assigned CNC Operation whose released Tool Table lists T1, T2 (required) and T3 (optional).</summary>
    private sealed class TestServer : IAsyncDisposable
    {
        private readonly string root;

        private TestServer(string root, WebApplication application, HttpClient client)
        {
            this.root = root;
            Application = application;
            Client = client;
        }

        internal WebApplication Application { get; }
        internal HttpClient Client { get; }

        internal static async Task<TestServer> StartAsync(bool verificationEnabled)
        {
            var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.ToolPreparation.Tests", Guid.NewGuid().ToString("N"));
            var releaseRoot = Path.Combine(root, "releases");
            Directory.CreateDirectory(root);
            var application = ServerApplication.Build(
                ["--Server:Host=127.0.0.1", "--Server:Port=5098", $"--Database:Path={Path.Combine(root, "test.db")}",
                 $"--GCode:ReleaseRoot={releaseRoot}", $"--ProductionPackages:PackageRoot={Path.Combine(root, "packages")}"],
                webHost => webHost.UseTestServer());
            await application.StartAsync();
            await SeedAsync(application.Services, releaseRoot, verificationEnabled);
            var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "tool-room-client");
            client.DefaultRequestHeaders.Add("X-Meimad-User-Id", "tool-room-user");
            return new TestServer(root, application, client);
        }

        internal async Task<IReadOnlyList<string>> QueueIdsAsync(string stage)
        {
            using var response = await Client.GetAsync($"/api/v1/preparation-queues/{stage}");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("items").EnumerateArray()
                .Select(value => value.GetProperty("batchOperationId").GetString()!).ToArray();
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using var connection = await Application.Services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        internal async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = await Application.Services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Application.StopAsync();
            await Application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        private static async Task SeedAsync(IServiceProvider services, string releaseRoot, bool verificationEnabled)
        {
            var gcodeRelative = "operations/case-operation-package/gcode/gcode-1/main.nc";
            var toolRelative = "operations/case-operation-package/tool-tables/tools-1/tools.csv";
            var gcodePath = Path.Combine(releaseRoot, gcodeRelative.Replace('/', Path.DirectorySeparatorChar));
            var toolPath = Path.Combine(releaseRoot, toolRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(gcodePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(toolPath)!);
            var template = string.Join("\r\n", new[]
            {
                "%", "O01995",
                "(PART: [[MEIMAD:PART_NAME]])", "(OPERATION: [[MEIMAD:OPERATION_NAME]])",
                "(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])", "(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])",
                "(MACHINE: [[MEIMAD:MACHINE_ID]])", "(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])",
                "(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])",
                "[[MEIMAD:VERIFICATION_HOOK]]", "[[MEIMAD:EVENT_CONTEXT]]",
                "[[MEIMAD:CYCLE_START]]", "G90", "[[MEIMAD:CYCLE_END]]", "M30", "%", ""
            });
            await File.WriteAllTextAsync(gcodePath, template, Encoding.ASCII);
            await File.WriteAllTextAsync(toolPath, "tool,description\nT1,FLAT END MILL D10\nT2,DRILL 8.4\nT3,PROBE\n", Encoding.UTF8);
            var gcodeBytes = await File.ReadAllBytesAsync(gcodePath);
            var toolBytes = await File.ReadAllBytesAsync(toolPath);
            var now = "2026-09-01T08:00:00Z";
            await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO working_calendars(id,name,time_zone_id,calendar_json)
                VALUES('calendar-package','Package Calendar','UTC','{}');
                INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active,
                                     display_enabled,execution_mode,usable_tool_positions)
                VALUES('machine-package','M-PKG','Package Mill','mill','calendar-package','active',1,1,'CNC_GCODE',20);
                INSERT INTO cases(id,part_number,name,working_folder_path)
                VALUES('case-package','PN-PKG','Package Part',$workingFolder);
                INSERT INTO case_operations(id,case_id,operation_number,route_position,name,required_machine_type,
                                            setup_seconds,cycle_seconds)
                VALUES('case-operation-package','case-package',10,0,'Finish','mill',60,60);
                INSERT INTO tool_table_releases(
                    id,case_operation_id,revision_number,original_file_name,stored_relative_path,file_size,file_hash,
                    released_at,released_by,release_comment,created_at,updated_at,required_tool_count)
                VALUES('tools-1','case-operation-package',1,'tools.csv',$toolPath,$toolSize,$toolHash,$at,'tool-user','Initial',$at,$at,2);
                INSERT INTO tool_table_release_tools(id,tool_table_release_id,row_number,tool_identifier,description,
                    is_required,requires_magazine_position,is_active,magazine_position,created_at,updated_at)
                VALUES('row-1','tools-1',1,'T1','FLAT END MILL D10',1,1,1,'1',$at,$at),
                       ('row-2','tools-1',2,'T2','DRILL 8.4',1,1,1,'2',$at,$at),
                       ('row-3','tools-1',3,'T3','PROBE',0,0,1,NULL,$at,$at);
                INSERT INTO process_revisions(
                    id,case_operation_id,revision_number,is_active,tool_table_release_id,created_at,created_by,
                    change_description,version,updated_at,manufacturing_program_id)
                VALUES('process-1','case-operation-package',1,1,'tools-1',$at,'nc-user','Initial',1,$at,
                       'case-operation:case-operation-package');
                INSERT INTO manufacturing_program_revision_outputs(
                    id,process_revision_id,case_operation_id,quantity_per_cycle,display_order,execution_metadata_json,created_at)
                VALUES('output-1','process-1','case-operation-package',1,0,'{}',$at);
                INSERT INTO postprocessors(id,name,is_active,version,created_at,updated_at)
                VALUES('post-1','Haas4x',1,1,$at,$at);
                INSERT INTO machine_supported_postprocessors(machine_id,postprocessor_id,created_at,updated_at)
                VALUES('machine-package','post-1',$at,$at);
                INSERT INTO gcode_releases(
                    id,case_operation_id,process_revision_id,postprocessor_id,post_specific_revision,
                    original_file_name,stored_relative_path,file_size,file_hash,released_at,released_by,
                    change_scope,release_comment,tool_table_release_id,created_at,updated_at)
                VALUES('gcode-1','case-operation-package','process-1','post-1',1,'main.nc',$gcodePath,$gcodeSize,$gcodeHash,
                       $at,'nc-user','NEW_PROCESS_REVISION','Initial','tools-1',$at,$at);
                INSERT INTO gcode_release_verification_hooks(
                    gcode_release_id,hook_version,invocation_kind,invocation_number,nc_identity_token,line_number,created_at,updated_at)
                VALUES('gcode-1',1,'G65',9002,483921,3,$at,$at);
                INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity)
                VALUES('batch-package','case-package','B-PKG','waiting',10);
                INSERT INTO batch_operations(
                    id,production_batch_id,source_case_operation_id,operation_number,route_position,name,
                    required_machine_type,setup_seconds,cycle_seconds,status)
                VALUES('operation-package','batch-package','case-operation-package',10,0,'Finish','mill',60,60,'not_started');
                INSERT INTO machine_assignments(id,batch_operation_id,machine_id,backlog_position)
                VALUES('assignment-package','operation-package','machine-package',0);
                UPDATE machine_assignments SET selected_gcode_release_id='gcode-1' WHERE id='assignment-package';
                INSERT INTO tool_offset_readiness_records(id,batch_operation_id,machine_id,process_revision_id,gcode_release_id,
                    status,confirmed_at,confirmed_by,recorded_at)
                VALUES('offsets-ready','operation-package','machine-package','process-1','gcode-1','READY',$at,'tool-room',$at);
                """;
            command.Parameters.AddWithValue("$workingFolder", Path.Combine(releaseRoot, "working"));
            command.Parameters.AddWithValue("$toolPath", toolRelative);
            command.Parameters.AddWithValue("$toolSize", toolBytes.Length);
            command.Parameters.AddWithValue("$gcodePath", gcodeRelative);
            command.Parameters.AddWithValue("$gcodeSize", gcodeBytes.Length);
            command.Parameters.AddWithValue("$toolHash", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(toolBytes)));
            command.Parameters.AddWithValue("$gcodeHash", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(gcodeBytes)));
            command.Parameters.AddWithValue("$at", now);
            await command.ExecuteNonQueryAsync();

            if (verificationEnabled)
            {
                await using var verification = connection.CreateCommand();
                verification.CommandText = """
                    INSERT INTO cnc_verification_settings(
                        machine_id,dprint_transport,dprint_port,challenge_program_number,verify_program_number,
                        custom_gcode_alias,nonce_variable,response_variable,verification_state_variable,
                        release_token_variable,expected_macro_version,response_code_digits,verification_timeout_seconds,
                        enabled,version,created_at,updated_at,finalize_program_number,event_sequence_variable)
                    VALUES('machine-package','HAAS_DPRNT_TCP',8080,9001,9002,NULL,10501,500,10502,10503,
                           10,6,120,1,1,$at,$at,9003,10504);
                    """;
                verification.Parameters.AddWithValue("$at", now);
                await verification.ExecuteNonQueryAsync();
            }
        }
    }
}
