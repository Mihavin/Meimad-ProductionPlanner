using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Maintenance;

public sealed class ServerMaintenanceApiTests
{
    [Fact]
    public async Task Status_and_preview_expose_only_the_protected_deletion_catalog()
    {
        await RunAsync(async (application, client, _) =>
        {
            await SeedAsync(application.Services);

            using var anonymous = await client.GetAsync("/api/v1/server-maintenance/database");
            Assert.Equal((HttpStatusCode)428, anonymous.StatusCode);

            AddIdentityHeaders(client, includeEditGeneration: false);
            using var status = await client.GetAsync("/api/v1/server-maintenance/database");
            status.EnsureSuccessStatusCode();
            using (var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync()))
            {
                var root = json.RootElement;
                Assert.True(root.GetProperty("database").GetProperty("databaseFileBytes").GetInt64() > 0);
                Assert.True(root.GetProperty("database").GetProperty("schemaVersion").GetInt32() > 0);
                Assert.Equal(3, root.GetProperty("deletableTypes").GetArrayLength());
                Assert.DoesNotContain("structured_event_log", root.GetRawText(), StringComparison.Ordinal);
                Assert.DoesNotContain("test.db", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
            }

            using var preview = await client.PostAsJsonAsync(
                "/api/v1/server-maintenance/collected-data/preview",
                new
                {
                    fromInclusive = "2026-08-01T00:00:00Z",
                    toExclusive = "2026-08-02T00:00:00Z",
                    types = new[] { "cnc_raw_telemetry", "cnc_state_history", "cnc_connection_events" },
                    machineId = "machine-maintenance"
                });
            preview.EnsureSuccessStatusCode();
            using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
            Assert.Equal(3, previewJson.RootElement.GetProperty("totalRows").GetInt64());

            using var unsupported = await client.PostAsJsonAsync(
                "/api/v1/server-maintenance/collected-data/preview",
                new
                {
                    fromInclusive = "2026-08-01T00:00:00Z",
                    toExclusive = "2026-08-02T00:00:00Z",
                    types = new[] { "structured_event_log" }
                });
            Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
            Assert.Contains("unsupported_collected_data_type", await unsupported.Content.ReadAsStringAsync());
        });
    }

    [Fact]
    public async Task Purge_requires_current_edit_authority_and_a_fresh_preview()
    {
        await RunAsync(async (application, client, backupFolder) =>
        {
            await SeedAsync(application.Services);
            AddIdentityHeaders(client, includeEditGeneration: false);
            var request = new
            {
                fromInclusive = "2026-08-01T00:00:00Z",
                toExclusive = "2026-08-02T00:00:00Z",
                types = new[] { "cnc_raw_telemetry" },
                machineId = "machine-maintenance",
                expectedTotalRows = 1,
                reason = "Test retention cleanup"
            };

            using var missingEdit = await client.PostAsJsonAsync(
                "/api/v1/server-maintenance/collected-data/purge", request);
            Assert.Equal((HttpStatusCode)428, missingEdit.StatusCode);

            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
            using var changed = await client.PostAsJsonAsync(
                "/api/v1/server-maintenance/collected-data/purge",
                new
                {
                    request.fromInclusive,
                    request.toExclusive,
                    request.types,
                    request.machineId,
                    expectedTotalRows = 2,
                    request.reason
                });
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.Contains("collected_data_preview_changed", await changed.Content.ReadAsStringAsync());
            Assert.False(Directory.Exists(backupFolder)
                && Directory.GetFiles(backupFolder, "*.db", SearchOption.TopDirectoryOnly).Length > 0);
            Assert.Equal(1, await CountAsync(application.Services, "machine_telemetry_raw", "raw-in"));

            client.DefaultRequestHeaders.Remove("X-Meimad-Edit-Generation");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "9");
            using var stale = await client.PostAsJsonAsync(
                "/api/v1/server-maintenance/collected-data/purge", request);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Contains("edit_generation_stale", await stale.Content.ReadAsStringAsync());
        });
    }

    [Fact]
    public async Task Purge_makes_verified_backup_then_deletes_only_selected_half_open_range()
    {
        await RunAsync(async (application, client, backupFolder) =>
        {
            await SeedAsync(application.Services);
            AddIdentityHeaders(client, includeEditGeneration: true);

            using var response = await client.PostAsJsonAsync(
                "/api/v1/server-maintenance/collected-data/purge",
                new
                {
                    fromInclusive = "2026-08-01T00:00:00Z",
                    toExclusive = "2026-08-02T00:00:00Z",
                    types = new[] { "cnc_raw_telemetry", "cnc_state_history", "cnc_connection_events" },
                    machineId = "machine-maintenance",
                    expectedTotalRows = 3,
                    reason = "Remove commissioned diagnostic samples"
                });
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(3, json.RootElement.GetProperty("totalDeletedRows").GetInt64());
            Assert.True(json.RootElement.GetProperty("backup").GetProperty("integrityVerified").GetBoolean());
            Assert.True(json.RootElement.GetProperty("backup").GetProperty("restoreVerified").GetBoolean());
            Assert.False(json.RootElement.GetProperty("backup").GetProperty("fileName").GetString()!.Contains('\\'));

            Assert.Equal(0, await CountAsync(application.Services, "machine_telemetry_raw", "raw-in"));
            Assert.Equal(1, await CountAsync(application.Services, "machine_telemetry_raw", "raw-end"));
            Assert.Equal(1, await CountAsync(application.Services, "machine_telemetry_raw", "raw-other-machine"));
            Assert.Equal(0, await CountAsync(application.Services, "machine_state_history", "state-in"));
            Assert.Equal(0, await CountAsync(application.Services, "machine_connection_events", "event-in"));
            Assert.Equal(1, await CountAsync(application.Services, "structured_event_log", "protected-audit"));
            Assert.Equal(1, await CountEventAsync(application.Services, "COLLECTED_DATA_PURGED"));

            var backups = Directory.GetFiles(backupFolder, "*.db", SearchOption.TopDirectoryOnly);
            Assert.Single(backups);
            await using var backup = new SqliteConnection($"Data Source={backups[0]};Mode=ReadOnly");
            await backup.OpenAsync();
            await using var backupCommand = backup.CreateCommand();
            backupCommand.CommandText = "SELECT COUNT(*) FROM machine_telemetry_raw WHERE id='raw-in';";
            Assert.Equal(1L, Convert.ToInt64(await backupCommand.ExecuteScalarAsync()));
        });
    }

    [Fact]
    public async Task Http_backup_returns_verified_SQLite_bytes_and_checksum()
    {
        await RunAsync(async (application, client, _) =>
        {
            await SeedAsync(application.Services);
            AddIdentityHeaders(client, includeEditGeneration: true);

            using var response = await client.PostAsync(
                "/api/v1/server-maintenance/backups/download", null);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var expectedHash = response.Headers.GetValues("X-Meimad-Checksum-SHA256").Single();
            Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(bytes)), ignoreCase: true);
            Assert.Equal("true", response.Headers.GetValues("X-Meimad-Integrity-Verified").Single());
            Assert.Equal("true", response.Headers.GetValues("X-Meimad-Restore-Verified").Single());
            Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(bytes, 0, 16));
            Assert.Equal(1, await CountEventAsync(application.Services, "DATABASE_BACKUP_CREATED_HTTP"));
        });
    }

    private static void AddIdentityHeaders(HttpClient client, bool includeEditGeneration)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "maintenance-client");
        client.DefaultRequestHeaders.Add("X-Meimad-User-Id", "maintenance-user");
        if (includeEditGeneration)
        {
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
        }
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id)
            VALUES ('calendar-maintenance', 'Maintenance', 'UTC');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES
              ('machine-maintenance', 'M-M', 'Maintenance Machine', 'mill', 'calendar-maintenance', 'active', 1),
              ('machine-other', 'M-O', 'Other Machine', 'mill', 'calendar-maintenance', 'active', 1);
            INSERT INTO machine_connections
              (id, machine_id, adapter_type, enabled, connection_status, polling_interval_ms,
               connection_timeout_ms, maximum_reconnect_backoff_ms, allow_read, allow_write,
               configuration_json, raw_telemetry_retention_days, version, created_at, updated_at)
            VALUES
              ('connection-maintenance', 'machine-maintenance', 'HAAS_NGC', 0, 'DISABLED', 2000,
               3000, 30000, 1, 0, '{}', 14, 1, $now, $now),
              ('connection-other', 'machine-other', 'HAAS_NGC', 0, 'DISABLED', 2000,
               3000, 30000, 1, 0, '{}', 14, 1, $now, $now);
            INSERT INTO machine_telemetry_raw
              (id, connection_id, machine_id, adapter_type, observed_at, operation, raw_payload)
            VALUES
              ('raw-in', 'connection-maintenance', 'machine-maintenance', 'HAAS_NGC', '2026-08-01T12:00:00Z', 'POLL', '{}'),
              ('raw-end', 'connection-maintenance', 'machine-maintenance', 'HAAS_NGC', '2026-08-02T00:00:00Z', 'POLL', '{}'),
              ('raw-other-machine', 'connection-other', 'machine-other', 'HAAS_NGC', '2026-08-01T12:00:00Z', 'POLL', '{}');
            INSERT INTO machine_state_history
              (id, machine_id, connection_id, observed_at, change_kind, snapshot_json)
            VALUES ('state-in', 'machine-maintenance', 'connection-maintenance', '2026-08-01T12:00:00Z', 'STATE', '{}');
            INSERT INTO machine_connection_events
              (id, connection_id, machine_id, event_type, occurred_at, detail_json)
            VALUES ('event-in', 'connection-maintenance', 'machine-maintenance', 'CONNECTED', '2026-08-01T12:00:00Z', '{}');
            INSERT INTO structured_event_log
              (id, event_type, occurred_at, user_id, related_entity_ids_json, reason_code, comment)
            VALUES ('protected-audit', 'PROTECTED', '2026-08-01T12:00:00Z', 'seed', '{}', 'test', 'must remain');
            UPDATE edit_tokens
            SET holder_client_id='maintenance-client', holder_user_id='maintenance-user', generation=1,
                acquired_at=$now, updated_at=$now, version=version+1
            WHERE id=1;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(IServiceProvider services, string table, string id)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountEventAsync(IServiceProvider services, string eventType)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM structured_event_log WHERE event_type=$type;";
        command.Parameters.AddWithValue("$type", eventType);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task RunAsync(
        Func<WebApplication, HttpClient, string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.Maintenance.Api", Guid.NewGuid().ToString("N"));
        var backupFolder = Path.Combine(root, "backups");
        var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1",
                "--Server:Port=5099",
                $"--Database:Path={Path.Combine(root, "test.db")}",
                $"--Backup:Folder={backupFolder}",
                "--Backup:RetentionCount=20"
            ],
            builder => builder.UseTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            await test(application, client, backupFolder);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
