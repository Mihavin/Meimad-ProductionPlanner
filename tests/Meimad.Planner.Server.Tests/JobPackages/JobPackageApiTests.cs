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

namespace Meimad.Planner.Server.Tests.JobPackages;

public sealed class JobPackageApiTests
{
    private const string DeviceId = "package-device";
    private const string DeviceToken = "mp_eink_package-test-token";

    [Fact]
    public async Task Active_editor_generates_immutable_official_package_and_device_manifest()
    {
        await RunAsync(async (application, client, workingFolder, packageRoot) =>
        {
            var previewBytes = new byte[] { 1, 2, 3, 4, 5 };
            var workerPhotoBytes = new byte[] { 9, 8, 7, 6 };
            var ncBytes = Encoding.UTF8.GetBytes("G90\nG00 X0 Y0\nM30\n");
            var textBytes = Encoding.UTF8.GetBytes("Clamp at station A.\n");
            await SeedAsync(application.Services, workingFolder, previewBytes, ncBytes, textBytes);
            await File.WriteAllBytesAsync(Path.Combine(workingFolder, "setup-worker.jpg"), workerPhotoBytes);
            AddEditorHeaders(client);

            using var response = await client.PostAsJsonAsync(
                "/api/v1/job-packages",
                Request("R1"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var generated = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = generated.RootElement;
            var packageId = root.GetProperty("packageId").GetString()!;
            Assert.Equal("R1", root.GetProperty("revision").GetString());
            Assert.Equal("M-PKG", root.GetProperty("snapshot").GetProperty("machineNumber").GetString());
            Assert.Equal("PN-PKG", root.GetProperty("snapshot").GetProperty("partNumber").GetString());
            Assert.Equal("B-PKG", root.GetProperty("snapshot").GetProperty("batchNumber").GetString());
            Assert.Equal("Miriam", root.GetProperty("snapshot").GetProperty("setupWorker")
                .GetProperty("firstName").GetString());
            Assert.True(root.GetProperty("snapshot").GetProperty("plannedSetupStartsAt").GetDateTimeOffset()
                < root.GetProperty("snapshot").GetProperty("plannedSetupEndsAt").GetDateTimeOffset());
            Assert.Single(root.GetProperty("snapshot").GetProperty("jobTools").EnumerateArray());
            Assert.Single(root.GetProperty("snapshot").GetProperty("expectedMachineTools").EnumerateArray());
            Assert.Equal(2, root.GetProperty("snapshot").GetProperty("localChecklistItems").GetArrayLength());
            Assert.Equal(7, root.GetProperty("assets").GetArrayLength());
            Assert.Equal(
                new[] { "other", "preview", "tool_table", "nc", "text", "offsets", "instructions" },
                root.GetProperty("assets").EnumerateArray()
                    .Select(asset => asset.GetProperty("assetType").GetString()).ToArray());

            Assert.Equal(previewBytes, await File.ReadAllBytesAsync(Path.Combine(workingFolder, "preview.png")));
            Assert.Equal(ncBytes, await File.ReadAllBytesAsync(Path.Combine(workingFolder, "programs", "main.nc")));
            Assert.Equal(textBytes, await File.ReadAllBytesAsync(Path.Combine(workingFolder, "notes", "setup.txt")));
            Assert.True(Directory.Exists(Path.Combine(packageRoot, packageId)));

            using var manifestResponse = await client.SendAsync(DeviceGet(
                $"/api/v1/eink/devices/{DeviceId}/package-manifest"));
            Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
            using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync());
            Assert.Equal("M-PKG", manifest.RootElement.GetProperty("metadata")
                .GetProperty("machine").GetProperty("number").GetString());
            Assert.Equal("PN-PKG", manifest.RootElement.GetProperty("metadata")
                .GetProperty("part").GetProperty("partNumber").GetString());
            Assert.Equal("B-PKG", manifest.RootElement.GetProperty("metadata")
                .GetProperty("batch").GetProperty("batchNumber").GetString());
            Assert.Equal(10, manifest.RootElement.GetProperty("metadata")
                .GetProperty("operation").GetProperty("operationNumber").GetInt32());
            var metadata = manifest.RootElement.GetProperty("metadata");
            var setup = metadata.GetProperty("setup");
            Assert.Equal("Miriam", setup.GetProperty("worker").GetProperty("firstName").GetString());
            Assert.Equal("Cohen", setup.GetProperty("worker").GetProperty("lastName").GetString());
            Assert.NotNull(setup.GetProperty("worker").GetProperty("photoDownloadPath").GetString());
            Assert.Single(metadata.GetProperty("tools").GetProperty("job").EnumerateArray());
            Assert.Equal("T99", Assert.Single(metadata.GetProperty("tools")
                .GetProperty("expectedOnMachine").EnumerateArray()).GetProperty("toolId").GetString());
            Assert.Equal("device_sd", metadata.GetProperty("localChecklist").GetProperty("storage").GetString());
            Assert.False(metadata.GetProperty("localChecklist").GetProperty("syncToServer").GetBoolean());
            Assert.True(metadata.GetProperty("localChecklist").GetProperty("commentsSupported").GetBoolean());
            Assert.Equal(2, metadata.GetProperty("localChecklist").GetProperty("items").GetArrayLength());
            Assert.Equal("wifi", metadata.GetProperty("tabletPolicy").GetProperty("transport").GetString());
            Assert.Equal("sd", metadata.GetProperty("tabletPolicy").GetProperty("persistentStorage").GetString());
            Assert.False(metadata.GetProperty("tabletPolicy").GetProperty("reverseSynchronization").GetBoolean());
            Assert.False(metadata.GetProperty("tabletPolicy").GetProperty("usbMassStorage").GetBoolean());

            foreach (var asset in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                Assert.False(asset.TryGetProperty("storageRelativePath", out _));
                using var download = await client.SendAsync(DeviceGet(
                    asset.GetProperty("downloadPath").GetString()!));
                Assert.Equal(HttpStatusCode.OK, download.StatusCode);
                var bytes = await download.Content.ReadAsByteArrayAsync();
                Assert.Equal(
                    asset.GetProperty("checksum").GetProperty("value").GetString(),
                    Sha256(bytes));
            }

            await using var connection = await application.Services
                .GetRequiredService<SqliteDatabase>()
                .OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT machine_number, part_number, batch_number, operation_number
                FROM eink_package_revisions
                WHERE id = $packageId;
                """;
            command.Parameters.AddWithValue("$packageId", packageId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("M-PKG", reader.GetString(0));
            Assert.Equal("PN-PKG", reader.GetString(1));
            Assert.Equal("B-PKG", reader.GetString(2));
            Assert.Equal(10, reader.GetInt32(3));
        });
    }

    [Fact]
    public async Task Duplicate_revision_invalid_paths_and_stale_editor_leave_no_partial_package()
    {
        await RunAsync(async (application, client, workingFolder, packageRoot) =>
        {
            await SeedAsync(
                application.Services,
                workingFolder,
                [1, 2, 3],
                Encoding.UTF8.GetBytes("M30\n"),
                Encoding.UTF8.GetBytes("setup\n"));
            AddEditorHeaders(client);

            using var first = await client.PostAsJsonAsync("/api/v1/job-packages", Request("R1"));
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            var firstDirectories = Directory.GetDirectories(packageRoot);
            Assert.Single(firstDirectories);

            using var duplicate = await client.PostAsJsonAsync("/api/v1/job-packages", Request("R1"));
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            Assert.Single(Directory.GetDirectories(packageRoot));
            Assert.Empty(Directory.GetDirectories(packageRoot, ".staging-*"));

            using var invalid = await client.PostAsJsonAsync(
                "/api/v1/job-packages",
                new
                {
                    batchOperationId = "operation-package",
                    revision = "R2",
                    includePreview = false,
                    files = new[]
                    {
                        new
                        {
                            assetType = "nc",
                            sourceRelativePath = "../secret.nc",
                            logicalPath = "nc/secret.nc"
                        }
                    }
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
            Assert.Single(Directory.GetDirectories(packageRoot));

            client.DefaultRequestHeaders.Remove("X-Meimad-Edit-Generation");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "99");
            using var stale = await client.PostAsJsonAsync("/api/v1/job-packages", Request("R2"));
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Single(Directory.GetDirectories(packageRoot));
            Assert.Empty(Directory.GetDirectories(packageRoot, ".staging-*"));
        });
    }

    [Fact]
    public async Task Device_credential_cannot_generate_or_modify_official_packages()
    {
        await RunAsync(async (application, client, workingFolder, _) =>
        {
            await SeedAsync(
                application.Services,
                workingFolder,
                [1],
                Encoding.UTF8.GetBytes("M30\n"),
                Encoding.UTF8.GetBytes("setup\n"));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/job-packages")
            {
                Content = JsonContent.Create(Request("R1"))
            };
            request.Headers.Authorization = new("Bearer", DeviceToken);
            request.Headers.Add("X-Meimad-Client-Id", "package-editor");
            request.Headers.Add("X-Meimad-Edit-Generation", "1");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            using var checklist = await client.SendAsync(DeviceGet(
                $"/api/v1/eink/devices/{DeviceId}/checklist-comments"));
            Assert.Equal(HttpStatusCode.NotFound, checklist.StatusCode);
        });
    }

    private static object Request(string revision) => new
    {
        batchOperationId = "operation-package",
        revision,
        toolCartId = "TC-42",
        includePreview = true,
        files = new object[]
        {
            new { assetType = "nc", sourceRelativePath = "programs/main.nc", logicalPath = "nc/main.nc" },
            new { assetType = "text", sourceRelativePath = "notes/setup.txt", logicalPath = "text/setup.txt" }
        },
        toolTable = new[]
        {
            new { toolId = "T01", description = "End mill", diameter = "10 mm", length = "75 mm", note = "Prepared" }
        },
        offsets = new[]
        {
            new { name = "G54 Z", value = "-125.40", unit = "mm", note = "Fixture top" }
        },
        instructions = "Verify fixture and dry-run the first cycle.",
        expectedMachineTools = new[]
        {
            new { toolId = "T99", description = "Probe", diameter = (string?)null, length = (string?)null, note = "Expected loaded" }
        },
        localChecklistItems = new[]
        {
            new { itemId = "tools-collected", label = "Tools collected from Tool Room" },
            new { itemId = "machine-verified", label = "Tools on Machine verified" }
        }
    };

    private static void AddEditorHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "package-editor");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static HttpRequestMessage DeviceGet(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", DeviceToken);
        return request;
    }

    private static async Task SeedAsync(
        IServiceProvider services,
        string workingFolder,
        byte[] previewBytes,
        byte[] ncBytes,
        byte[] textBytes)
    {
        Directory.CreateDirectory(Path.Combine(workingFolder, "programs"));
        Directory.CreateDirectory(Path.Combine(workingFolder, "notes"));
        await File.WriteAllBytesAsync(Path.Combine(workingFolder, "preview.png"), previewBytes);
        await File.WriteAllBytesAsync(Path.Combine(workingFolder, "programs", "main.nc"), ncBytes);
        await File.WriteAllBytesAsync(Path.Combine(workingFolder, "notes", "setup.txt"), textBytes);
        var workerPhotoPath = Path.Combine(workingFolder, "setup-worker.jpg");
        if (!File.Exists(workerPhotoPath))
        {
            await File.WriteAllBytesAsync(workerPhotoPath, [9, 8, 7, 6]);
        }
        var now = DateTimeOffset.UtcNow;
        var calendarJson = JsonSerializer.Serialize(new
        {
            availability = new[]
            {
                new { startsAt = now.UtcDateTime.Date, endsAt = now.UtcDateTime.Date.AddDays(31) }
            }
        });

        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
            VALUES ('calendar-package', 'Package calendar', 'UTC', $calendarJson);
            INSERT INTO application_settings (key, value)
            VALUES ('timeline.setup_calendar_json', $calendarJson);
            INSERT INTO employee_resources (
                id, employee_number, name, resource_type, first_name, last_name,
                skills_json, assigned_calendar_id, photo_path, is_active)
            VALUES ('resource-package-setup', 'E-PKG', 'Miriam Cohen', 'setup_worker',
                    'Miriam', 'Cohen', '["mill"]', 'calendar-package', $workerPhotoPath, 1);
            INSERT INTO machines (
                id, number, name, machine_type, working_calendar_id, status,
                is_active, display_enabled)
            VALUES (
                'machine-package', 'M-PKG', 'Package Mill', 'mill',
                'calendar-package', 'active', 1, 1);
            INSERT INTO cases (
                id, part_number, name, revision, customer,
                working_folder_path, preview_reference)
            VALUES (
                'case-package', 'PN-PKG', 'Package Part', 'C', 'Factory Customer',
                $workingFolder, $previewPath);
            INSERT INTO production_batches (
                id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-package', 'case-package', 'B-PKG', 'waiting', 12);
            INSERT INTO case_operations (
                id, case_id, operation_number, route_position, name,
                required_machine_type, setup_seconds, cycle_seconds)
            VALUES ('case-operation-package', 'case-package', 10, 0, 'Finish mill', 'mill', 60, 60);
            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, required_machine_type,
                setup_seconds, cycle_seconds, status)
            VALUES (
                'operation-package', 'batch-package', 'case-operation-package',
                10, 0, 'Finish mill', 'mill', 60, 60, 'not_started');
            INSERT INTO machine_assignments (
                id, batch_operation_id, machine_id, backlog_position)
            VALUES ('assignment-package', 'operation-package', 'machine-package', 0);
            INSERT INTO device_registry (
                id, device_type, device_name, machine_id, credential_hash,
                access_mode, is_enabled)
            VALUES (
                $deviceId, 'eink', 'Package tablet', 'machine-package',
                $credentialHash, 'read_only', 1);
            UPDATE edit_tokens
            SET holder_client_id = 'package-editor',
                holder_user_id = 'package-user',
                generation = 1,
                acquired_at = '2026-08-11T00:00:00Z',
                updated_at = '2026-08-11T00:00:00Z'
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$workingFolder", workingFolder);
        command.Parameters.AddWithValue("$previewPath", Path.Combine(workingFolder, "preview.png"));
        command.Parameters.AddWithValue("$calendarJson", calendarJson);
        command.Parameters.AddWithValue("$workerPhotoPath", workerPhotoPath);
        command.Parameters.AddWithValue("$deviceId", DeviceId);
        command.Parameters.AddWithValue(
            "$credentialHash",
            Sha256(Encoding.UTF8.GetBytes(DeviceToken)));
        await command.ExecuteNonQueryAsync();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task RunAsync(
        Func<WebApplication, HttpClient, string, string, Task> test)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner.JobPackage.Tests",
            Guid.NewGuid().ToString("N"));
        var workingFolder = Path.Combine(root, "case-working-folder");
        var packageRoot = Path.Combine(root, "published-packages");
        Directory.CreateDirectory(workingFolder);
        var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1",
                "--Server:Port=5098",
                $"--Database:Path={Path.Combine(root, "test.db")}",
                $"--EInk:PackageRoot={packageRoot}"
            ],
            webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            await test(application, client, workingFolder, packageRoot);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
