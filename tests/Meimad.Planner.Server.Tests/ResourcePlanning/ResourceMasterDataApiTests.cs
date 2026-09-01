using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.ResourcePlanning;

public sealed class ResourceMasterDataApiTests
{
    [Fact]
    public async Task Workstation_types_skills_instances_and_employee_mapping_are_data_driven()
    {
        var root=Path.Combine(Path.GetTempPath(),"MeimadPlanner.ResourceMaster.Tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var app=ServerApplication.Build(["--Server:Host=127.0.0.1","--Server:Port=5098",$"--Database:Path={Path.Combine(root,"test.db")}"],b=>b.UseTestServer());
        try
        {
            await app.StartAsync();
            await SeedAuthorityAsync(app.Services);
            using var client=app.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id","resource-editor");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation","1");
            using var skillResponse=await client.PostAsJsonAsync("/api/v1/resources/skills",new{name="Future coating skill",description="User-defined"});
            Assert.Equal(HttpStatusCode.OK,skillResponse.StatusCode);
            using var skillJson=JsonDocument.Parse(await skillResponse.Content.ReadAsStringAsync());
            var skillId=skillJson.RootElement.GetProperty("id").GetString()!;
            using var typeResponse=await client.PostAsJsonAsync("/api/v1/resources/workstation-types",new{name="Future finishing station",propertySchemaJson="{\"temperature\":\"number\"}"});
            typeResponse.EnsureSuccessStatusCode();
            using var typeJson=JsonDocument.Parse(await typeResponse.Content.ReadAsStringAsync());
            var typeId=typeJson.RootElement.GetProperty("id").GetString()!;
            using var station=await client.PostAsJsonAsync("/api/v1/resources/workstations",new{name="Station A",workstationTypeId=typeId,workingCalendarId="resource-calendar",capacity=1,capabilities=new[]{"small-parts"},propertiesJson="{\"temperature\":22}"});
            station.EnsureSuccessStatusCode();
            using var assignment=await client.PutAsJsonAsync("/api/v1/resources/employees/employee-resource/skills",new{skillIds=new[]{skillId}});
            assignment.EnsureSuccessStatusCode();
            var savedAssignment=await client.GetFromJsonAsync<JsonElement>("/api/v1/resources/employees/employee-resource/skills");
            Assert.Equal("employee-resource",savedAssignment.GetProperty("employeeId").GetString());
            Assert.Equal(skillId,savedAssignment.GetProperty("skillIds")[0].GetString());
            using var list=await client.GetAsync("/api/v1/resources/workstations");
            using var listJson=JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Assert.Equal("Future finishing station",(await client.GetFromJsonAsync<JsonElement[]>("/api/v1/resources/workstation-types"))![0].GetProperty("name").GetString());
            Assert.Equal("Station A",listJson.RootElement[0].GetProperty("name").GetString());
            Assert.Equal(1,listJson.RootElement[0].GetProperty("capacity").GetInt32());

            using var updateSkill=await client.PatchAsJsonAsync($"/api/v1/resources/skills/{skillId}",new{name="Coating specialist",description="Edited",isActive=true,expectedVersion=1});
            updateSkill.EnsureSuccessStatusCode();
            Assert.Equal(2,(await updateSkill.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32());
            using var blockedDelete=await client.DeleteAsync($"/api/v1/resources/skills/{skillId}?version=2");
            Assert.Equal(HttpStatusCode.UnprocessableEntity,blockedDelete.StatusCode);

            using var disposable=await client.PostAsJsonAsync("/api/v1/resources/skills",new{name="Disposable skill"});
            disposable.EnsureSuccessStatusCode();
            var disposableId=(await disposable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            using var deleted=await client.DeleteAsync($"/api/v1/resources/skills/{disposableId}?version=1");
            deleted.EnsureSuccessStatusCode();
        }
        finally { await app.StopAsync();SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true); }
    }

    private static async Task SeedAuthorityAsync(IServiceProvider services)
    {
        await using var c=await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();await using var q=c.CreateCommand();
        q.CommandText="""
            INSERT INTO working_calendars(id,name,time_zone_id,calendar_json) VALUES('resource-calendar','Resource calendar','UTC','{}');
            INSERT INTO employee_resources(id,employee_number,name,resource_type,skills_json,assigned_calendar_id,is_active,version,created_at,updated_at,first_name,last_name)
            VALUES('employee-resource','E1','Employee One','regular_worker','[]','resource-calendar',1,1,'2026-09-01T00:00:00Z','2026-09-01T00:00:00Z','Employee','One');
            UPDATE edit_tokens SET holder_client_id='resource-editor',holder_user_id='user',generation=1,
                acquired_at='2026-09-01T00:00:00Z',version=version+1 WHERE id=1;
            """;
        await q.ExecuteNonQueryAsync();
    }
}
