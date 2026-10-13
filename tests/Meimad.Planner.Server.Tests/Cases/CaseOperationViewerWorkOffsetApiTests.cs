using System.Net;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Cases;

public sealed class CaseOperationViewerWorkOffsetApiTests
{
    private const string Route = "/api/v1/cases/case-1/operations/case-op-1/viewer-work-offsets";
    private const string Machine = "meimad-machine-m15";
    private const string MachineRoute = Route + "/" + Machine;
    private const string Tag1 = "\"case-operation-viewer-work-offsets:case-op-1/meimad-machine-m15:v1\"";
    private const string Tag2 = "\"case-operation-viewer-work-offsets:case-op-1/meimad-machine-m15:v2\"";

    private const string Body = """
        {"offsets":{"g54":{"x":-3.5627,"Y":2.4546,"z":-450.25,"B":null},"G54.1  p3":{"C":90}}}
        """;

    [Fact]
    public async Task Work_offsets_are_created_listed_replaced_and_deleted_per_viewer_machine()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            var operationBefore = await ScalarAsync(application.Services, "SELECT version || '|' || updated_at FROM case_operations WHERE id = 'case-op-1';");
            var planningBefore = await ScalarAsync(application.Services, "SELECT revision - non_planning_revision FROM projection_revision WHERE id = 1;");

            using (var empty = await client.GetAsync(Route))
            {
                Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
                using var json = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
                Assert.Equal(0, json.RootElement.GetProperty("items").GetArrayLength());
            }

            using (var created = await SendAsync(client, HttpMethod.Put, Route + "/Meimad-Machine-M15", Body, ifNoneMatch: "*"))
            {
                var text = await created.Content.ReadAsStringAsync();
                Assert.True(created.StatusCode == HttpStatusCode.Created, text);
                Assert.Equal(Tag1, created.Headers.ETag?.Tag);
                using var json = JsonDocument.Parse(text);
                var root = json.RootElement;
                Assert.Equal(Machine, root.GetProperty("viewerMachine").GetString());
                Assert.Equal(Tag1, root.GetProperty("entityTag").GetString());
                var g54 = root.GetProperty("offsets").GetProperty("G54");
                Assert.Equal(-3.5627, g54.GetProperty("X").GetDouble());
                Assert.Equal(2.4546, g54.GetProperty("Y").GetDouble());
                Assert.Equal(-450.25, g54.GetProperty("Z").GetDouble());
                Assert.False(g54.TryGetProperty("B", out _));
                Assert.Equal(90, root.GetProperty("offsets").GetProperty("G54.1 P3").GetProperty("C").GetDouble());
                Assert.Equal("cam-user", root.GetProperty("updatedBy").GetString());
            }

            // A second viewer machine of the same Operation is its own set.
            using (var other = await SendAsync(client, HttpMethod.Put, Route + "/haas-umc-500", """{"offsets":{"G55":{"X":1}}}""", ifNoneMatch: "*"))
                Assert.Equal(HttpStatusCode.Created, other.StatusCode);

            using (var replaced = await SendAsync(client, HttpMethod.Put, MachineRoute, """{"offsets":{"G54":{"X":-4}}}""", ifMatch: Tag1))
            {
                Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
                Assert.Equal(Tag2, replaced.Headers.ETag?.Tag);
            }

            using (var list = await client.GetAsync(Route))
            {
                using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
                var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
                Assert.Equal(["haas-umc-500", Machine], items.Select(item => item.GetProperty("viewerMachine").GetString()));
                var offsets = items[1].GetProperty("offsets");
                Assert.Equal(-4, offsets.GetProperty("G54").GetProperty("X").GetDouble());
                Assert.False(offsets.TryGetProperty("G54.1 P3", out _));
                Assert.Equal(2, items[1].GetProperty("version").GetInt32());
            }

            // The Case Operation, its planning data and the planning revision are untouched.
            Assert.Equal(operationBefore, await ScalarAsync(application.Services, "SELECT version || '|' || updated_at FROM case_operations WHERE id = 'case-op-1';"));
            Assert.Equal(planningBefore, await ScalarAsync(application.Services, "SELECT revision - non_planning_revision FROM projection_revision WHERE id = 1;"));

            using (var deleted = await SendAsync(client, HttpMethod.Delete, MachineRoute, null, ifMatch: Tag2))
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            using (var again = await SendAsync(client, HttpMethod.Delete, MachineRoute, null, ifMatch: Tag2))
                await AssertErrorAsync(again, HttpStatusCode.NotFound, "viewer_work_offsets_not_found");
            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM case_operation_viewer_work_offsets;"));

            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM structured_event_log WHERE event_type = 'case_operation_viewer_work_offsets_saved' AND user_id = 'cam-user' AND reason_code = 'planner_entered' AND before_data_json IS NOT NULL;"));
            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM structured_event_log WHERE event_type = 'case_operation_viewer_work_offsets_removed';"));
        });
    }

    [Fact]
    public async Task Preconditions_are_required_and_stale_changes_are_refused_with_an_explanation()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);

            using (var noHeader = await SendAsync(client, HttpMethod.Put, MachineRoute, Body))
                await AssertErrorAsync(noHeader, (HttpStatusCode)428, "precondition_required");
            using (var missing = await SendAsync(client, HttpMethod.Put, MachineRoute, Body, ifMatch: Tag1))
                await AssertErrorAsync(missing, HttpStatusCode.PreconditionFailed, "resource_version_stale");
            using (var created = await SendAsync(client, HttpMethod.Put, MachineRoute, Body, ifNoneMatch: "*"))
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            using (var duplicate = await SendAsync(client, HttpMethod.Put, MachineRoute, Body, ifNoneMatch: "*", user: "other-planner"))
            {
                var error = await AssertErrorAsync(duplicate, HttpStatusCode.PreconditionFailed, "resource_version_stale");
                var conflict = error.GetProperty("conflict");
                Assert.Equal("Operation work offsets", conflict.GetProperty("resource").GetString());
                Assert.Equal("cam-user", conflict.GetProperty("changedBy").GetString());
                Assert.Equal(JsonValueKind.String, conflict.GetProperty("changedAt").ValueKind);
                Assert.Equal(Tag1, error.GetProperty("details")[0].GetProperty("currentEntityTag").GetString());
            }

            using (var stale = await SendAsync(client, HttpMethod.Put, MachineRoute, Body, ifMatch: Tag2))
                await AssertErrorAsync(stale, HttpStatusCode.PreconditionFailed, "resource_version_stale");
            // The tag of another viewer machine does not replace this one.
            using (var otherMachine = await SendAsync(client, HttpMethod.Put, MachineRoute, Body,
                       ifMatch: "\"case-operation-viewer-work-offsets:case-op-1/haas-umc-500:v1\""))
                await AssertErrorAsync(otherMachine, HttpStatusCode.PreconditionFailed, "resource_version_stale");
            using (var deleteWithout = await SendAsync(client, HttpMethod.Delete, MachineRoute, null))
                await AssertErrorAsync(deleteWithout, (HttpStatusCode)428, "precondition_required");
            using (var deleteStale = await SendAsync(client, HttpMethod.Delete, MachineRoute, null, ifMatch: Tag2))
                await AssertErrorAsync(deleteStale, HttpStatusCode.PreconditionFailed, "resource_version_stale");

            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT version FROM case_operation_viewer_work_offsets;"));
        });
    }

    [Fact]
    public async Task Writes_need_cases_edit_reads_need_only_sign_in_and_the_operation_must_belong_to_the_case()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using (client.SignedInWithOnly(Permissions.ManageSetup))
            {
                using (var put = await SendAsync(client, HttpMethod.Put, MachineRoute, Body, ifNoneMatch: "*"))
                    await AssertErrorAsync(put, HttpStatusCode.Forbidden, "permission_required");
                using (var delete = await SendAsync(client, HttpMethod.Delete, MachineRoute, null, ifMatch: Tag1))
                    await AssertErrorAsync(delete, HttpStatusCode.Forbidden, "permission_required");
            }
            Assert.Equal(0L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM case_operation_viewer_work_offsets;"));

            using (client.SignedInWithOnly())
            using (var read = await client.GetAsync(Route))
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using (client.SignedOut())
            using (var anonymous = await client.GetAsync(Route))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            foreach (var path in new[]
                     {
                         "/api/v1/cases/case-1/operations/case-op-other/viewer-work-offsets",
                         "/api/v1/cases/case-1/operations/no-such-op/viewer-work-offsets",
                         "/api/v1/cases/no-such-case/operations/case-op-1/viewer-work-offsets"
                     })
            {
                using (var get = await client.GetAsync(path))
                    await AssertErrorAsync(get, HttpStatusCode.NotFound, "resource_not_found");
                using (var put = await SendAsync(client, HttpMethod.Put, path + "/" + Machine, Body, ifNoneMatch: "*"))
                    await AssertErrorAsync(put, HttpStatusCode.NotFound, "resource_not_found");
            }
            Assert.Equal(0L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM case_operation_viewer_work_offsets;"));
        });
    }

    public static TheoryData<string, string, string, string> InvalidBodies => new()
    {
        { Machine, """{"offsets":{}}""", "offsets", "required" },
        { Machine, """{"offsets":{"G54":{"X":null}}}""", "offsets", "required" },
        { Machine, """{"offsets":{"G53":{"X":1}}}""", "offsets.G53", "invalid_work_offset" },
        { Machine, """{"offsets":{"G54.1 P1000":{"X":1}}}""", "offsets.G54.1 P1000", "invalid_work_offset" },
        { Machine, """{"offsets":{"G54":{"Q":1}}}""", "offsets.G54.Q", "invalid_axis" },
        { Machine, """{"offsets":{"G54":{"Z":100000.5}}}""", "offsets.G54.Z", "out_of_range" },
        { Machine, """{"offsets":{"G54":{"X":1},"g54":{"Y":1}}}""", "offsets.g54", "duplicate_work_offset" },
        { "-bad%20machine", """{"offsets":{"G54":{"X":1}}}""", "viewerMachine", "invalid_viewer_machine" }
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Invalid_bodies_are_refused(string machine, string body, string field, string code)
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using var response = await SendAsync(client, HttpMethod.Put, Route + "/" + machine, body, ifNoneMatch: "*");
            var error = await AssertErrorAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.Contains(error.GetProperty("details").EnumerateArray(),
                detail => detail.GetProperty("field").GetString() == field && detail.GetProperty("code").GetString() == code);
            Assert.Equal(0L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM case_operation_viewer_work_offsets;"));
        });
    }

    [Fact]
    public async Task Unknown_fields_are_refused_and_deleting_the_operation_removes_its_work_offsets()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using (var unknown = await SendAsync(client, HttpMethod.Put, MachineRoute, """{"offsets":{"G54":{"X":1}},"scale":2}""", ifNoneMatch: "*"))
                Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            using (var created = await SendAsync(client, HttpMethod.Put, MachineRoute, Body, ifNoneMatch: "*"))
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            using (var deleted = await client.DeleteAsync("/api/v1/cases/case-1/operations/case-op-1"))
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Equal(0L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM case_operation_viewer_work_offsets;"));
        });
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? body,
        string? ifMatch = null, string? ifNoneMatch = null, string user = "cam-user")
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        request.Headers.TryAddWithoutValidation("X-Meimad-User-Id", user);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        return error.Clone();
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-1', 'PN-1', 'Part', 'C:\Cases\PN-1'),
                   ('case-other', 'PN-2', 'Other', 'C:\Cases\PN-2');
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name)
            VALUES ('case-op-1', 'case-1', 10, 0, 'Mill'),
                   ('case-op-other', 'case-other', 10, 0, 'Mill');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(IServiceProvider services, string sql)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "MeimadPlanner.ViewerWorkOffsets.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5102", $"--Database:Path={Path.Combine(path, "test.db")}"],
            webHost => webHost.UseSignedInTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "viewer-work-offsets-client");
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
