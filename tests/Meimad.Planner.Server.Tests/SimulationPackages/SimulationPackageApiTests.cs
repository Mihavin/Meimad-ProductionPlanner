using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.SimulationPackages;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.SimulationPackage.Geometry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.SimulationPackages;

/// <summary>
/// Server import of Cimatron simulation packages (schema v117): idempotent import, links + Work CS,
/// supersede, stale 409s, blocked 422 reports, Machine mapping order and candidates, stock choice,
/// manual Work CS protection, permissions, the mapping table and the folder watcher.
/// </summary>
public sealed class SimulationPackageApiTests : IDisposable
{
    private const string Op10 = "/api/v1/cases/case-1/operations/case-op-10/simulation-packages";
    private const string Op20 = "/api/v1/cases/case-1/operations/case-op-20/simulation-packages";
    private const string Op30 = "/api/v1/cases/case-1/operations/case-op-30/simulation-packages";
    private readonly GoldenPackages golden = new();

    public void Dispose() => golden.Dispose();

    [Fact]
    public async Task Migration_creates_the_package_tables()
    {
        await RunAsync(async (application, _) =>
        {
            Assert.True(DatabaseMigrator.LatestVersion >= 117);
            foreach (var table in new[] { "simulation_package_imports", "simulation_package_import_files", "simulation_package_machine_mappings" })
                Assert.Equal(1L, await ScalarAsync(application.Services, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';"));
        });
    }

    [Fact]
    public async Task Package_import_links_geometry_saves_work_cs_and_is_idempotent()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            var package = golden.Copy("3-axis");
            var planningBefore = await ScalarAsync(application.Services, "SELECT revision - non_planning_revision FROM projection_revision WHERE id = 1;");

            using var first = await ImportAsync(client, Op10, package, 0);
            var body = await AssertStatusAsync(first, HttpStatusCode.Created);
            Assert.Equal("imported", body.GetProperty("outcome").GetString());
            var import = body.GetProperty("import");
            var importId = import.GetProperty("importId").GetString()!;
            Assert.Equal("machine-10", import.GetProperty("machineId").GetString());
            Assert.Equal("postprocessor", import.GetProperty("machineMatchedBy").GetString());
            Assert.Equal("geometry/stock.stl", import.GetProperty("selectedStockPath").GetString());
            Assert.Equal("2026.0", import.GetProperty("cimatronVersion").GetString());
            Assert.Equal(new[] { 1, 2 }, import.GetProperty("toolNumbers").EnumerateArray().Select(item => item.GetInt32()));
            Assert.Equal(PackageHashing.Sha256OfFile(Path.Combine(package, "manifest.json")), import.GetProperty("manifestSha256").GetString());
            Assert.False(body.GetProperty("report").GetProperty("hasBlockingErrors").GetBoolean());

            Assert.Equal("fixture,fixture,part,stock", await ScalarAsync(application.Services,
                "SELECT group_concat(kind, ',') FROM (SELECT kind FROM case_model_files WHERE case_operation_id = 'case-op-10' ORDER BY kind);"));
            Assert.Equal(Path.Combine(package, "geometry", "stock.stl"), await ScalarAsync(application.Services,
                "SELECT file_path FROM case_model_files WHERE case_operation_id = 'case-op-10' AND kind = 'stock';"));
            Assert.Equal("cimatron|G54|0.0|0.0|0.0|0.0|0.0|0.0|1", await ScalarAsync(application.Services, """
                SELECT source || '|' || work_offset || '|' || origin_x_mm || '|' || origin_y_mm || '|' || origin_z_mm || '|'
                       || rotation_x_deg || '|' || rotation_y_deg || '|' || rotation_z_deg || '|' || version
                FROM case_operation_cam_setups WHERE case_operation_id = 'case-op-10';
                """));
            Assert.Equal(6L, await ScalarAsync(application.Services, $"SELECT COUNT(*) FROM simulation_package_import_files WHERE import_id = '{importId}';"));
            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM structured_event_log WHERE event_type = 'simulation_package_imported';"));
            // The package tables advance only the non-planning revision; the four model-file links advance the planning
            // revision exactly as links added from the Case workspace do (existing case_model_files triggers).
            Assert.Equal((long)planningBefore! + 4, await ScalarAsync(application.Services, "SELECT revision - non_planning_revision FROM projection_revision WHERE id = 1;"));

            // Same manifest again: nothing changes.
            using var again = await ImportAsync(client, Op10, package, 1, importId);
            var repeated = await AssertStatusAsync(again, HttpStatusCode.OK);
            Assert.Equal("already_imported", repeated.GetProperty("outcome").GetString());
            Assert.Equal(importId, repeated.GetProperty("import").GetProperty("importId").GetString());
            Assert.Equal(4L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM case_model_files;"));
            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM simulation_package_imports;"));
            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT version FROM case_operation_cam_setups;"));

            // A new export of the same Operation supersedes the old package and replaces only its links.
            await AddUnrelatedModelFileAsync(application.Services);
            File.WriteAllBytes(Path.Combine(package, "geometry", "part.stl"), MeshFiles.WriteBinaryStl(MeshFiles.Box(new(6, 6, -25), new(94, 54, 0))));
            GoldenPackages.EditManifest(package, _ => { }, rehash: true);
            using var stale = await ImportAsync(client, Op10, package, 1, expectedCurrentImportId: null);
            var conflict = await AssertErrorAsync(stale, HttpStatusCode.Conflict, "simulation_package_import_changed");
            Assert.Equal("cam-user", conflict.GetProperty("conflict").GetProperty("changedBy").GetString());

            using var reexport = await ImportAsync(client, Op10, package, 1, importId);
            var second = await AssertStatusAsync(reexport, HttpStatusCode.Created);
            var secondId = second.GetProperty("import").GetProperty("importId").GetString()!;
            Assert.Equal($"superseded|{secondId}", await ScalarAsync(application.Services,
                $"SELECT status || '|' || superseded_by_import_id FROM simulation_package_imports WHERE id = '{importId}';"));
            Assert.Equal(5L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM case_model_files;"));
            Assert.Equal(2L, await ScalarAsync(application.Services, "SELECT version FROM case_operation_cam_setups;"));

            using var list = await client.GetAsync(Op10);
            var items = (await AssertStatusAsync(list, HttpStatusCode.OK)).GetProperty("items");
            Assert.Equal(new[] { "imported", "superseded" }, items.EnumerateArray().Select(item => item.GetProperty("status").GetString()));
            using var read = await client.GetAsync($"/api/v1/simulation-packages/{secondId}");
            var detail = await AssertStatusAsync(read, HttpStatusCode.OK);
            Assert.Equal(6, detail.GetProperty("files").GetArrayLength());
            Assert.Equal("UCS_OP010", detail.GetProperty("coordinateSystem").GetProperty("name").GetString());
        });
    }

    [Fact]
    public async Task Stale_cam_setup_version_is_refused_with_who_and_when()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using (var created = await SendAsync(client, HttpMethod.Put, "/api/v1/cases/case-1/operations/case-op-10/cam-setup",
                       """{"source":"cimatron","workOffset":"G54"}""", ifNoneMatch: "*", user: "other-planner"))
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            using var response = await ImportAsync(client, Op10, golden.Copy("3-axis"), 0);
            var error = await AssertErrorAsync(response, HttpStatusCode.Conflict, "cam_setup_changed");
            Assert.Equal("other-planner", error.GetProperty("conflict").GetProperty("changedBy").GetString());
            Assert.Equal(1, error.GetProperty("details")[0].GetProperty("currentCamSetupVersion").GetInt32());
            Assert.Equal(0L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM simulation_package_imports;"));
            Assert.Equal(0L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM case_model_files;"));

            using var missing = await SendAsync(client, HttpMethod.Post, Op10, JsonSerializer.Serialize(new { packagePath = golden.Root }));
            await AssertErrorAsync(missing, HttpStatusCode.PreconditionRequired, "precondition_required");
        });
    }

    [Fact]
    public async Task Blocked_package_answers_422_with_the_report_and_changes_nothing()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using var response = await ImportAsync(client, Op10, golden.Copy("broken/hash-mismatch"), 0);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using var json = JsonDocument.Parse(text);
            Assert.Equal("simulation_package_blocked", json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains(json.RootElement.GetProperty("report").GetProperty("diagnostics").EnumerateArray(),
                item => item.GetProperty("code").GetString() == "file_hash_mismatch" && item.GetProperty("severity").GetString() == "error"
                        && item.GetProperty("context").GetProperty("path").GetString() == "nc/O1001.nc");
            Assert.Equal(0L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM simulation_package_imports;"));
            Assert.Equal(0L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM case_model_files;"));
            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM structured_event_log WHERE event_type = 'simulation_package_import_blocked';"));

            var outside = Path.Combine(Path.GetTempPath(), "MeimadPlanner.SimulationPackages.Outside", Guid.NewGuid().ToString("N"));
            using var refused = await ImportAsync(client, Op10, outside, 0);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("package_root_not_approved", await refused.Content.ReadAsStringAsync());

            using var incomplete = await ImportAsync(client, Op10, golden.Copy("broken/missing-complete"), 0);
            Assert.Contains("package_incomplete", await incomplete.Content.ReadAsStringAsync());
        });
    }

    [Fact]
    public async Task Machine_mapping_lists_ambiguous_candidates_and_never_picks_one()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            var package = golden.Copy("3plus2");
            using var ambiguous = await ImportAsync(client, Op20, package, 0);
            var report = await AssertBlockedAsync(ambiguous, "machine_mapping_ambiguous");
            Assert.Equal(new[] { "machine-14", "machine-15" },
                report.GetProperty("machineCandidates").EnumerateArray().Select(item => item.GetProperty("machineId").GetString()));
            Assert.All(report.GetProperty("machineCandidates").EnumerateArray(),
                item => Assert.Equal("postprocessor", item.GetProperty("matchedBy").GetString()));

            using var notCandidate = await ImportAsync(client, Op20, package, 0, machineId: "machine-10");
            await AssertBlockedAsync(notCandidate, "machine_selection_invalid");

            using (client.SignedInWithOnly(Permissions.EditCases))
            {
                using var remember = await ImportAsync(client, Op20, package, 0, machineId: "machine-15", remember: true);
                Assert.Equal(HttpStatusCode.Forbidden, remember.StatusCode);
            }

            using var chosen = await ImportAsync(client, Op20, package, 0, machineId: "machine-15", remember: true);
            var import = (await AssertStatusAsync(chosen, HttpStatusCode.Created)).GetProperty("import");
            Assert.Equal("machine-15", import.GetProperty("machineId").GetString());
            Assert.Equal("planner_selected", import.GetProperty("machineMatchedBy").GetString());
            // Mode B: the Work CS is the UCS origin and RzRyRx rotation in part-model coordinates.
            Assert.Equal("cimatron|G55|80.0|0.0|40.0|0.0|90.0|0.0", await ScalarAsync(application.Services, """
                SELECT source || '|' || work_offset || '|' || origin_x_mm || '|' || origin_y_mm || '|' || origin_z_mm || '|'
                       || rotation_x_deg || '|' || rotation_y_deg || '|' || rotation_z_deg
                FROM case_operation_cam_setups WHERE case_operation_id = 'case-op-20';
                """));
            Assert.Equal("external_id|UMC500_01|machine-15", await ScalarAsync(application.Services,
                "SELECT match_field || '|' || match_value || '|' || machine_id FROM simulation_package_machine_mappings;"));
            // The mapping never assigns the Operation to a Machine.
            Assert.Equal(0L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM machine_assignments;"));
        });
    }

    [Fact]
    public async Task External_id_mapping_wins_and_unresolved_lists_every_machine()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using (client.SignedInWithOnly(Permissions.EditCases))
            {
                using var forbidden = await SendAsync(client, HttpMethod.Post, "/api/v1/simulation-packages/machine-mappings",
                    """{"matchField":"external_id","matchValue":"UMC500_01","machineId":"machine-14"}""");
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }

            using (var added = await SendAsync(client, HttpMethod.Post, "/api/v1/simulation-packages/machine-mappings",
                       """{"matchField":"external_id","matchValue":"umc500_01","machineId":"machine-14"}"""))
                await AssertStatusAsync(added, HttpStatusCode.Created);
            using (var duplicate = await SendAsync(client, HttpMethod.Post, "/api/v1/simulation-packages/machine-mappings",
                       """{"matchField":"external_id","matchValue":"UMC500_01","machineId":"machine-15"}"""))
                await AssertErrorAsync(duplicate, HttpStatusCode.Conflict, "machine_mapping_exists");

            using var mapped = await ImportAsync(client, Op20, golden.Copy("3plus2"), 0);
            var import = (await AssertStatusAsync(mapped, HttpStatusCode.Created)).GetProperty("import");
            Assert.Equal("machine-14", import.GetProperty("machineId").GetString());
            Assert.Equal("external_id", import.GetProperty("machineMatchedBy").GetString());

            var unknown = golden.Copy("5-axis");
            GoldenPackages.EditManifest(unknown, node => node["machine"] = new JsonObject
            {
                ["externalId"] = "DMU_99", ["name"] = "DMG DMU 99", ["postprocessor"] = "HEIDENHAIN_DMU"
            });
            using var unresolved = await ImportAsync(client, Op30, unknown, 0);
            var report = await AssertBlockedAsync(unresolved, "machine_mapping_unresolved");
            Assert.Equal(3, report.GetProperty("machineCandidates").GetArrayLength());

            // The explicit mapping table (step 3) resolves the postprocessor name.
            using (var table = await SendAsync(client, HttpMethod.Post, "/api/v1/simulation-packages/machine-mappings",
                       """{"matchField":"postprocessor","matchValue":"HEIDENHAIN_DMU","machineId":"machine-15"}"""))
                await AssertStatusAsync(table, HttpStatusCode.Created);
            using var viaTable = await ImportAsync(client, Op30, unknown, 0);
            Assert.Equal("mapping_table", (await AssertStatusAsync(viaTable, HttpStatusCode.Created)).GetProperty("import").GetProperty("machineMatchedBy").GetString());

            using var mappings = await client.GetAsync("/api/v1/simulation-packages/machine-mappings");
            Assert.Equal(2, (await AssertStatusAsync(mappings, HttpStatusCode.OK)).GetProperty("items").GetArrayLength());
        });
    }

    [Fact]
    public async Task Compatible_pair_matches_the_machine_name()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            var package = golden.Copy("5-axis");
            GoldenPackages.EditManifest(package, node => node["machine"] = new JsonObject { ["name"] = "haas vf 3" });
            using var response = await ImportAsync(client, Op30, package, 0);
            var import = (await AssertStatusAsync(response, HttpStatusCode.Created)).GetProperty("import");
            Assert.Equal("machine-10", import.GetProperty("machineId").GetString());
            Assert.Equal("compatible_pair", import.GetProperty("machineMatchedBy").GetString());
        });
    }

    [Fact]
    public async Task Several_stock_candidates_need_the_planners_choice()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            var package = golden.Copy("3-axis");
            File.WriteAllBytes(Path.Combine(package, "geometry", "stock-op010-from-op005.stl"),
                MeshFiles.WriteBinaryStl(MeshFiles.Box(new(0, 0, -28), new(100, 60, 0))));
            GoldenPackages.EditManifest(package, node => node["files"]!["stock"]!.AsArray().Add(new JsonObject
            {
                ["path"] = "geometry/stock-op010-from-op005.stl", ["kind"] = "remaining", ["sourceOperation"] = "005",
                ["sha256"] = new string('0', 64)
            }), rehash: true);

            using var required = await ImportAsync(client, Op10, package, 0);
            var report = await AssertBlockedAsync(required, "stock_selection_required");
            Assert.Equal(2, report.GetProperty("stockCandidates").GetArrayLength());
            using var invalid = await ImportAsync(client, Op10, package, 0, stock: "geometry/part.stl");
            await AssertBlockedAsync(invalid, "stock_selection_invalid");

            using var chosen = await ImportAsync(client, Op10, package, 0, stock: "geometry/stock-op010-from-op005.stl");
            var import = (await AssertStatusAsync(chosen, HttpStatusCode.Created)).GetProperty("import");
            Assert.Equal("geometry/stock-op010-from-op005.stl", import.GetProperty("selectedStockPath").GetString());
            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM case_model_files WHERE kind = 'stock';"));
            Assert.Equal("Remaining stock from OP005: stock-op010-from-op005.stl", await ScalarAsync(application.Services,
                "SELECT label FROM case_model_files WHERE kind = 'stock';"));
            Assert.Equal("remainingStock|1|initialStock|0", await ScalarAsync(application.Services, """
                SELECT group_concat(role || '|' || selected, '|') FROM (SELECT role, selected FROM simulation_package_import_files
                WHERE role LIKE '%Stock' ORDER BY role DESC);
                """));
        });
    }

    [Fact]
    public async Task Manual_work_cs_is_replaced_only_after_confirmation()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using (var manual = await SendAsync(client, HttpMethod.Put, "/api/v1/cases/case-1/operations/case-op-10/cam-setup",
                       """{"source":"manual","workOffset":"G54","originMm":{"x":5}}""", ifNoneMatch: "*"))
                Assert.Equal(HttpStatusCode.Created, manual.StatusCode);
            var package = golden.Copy("3-axis");
            using var refused = await ImportAsync(client, Op10, package, 1);
            await AssertBlockedAsync(refused, "work_cs_manual_conflict");
            Assert.Equal("manual", await ScalarAsync(application.Services, "SELECT source FROM case_operation_cam_setups;"));

            using var confirmed = await ImportAsync(client, Op10, package, 1, overwriteManual: true);
            await AssertStatusAsync(confirmed, HttpStatusCode.Created);
            Assert.Equal("cimatron|0.0|2", await ScalarAsync(application.Services, "SELECT source || '|' || origin_x_mm || '|' || version FROM case_operation_cam_setups;"));
        });
    }

    [Fact]
    public async Task Import_needs_cases_edit_and_reads_need_only_sign_in()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            var package = golden.Copy("3-axis");
            using (client.SignedInWithOnly(Permissions.ManageSetup))
            {
                using var forbidden = await ImportAsync(client, Op10, package, 0);
                await AssertErrorAsync(forbidden, HttpStatusCode.Forbidden, "permission_required");
            }

            using (client.SignedOut())
            {
                using var signedOut = await client.GetAsync(Op10);
                Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
            }

            using (client.SignedInWithOnly())
            {
                using var list = await client.GetAsync("/api/v1/simulation-packages?status=imported");
                await AssertStatusAsync(list, HttpStatusCode.OK);
                using var validate = await SendAsync(client, HttpMethod.Post, "/api/v1/simulation-packages/validate",
                    JsonSerializer.Serialize(new { packagePath = package, caseId = "case-1" }));
                var report = (await AssertStatusAsync(validate, HttpStatusCode.OK)).GetProperty("report");
                Assert.False(report.GetProperty("hasBlockingErrors").GetBoolean());
                Assert.Equal(6, report.GetProperty("files").GetArrayLength());
                using var mappings = await SendAsync(client, HttpMethod.Delete, "/api/v1/simulation-packages/machine-mappings/x", null);
                Assert.Equal(HttpStatusCode.Forbidden, mappings.StatusCode);
            }

            Assert.Equal(0L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM simulation_package_imports;"));
        });
    }

    [Fact]
    public async Task Watcher_imports_complete_packages_once_and_records_blocked_ones()
    {
        await RunAsync(async (application, _) =>
        {
            await SeedAsync(application.Services);
            var good = golden.Copy("3-axis", Path.Combine("Gcode", "GOLDEN-PART", "010", "cimatron-export"));
            var multiStock = golden.Copy("5-axis", Path.Combine("Gcode", "GOLDEN-PART", "030", "cimatron-export"));
            File.Copy(Path.Combine(multiStock, "geometry", "stock.stl"), Path.Combine(multiStock, "geometry", "stock-b.stl"));
            GoldenPackages.EditManifest(multiStock, node => node["files"]!["stock"]!.AsArray().Add(new JsonObject
            {
                ["path"] = "geometry/stock-b.stl", ["kind"] = "remaining", ["sourceOperation"] = "020", ["sha256"] = new string('0', 64)
            }), rehash: true);
            golden.Copy("broken/missing-complete", Path.Combine("Gcode", "GOLDEN-PART", "020", "cimatron-export"));

            var watcher = application.Services.GetRequiredService<SimulationPackageWatcherService>();
            Assert.Equal((1, 1), await watcher.RunOnceAsync(CancellationToken.None));
            Assert.Equal((0, 0), await watcher.RunOnceAsync(CancellationToken.None));
            Assert.Equal("imported|case-op-10|package-watcher", await ScalarAsync(application.Services,
                "SELECT status || '|' || case_operation_id || '|' || imported_by FROM simulation_package_imports WHERE status = 'imported';"));
            var blocked = (string)(await ScalarAsync(application.Services, "SELECT report_json FROM simulation_package_imports WHERE status = 'blocked';"))!;
            Assert.Contains("stock_selection_required", blocked);
            Assert.Equal(good, await ScalarAsync(application.Services, "SELECT package_path FROM simulation_package_imports WHERE status = 'imported';"));
        }, watchRoot: true);
    }

    private static async Task AddUnrelatedModelFileAsync(IServiceProvider services)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO case_model_files (id, case_id, case_operation_id, kind, format, file_path, label, is_primary, sort_order, version, created_at, updated_at)
            VALUES ('manual-link', 'case-1', 'case-op-10', 'fixture', 'stl', 'C:\manual.stl', 'Manual', 0, 99, 1, '2026-10-12T00:00:00Z', '2026-10-12T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static Task<HttpResponseMessage> ImportAsync(
        HttpClient client, string route, string packagePath, int expectedCamSetupVersion, string? expectedCurrentImportId = null,
        string? machineId = null, bool remember = false, string? stock = null, bool overwriteManual = false) =>
        SendAsync(client, HttpMethod.Post, route, JsonSerializer.Serialize(new
        {
            packagePath,
            expectedCamSetupVersion,
            expectedCurrentImportId,
            machineId,
            rememberMachineMapping = remember,
            selectedStockPath = stock,
            overwriteManualWorkCs = overwriteManual
        }));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? body,
        string? ifNoneMatch = null, string user = "cam-user")
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        request.Headers.TryAddWithoutValidation("X-Meimad-User-Id", user);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> AssertStatusAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {text}");
        if (text.Length == 0) return default;
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    private static async Task<JsonElement> AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await AssertStatusAsync(response, status);
        var error = body.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        return error;
    }

    private static async Task<JsonElement> AssertBlockedAsync(HttpResponseMessage response, string code)
    {
        var body = await AssertStatusAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("simulation_package_blocked", body.GetProperty("error").GetProperty("code").GetString());
        var report = body.GetProperty("report");
        Assert.Contains(report.GetProperty("diagnostics").EnumerateArray(), item => item.GetProperty("code").GetString() == code);
        return report;
    }

    private async Task SeedAsync(IServiceProvider services)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-1', 'Day', 'UTC');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active, execution_mode, usable_tool_positions, machine_time_factor)
            VALUES ('machine-10', '10', 'Haas VF-3', 'mill', 'calendar-1', 'active', 1, 'CNC_GCODE', 30, 1.0),
                   ('machine-14', '14', 'Haas UMC-500', 'mill', 'calendar-1', 'active', 1, 'CNC_GCODE', 30, 1.0),
                   ('machine-15', '15', 'Haas UMC-500 SS', 'mill', 'calendar-1', 'active', 1, 'CNC_GCODE', 30, 1.0);
            INSERT INTO postprocessors (id, name, is_active) VALUES ('post-vf3', 'HAAS_NGC_VF3', 1), ('post-umc', 'HAAS_NGC_UMC500', 1);
            INSERT INTO machine_supported_postprocessors (machine_id, postprocessor_id)
            VALUES ('machine-10', 'post-vf3'), ('machine-14', 'post-umc'), ('machine-15', 'post-umc');
            INSERT INTO cases (id, part_number, name, working_folder_path) VALUES ('case-1', 'GOLDEN-PART', 'Golden part', $folder);
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name)
            VALUES ('case-op-10', 'case-1', 10, 0, 'Pocket'), ('case-op-20', 'case-1', 20, 1, 'Side face'), ('case-op-30', 'case-1', 30, 2, 'Swarf');
            """;
        command.Parameters.AddWithValue("$folder", golden.Root);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(IServiceProvider services, string sql)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private async Task RunAsync(Func<WebApplication, HttpClient, Task> test, bool watchRoot = false)
    {
        var path = Path.Combine(Path.GetTempPath(), "MeimadPlanner.SimulationPackageApi.Tests", Guid.NewGuid().ToString("N"));
        var arguments = new List<string> { "--Server:Host=127.0.0.1", "--Server:Port=5102", $"--Database:Path={Path.Combine(path, "test.db")}" };
        if (watchRoot)
        {
            arguments.Add($"--SimulationPackages:ApprovedRoots:0={golden.Root}");
            arguments.Add($"--SimulationPackages:Watcher:Roots:0={golden.Root}");
        }

        var application = ServerApplication.Build([.. arguments], webHost => webHost.UseSignedInTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "package-client");
            await test(application, client);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
