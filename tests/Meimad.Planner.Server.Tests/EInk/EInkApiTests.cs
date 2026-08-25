using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.EInk;

public sealed class EInkApiTests
{
    private const string DeviceId = "device-eink-1";
    private const string Token = "mp_eink_test-token-1";

    [Fact]
    public async Task Device_reads_version_screen_manifest_file_and_time_configuration()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            var fileBytes = await SeedAsync(application.Services, packageRoot);
            using var versionRequest = Get($"/api/v1/eink/devices/{DeviceId}/version");
            using var versionResponse = await client.SendAsync(versionRequest);
            Assert.Equal(HttpStatusCode.OK, versionResponse.StatusCode);
            Assert.NotNull(versionResponse.Headers.ETag);
            using var version = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
            Assert.Equal("machine-eink-1", version.RootElement.GetProperty("machineId").GetString());
            Assert.Equal("package-eink-1", version.RootElement.GetProperty("package").GetProperty("packageId").GetString());

            using var conditional = Get($"/api/v1/eink/devices/{DeviceId}/version");
            conditional.Headers.IfNoneMatch.Add(versionResponse.Headers.ETag!);
            using var unchanged = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);

            using var screenResponse = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{DeviceId}/machine-screen"));
            using var screen = JsonDocument.Parse(await screenResponse.Content.ReadAsStringAsync());
            Assert.Equal("M-EINK-1", screen.RootElement.GetProperty("machine").GetProperty("number").GetString());
            Assert.Equal("operation-eink-1", screen.RootElement.GetProperty("current").GetProperty("batchOperationId").GetString());
            Assert.Equal(3, screen.RootElement.GetProperty("next").GetArrayLength());
            Assert.Equal("current", screen.RootElement.GetProperty("status").GetProperty("code").GetString());

            using var manifestResponse = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{DeviceId}/package-manifest"));
            Assert.Equal(
                $"/api/v1/eink/devices/{DeviceId}/packages/package-eink-1/revisions/R1/manifest",
                manifestResponse.Content.Headers.ContentLocation?.OriginalString);
            using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync());
            var file = Assert.Single(manifest.RootElement.GetProperty("files").EnumerateArray());
            Assert.Equal("instructions/setup.txt", file.GetProperty("logicalPath").GetString());
            Assert.False(file.TryGetProperty("storageRelativePath", out _));
            Assert.False(file.TryGetProperty("fullPath", out _));

            var downloadPath = file.GetProperty("downloadPath").GetString()!;
            using var fileResponse = await client.SendAsync(Get(downloadPath));
            Assert.Equal(HttpStatusCode.OK, fileResponse.StatusCode);
            Assert.Equal(fileBytes, await fileResponse.Content.ReadAsByteArrayAsync());
            Assert.Equal(
                Sha256(fileBytes),
                fileResponse.Headers.GetValues("X-Meimad-Checksum-SHA256").Single());

            using var timeResponse = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{DeviceId}/time-config"));
            using var time = JsonDocument.Parse(await timeResponse.Content.ReadAsStringAsync());
            Assert.Equal("Asia/Jerusalem", time.RootElement.GetProperty("timeZoneId").GetString());
            Assert.Equal(300, time.RootElement.GetProperty("pollIntervalSeconds").GetInt32());
        });
    }

    [Fact]
    public async Task Device_credentials_are_scoped_revocable_and_read_only()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);

            using var wrongToken = Get($"/api/v1/eink/devices/{DeviceId}/version", "mp_eink_wrong");
            using var wrongResponse = await client.SendAsync(wrongToken);
            Assert.Equal(HttpStatusCode.NotFound, wrongResponse.StatusCode);

            using var otherDevice = Get("/api/v1/eink/devices/device-eink-2/version", Token);
            using var otherResponse = await client.SendAsync(otherDevice);
            Assert.Equal(HttpStatusCode.NotFound, otherResponse.StatusCode);

            using var planningRead = Get("/api/v1/cases");
            using var planningReadResponse = await client.SendAsync(planningRead);
            Assert.Equal(HttpStatusCode.Forbidden, planningReadResponse.StatusCode);

            using var spacedCredential = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases");
            spacedCredential.Headers.TryAddWithoutValidation("Authorization", $"Bearer    {Token}");
            using var spacedCredentialResponse = await client.SendAsync(spacedCredential);
            Assert.Equal(HttpStatusCode.Forbidden, spacedCredentialResponse.StatusCode);

            using var mutation = new HttpRequestMessage(HttpMethod.Post, "/api/v1/edit-mode/requests");
            mutation.Headers.Authorization = new("bearer", Token);
            mutation.Headers.Add("X-Meimad-Client-Id", "device-client");
            mutation.Headers.Add("X-Meimad-User-Id", "device-user");
            using var mutationResponse = await client.SendAsync(mutation);
            Assert.Equal(HttpStatusCode.Forbidden, mutationResponse.StatusCode);

            await SetDeviceEnabledAsync(application.Services, false);
            using var revokedResponse = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{DeviceId}/machine-screen"));
            Assert.Equal(HttpStatusCode.NotFound, revokedResponse.StatusCode);
        });
    }

    [Fact]
    public async Task Tablet_bootstrap_requires_matching_enabled_token_and_hardware_id()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);

            using var valid = Get("/api/tablet/ping?hardwareId=a4-cf-12-83-76-91");
            valid.Headers.Add("X-Meimad-Battery-Voltage", "3.860");
            using var response = await client.SendAsync(valid);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("3041", document.RootElement.GetProperty("tabletId").GetString());
            Assert.Equal(DeviceId, document.RootElement.GetProperty("deviceId").GetString());
            Assert.Equal("machine-eink-1", document.RootElement.GetProperty("machineId").GetString());

            using var wrongHardware = await client.SendAsync(Get(
                "/api/tablet/ping?hardwareId=A4:CF:12:83:76:92"));
            Assert.Equal(HttpStatusCode.NotFound, wrongHardware.StatusCode);

            using var otherToken = await client.SendAsync(Get(
                "/api/tablet/ping?hardwareId=A4:CF:12:83:76:91", "mp_eink_other-token"));
            Assert.Equal(HttpStatusCode.NotFound, otherToken.StatusCode);

            await SetDeviceEnabledAsync(application.Services, false);
            using var revoked = await client.SendAsync(Get(
                "/api/tablet/ping?hardwareId=A4:CF:12:83:76:91"));
            Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        });
    }

    [Fact]
    public async Task Corrupt_package_file_is_rejected_without_returning_bytes()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);
            await File.WriteAllTextAsync(
                Path.Combine(packageRoot, "package-eink-1", "setup.txt"),
                "corrupted package content");

            using var response = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{DeviceId}/packages/package-eink-1/revisions/R1/files/file-eink-1"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("package_integrity_failed", document.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.DoesNotContain("corrupted package content", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Active_editor_can_register_bind_revoke_and_rotate_a_device_token()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);
            await GrantEditAsync(application.Services);
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "eink-admin-client");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");

            using var create = await client.PostAsJsonAsync(
                "/api/v1/eink/device-registrations",
                new { deviceName = "Spare Tablet", machineId = (string?)null });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            var deviceId = created.RootElement.GetProperty("deviceId").GetString()!;
            var firstToken = created.RootElement.GetProperty("registrationToken").GetString()!;
            Assert.StartsWith("mp_eink_", firstToken, StringComparison.Ordinal);

            await UnbindDeviceAsync(application.Services, DeviceId);
            using var updateRequest = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/api/v1/eink/device-registrations/{deviceId}")
            {
                Content = JsonContent.Create(new
                {
                    machineId = "machine-eink-1",
                    isEnabled = true,
                    rotateCredential = true
                })
            };
            using var update = await client.SendAsync(updateRequest);
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            using var updated = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
            var rotatedToken = updated.RootElement.GetProperty("registrationToken").GetString()!;
            Assert.NotEqual(firstToken, rotatedToken);

            using var oldCredential = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{deviceId}/version",
                firstToken));
            Assert.Equal(HttpStatusCode.NotFound, oldCredential.StatusCode);
            using var newCredential = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{deviceId}/version",
                rotatedToken));
            Assert.Equal(HttpStatusCode.OK, newCredential.StatusCode);

            using var revokeRequest = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/api/v1/eink/device-registrations/{deviceId}")
            {
                Content = JsonContent.Create(new
                {
                    machineId = "machine-eink-1",
                    isEnabled = false,
                    rotateCredential = false
                })
            };
            using var revoke = await client.SendAsync(revokeRequest);
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
            using var revoked = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{deviceId}/version",
                rotatedToken));
            Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        });
    }

    [Fact]
    public async Task Simulator_is_local_read_only_and_has_no_write_back_or_usb_surface()
    {
        await RunWithServerAsync(async (_, client, _) =>
        {
            using var page = await client.GetAsync("/eink-simulator/");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("READ-ONLY", html, StringComparison.Ordinal);
            Assert.Contains("NO WRITE-BACK", html, StringComparison.Ordinal);
            Assert.DoesNotContain("textarea", html, StringComparison.OrdinalIgnoreCase);

            using var script = await client.GetAsync("/eink-simulator/app.js");
            var javascript = await script.Content.ReadAsStringAsync();
            Assert.Contains("GET version (small change check)", javascript, StringComparison.Ordinal);
            Assert.Contains("crypto.subtle.digest", javascript, StringComparison.Ordinal);
            Assert.DoesNotContain("method: \"POST\"", javascript, StringComparison.Ordinal);
            Assert.DoesNotContain("edit-mode", javascript, StringComparison.OrdinalIgnoreCase);

            using var usb = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{DeviceId}/usb-mass-storage"));
            Assert.Equal(HttpStatusCode.NotFound, usb.StatusCode);
        });
    }

    private static HttpRequestMessage Get(string path, string token = Token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", token);
        return request;
    }

    private static async Task<byte[]> SeedAsync(IServiceProvider services, string packageRoot)
    {
        var packageDirectory = Path.Combine(packageRoot, "package-eink-1");
        Directory.CreateDirectory(packageDirectory);
        var fileBytes = Encoding.UTF8.GetBytes("SETUP INSTRUCTIONS\n1. Verify fixture.\n2. Load tools.\n");
        await File.WriteAllBytesAsync(Path.Combine(packageDirectory, "setup.txt"), fileBytes);
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var calendar = JsonSerializer.Serialize(new
        {
            availability = new[] { new { startsAt = start, endsAt = start.AddDays(7) } }
        });
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
            VALUES ('calendar-eink', 'E-Ink calendar', 'UTC', $calendar);
            INSERT INTO application_settings (key, value)
            VALUES ('timeline.setup_calendar_json', $calendar);
            INSERT INTO machines (
                id, number, name, machine_type, working_calendar_id, status,
                is_active, display_enabled)
            VALUES ('machine-eink-1', 'M-EINK-1', 'E-Ink Mill', 'mill',
                    'calendar-eink', 'active', 1, 1);
            INSERT INTO employee_resources (
                id, employee_number, name, resource_type, first_name, last_name,
                skills_json, assigned_calendar_id, is_active)
            VALUES ('resource-eink-setup', 'E-EINK-SETUP', 'Setup Worker', 'setup_worker',
                    'Setup', 'Worker', '["mill"]', 'calendar-eink', 1);
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-eink', 'PN-EINK', 'E-Ink Part', 'C:\Cases\PN-EINK');
            INSERT INTO production_batches (
                id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-eink', 'case-eink', 'B-EINK', 'waiting', 4);
            INSERT INTO case_operations (
                id, case_id, operation_number, route_position, name,
                required_machine_type, setup_seconds, cycle_seconds)
            VALUES
                ('case-op-eink-1', 'case-eink', 10, 0, 'Rough', 'mill', 60, 60),
                ('case-op-eink-2', 'case-eink', 20, 1, 'Finish', 'mill', 60, 60),
                ('case-op-eink-3', 'case-eink', 30, 2, 'Deburr', 'mill', 60, 60),
                ('case-op-eink-4', 'case-eink', 40, 3, 'Inspect', 'mill', 60, 60);
            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, required_machine_type,
                setup_seconds, cycle_seconds, status)
            VALUES
                ('operation-eink-1', 'batch-eink', 'case-op-eink-1', 10, 0, 'Rough', 'mill', 60, 60, 'not_started'),
                ('operation-eink-2', 'batch-eink', 'case-op-eink-2', 20, 1, 'Finish', 'mill', 60, 60, 'not_started'),
                ('operation-eink-3', 'batch-eink', 'case-op-eink-3', 30, 2, 'Deburr', 'mill', 60, 60, 'not_started'),
                ('operation-eink-4', 'batch-eink', 'case-op-eink-4', 40, 3, 'Inspect', 'mill', 60, 60, 'not_started');
            INSERT INTO machine_assignments (
                id, batch_operation_id, machine_id, backlog_position)
            VALUES
                ('assignment-eink-1', 'operation-eink-1', 'machine-eink-1', 0),
                ('assignment-eink-2', 'operation-eink-2', 'machine-eink-1', 1),
                ('assignment-eink-3', 'operation-eink-3', 'machine-eink-1', 2),
                ('assignment-eink-4', 'operation-eink-4', 'machine-eink-1', 3);
            INSERT INTO device_registry (
                id, tablet_id, hardware_id, device_type, device_name, machine_id, credential_hash,
                access_mode, is_enabled)
            VALUES
                ('device-eink-1', '3041', 'A4:CF:12:83:76:91', 'eink', 'Tablet One', 'machine-eink-1',
                 $credentialHash, 'read_only', 1),
                ('device-eink-2', '3042', 'A4:CF:12:83:76:92', 'eink', 'Tablet Two', NULL,
                 $otherCredentialHash, 'read_only', 1);
            INSERT INTO eink_package_revisions (
                id, batch_operation_id, revision, tool_cart_id, published_at)
            VALUES ('package-eink-1', 'operation-eink-1', 'R1', 'TC-12', $publishedAt);
            INSERT INTO eink_package_files (
                id, package_revision_id, logical_path, storage_relative_path,
                media_type, byte_length, sha256, modified_at, display_order)
            VALUES (
                'file-eink-1', 'package-eink-1', 'instructions/setup.txt',
                'package-eink-1/setup.txt', 'text/plain; charset=utf-8',
                $byteLength, $sha256, $publishedAt, 0);
            """;
        command.Parameters.AddWithValue("$calendar", calendar);
        command.Parameters.AddWithValue("$credentialHash", Sha256(Encoding.UTF8.GetBytes(Token)));
        command.Parameters.AddWithValue("$otherCredentialHash", Sha256(Encoding.UTF8.GetBytes("mp_eink_other-token")));
        command.Parameters.AddWithValue("$publishedAt", now.ToString("O"));
        command.Parameters.AddWithValue("$byteLength", fileBytes.LongLength);
        command.Parameters.AddWithValue("$sha256", Sha256(fileBytes));
        await command.ExecuteNonQueryAsync();
        return fileBytes;
    }

    private static async Task SetDeviceEnabledAsync(IServiceProvider services, bool enabled)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE device_registry SET is_enabled = $enabled WHERE id = $deviceId;";
        command.Parameters.AddWithValue("$enabled", enabled);
        command.Parameters.AddWithValue("$deviceId", DeviceId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UnbindDeviceAsync(IServiceProvider services, string deviceId)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE device_registry SET machine_id = NULL WHERE id = $deviceId;";
        command.Parameters.AddWithValue("$deviceId", deviceId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task GrantEditAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = 'eink-admin-client',
                holder_user_id = 'eink-admin-user',
                generation = 1,
                acquired_at = '2026-08-11T00:00:00Z',
                updated_at = '2026-08-11T00:00:00Z'
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task RunWithServerAsync(
        Func<WebApplication, HttpClient, string, Task> test)
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(), "MeimadPlanner.EInk.Tests", Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(directoryPath, "packages");
        var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1",
                "--Server:Port=5099",
                $"--Database:Path={Path.Combine(directoryPath, "api-test.db")}",
                $"--EInk:PackageRoot={packageRoot}"
            ],
            webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            await test(application, client, packageRoot);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
