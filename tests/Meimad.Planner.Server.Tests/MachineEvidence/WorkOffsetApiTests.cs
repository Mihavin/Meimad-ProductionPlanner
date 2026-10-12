using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.MachineEvidence;

/// <summary>
/// The measured work offset API: idempotent ingest, current and history, the review of candidates with
/// the parallel-edit conflict rule, the report program, and the sign-in and permission boundaries.
/// </summary>
public sealed class WorkOffsetApiTests
{
    private const string Ingest = "/api/v1/machine-evidence/work-offset";
    private const string View = "/api/v1/machines/machine-10/operations/case-operation-wo/work-offsets";

    private static object Body(string session, double x = 125.402, string release = "654321", string machine = "10") => new
    {
        machineId = machine,
        operationNumber = "020",
        program = "O" + release,
        releaseId = release,
        workOffset = "G54",
        positionMm = new { x, y = -80.016, z = 42.691 },
        rotationDeg = new { a = 0.0, b = 0.0, c = 90.0 },
        source = "DPRNT_TCP",
        sourceSession = session
    };

    [Fact]
    public async Task Ingest_is_idempotent_and_the_operation_view_shows_current_and_history()
    {
        await RunAsync(async client =>
        {
            using var created = await client.PostAsJsonAsync(Ingest, Body("20261012-00017"));
            var first = await JsonAsync(created, HttpStatusCode.Created);
            Assert.False(first.GetProperty("duplicate").GetBoolean());
            var record = first.GetProperty("record");
            Assert.Equal("VALID", record.GetProperty("status").GetString());
            Assert.Equal("machine-10", record.GetProperty("machineId").GetString());
            Assert.Equal("gcode-wo-current", record.GetProperty("ncReleaseId").GetString());
            Assert.Equal(90.0, record.GetProperty("rotationDeg").GetProperty("c").GetDouble());
            Assert.Equal("test-admin", record.GetProperty("receivedBy").GetString());

            using var repeated = await client.PostAsJsonAsync(Ingest, Body("20261012-00017"));
            var second = await JsonAsync(repeated, HttpStatusCode.OK);
            Assert.True(second.GetProperty("duplicate").GetBoolean());
            Assert.Equal(record.GetProperty("workOffsetId").GetString(), second.GetProperty("record").GetProperty("workOffsetId").GetString());

            var view = await GetAsync(client, View + "?offset=G54");
            Assert.Equal("G54", view.GetProperty("workOffset").GetString());
            Assert.Single(view.GetProperty("current").EnumerateArray());
            Assert.Single(view.GetProperty("history").EnumerateArray());
            Assert.Equal(200, (int)(await client.GetAsync(View)).StatusCode);

            using var unknown = await client.GetAsync("/api/v1/machines/no-such/operations/case-operation-wo/work-offsets");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            using var invalid = await client.GetAsync(View + "?offset=G99");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        });
    }

    [Fact]
    public async Task Ingest_refuses_requests_without_identity_and_keeps_unmatched_evidence()
    {
        await RunAsync(async client =>
        {
            foreach (var body in new object[]
            {
                new { operationNumber = "20", releaseId = "654321", workOffset = "G54", positionMm = new { x = 1.0, z = 1.0 }, sourceSession = "s" },
                new { machineId = "10", releaseId = "654321", workOffset = "G54", positionMm = new { x = 1.0, z = 1.0 }, sourceSession = "s" },
                new { machineId = "10", operationNumber = "20", workOffset = "G54", positionMm = new { x = 1.0, z = 1.0 }, sourceSession = "s" },
                new { machineId = "10", operationNumber = "20", releaseId = "654321", workOffset = "G54", positionMm = new { x = 1.0 }, sourceSession = "s" },
                new { machineId = "10", operationNumber = "20", releaseId = "654321", workOffset = "G54", positionMm = new { x = 1.0, z = 1.0 } },
                new { machineId = "10", operationNumber = "20", releaseId = "654321", workOffset = "G54", positionMm = new { x = 1.0, z = 1.0 }, sourceSession = "s", units = "inch" }
            })
            {
                using var response = await client.PostAsJsonAsync(Ingest, body);
                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            }

            using var unknownMachine = await client.PostAsJsonAsync(Ingest, Body("s-1", machine: "HAAS_VF3_99"));
            var unmatched = (await JsonAsync(unknownMachine, HttpStatusCode.Created)).GetProperty("record");
            Assert.Equal("UNMATCHED", unmatched.GetProperty("status").GetString());
            Assert.Equal("unknown_machine", unmatched.GetProperty("statusReason").GetString());
            Assert.Equal(JsonValueKind.Null, unmatched.GetProperty("machineId").ValueKind);

            var list = await GetAsync(client, "/api/v1/machine-evidence/work-offsets?status=UNMATCHED");
            Assert.Single(list.GetProperty("items").EnumerateArray());
            using var badStatus = await client.GetAsync("/api/v1/machine-evidence/work-offsets?status=MAYBE");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, badStatus.StatusCode);
        });
    }

    [Fact]
    public async Task A_reviewer_accepts_a_candidate_with_the_version_and_current_record_they_saw()
    {
        await RunAsync(async client =>
        {
            var first = await CreateAsync(client, Body("s-1"));
            var candidate = await CreateAsync(client, Body("s-2", x: 140));
            Assert.Equal("REVIEW_REQUIRED", candidate.GetProperty("status").GetString());
            Assert.Equal("large_change", candidate.GetProperty("statusReason").GetString());
            var candidateId = candidate.GetProperty("workOffsetId").GetString();
            var firstId = first.GetProperty("workOffsetId").GetString();

            // A stale version is refused with who changed it and when.
            using var stale = await client.PostAsJsonAsync($"/api/v1/machine-evidence/work-offsets/{candidateId}/accept",
                new { version = 7, expectedCurrentId = firstId });
            var conflict = await JsonAsync(stale, HttpStatusCode.Conflict);
            Assert.Equal("edit_conflict", conflict.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("test-admin", conflict.GetProperty("error").GetProperty("conflict").GetProperty("changedBy").GetString());

            // Comparing with a record that is not current any more is refused too.
            using var wrongCurrent = await client.PostAsJsonAsync($"/api/v1/machine-evidence/work-offsets/{candidateId}/accept",
                new { version = 1, expectedCurrentId = "" });
            Assert.Equal(HttpStatusCode.Conflict, wrongCurrent.StatusCode);

            using var accepted = await client.PostAsJsonAsync($"/api/v1/machine-evidence/work-offsets/{candidateId}/accept",
                new { version = 1, expectedCurrentId = firstId, comment = "Fixture moved" });
            var value = await JsonAsync(accepted, HttpStatusCode.OK);
            Assert.Equal("VALID", value.GetProperty("status").GetString());
            Assert.Equal(firstId, value.GetProperty("supersedesId").GetString());
            Assert.Equal("Fixture moved", value.GetProperty("reviewComment").GetString());
            Assert.Equal(2, value.GetProperty("version").GetInt32());

            var view = await GetAsync(client, View + "?offset=G54");
            Assert.Equal(candidateId, view.GetProperty("current")[0].GetProperty("workOffsetId").GetString());
            var old = (await GetAsync(client, $"/api/v1/machine-evidence/work-offsets/{firstId}"));
            Assert.Equal("SUPERSEDED", old.GetProperty("status").GetString());

            // Deciding again on the same candidate is a conflict: it is no longer waiting for review.
            using var again = await client.PostAsJsonAsync($"/api/v1/machine-evidence/work-offsets/{candidateId}/reject",
                new { version = 2 });
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        });
    }

    [Fact]
    public async Task A_rejected_candidate_never_becomes_current()
    {
        await RunAsync(async client =>
        {
            var candidate = await CreateAsync(client, Body("s-old", release: "654320"));
            Assert.Equal("stale_release", candidate.GetProperty("statusReason").GetString());
            using var rejected = await client.PostAsJsonAsync(
                $"/api/v1/machine-evidence/work-offsets/{candidate.GetProperty("workOffsetId").GetString()}/reject",
                new { version = 1, comment = "Old program" });
            Assert.Equal("REJECTED", (await JsonAsync(rejected, HttpStatusCode.OK)).GetProperty("status").GetString());
            Assert.Empty((await GetAsync(client, View)).GetProperty("current").EnumerateArray());

            using var missing = await client.PostAsJsonAsync("/api/v1/machine-evidence/work-offsets/nothing/reject", new { version = 1 });
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var noVersion = await client.PostAsJsonAsync("/api/v1/machine-evidence/work-offsets/nothing/reject", new { version = 0 });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, noVersion.StatusCode);
        });
    }

    [Fact]
    public async Task The_report_program_is_rendered_in_the_Machine_s_dialect()
    {
        await RunAsync(async client =>
        {
            var haas = await GetAsync(client,
                "/api/v1/machines/machine-10/work-offset-report?operationId=case-operation-wo&releaseId=gcode-wo-current&offsets=G54,G110");
            Assert.Equal("HAAS_NGC", haas.GetProperty("ncDialect").GetString());
            var text = haas.GetProperty("text").GetString()!;
            Assert.Contains("RELEASE/654321]", text);
            Assert.Contains("OFFSET/G154P1/", text);
            Assert.Contains("#14001[44]", text);

            var okuma = await GetAsync(client,
                "/api/v1/machines/machine-13/work-offset-report?operationId=case-operation-wo&releaseId=gcode-wo-current&offsets=H1&axes=X1,Z2");
            Assert.Equal("work-offsets/O1992.MIN", okuma.GetProperty("fileName").GetString());

            using var unsupported = await client.GetAsync(
                "/api/v1/machines/machine-13/work-offset-report?operationId=case-operation-wo&offsets=G54");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unsupported.StatusCode);
            using var foreignRelease = await client.GetAsync(
                "/api/v1/machines/machine-10/work-offset-report?operationId=case-operation-wo&releaseId=gcode-wo-op30");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignRelease.StatusCode);
            using var unknown = await client.GetAsync("/api/v1/machines/machine-10/work-offset-report?operationId=nothing");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        });
    }

    [Fact]
    public async Task Ingest_needs_setup_permission_and_review_needs_run_operations()
    {
        await RunAsync(async client =>
        {
            var candidate = await CreateAsync(client, Body("s-old", release: "654320"));
            var review = $"/api/v1/machine-evidence/work-offsets/{candidate.GetProperty("workOffsetId").GetString()}/accept";

            using (client.SignedOut())
            {
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Ingest, Body("s-2"))).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(review, new { version = 1 })).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/machine-evidence/work-offsets")).StatusCode);
            }
            using (client.SignedInWithOnly(Permissions.RunOperations))
            {
                using var forbidden = await client.PostAsJsonAsync(Ingest, Body("s-2"));
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
                Assert.Equal("permission_required",
                    (await JsonAsync(forbidden, HttpStatusCode.Forbidden)).GetProperty("error").GetProperty("code").GetString());
            }
            using (client.SignedInWithOnly(Permissions.ManageSetup))
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(review, new { version = 1 })).StatusCode);
                Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Ingest, Body("s-3"))).StatusCode);
            }
            using (client.SignedInWithOnly())
            {
                // Viewing needs only a signed-in account.
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/machine-evidence/work-offsets")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(View)).StatusCode);
            }
            using (client.SignedInWithOnly(Permissions.RunOperations))
            {
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(review, new { version = 1 })).StatusCode);
            }
        });
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync(Ingest, body);
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("record");
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        return await JsonAsync(response, HttpStatusCode.OK);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task RunAsync(Func<HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.WorkOffsetApi.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "test.db")}"],
            webHost => webHost.UseSignedInTestServer());
        try
        {
            await application.StartAsync();
            await WorkOffsetTestData.SeedAsync(application.Services.GetRequiredService<SqliteDatabase>());
            using var client = application.GetTestClient();
            await test(client);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }
}
