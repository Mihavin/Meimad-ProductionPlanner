using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Application.Kitaron;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Kitaron;

/// <summary>
/// The route-master import (OD-038): stations are discovered and decided, machining steps become
/// Case Operations, auxiliary steps become resource requirements with Kitaron ownership, and a
/// planner's deletion or a changed decision is respected by the next synchronization.
/// </summary>
public sealed class KitaronRouteSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Synchronization_discovers_stations_and_imports_operations_and_requirements_from_the_route_master()
    {
        await RunAsync(async (application, client) =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await SeedAuthorityAsync(database);
            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();

            // First pass: only the stations are discovered; every station is undecided.
            var discovery = await repository.ApplyAsync(
                Plan([], [], stations: Stations()), Now, CancellationToken.None);
            Assert.Equal("succeeded", discovery.Status);
            var listed = await client.GetFromJsonAsync<JsonElement>("/api/v1/kitaron/stations");
            var items = listed.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(3, items.Length);
            Assert.All(items, item => Assert.Equal("UNDECIDED", item.GetProperty("importRole").GetString()));
            var doosan = items.Single(item => item.GetProperty("kitaronStationId").GetInt32() == 1046);
            Assert.Equal("MACHINE", doosan.GetProperty("suggestedRole").GetString());
            Assert.Equal(114, doosan.GetProperty("routeRows").GetInt32());
            var inspection = items.Single(item => item.GetProperty("kitaronStationId").GetInt32() == 4);
            Assert.Equal("WORKSTATION", inspection.GetProperty("suggestedRole").GetString());

            // The planner decides the stations through the API (Edit Mode required).
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "route-editor");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
            client.DefaultRequestHeaders.Add("X-Meimad-User-Id", "planner");
            using var decideDoosan = await client.PutAsJsonAsync("/api/v1/kitaron/stations/1046", new
            {
                importRole = "MACHINE", machineType = "Mill 3x", defaultMinutesPerPart = 0, defaultMinutesPerBatch = 0,
                capacityRequired = 1, expectedVersion = doosan.GetProperty("version").GetInt32()
            });
            Assert.Equal(HttpStatusCode.OK, decideDoosan.StatusCode);
            using var decideInspection = await client.PutAsJsonAsync("/api/v1/kitaron/stations/4", new
            {
                importRole = "WORKSTATION", workstationTypeId = "type-inspection", defaultMinutesPerPart = 2,
                defaultMinutesPerBatch = 15, capacityRequired = 1, expectedVersion = inspection.GetProperty("version").GetInt32()
            });
            Assert.Equal(HttpStatusCode.OK, decideInspection.StatusCode);
            using var decideChrome = await client.PutAsJsonAsync("/api/v1/kitaron/stations/41", new
            {
                importRole = "EXTERNAL", externalResourceId = "external-chrome", defaultMinutesPerPart = 0,
                defaultMinutesPerBatch = 0, capacityRequired = 1, expectedVersion = 1
            });
            Assert.Equal(HttpStatusCode.OK, decideChrome.StatusCode);

            // The service builds the plan from the route master with the decisions applied.
            var service = application.Services.GetRequiredService<KitaronSyncService>();
            var mapping = await application.Services.GetRequiredService<KitaronMappingService>().GetAsync(CancellationToken.None);
            var stations = (await application.Services.GetRequiredService<IKitaronStationRepository>().ListAsync(CancellationToken.None))
                .ToDictionary(station => station.KitaronStationId);
            var snapshot = new KitaronSourceSnapshot(
                [PlanningRow("PN-ROUTE", 30, "ייצור\nOld planning-view name"), PlanningRow("PN-VIEW", 10, "View only")],
                [Order("9001", "PN-ROUTE"), Order("9002", "PN-VIEW")],
                [], [],
                [
                    RouteStep("PN-ROUTE", 1, "20", 4, "Incoming inspection"),
                    RouteStep("PN-ROUTE", 2, "30", 1046, "MACHINE PER PS551170", production: 150, setup: 360),
                    RouteStep("PN-ROUTE", 3, "40", 4, "Setup inspection", production: 10, setup: 30),
                    RouteStep("PN-ROUTE", 4, "50", 41, "Chrome plate")
                ],
                Stations());
            var plan = service.BuildPlan(snapshot, mapping.Fields.Where(field => field.Enabled).ToArray(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), mapping.Version, stations);

            // The route master wins for PN-ROUTE; the planning view stays the source for PN-VIEW only.
            var routeOperation = Assert.Single(plan.Operations, item => item.CaseSourceKey == "PN-ROUTE");
            Assert.Equal("MACHINE PER PS551170", routeOperation.Name);
            Assert.Equal("Mill 3x", routeOperation.RequiredMachineType);
            Assert.Equal(360 * 60, routeOperation.SetupSeconds);
            var viewOperation = Assert.Single(plan.Operations, item => item.CaseSourceKey == "PN-VIEW");
            Assert.Equal("View only", viewOperation.Name);
            Assert.Equal(3, plan.Requirements!.Count);
            Assert.Equal(3, plan.Stations!.Count);

            var applied = await repository.ApplyAsync(plan, Now.AddMinutes(1), CancellationToken.None);
            Assert.Equal("succeeded", applied.Status);
            Assert.Equal(2, applied.OperationsCreated);
            Assert.Equal(3, applied.RequirementsCreated);
            Assert.Equal(0, applied.RouteStepsSkipped);
            Assert.Contains("3 auxiliary route step(s) created", applied.Message);

            await using (var connection = await database.OpenConnectionAsync())
            {
                Assert.Equal(3L, await ScalarAsync(connection, "SELECT COUNT(*) FROM operation_resource_requirements WHERE is_active = 1;"));
                Assert.Equal(3L, await ScalarAsync(connection, "SELECT COUNT(*) FROM kitaron_sync_links WHERE source_entity = 'operation_requirement' AND owns_target = 1;"));
                Assert.Equal("BACKWARD", await ScalarAsync(connection, "SELECT direction FROM operation_resource_requirements WHERE step_number = 20;"));
                Assert.Equal("FORWARD", await ScalarAsync(connection, "SELECT direction FROM operation_resource_requirements WHERE step_number = 40;"));
                Assert.Equal(600L, await ScalarAsync(connection, "SELECT duration_per_unit_seconds FROM operation_resource_requirements WHERE step_number = 40;"));
                Assert.Equal(1800L, await ScalarAsync(connection, "SELECT estimated_duration_seconds FROM operation_resource_requirements WHERE step_number = 40;"));
                Assert.Equal(120L, await ScalarAsync(connection, "SELECT duration_per_unit_seconds FROM operation_resource_requirements WHERE step_number = 20;"));
                Assert.Equal("external-chrome", await ScalarAsync(connection, "SELECT external_resource_id FROM operation_resource_requirements WHERE step_number = 50;"));
                Assert.Equal(1L, await ScalarAsync(connection, """
                    SELECT COUNT(*) FROM operation_resource_requirements chrome
                    JOIN operation_resource_requirements setup ON setup.id = chrome.predecessor_requirement_id
                    WHERE chrome.step_number = 50 AND setup.step_number = 40;
                    """));
            }

            // A second pass with the same facts changes nothing.
            var again = await repository.ApplyAsync(plan, Now.AddMinutes(2), CancellationToken.None);
            Assert.Equal(0, again.RequirementsCreated);
            Assert.Equal(0, again.RequirementsUpdated);
            Assert.Equal(3, again.RequirementsMatched);
            Assert.Equal(0, again.OperationsCreated);

            // The requirements are visible on the operation with their Kitaron origin.
            string operationId;
            await using (var connection = await database.OpenConnectionAsync())
                operationId = (string)(await ScalarAsync(connection, "SELECT id FROM case_operations WHERE operation_number = 30;"))!;
            var requirements = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/case-operations/{operationId}/resource-requirements");
            Assert.Equal(3, requirements!.Length);
            Assert.All(requirements, item => Assert.True(item.GetProperty("isKitaronManaged").GetBoolean()));
            Assert.Equal("Setup inspection", requirements.Single(item => item.GetProperty("stepNumber").GetInt32() == 40).GetProperty("name").GetString());
        });
    }

    [Fact]
    public async Task A_deleted_kitaron_step_stays_out_and_a_step_missing_from_the_route_is_deactivated()
    {
        await RunAsync(async (application, client) =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await SeedAuthorityAsync(database);
            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();
            var operationKey = KitaronRoutePlanner.OperationKey("PN-ROUTE", 30);
            var operation = new KitaronSyncOperation(operationKey, "PN-ROUTE", 30, 0, "Mill", "Mill 3x", null, null, "op-hash");
            var deburr = Requirement("PN-ROUTE", operationKey, 40, 0, "Deburr", "type-inspection", null, 180);
            var final = Requirement("PN-ROUTE", operationKey, 50, 1, "Final inspection", "type-inspection", deburr.SourceKey, 600);
            var first = await repository.ApplyAsync(Plan([operation], [deburr, final]), Now, CancellationToken.None);
            Assert.Equal(2, first.RequirementsCreated);

            string finalId;
            int finalVersion;
            await using (var connection = await database.OpenConnectionAsync())
            {
                finalId = (string)(await ScalarAsync(connection, "SELECT id FROM operation_resource_requirements WHERE step_number = 50;"))!;
                finalVersion = (int)(long)(await ScalarAsync(connection, "SELECT version FROM operation_resource_requirements WHERE step_number = 50;"))!;
            }

            // The planner deletes the imported final inspection through the normal endpoint.
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "route-editor");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
            using var delete = await client.DeleteAsync($"/api/v1/resource-requirements/{finalId}?version={finalVersion}");
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

            var second = await repository.ApplyAsync(Plan([operation], [deburr, final]), Now.AddMinutes(1), CancellationToken.None);
            Assert.Equal(0, second.RequirementsCreated);
            Assert.Equal(1, second.RequirementsMatched);
            await using (var connection = await database.OpenConnectionAsync())
            {
                Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM operation_resource_requirements;"));
                Assert.Equal(1L, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM kitaron_suppressed_operations WHERE source_key = 'PN-ROUTE' || char(31) || 'step' || char(31) || '50';"));
            }

            // Kitaron no longer lists the deburr step (station re-decided as Ignore): it is deactivated, not deleted.
            var third = await repository.ApplyAsync(Plan([operation], []), Now.AddMinutes(2), CancellationToken.None);
            Assert.Equal(1, third.RequirementsUpdated);
            await using (var connection = await database.OpenConnectionAsync())
            {
                Assert.Equal(0L, await ScalarAsync(connection, "SELECT is_active FROM operation_resource_requirements WHERE step_number = 40;"));
            }

            // Listed again, it comes back active with the current facts.
            var fourth = await repository.ApplyAsync(Plan([operation], [deburr]), Now.AddMinutes(3), CancellationToken.None);
            Assert.Equal(1, fourth.RequirementsUpdated);
            await using (var connection = await database.OpenConnectionAsync())
            {
                Assert.Equal(1L, await ScalarAsync(connection, "SELECT is_active FROM operation_resource_requirements WHERE step_number = 40;"));
            }

            // Deleting the operation removes its remaining requirement and records the suppression.
            string operationId;
            string caseId;
            await using (var connection = await database.OpenConnectionAsync())
            {
                operationId = (string)(await ScalarAsync(connection, "SELECT id FROM case_operations WHERE operation_number = 30;"))!;
                caseId = (string)(await ScalarAsync(connection, "SELECT case_id FROM case_operations WHERE operation_number = 30;"))!;
            }
            using var deleteOperation = await client.DeleteAsync($"/api/v1/cases/{caseId}/operations/{operationId}");
            Assert.Equal(HttpStatusCode.NoContent, deleteOperation.StatusCode);
            await using (var verify = await database.OpenConnectionAsync())
            {
                Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM operation_resource_requirements;"));
                Assert.Equal(2L, await ScalarAsync(verify, "SELECT COUNT(*) FROM kitaron_suppressed_operations WHERE source_key LIKE '%' || char(31) || 'step' || char(31) || '%';"));
            }
            var fifth = await repository.ApplyAsync(Plan([operation], [deburr]), Now.AddMinutes(4), CancellationToken.None);
            Assert.Equal(0, fifth.OperationsCreated);
            Assert.Equal(0, fifth.RequirementsCreated);
        });
    }

    [Fact]
    public async Task Station_decisions_are_validated_and_need_edit_mode()
    {
        await RunAsync(async (application, client) =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await SeedAuthorityAsync(database);
            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();
            await repository.ApplyAsync(Plan([], [], stations: Stations()), Now, CancellationToken.None);

            using var noEditMode = await client.PutAsJsonAsync("/api/v1/kitaron/stations/4", new
            {
                importRole = "IGNORE", defaultMinutesPerPart = 0, defaultMinutesPerBatch = 0, capacityRequired = 1, expectedVersion = 1
            });
            Assert.Equal(HttpStatusCode.PreconditionRequired, noEditMode.StatusCode);

            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "someone-else");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
            using var wrongHolder = await client.PutAsJsonAsync("/api/v1/kitaron/stations/4", new
            {
                importRole = "IGNORE", defaultMinutesPerPart = 0, defaultMinutesPerBatch = 0, capacityRequired = 1, expectedVersion = 1
            });
            Assert.Equal(HttpStatusCode.Conflict, wrongHolder.StatusCode);

            client.DefaultRequestHeaders.Remove("X-Meimad-Client-Id");
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "route-editor");
            client.DefaultRequestHeaders.Add("X-Meimad-User-Id", "planner");
            using var missingType = await client.PutAsJsonAsync("/api/v1/kitaron/stations/4", new
            {
                importRole = "WORKSTATION", defaultMinutesPerPart = 0, defaultMinutesPerBatch = 0, capacityRequired = 1, expectedVersion = 1
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, missingType.StatusCode);
            using var unknownType = await client.PutAsJsonAsync("/api/v1/kitaron/stations/4", new
            {
                importRole = "WORKSTATION", workstationTypeId = "no-such-type", defaultMinutesPerPart = 0,
                defaultMinutesPerBatch = 0, capacityRequired = 1, expectedVersion = 1
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownType.StatusCode);
            using var unknownStation = await client.PutAsJsonAsync("/api/v1/kitaron/stations/999", new
            {
                importRole = "IGNORE", defaultMinutesPerPart = 0, defaultMinutesPerBatch = 0, capacityRequired = 1, expectedVersion = 1
            });
            Assert.Equal(HttpStatusCode.NotFound, unknownStation.StatusCode);
            using var decided = await client.PutAsJsonAsync("/api/v1/kitaron/stations/4", new
            {
                importRole = "WORKSTATION", workstationTypeId = "type-inspection", defaultMinutesPerPart = 1.5,
                defaultMinutesPerBatch = 0, capacityRequired = 1, notes = "QC room", expectedVersion = 1
            });
            Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
            var value = await decided.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, value.GetProperty("version").GetInt32());
            Assert.Equal("planner", value.GetProperty("decidedBy").GetString());
            using var stale = await client.PutAsJsonAsync("/api/v1/kitaron/stations/4", new
            {
                importRole = "IGNORE", defaultMinutesPerPart = 0, defaultMinutesPerBatch = 0, capacityRequired = 1, expectedVersion = 1
            });
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

            // A later discovery refreshes the Kitaron facts but keeps the decision.
            await repository.ApplyAsync(Plan([], [], stations: [new KitaronDiscoveredStation(4, "Inspection room", "QC", false, 20000, 60, 0)]),
                Now.AddMinutes(5), CancellationToken.None);
            var listed = await client.GetFromJsonAsync<JsonElement>("/api/v1/kitaron/stations");
            var inspection = listed.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("kitaronStationId").GetInt32() == 4);
            Assert.Equal("Inspection room", inspection.GetProperty("stationName").GetString());
            Assert.Equal(20000, inspection.GetProperty("routeRows").GetInt32());
            Assert.Equal("WORKSTATION", inspection.GetProperty("importRole").GetString());
            Assert.Equal("type-inspection", inspection.GetProperty("workstationTypeId").GetString());
        });
    }

    private static KitaronSyncPlan Plan(
        IReadOnlyList<KitaronSyncOperation> operations,
        IReadOnlyList<KitaronSyncRequirement> requirements,
        IReadOnlyList<KitaronDiscoveredStation>? stations = null) => new(
        1,
        [new KitaronSyncCase("PN-ROUTE", "PN-ROUTE", "Routed part", null, null, @"C:\Kitaron\PN-ROUTE", "case-hash")],
        [], operations, [], new HashSet<string>(), [], 1, null, requirements, stations);

    private static KitaronSyncRequirement Requirement(
        string part, string operationKey, int step, int position, string name, string? workstationTypeId,
        string? predecessorKey, int perBatchSeconds, string direction = "FORWARD") => new(
        KitaronRoutePlanner.RequirementKey(part, step), part, operationKey, step, position, name,
        "WORKSTATION", workstationTypeId, null, 1, perBatchSeconds, 0, direction, predecessorKey, $"hash-{step}-{perBatchSeconds}");

    private static IReadOnlyList<KitaronDiscoveredStation> Stations() =>
    [
        new(1046, "DOOSAN-1", null, false, 114, 107, 0),
        new(4, "Inspection", "Inspection room", false, 17576, 55, 0),
        new(41, "Chrome", null, true, 17, 0, 15)
    ];

    private static KitaronSourceRouteStep RouteStep(
        string part, int order, string action, int station, string description, double? production = null, double? setup = null) =>
        new(part, "A", 1, "A", true, false, null, order, order, action, description, null, station, false, production, setup, null);

    private static KitaronSourceRow PlanningRow(string part, int operation, string name) => new(
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["RecordID"] = 500 + operation, ["DetailNumber"] = part, ["DetailName"] = $"{part} name", ["REV"] = "A",
            ["CompanyName"] = "Customer", ["OrderNumber"] = "SO-1", ["OrdAmount"] = 5,
            ["SupplyDate"] = new DateTime(2026, 12, 1), ["ActionNumber"] = operation, ["ActionDescription"] = name,
            ["Station"] = "MILL", ["RootID"] = "WO-1", ["ProductionAmount"] = 5
        });

    private static KitaronSourceOrder Order(string recordId, string part) =>
        new(recordId, part, $"{part} name", "A", "SO-1", 5, new DateTime(2026, 12, 1), false);

    private static async Task SeedAuthorityAsync(SqliteDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json) VALUES ('calendar-r', 'Resources', 'UTC', '{}');
            INSERT INTO workstation_types (id, name, created_at, updated_at) VALUES ('type-inspection', 'Inspection', '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z');
            INSERT INTO external_resources (id, name, supplier_name, promised_lead_time_minutes, safety_buffer_minutes, created_at, updated_at)
            VALUES ('external-chrome', 'Chrome plating', 'Chromate Ltd', 4320, 0, '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z');
            UPDATE edit_tokens SET holder_client_id = 'route-editor', holder_user_id = 'planner', generation = 1,
                acquired_at = '2026-09-25T00:00:00Z', version = version + 1 WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.KitaronRoute.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "route-test.db")}"],
            webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            await test(application, client);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            for (var attempt = 0; Directory.Exists(directory); attempt++)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(100 * (attempt + 1));
                    SqliteConnection.ClearAllPools();
                }
            }
        }
    }
}
