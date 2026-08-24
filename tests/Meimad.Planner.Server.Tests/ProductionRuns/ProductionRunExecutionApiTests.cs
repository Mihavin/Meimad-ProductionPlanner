using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.ProductionRuns;

public sealed class ProductionRunExecutionApiTests
{
    [Fact]
    public async Task One_cycle_advances_all_coupled_outputs_idempotently_and_stops_exactly()
    {
        await RunAsync(async(app,client)=>
        {
            await SeedAsync(app.Services);client.DefaultRequestHeaders.Add("X-Meimad-Client-Id","run-client");client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation","1");
            using var first=Request("\"production-run:run-1:v1\"","event-1");using var response=await client.SendAsync(first);
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("wasDuplicate").GetBoolean());
            var outputs=json.RootElement.GetProperty("run").GetProperty("programs")[0].GetProperty("outputs");
            Assert.Equal(2,outputs[0].GetProperty("producedQuantity").GetInt32());Assert.Equal(1,outputs[1].GetProperty("producedQuantity").GetInt32());
            using var duplicate=Request("\"production-run:run-1:v1\"","event-1");using var duplicateResponse=await client.SendAsync(duplicate);duplicateResponse.EnsureSuccessStatusCode();
            using var duplicateJson=JsonDocument.Parse(await duplicateResponse.Content.ReadAsStringAsync());Assert.True(duplicateJson.RootElement.GetProperty("wasDuplicate").GetBoolean());
            using var second=Request("\"production-run:run-1:v2\"","event-2");using var completed=await client.SendAsync(second);completed.EnsureSuccessStatusCode();
            using var completedJson=JsonDocument.Parse(await completed.Content.ReadAsStringAsync());Assert.Equal("COMPLETED",completedJson.RootElement.GetProperty("run").GetProperty("status").GetString());
            using var extra=Request("\"production-run:run-1:v3\"","event-3");Assert.Equal(HttpStatusCode.Conflict,(await client.SendAsync(extra)).StatusCode);
        });
    }
    private static HttpRequestMessage Request(string tag,string eventId){var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/production-runs/run-1/programs/program-1/cycles"){Content=JsonContent.Create(new{source="TEST",sourceEventId=eventId,observedAt="2026-08-23T12:00:00Z"})};request.Headers.TryAddWithoutValidation("If-Match",tag);return request;}
    private static async Task SeedAsync(IServiceProvider services)
    {
        var db=services.GetRequiredService<SqliteDatabase>();await using var c=await db.OpenConnectionAsync();await using var q=c.CreateCommand();q.CommandText="""
            INSERT INTO working_calendars(id,name,time_zone_id)VALUES('calendar','Calendar','UTC');
            INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active,execution_mode,machine_time_factor)VALUES('machine','1','Machine','mill','calendar','active',1,'MANUAL',1);
            INSERT INTO cases(id,part_number,name,working_folder_path)VALUES('case-a','A','A','C:\\A'),('case-b','B','B','C:\\B');
            INSERT INTO case_operations(id,case_id,operation_number,route_position,name)VALUES('case-op-a','case-a',10,0,'A'),('case-op-b','case-b',10,0,'B');
            INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity)VALUES('batch-a','case-a','A-1','in_production',4),('batch-b','case-b','B-1','in_production',2);
            INSERT INTO batch_operations(id,production_batch_id,source_case_operation_id,operation_number,route_position,name,status)VALUES('op-a','batch-a','case-op-a',10,0,'A','started'),('op-b','batch-b','case-op-b',10,0,'B','started');
            INSERT INTO production_runs(id,status,shared_setup_seconds,setup_snapshot_json,structure_locked_at,version,created_at,updated_at)VALUES('run-1','PLANNED',0,'{}',NULL,1,'2026-08-23T10:00:00Z','2026-08-23T10:00:00Z');
            INSERT INTO production_run_programs(id,production_run_id,manufacturing_program_id,sequence_position,target_cycle_count,completed_cycle_count,status,cycle_seconds_snapshot,legacy_unmanaged,version,created_at,updated_at)VALUES('program-1','run-1','case-operation:case-op-a',0,2,0,'ACTIVE',5,1,1,'2026-08-23T10:00:00Z','2026-08-23T10:00:00Z');
            INSERT INTO production_run_outputs(id,production_run_program_id,batch_operation_id,quantity_per_cycle,target_quantity,produced_quantity,status,version,created_at,updated_at)VALUES('output-a','program-1','op-a',2,4,0,'ALLOCATED',1,'2026-08-23T10:00:00Z','2026-08-23T10:00:00Z'),('output-b','program-1','op-b',1,2,0,'ALLOCATED',1,'2026-08-23T10:00:00Z','2026-08-23T10:00:00Z');
            INSERT INTO machine_assignments(id,batch_operation_id,machine_id,backlog_position,planning_mode,production_run_id)VALUES('assignment','op-a','machine',0,'manual','run-1');
            UPDATE production_runs SET status='IN_PROGRESS',structure_locked_at='2026-08-23T10:00:00Z' WHERE id='run-1';
            UPDATE edit_tokens SET holder_client_id='run-client',holder_user_id='run-user',generation=1,acquired_at='2026-08-23T10:00:00Z',updated_at='2026-08-23T10:00:00Z' WHERE id=1;
            """;await q.ExecuteNonQueryAsync();
    }
    private static async Task RunAsync(Func<WebApplication,HttpClient,Task> test){var directory=Path.Combine(Path.GetTempPath(),"MeimadRunApi",Guid.NewGuid().ToString("N"));var app=ServerApplication.Build(["--Server:Host=127.0.0.1","--Server:Port=5099",$"--Database:Path={Path.Combine(directory,"test.db")}"],web=>web.UseTestServer());try{await app.StartAsync();using var client=app.GetTestClient();await test(app,client);await app.StopAsync();}finally{await app.DisposeAsync();SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}}
}
