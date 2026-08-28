using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Cnc;

public sealed class CncVerificationApiTests
{
    [Fact]
    public async Task Windows_recovery_routes_require_edit_authority_and_preserve_release_history()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.CncRecovery.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1", "--Server:Port=5096",
                $"--Database:Path={Path.Combine(root, "test.db")}"
            ], webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await CncVerificationFoundationTests.SeedAsync(database);
            var foundation = application.Services.GetRequiredService<CncVerificationFoundationService>();
            var authority = new EditAuthority("verification-client", 1);
            await foundation.UpdateSettingsAsync("machine-verification", new(
                "HAAS_DPRNT_TCP", 8080, 9001, 9002, 605, 10501, 10500, 10502, 10503,
                9003, 10504, "machine-secret-value", 6, 6, 300, true), 0, authority);
            var first = await foundation.CreateOffsetLoaderReleaseAsync(
                "run-verification", new("machine-verification", "gcode-verification",
                    "tools-verification"), authority);
            await application.Services.GetRequiredService<ProductionRunWorkflowEventService>()
                .AppendAsync(new(
                    "run-verification", "machine-verification", "OFFSET_LOADER_COMPLETED",
                    "TEST", "API-OLC-1", 1, NcReleaseId: "gcode-verification",
                    OffsetLoaderReleaseId: first.OffsetLoaderReleaseId,
                    VerificationSession: new(731841, 6, 6, 300)));

            using var client = application.GetTestClient();
            using var denied = await client.PostAsJsonAsync(
                "/api/v1/production-runs/run-verification/verification/invalidate",
                new { machineId = "machine-verification", reason = "Fixture changed" });
            Assert.Equal(HttpStatusCode.PreconditionRequired, denied.StatusCode);

            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "verification-client");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
            using var invalidated = await client.PostAsJsonAsync(
                "/api/v1/production-runs/run-verification/verification/invalidate",
                new { machineId = "machine-verification", reason = "Fixture changed" });
            Assert.Equal(HttpStatusCode.OK, invalidated.StatusCode);
            using var invalidatedJson = JsonDocument.Parse(
                await invalidated.Content.ReadAsStringAsync());
            Assert.Equal("INVALIDATE_VERIFICATION",
                invalidatedJson.RootElement.GetProperty("action").GetString());

            using var replacement = await client.PostAsJsonAsync(
                "/api/v1/production-runs/run-verification/offset-loader-releases",
                new
                {
                    machineId = "machine-verification",
                    ncReleaseId = "gcode-verification",
                    toolTableReleaseId = "tools-verification",
                    metadataJson = "{}"
                });
            Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
            using var replacementJson = JsonDocument.Parse(
                await replacement.Content.ReadAsStringAsync());
            var replacementId = replacementJson.RootElement
                .GetProperty("offsetLoaderReleaseId").GetString();
            Assert.NotEqual(first.OffsetLoaderReleaseId, replacementId);

            using var revoked = await client.PostAsJsonAsync(
                "/api/v1/production-runs/run-verification/offset-loader/current/revoke",
                new { machineId = "machine-verification", reason = "Offsets invalid" });
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
            using var revokedJson = JsonDocument.Parse(await revoked.Content.ReadAsStringAsync());
            Assert.Equal("REVOKE_CURRENT_OFFSET_LOADER",
                revokedJson.RootElement.GetProperty("action").GetString());
            Assert.Equal(replacementId,
                revokedJson.RootElement.GetProperty("offsetLoaderReleaseId").GetString());

            await using var connection = await database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM offset_loader_releases WHERE production_run_id='run-verification';";
            Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = "SELECT COUNT(*) FROM production_run_current_offset_loaders WHERE production_run_id='run-verification';";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            await application.StopAsync();
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
