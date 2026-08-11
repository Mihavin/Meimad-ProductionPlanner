using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Backup;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Acceptance;

public sealed class EndToEndAcceptanceTests
{
    private const string DeviceId = "acceptance-eink-01";
    private const string DeviceToken = "mp_eink_acceptance-token";

    [Fact]
    public async Task Acceptance_dataset_exercises_server_owned_end_to_end_read_models()
    {
        await RunWithServerAsync(async (application, client, paths) =>
        {
            var window = await SeedAsync(application.Services, paths.PackageRoot);
            await AssertDatasetShapeAsync(application.Services);
            await AssertAllocationScenariosAsync(application.Services);

            var positionsBefore = await ReadAssignmentPositionsAsync(application.Services);

            using (var boardResponse = await client.GetAsync("/api/v1/planning-board"))
            {
                Assert.Equal(HttpStatusCode.OK, boardResponse.StatusCode);
                using var board = JsonDocument.Parse(await boardResponse.Content.ReadAsStringAsync());
                Assert.Equal(15, board.RootElement.GetProperty("machines").GetArrayLength());
                Assert.Empty(board.RootElement.GetProperty("pool").EnumerateArray());
            }

            var timelinePath = string.Create(
                CultureInfo.InvariantCulture,
                $"/api/v1/timeline?from={Uri.EscapeDataString(window.Start.ToString("O"))}&to={Uri.EscapeDataString(window.End.ToString("O"))}");
            using (var timelineResponse = await client.GetAsync(timelinePath))
            {
                Assert.Equal(HttpStatusCode.OK, timelineResponse.StatusCode);
                using var timeline = JsonDocument.Parse(await timelineResponse.Content.ReadAsStringAsync());
                var root = timeline.RootElement;
                Assert.Equal(11, root.GetProperty("batches").GetArrayLength());
                Assert.Contains(
                    root.GetProperty("dependencies").EnumerateArray(),
                    value => value.GetProperty("type").GetString() == "SEQUENTIAL");
                Assert.Contains(
                    root.GetProperty("dependencies").EnumerateArray(),
                    value => value.GetProperty("type").GetString() == "PARALLEL_CAPABLE");
                Assert.Contains(
                    root.GetProperty("dependencies").EnumerateArray(),
                    value => value.GetProperty("type").GetString() == "LOCKED_SIMULTANEOUS");
                Assert.Contains(
                    root.GetProperty("machines").EnumerateArray()
                        .SelectMany(machine => machine.GetProperty("intervals").EnumerateArray()),
                    interval => interval.GetProperty("type").GetString() == "downtime");
                var conflicts = root.GetProperty("conflicts").EnumerateArray().ToArray();
                Assert.Contains(conflicts, value => value.GetProperty("code").GetString() == "missing_timing");
                Assert.Contains(conflicts, value => value.GetProperty("code").GetString() == "insufficient_availability");
            }

            Assert.Equal(positionsBefore, await ReadAssignmentPositionsAsync(application.Services));

            using (var tvResponse = await client.GetAsync("/api/v1/tv-dashboard"))
            {
                Assert.Equal(HttpStatusCode.OK, tvResponse.StatusCode);
                using var tv = JsonDocument.Parse(await tvResponse.Content.ReadAsStringAsync());
                var root = tv.RootElement;
                Assert.Equal(15, root.GetProperty("summary").GetProperty("machineCount").GetInt32());
                Assert.True(root.GetProperty("summary").GetProperty("urgentBatchCount").GetInt32() >= 1);
                Assert.True(root.GetProperty("summary").GetProperty("criticalConflictCount").GetInt32() >= 1);
                Assert.Contains(
                    root.GetProperty("machines").EnumerateArray(),
                    machine => machine.GetProperty("downtime").ValueKind == JsonValueKind.Object);
                Assert.Contains(
                    root.GetProperty("machines").EnumerateArray(),
                    machine => machine.GetProperty("current").ValueKind == JsonValueKind.Object
                        && machine.GetProperty("next").ValueKind == JsonValueKind.Object);
            }

            using (var forbiddenTvWrite = await client.PostAsync("/api/v1/tv-dashboard", null))
            {
                Assert.Equal(HttpStatusCode.MethodNotAllowed, forbiddenTvWrite.StatusCode);
            }

            using (var versionResponse = await client.SendAsync(DeviceGet($"/api/v1/eink/devices/{DeviceId}/version")))
            {
                Assert.Equal(HttpStatusCode.OK, versionResponse.StatusCode);
            }

            string downloadPath;
            using (var manifestResponse = await client.SendAsync(DeviceGet(
                       $"/api/v1/eink/devices/{DeviceId}/package-manifest")))
            {
                Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
                using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync());
                Assert.Equal("acceptance-package-01", manifest.RootElement.GetProperty("packageId").GetString());
                Assert.Equal("TC-ACC-01", manifest.RootElement.GetProperty("toolCartId").GetString());
                downloadPath = manifest.RootElement.GetProperty("files")[0]
                    .GetProperty("downloadPath").GetString()!;
            }

            using (var fileResponse = await client.SendAsync(DeviceGet(downloadPath)))
            {
                Assert.Equal(HttpStatusCode.OK, fileResponse.StatusCode);
                var bytes = await fileResponse.Content.ReadAsByteArrayAsync();
                Assert.Equal(Sha256(bytes), fileResponse.Headers.GetValues(
                    "X-Meimad-Checksum-SHA256").Single());
            }

            using (var forbiddenDeviceMutation = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases"))
            {
                forbiddenDeviceMutation.Headers.Authorization = new("Bearer", DeviceToken);
                using var response = await client.SendAsync(forbiddenDeviceMutation);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }

            var backupService = application.Services.GetRequiredService<SqliteBackupService>();
            var backup = await backupService.CreateBackupAsync();
            Assert.True(backup.IntegrityVerified);
            Assert.True(backup.RestoreVerified);
            Assert.Equal(10, await ReadCountFromDatabaseFileAsync(backup.BackupPath, "cases"));
            Assert.Equal(15, await ReadCountFromDatabaseFileAsync(backup.BackupPath, "machines"));
            Assert.Equal(1, await ReadCountFromDatabaseFileAsync(
                backup.BackupPath,
                "eink_package_revisions"));
        });
    }

    private static async Task<AcceptanceWindow> SeedAsync(
        IServiceProvider services,
        string packageRoot)
    {
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var end = start.AddDays(7);
        var packageBytes = Encoding.UTF8.GetBytes(
            "ACCEPTANCE SETUP INSTRUCTIONS\n1. Verify fixture.\n2. Load approved tools.\n");
        var packageDirectory = Path.Combine(packageRoot, "acceptance-package-01");
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllBytesAsync(Path.Combine(packageDirectory, "setup.txt"), packageBytes);

        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = ReadEmbeddedDataset();
        command.Parameters.AddWithValue("$calendarDay", CalendarJson(start.AddHours(6), start.AddDays(7).AddHours(18)));
        command.Parameters.AddWithValue("$calendarExtended", CalendarJson(start, end));
        command.Parameters.AddWithValue("$calendarLimited", CalendarJson(start.AddHours(8), start.AddHours(8.5)));
        command.Parameters.AddWithValue("$urgentDue", DateOnly.FromDateTime(now.UtcDateTime.AddDays(1)).ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$normalDue", DateOnly.FromDateTime(now.UtcDateTime.AddDays(20)).ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$downtimeCurrentStart", now.AddMinutes(-10).ToString("O"));
        command.Parameters.AddWithValue("$downtimeCurrentEnd", now.AddMinutes(50).ToString("O"));
        command.Parameters.AddWithValue("$downtimeFutureStart", start.AddDays(1).AddHours(10).ToString("O"));
        command.Parameters.AddWithValue("$downtimeFutureEnd", start.AddDays(1).AddHours(12).ToString("O"));
        command.Parameters.AddWithValue("$downtimeInspectionStart", start.AddHours(14).ToString("O"));
        command.Parameters.AddWithValue("$downtimeInspectionEnd", start.AddHours(15).ToString("O"));
        command.Parameters.AddWithValue("$credentialHash", Sha256(Encoding.UTF8.GetBytes(DeviceToken)));
        command.Parameters.AddWithValue("$publishedAt", now.ToString("O"));
        command.Parameters.AddWithValue("$packageByteLength", packageBytes.LongLength);
        command.Parameters.AddWithValue("$packageSha256", Sha256(packageBytes));
        await command.ExecuteNonQueryAsync();
        return new AcceptanceWindow(start, end);
    }

    private static string CalendarJson(DateTimeOffset startsAt, DateTimeOffset endsAt) =>
        JsonSerializer.Serialize(new
        {
            availability = new[] { new { startsAt, endsAt } }
        });

    private static string ReadEmbeddedDataset()
    {
        var assembly = typeof(EndToEndAcceptanceTests).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith("Acceptance.acceptance-dataset.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The acceptance dataset resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task AssertDatasetShapeAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        Assert.Equal(10, await ScalarAsync(connection, "SELECT COUNT(*) FROM cases;"));
        Assert.Equal(15, await ScalarAsync(connection, "SELECT COUNT(*) FROM orders;"));
        Assert.Equal(11, await ScalarAsync(connection, "SELECT COUNT(*) FROM production_batches;"));
        Assert.Equal(15, await ScalarAsync(connection, "SELECT COUNT(*) FROM machines;"));
        Assert.Equal(3, await ScalarAsync(connection, "SELECT COUNT(*) FROM working_calendars;"));
        Assert.Equal(3, await ScalarAsync(connection, "SELECT COUNT(*) FROM downtimes;"));
        Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(4, await ScalarAsync(connection, "SELECT COUNT(DISTINCT dependency_type) FROM case_operations;"));
    }

    private static async Task AssertAllocationScenariosAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        Assert.Equal(2, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM batch_allocations WHERE production_batch_id = 'batch-01' AND allocation_type = 'order';"));
        Assert.Equal(2, await ScalarAsync(connection,
            "SELECT COUNT(DISTINCT production_batch_id) FROM batch_allocations WHERE order_id = 'order-02a';"));
        Assert.Equal(1, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM production_batches pb WHERE pb.id = 'batch-03' AND NOT EXISTS (SELECT 1 FROM batch_allocations ba WHERE ba.production_batch_id = pb.id AND ba.allocation_type = 'order');"));
        Assert.Equal(0, await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM production_batches pb
            LEFT JOIN batch_allocations ba ON ba.production_batch_id = pb.id
            GROUP BY pb.id, pb.planned_quantity
            HAVING pb.planned_quantity <> SUM(ba.quantity);
            """));
    }

    private static async Task<string[]> ReadAssignmentPositionsAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT machine_id || ':' || backlog_position || ':' || batch_operation_id
            FROM machine_assignments
            ORDER BY machine_id, backlog_position;
            """;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values.ToArray();
    }

    private static async Task<int> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<int> ReadCountFromDatabaseFileAsync(string path, string table)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return await ScalarAsync(connection, $"SELECT COUNT(*) FROM {table};");
    }

    private static HttpRequestMessage DeviceGet(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", DeviceToken);
        return request;
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task RunWithServerAsync(
        Func<WebApplication, HttpClient, AcceptancePaths, Task> test)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner.Acceptance.Tests",
            Guid.NewGuid().ToString("N"));
        var paths = new AcceptancePaths(
            root,
            Path.Combine(root, "packages"),
            Path.Combine(root, "backups"));
        var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1",
                "--Server:Port=5099",
                $"--Database:Path={Path.Combine(root, "acceptance.db")}",
                $"--EInk:PackageRoot={paths.PackageRoot}",
                $"--Backup:Folder={paths.BackupRoot}",
                "--Backup:RetentionCount=2"
            ],
            webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            await test(application, client, paths);
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

    private sealed record AcceptanceWindow(DateTimeOffset Start, DateTimeOffset End);

    private sealed record AcceptancePaths(
        string Root,
        string PackageRoot,
        string BackupRoot);
}
