using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.ProductionRuns;

public sealed class ProductionRunCancelApiTests
{
    [Fact]
    public async Task Cancelling_a_started_run_unassigns_the_machine_and_returns_the_operation_to_not_started()
    {
        await RunAsync(async (app, client) =>
        {
            await SeedAsync(app.Services);
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "run-client");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");

            // Start the run and record one cycle so both coupled outputs have produced_quantity > 0
            // before cancelling, so the assertions below confirm progress is preserved, not zeroed.
            using var cycle = new HttpRequestMessage(
                HttpMethod.Post, "/api/v1/production-runs/run-1/programs/program-1/cycles")
            {
                Content = JsonContent.Create(new { source = "TEST", sourceEventId = "event-1", observedAt = "2026-08-23T12:00:00Z" })
            };
            cycle.Headers.TryAddWithoutValidation("If-Match", "\"production-run:run-1:v1\"");
            using var cycleResponse = await client.SendAsync(cycle);
            cycleResponse.EnsureSuccessStatusCode();

            using var cancel = new HttpRequestMessage(HttpMethod.Post, "/api/v1/production-runs/run-1/cancel")
            {
                Content = JsonContent.Create(new { reason = "test stop" })
            };
            cancel.Headers.TryAddWithoutValidation("If-Match", "\"production-run:run-1:v2\"");
            using var cancelResponse = await client.SendAsync(cancel);
            var body = await cancelResponse.Content.ReadAsStringAsync();
            Assert.True(HttpStatusCode.OK == cancelResponse.StatusCode, body);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("CANCELLED", json.RootElement.GetProperty("status").GetString());

            var db = app.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await db.OpenConnectionAsync();

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM machine_assignments WHERE production_run_id='run-1';";
                Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT status,actual_start,actual_machine_id FROM batch_operations WHERE id='op-a';";
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("not_started", reader.GetString(0));
                Assert.True(reader.IsDBNull(1));
                Assert.True(reader.IsDBNull(2));
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT batch_operation_id,produced_quantity,status FROM production_run_outputs ORDER BY batch_operation_id;";
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("op-a", reader.GetString(0));
                Assert.Equal(2, reader.GetInt32(1));
                Assert.Equal("ABORTED_REMAINDER_RELEASED", reader.GetString(2));
                Assert.True(await reader.ReadAsync());
                Assert.Equal("op-b", reader.GetString(0));
                Assert.Equal(1, reader.GetInt32(1));
                Assert.Equal("ABORTED_REMAINDER_RELEASED", reader.GetString(2));
            }
        });
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<SqliteDatabase>();
        await using var c = await db.OpenConnectionAsync();
        await using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO working_calendars(id,name,time_zone_id)VALUES('calendar','Calendar','UTC');
            INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active,execution_mode,machine_time_factor)VALUES('machine','1','Machine','mill','calendar','active',1,'MANUAL',1);
            INSERT INTO cases(id,part_number,name,working_folder_path)VALUES('case-a','A','A','C:\\A'),('case-b','B','B','C:\\B');
            INSERT INTO case_operations(id,case_id,operation_number,route_position,name)VALUES('case-op-a','case-a',10,0,'A'),('case-op-b','case-b',10,0,'B');
            INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity)VALUES('batch-a','case-a','A-1','in_production',4),('batch-b','case-b','B-1','in_production',2);
            INSERT INTO batch_operations(id,production_batch_id,source_case_operation_id,operation_number,route_position,name,status,actual_start,actual_machine_id)VALUES('op-a','batch-a','case-op-a',10,0,'A','in_progress','2026-08-23T10:00:00Z','machine'),('op-b','batch-b','case-op-b',10,0,'B','in_progress','2026-08-23T10:00:00Z','machine');
            INSERT INTO production_runs(id,status,shared_setup_seconds,setup_snapshot_json,structure_locked_at,version,created_at,updated_at)VALUES('run-1','PLANNED',0,'{}',NULL,1,'2026-08-23T10:00:00Z','2026-08-23T10:00:00Z');
            INSERT INTO production_run_programs(id,production_run_id,manufacturing_program_id,sequence_position,target_cycle_count,completed_cycle_count,status,cycle_seconds_snapshot,legacy_unmanaged,version,created_at,updated_at)VALUES('program-1','run-1','case-operation:case-op-a',0,2,0,'ACTIVE',5,1,1,'2026-08-23T10:00:00Z','2026-08-23T10:00:00Z');
            INSERT INTO production_run_outputs(id,production_run_program_id,batch_operation_id,quantity_per_cycle,target_quantity,produced_quantity,status,version,created_at,updated_at)VALUES('output-a','program-1','op-a',2,4,0,'ALLOCATED',1,'2026-08-23T10:00:00Z','2026-08-23T10:00:00Z'),('output-b','program-1','op-b',1,2,0,'ALLOCATED',1,'2026-08-23T10:00:00Z','2026-08-23T10:00:00Z');
            INSERT INTO machine_assignments(id,batch_operation_id,machine_id,backlog_position,planning_mode,production_run_id)VALUES('assignment','op-a','machine',0,'manual','run-1');
            UPDATE production_runs SET status='IN_PROGRESS',structure_locked_at='2026-08-23T10:00:00Z' WHERE id='run-1';
            UPDATE edit_tokens SET holder_client_id='run-client',holder_user_id='run-user',generation=1,acquired_at='2026-08-23T10:00:00Z',updated_at='2026-08-23T10:00:00Z' WHERE id=1;
            """;
        await q.ExecuteNonQueryAsync();
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadRunCancelApi", Guid.NewGuid().ToString("N"));
        var app = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5098", $"--Database:Path={Path.Combine(directory, "test.db")}"],
            web => web.UseTestServer());
        try
        {
            await app.StartAsync();
            using var client = app.GetTestClient();
            await test(app, client);
            await app.StopAsync();
        }
        finally
        {
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
