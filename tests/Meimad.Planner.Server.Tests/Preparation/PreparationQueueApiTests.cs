using System.Net;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Preparation;

public sealed class PreparationQueueApiTests
{
    [Fact]
    public async Task Assignment_release_and_tool_facts_drive_exclusive_recomputable_queues()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "MeimadPlanner.Preparation.Tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "test.db");
        try
        {
            await using (var application = Build(databasePath))
            {
                await application.StartAsync();
                await SeedBaseAsync(application.Services);
                using var client = application.GetTestClient();

                Assert.Equal(["assigned-op"], await IdsAsync(client, "PROGRAMMING_PENDING"));
                Assert.Empty(await IdsAsync(client, "TOOL_PREPARATION_PENDING"));
                Assert.Empty(await IdsAsync(client, "SETUP_PENDING"));
                using (var forbiddenMutation = await client.PostAsync(
                    "/api/v1/preparation-queues/PROGRAMMING_PENDING",
                    new StringContent("{}")))
                    Assert.Equal(HttpStatusCode.MethodNotAllowed, forbiddenMutation.StatusCode);

                await ReleaseNcAsync(application.Services);
                Assert.Empty(await IdsAsync(client, "PROGRAMMING_PENDING"));
                Assert.Equal(["assigned-op"], await IdsAsync(client, "TOOL_PREPARATION_PENDING"));
                Assert.Empty(await IdsAsync(client, "SETUP_PENDING"));

                await ConfirmToolsAsync(application.Services);
                Assert.Empty(await IdsAsync(client, "PROGRAMMING_PENDING"));
                Assert.Equal(["assigned-op"], await IdsAsync(client, "TOOL_PREPARATION_PENDING"));
                Assert.Empty(await IdsAsync(client, "SETUP_PENDING"));

                using var invalid = await client.GetAsync("/api/v1/preparation-queues/not-a-stage");
                Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
                await application.StopAsync();
            }

            SqliteConnection.ClearAllPools();
            await using (var restarted = Build(databasePath))
            {
                await restarted.StartAsync();
                using var client = restarted.GetTestClient();
                Assert.Equal(["assigned-op"], await IdsAsync(client, "TOOL_PREPARATION_PENDING"));
                Assert.DoesNotContain("unassigned-op", await IdsAsync(client, "PROGRAMMING_PENDING"));
                await restarted.StopAsync();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static WebApplication Build(string databasePath) => ServerApplication.Build(
        ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={databasePath}"],
        builder => builder.UseTestServer());

    private static async Task<IReadOnlyList<string>> IdsAsync(HttpClient client, string stage)
    {
        using var response = await client.GetAsync($"/api/v1/preparation-queues/{stage}");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("batchOperationId").GetString()!)
            .ToArray();
    }

    private static async Task SeedBaseAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id,name,time_zone_id,calendar_json)
            VALUES ('calendar-1','Calendar','UTC','{}');
            INSERT INTO machines (
                id,number,name,machine_type,working_calendar_id,status,
                execution_mode,usable_tool_positions)
            VALUES ('machine-1','M01','Mill','Mill','calendar-1','available','CNC_GCODE',20);
            INSERT INTO postprocessors (id,name) VALUES ('post-1','Haas');
            INSERT INTO machine_supported_postprocessors (machine_id,postprocessor_id)
            VALUES ('machine-1','post-1');
            INSERT INTO cases (id,part_number,name,working_folder_path)
            VALUES ('case-1','PN-1','Part','C:\Cases\PN-1');
            INSERT INTO case_operations (
                id,case_id,operation_number,route_position,name,setup_seconds,cycle_seconds)
            VALUES
                ('case-op-1','case-1',10,0,'Rough',60,30),
                ('case-op-2','case-1',20,1,'Finish',60,30);
            INSERT INTO production_batches (id,case_id,batch_number,status,planned_quantity)
            VALUES ('batch-1','case-1','B1','waiting',10);
            INSERT INTO batch_operations (
                id,production_batch_id,source_case_operation_id,
                operation_number,route_position,name,setup_seconds,cycle_seconds,status)
            VALUES
                ('assigned-op','batch-1','case-op-1',10,0,'Rough',60,30,'not_started'),
                ('unassigned-op','batch-1','case-op-2',20,1,'Finish',60,30,'not_started');
            INSERT INTO machine_assignments (
                id,batch_operation_id,machine_id,backlog_position)
            VALUES ('assignment-1','assigned-op','machine-1',0);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ReleaseNcAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tool_table_releases (
                id,case_operation_id,revision_number,original_file_name,stored_relative_path,
                file_size,file_hash,released_at,released_by,release_comment,created_at,updated_at,
                required_tool_count)
            VALUES (
                'tools-1','case-op-1',1,'tools.csv','case-op-1/tools.csv',10,
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                '2026-09-01T06:00:00Z','tool-room','Initial','2026-09-01T06:00:00Z',
                '2026-09-01T06:00:00Z',1);
            INSERT INTO tool_table_release_tools (
                id,tool_table_release_id,row_number,tool_identifier,description,is_required,
                requires_magazine_position,is_active,magazine_position,created_at,updated_at)
            VALUES ('tool-row-1','tools-1',1,'T01','End mill',1,1,1,'1',
                    '2026-09-01T06:00:00Z','2026-09-01T06:00:00Z');
            INSERT INTO process_revisions (
                id,case_operation_id,revision_number,is_active,tool_table_release_id,
                created_at,created_by,change_description,updated_at)
            VALUES ('process-1','case-op-1',1,1,'tools-1','2026-09-01T06:00:00Z',
                    'programmer','Initial','2026-09-01T06:00:00Z');
            INSERT INTO gcode_releases (
                id,case_operation_id,process_revision_id,postprocessor_id,post_specific_revision,
                original_file_name,stored_relative_path,file_size,file_hash,released_at,released_by,
                change_scope,release_comment,tool_table_release_id,created_at,updated_at)
            VALUES ('gcode-1','case-op-1','process-1','post-1',1,'O1000.nc',
                    'case-op-1/O1000.nc',20,
                    'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                    '2026-09-01T06:01:00Z','programmer','LOCAL_POST_REVISION','Initial',
                    'tools-1','2026-09-01T06:01:00Z','2026-09-01T06:01:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ConfirmToolsAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tool_offset_readiness_records (
                id,batch_operation_id,machine_id,process_revision_id,gcode_release_id,
                status,confirmed_at,confirmed_by,comment,recorded_at,updated_at)
            VALUES ('offset-ready-1','assigned-op','machine-1','process-1','gcode-1',
                    'READY','2026-09-01T06:02:00Z','tool-room','Prepared',
                    '2026-09-01T06:02:00Z','2026-09-01T06:02:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }
}
