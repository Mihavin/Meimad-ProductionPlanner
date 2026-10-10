using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Cases;

public sealed class CaseOperationCamSetupApiTests
{
    private const string Route = "/api/v1/cases/case-1/operations/case-op-1/cam-setup";

    private const string CimatronBody = """
        {"source":"cimatron","workOffset":"g54.1  p3","originMm":{"x":-12.5,"y":40,"z":25.125},
         "rotationDeg":{"x":0,"y":-90,"z":180},"notes":"  UCS MODEL_ZERO_OP10  "}
        """;

    [Fact]
    public async Task Cam_setup_is_created_read_updated_and_deleted_with_entity_tags()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            var operationVersionBefore = await ScalarAsync(application.Services, "SELECT version || '|' || updated_at FROM case_operations WHERE id = 'case-op-1';");
            var planningBefore = await ScalarAsync(application.Services, "SELECT revision - non_planning_revision FROM projection_revision WHERE id = 1;");

            using (var none = await client.GetAsync(Route))
                await AssertErrorAsync(none, HttpStatusCode.NotFound, "cam_setup_not_found");

            using var created = await SendAsync(client, HttpMethod.Put, Route, CimatronBody, ifNoneMatch: "*");
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal("\"case-operation-cam-setup:case-op-1:v1\"", created.Headers.ETag?.Tag);
            using (var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
            {
                var root = json.RootElement;
                Assert.Equal("case-op-1", root.GetProperty("caseOperationId").GetString());
                Assert.Equal("cimatron", root.GetProperty("source").GetString());
                Assert.Equal("G54.1 P3", root.GetProperty("workOffset").GetString());
                Assert.Equal(-12.5, root.GetProperty("originMm").GetProperty("x").GetDouble());
                Assert.Equal(25.125, root.GetProperty("originMm").GetProperty("z").GetDouble());
                Assert.Equal(-90, root.GetProperty("rotationDeg").GetProperty("y").GetDouble());
                Assert.Equal("UCS MODEL_ZERO_OP10", root.GetProperty("notes").GetString());
                Assert.Equal(1, root.GetProperty("version").GetInt32());
                Assert.Equal("cam-user", root.GetProperty("updatedBy").GetString());
                Assert.True(root.TryGetProperty("updatedAt", out _));
            }

            using (var read = await client.GetAsync(Route))
            {
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                Assert.Equal("\"case-operation-cam-setup:case-op-1:v1\"", read.Headers.ETag?.Tag);
            }

            using var updated = await SendAsync(client, HttpMethod.Put, Route,
                """{"source":"manual","workOffset":null,"originMm":{"x":1},"notes":null}""",
                ifMatch: "\"case-operation-cam-setup:case-op-1:v1\"");
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            Assert.Equal("\"case-operation-cam-setup:case-op-1:v2\"", updated.Headers.ETag?.Tag);
            using (var json = JsonDocument.Parse(await updated.Content.ReadAsStringAsync()))
            {
                var root = json.RootElement;
                Assert.Equal("manual", root.GetProperty("source").GetString());
                Assert.Equal(JsonValueKind.Null, root.GetProperty("workOffset").ValueKind);
                Assert.Equal(1, root.GetProperty("originMm").GetProperty("x").GetDouble());
                Assert.Equal(0, root.GetProperty("originMm").GetProperty("y").GetDouble());
                Assert.Equal(0, root.GetProperty("rotationDeg").GetProperty("z").GetDouble());
                Assert.Equal(2, root.GetProperty("version").GetInt32());
            }

            // The Case Operation, its planning data and the planning revision are untouched.
            Assert.Equal(operationVersionBefore, await ScalarAsync(application.Services, "SELECT version || '|' || updated_at FROM case_operations WHERE id = 'case-op-1';"));
            Assert.Equal(planningBefore, await ScalarAsync(application.Services, "SELECT revision - non_planning_revision FROM projection_revision WHERE id = 1;"));

            using (var deleted = await SendAsync(client, HttpMethod.Delete, Route, null, ifMatch: "\"case-operation-cam-setup:case-op-1:v2\""))
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            using (var gone = await client.GetAsync(Route))
                await AssertErrorAsync(gone, HttpStatusCode.NotFound, "cam_setup_not_found");
            using (var again = await SendAsync(client, HttpMethod.Delete, Route, null, ifMatch: "\"case-operation-cam-setup:case-op-1:v2\""))
                await AssertErrorAsync(again, HttpStatusCode.NotFound, "cam_setup_not_found");

            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM structured_event_log WHERE event_type = 'case_operation_cam_setup_saved' AND user_id = 'cam-user' AND reason_code = 'planner_entered' AND before_data_json IS NOT NULL;"));
            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM structured_event_log WHERE event_type = 'case_operation_cam_setup_removed';"));
        });
    }

    [Fact]
    public async Task Preconditions_are_required_and_stale_changes_are_refused_with_an_explanation()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);

            using (var noHeader = await SendAsync(client, HttpMethod.Put, Route, CimatronBody))
                await AssertErrorAsync(noHeader, (HttpStatusCode)428, "precondition_required");
            using (var weird = await SendAsync(client, HttpMethod.Put, Route, CimatronBody, ifNoneMatch: "\"case-operation-cam-setup:case-op-1:v1\""))
                await AssertErrorAsync(weird, (HttpStatusCode)428, "precondition_required");
            // Replacing a setup that does not exist is based on stale data.
            using (var missing = await SendAsync(client, HttpMethod.Put, Route, CimatronBody, ifMatch: "\"case-operation-cam-setup:case-op-1:v1\""))
                await AssertErrorAsync(missing, HttpStatusCode.PreconditionFailed, "resource_version_stale");

            using (var created = await SendAsync(client, HttpMethod.Put, Route, CimatronBody, ifNoneMatch: "*"))
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            // A second create: someone else created it first.
            using (var duplicate = await SendAsync(client, HttpMethod.Put, Route, CimatronBody, ifNoneMatch: "*", user: "other-planner"))
            {
                var error = await AssertErrorAsync(duplicate, HttpStatusCode.PreconditionFailed, "resource_version_stale");
                var conflict = error.GetProperty("conflict");
                Assert.Equal("Operation CAM setup", conflict.GetProperty("resource").GetString());
                Assert.Equal("cam-user", conflict.GetProperty("changedBy").GetString());
                Assert.True(conflict.TryGetProperty("changedAt", out var changedAt) && changedAt.ValueKind == JsonValueKind.String);
                Assert.Contains("Refresh", conflict.GetProperty("advice").GetString(), StringComparison.Ordinal);
                Assert.Equal(1, error.GetProperty("details")[0].GetProperty("currentVersion").GetInt32());
            }

            using (var both = await SendAsync(client, HttpMethod.Put, Route, CimatronBody, ifMatch: "\"case-operation-cam-setup:case-op-1:v1\"", ifNoneMatch: "*"))
                await AssertErrorAsync(both, (HttpStatusCode)428, "precondition_required");
            using (var stale = await SendAsync(client, HttpMethod.Put, Route, CimatronBody, ifMatch: "\"case-operation-cam-setup:case-op-1:v7\""))
            {
                var error = await AssertErrorAsync(stale, HttpStatusCode.PreconditionFailed, "resource_version_stale");
                Assert.Equal("cam-user", error.GetProperty("conflict").GetProperty("changedBy").GetString());
            }
            using (var otherTag = await SendAsync(client, HttpMethod.Put, Route, CimatronBody, ifMatch: "\"case-operation-cam-setup:case-op-2:v1\""))
                await AssertErrorAsync(otherTag, HttpStatusCode.PreconditionFailed, "resource_version_stale");

            using (var deleteWithout = await SendAsync(client, HttpMethod.Delete, Route, null))
                await AssertErrorAsync(deleteWithout, (HttpStatusCode)428, "precondition_required");
            using (var deleteStale = await SendAsync(client, HttpMethod.Delete, Route, null, ifMatch: "\"case-operation-cam-setup:case-op-1:v9\""))
                await AssertErrorAsync(deleteStale, HttpStatusCode.PreconditionFailed, "resource_version_stale");

            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT version FROM case_operation_cam_setups WHERE case_operation_id = 'case-op-1';"));
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
                using (var put = await SendAsync(client, HttpMethod.Put, Route, CimatronBody, ifNoneMatch: "*"))
                    await AssertErrorAsync(put, HttpStatusCode.Forbidden, "permission_required");
                using (var delete = await SendAsync(client, HttpMethod.Delete, Route, null, ifMatch: "\"case-operation-cam-setup:case-op-1:v1\""))
                    await AssertErrorAsync(delete, HttpStatusCode.Forbidden, "permission_required");
            }
            Assert.Equal(0L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM case_operation_cam_setups;"));

            using (var created = await SendAsync(client, HttpMethod.Put, Route, CimatronBody, ifNoneMatch: "*"))
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using (client.SignedInWithOnly())
            using (var read = await client.GetAsync(Route))
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using (client.SignedOut())
            using (var anonymous = await client.GetAsync(Route))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            // An Operation of another Case, an unknown Operation and an unknown Case are not found.
            foreach (var path in new[]
                     {
                         "/api/v1/cases/case-1/operations/case-op-other/cam-setup",
                         "/api/v1/cases/case-1/operations/no-such-op/cam-setup",
                         "/api/v1/cases/no-such-case/operations/case-op-1/cam-setup"
                     })
            {
                using (var get = await client.GetAsync(path))
                    await AssertErrorAsync(get, HttpStatusCode.NotFound, "resource_not_found");
                using (var put = await SendAsync(client, HttpMethod.Put, path, CimatronBody, ifNoneMatch: "*"))
                    await AssertErrorAsync(put, HttpStatusCode.NotFound, "resource_not_found");
            }
            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM case_operation_cam_setups;"));
        });
    }

    public static TheoryData<string, string, string> InvalidBodies => new()
    {
        { """{"workOffset":"G54"}""", "source", "required" },
        { """{"source":"mastercam"}""", "source", "invalid_source" },
        { """{"source":"manual","workOffset":"G53"}""", "workOffset", "invalid_work_offset" },
        { """{"source":"manual","workOffset":"G54.1 P1000"}""", "workOffset", "invalid_work_offset" },
        { """{"source":"manual","originMm":{"y":100000.001}}""", "originMm.y", "out_of_range" },
        { """{"source":"manual","rotationDeg":{"z":-361}}""", "rotationDeg.z", "out_of_range" },
        { "{\"source\":\"manual\",\"notes\":\"" + new string('n', 501) + "\"}", "notes", "too_long" }
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Invalid_bodies_are_refused(string body, string field, string code)
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using var response = await SendAsync(client, HttpMethod.Put, Route, body, ifNoneMatch: "*");
            var error = await AssertErrorAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.Contains(error.GetProperty("details").EnumerateArray(),
                detail => detail.GetProperty("field").GetString() == field && detail.GetProperty("code").GetString() == code);
            Assert.Equal(0L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM case_operation_cam_setups;"));
        });
    }

    [Fact]
    public async Task Accepted_work_offsets_and_unknown_fields()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            var version = 0;
            foreach (var (offset, normalized) in new[]
                     {
                         ("G54", "G54"), ("g59", "G59"), ("G54.1P48", "G54.1P48"), ("G154 P99", "G154 P99"),
                         ("G110", "G110"), ("G129", "G129"), ("G15 H1", "G15 H1")
                     })
            {
                using var response = await SendAsync(client, HttpMethod.Put, Route,
                    $$"""{"source":"manual","workOffset":"{{offset}}"}""",
                    ifNoneMatch: version == 0 ? "*" : null,
                    ifMatch: version == 0 ? null : $"\"case-operation-cam-setup:case-op-1:v{version}\"");
                var text = await response.Content.ReadAsStringAsync();
                Assert.True(response.IsSuccessStatusCode, text);
                using var json = JsonDocument.Parse(text);
                Assert.Equal(normalized, json.RootElement.GetProperty("workOffset").GetString());
                version = json.RootElement.GetProperty("version").GetInt32();
            }

            using var unknown = await SendAsync(client, HttpMethod.Put, Route, """{"source":"manual","scale":2}""",
                ifMatch: $"\"case-operation-cam-setup:case-op-1:v{version}\"");
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        });
    }

    [Fact]
    public async Task Deleting_the_operation_removes_its_cam_setup()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using (var created = await SendAsync(client, HttpMethod.Put, Route, CimatronBody, ifNoneMatch: "*"))
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            using (var deleted = await client.DeleteAsync("/api/v1/cases/case-1/operations/case-op-1"))
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Equal(0L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM case_operation_cam_setups;"));
            using var gone = await client.GetAsync(Route);
            await AssertErrorAsync(gone, HttpStatusCode.NotFound, "resource_not_found");
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
        var path = Path.Combine(Path.GetTempPath(), "MeimadPlanner.CamSetup.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5102", $"--Database:Path={Path.Combine(path, "test.db")}"],
            webHost => webHost.UseSignedInTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "cam-setup-client");
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
