using System.Globalization;
using System.Net;
using System.Text.Json;
using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Application.LegacyImport;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.ToolRequirements;

public sealed class ToolRequirementApiTests
{
    [Fact]
    public async Task Planned_operations_give_the_tools_of_the_period_with_copies_materials_and_missing_tool_tables()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            var now = DateTimeOffset.UtcNow;
            await SeedAsync(application, now);
            var query = Query(now.AddHours(-1), now.AddDays(5));

            using var response = await client.GetAsync($"/api/v1/tool-requirements?{query}");
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal(3, root.GetProperty("plannedOperationCount").GetInt32());

            var tools = root.GetProperty("tools").EnumerateArray().ToArray();
            // FIN_10 runs on both Machines at the same time: two copies; the drill once.
            var fin = Assert.Single(tools, tool => tool.GetProperty("toolName").GetString() == "FIN_10");
            Assert.Equal("ALUMINUM", fin.GetProperty("materialGroup").GetString());
            Assert.Equal("END_MILL", fin.GetProperty("toolType").GetString());
            Assert.Equal(10, fin.GetProperty("diameter").GetDouble());
            Assert.Equal(2, fin.GetProperty("copiesNeeded").GetInt32());
            Assert.Equal(2, fin.GetProperty("machines").GetArrayLength());
            var use = fin.GetProperty("uses").EnumerateArray().First();
            Assert.Equal("HOLDER-50_NEW", use.GetProperty("holder").GetString());
            Assert.Equal(34, use.GetProperty("length").GetDouble());
            var drill = Assert.Single(tools, tool => tool.GetProperty("toolName").GetString() == "DRILL_2.5");
            Assert.Equal(2.5, drill.GetProperty("diameter").GetDouble());
            Assert.Equal(1, drill.GetProperty("copiesNeeded").GetInt32());

            var missing = Assert.Single(root.GetProperty("operationsWithoutToolTable").EnumerateArray());
            Assert.Equal("4102", missing.GetProperty("workOrderNumber").GetString());
            Assert.Equal("TITANIUM", missing.GetProperty("materialGroup").GetString());

            using var export = await client.GetAsync($"/api/v1/tool-requirements/export?{query}");
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            await using var stream = new MemoryStream(await export.Content.ReadAsByteArrayAsync());
            var workbook = await new OpenXmlLegacyWorkbookReader().ReadAsync(stream, "tools.xlsx", CancellationToken.None);
            Assert.Equal(["Tools", "Uses", "No tool table"], workbook.Sheets.Select(sheet => sheet.Name));
            var sheet = workbook.Sheets[0];
            var finRow = sheet.Rows.Values.Single(row => row.Values.Any(cell => cell.Value == "FIN_10"));
            Assert.Contains(finRow.Values, cell => cell.Value == "Aluminum");
            Assert.Contains(finRow.Values, cell => cell.Value == "2");
        });
    }

    [Fact]
    public async Task The_period_must_be_ordered_and_bounded()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            var now = DateTimeOffset.UtcNow;
            using var reversed = await client.GetAsync($"/api/v1/tool-requirements?{Query(now, now.AddHours(-1))}");
            Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
            using var tooLong = await client.GetAsync($"/api/v1/tool-requirements?{Query(now, now.AddDays(200))}");
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
            Assert.Contains("tool_requirement_period_too_long", await tooLong.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        });
    }

    private static string Query(DateTimeOffset from, DateTimeOffset to) =>
        $"from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}";

    private static async Task SeedAsync(WebApplication application, DateTimeOffset now)
    {
        var availability = JsonSerializer.Serialize(new
        {
            availability = new[] { new { startsAt = now.AddDays(-1).ToString("O"), endsAt = now.AddDays(10).ToString("O") } }
        });
        var database = application.Services.GetRequiredService<SqliteDatabase>();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO working_calendars (id, name, time_zone_id, calendar_json) VALUES ('calendar-1', 'Always', 'UTC', $calendar);
                INSERT INTO application_settings (key, value) VALUES ('timeline.setup_calendar_json', $calendar);
                INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
                VALUES ('machine-1', 'M-1', 'Mill One', 'mill', 'calendar-1', 'active', 1),
                       ('machine-2', 'M-2', 'Mill Two', 'mill', 'calendar-1', 'active', 1);
                INSERT INTO employee_resources (id, employee_number, name, resource_type, first_name, last_name, skills_json, assigned_calendar_id, is_active)
                VALUES ('resource-setup', 'E-SETUP', 'Setup Worker', 'setup_worker', 'Setup', 'Worker', '["machine-1","machine-2"]', 'calendar-1', 1),
                       ('resource-setup-2', 'E-SETUP-2', 'Second Setup', 'setup_worker', 'Second', 'Setup', '["machine-1","machine-2"]', 'calendar-1', 1),
                       ('resource-qa', 'E-QA', 'QA Worker', 'qa_worker', 'QA', 'Worker', '[]', 'calendar-1', 1),
                       ('resource-regular', 'E-REG', 'Regular Worker', 'regular_worker', 'Regular', 'Worker', '[]', 'calendar-1', 1);
                INSERT INTO cases (id, part_number, name, working_folder_path, material_type)
                VALUES ('case-1', 'PN-1', 'Aluminum part', 'C:\Cases\PN-1', 'AL 7075-T7351'),
                       ('case-2', 'PN-2', 'Titanium part', 'C:\Cases\PN-2', NULL);
                INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity, release_state)
                VALUES ('batch-1', 'case-1', 'B-1', 'waiting', 2, 'released'),
                       ('batch-2', 'case-2', '4102', 'waiting', 2, 'released');
                -- The titanium part has no Case material: Kitaron's raw material of its work order gives it.
                INSERT INTO kitaron_work_orders (work_order_number, part_number, raw_material_id, imported_at)
                VALUES (4102, 'PN-2', '8516', '2026-09-27T00:00:00Z');
                INSERT INTO kitaron_material_orders (
                    source_key, purchase_order_number, line_number, material_number, description, ordered_quantity,
                    closed, source_hash, first_imported_at, last_imported_at, updated_at)
                VALUES ('po-1', '76504', '1', '8516', 'TI-6AL-4V-ANNEALED AMS 4911L Plate 1 "', 1,
                        0, 'hash', '2026-09-27T00:00:00Z', '2026-09-27T00:00:00Z', '2026-09-27T00:00:00Z');
                INSERT INTO case_operations (id, case_id, operation_number, route_position, name, required_machine_type, setup_seconds, cycle_seconds)
                VALUES ('case-op-1', 'case-1', 10, 0, 'First', 'mill', 1800, 1800),
                       ('case-op-2', 'case-1', 20, 1, 'Second', 'mill', 1800, 1800),
                       ('case-op-3', 'case-2', 10, 0, 'Titanium', 'mill', 1800, 1800);
                INSERT INTO batch_operations (id, production_batch_id, source_case_operation_id, operation_number, route_position, name, required_machine_type, setup_seconds, cycle_seconds, status)
                VALUES ('op-1', 'batch-1', 'case-op-1', 10, 0, 'First', 'mill', 1800, 1800, 'not_started'),
                       ('op-2', 'batch-1', 'case-op-2', 20, 1, 'Second', 'mill', 1800, 1800, 'not_started'),
                       ('op-3', 'batch-2', 'case-op-3', 10, 0, 'Titanium', 'mill', 1800, 1800, 'not_started');
                INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
                VALUES ('assignment-1', 'op-1', 'machine-1', 0),
                       ('assignment-2', 'op-2', 'machine-2', 0),
                       ('assignment-3', 'op-3', 'machine-1', 1);
                INSERT INTO tool_table_releases (id, case_operation_id, revision_number, original_file_name, stored_relative_path, file_size, file_hash, released_at, released_by, release_comment, created_at, updated_at, required_tool_count)
                VALUES ('release-1', 'case-op-1', 1, 'TP_MODEL.TOOLS.mht', 'operations/case-op-1/tool-tables/release-1/TP_MODEL.TOOLS.mht', 100, $hash, '2026-09-27T00:00:00Z', 'nc', 'First', '2026-09-27T00:00:00Z', '2026-09-27T00:00:00Z', 2),
                       ('release-2', 'case-op-2', 1, 'TP_MODEL.TOOLS.mht', 'operations/case-op-2/tool-tables/release-2/TP_MODEL.TOOLS.mht', 100, $hash, '2026-09-27T00:00:00Z', 'nc', 'Second', '2026-09-27T00:00:00Z', '2026-09-27T00:00:00Z', 1);
                INSERT INTO tool_table_release_tools (id, tool_table_release_id, row_number, tool_identifier, description, is_required, requires_magazine_position, is_active, created_at, updated_at)
                VALUES ('tool-1', 'release-1', 1, 'T10', 'FIN_10', 1, 1, 1, '2026-09-27T00:00:00Z', '2026-09-27T00:00:00Z'),
                       ('tool-2', 'release-1', 2, 'T12', 'DRILL_2.5', 1, 1, 1, '2026-09-27T00:00:00Z', '2026-09-27T00:00:00Z'),
                       ('tool-3', 'release-2', 1, 'T3', 'FIN_10', 1, 1, 1, '2026-09-27T00:00:00Z', '2026-09-27T00:00:00Z');
                INSERT INTO process_revisions (id, case_operation_id, revision_number, is_active, tool_table_release_id, created_at, created_by, change_description, updated_at)
                VALUES ('process-1', 'case-op-1', 1, 1, 'release-1', '2026-09-27T00:00:00Z', 'nc', 'First', '2026-09-27T00:00:00Z'),
                       ('process-2', 'case-op-2', 1, 1, 'release-2', '2026-09-27T00:00:00Z', 'nc', 'Second', '2026-09-27T00:00:00Z');
                """;
            command.Parameters.AddWithValue("$calendar", availability);
            command.Parameters.AddWithValue("$hash", new string('a', 64));
            await command.ExecuteNonQueryAsync();
        }

        var root = application.Services.GetRequiredService<GCodeArtifactStore>().RootPath;
        await WriteReportAsync(Path.Combine(root, "operations", "case-op-1", "tool-tables", "release-1", "TP_MODEL.TOOLS.mht"),
            ("T10", "FIN_10", "10.", "34.", "HOLDER-50_NEW"), ("T12", "DRILL_2.5", "2.5", "35.", "HOLDER-29_NEW"));
        await WriteReportAsync(Path.Combine(root, "operations", "case-op-2", "tool-tables", "release-2", "TP_MODEL.TOOLS.mht"),
            ("T3", "FIN_10", "10.", "34.", "HOLDER-50_NEW"));
    }

    private static async Task WriteReportAsync(string path, params (string Number, string Name, string Dia, string Length, string Holder)[] tools)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rows = string.Join(Environment.NewLine, tools.Select(tool =>
            $"<tr><td>{tool.Number}</td><td>{tool.Name}</td><td>{tool.Dia}</td><td>{tool.Length}</td><td>20.</td><td>{tool.Length}</td><td>{tool.Holder}</td></tr>"));
        await File.WriteAllTextAsync(path, $"""
            MIME-Version: 1.0
            Content-Type: multipart/related; boundary="cam-boundary"

            --cam-boundary
            Content-Type: text/html; charset="us-ascii"

            <html><body><table>
            <tr><td>Number</td><td>Name</td><td>Dia</td><td>LENGTH</td><td>CUT</td><td>Shank</td><td>Holder</td></tr>
            {rows}
            </table></body></html>
            --cam-boundary--
            """);
    }

    private static async Task RunWithServerAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.ToolRequirements.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "test.db")}",
             $"--GCode:ReleaseRoot={Path.Combine(directory, "releases")}"],
            builder => builder.UseSignedInTestServer());
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
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
