using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Backup;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.TvDashboard;

/// <summary>
/// The part picture in each TV row (owner request 2026-10-12, schema v118): source priority, the current Operation, the
/// placeholder, the small read-only picture route and the signed-in Operation thumbnail routes.
/// </summary>
public sealed class TvDashboardThumbnailTests
{
    private const string OperationsPath = "/api/v1/cases/case-th/operations";

    [Fact]
    public async Task Row_picture_follows_the_priority_Case_picture_then_package_then_snapshot_then_placeholder()
    {
        await RunAsync(async (application, client, _) =>
        {
            await SeedAsync(application.Services);
            Assert.Equal("placeholder", (await CurrentAsync(client))["thumbnailSource"]!.GetValue<string>());

            var snapshot = Png(300, 200, 10);
            await PutThumbnailAsync(client, "case-op-10", "snapshot", snapshot, null);
            var current = await CurrentAsync(client);
            Assert.Equal("snapshot", current["thumbnailSource"]!.GetValue<string>());
            Assert.Equal("1-" + Sha256(snapshot), current["thumbnailVersion"]!.GetValue<string>());
            Assert.Equal("/api/v1/tv-dashboard/thumbnails/1-" + Sha256(snapshot), current["thumbnailUrl"]!.GetValue<string>());

            var package = Png(400, 400, 80);
            await PutThumbnailAsync(client, "case-op-10", "package", package, null);
            current = await CurrentAsync(client);
            Assert.Equal("package", current["thumbnailSource"]!.GetValue<string>());
            Assert.Equal("1-" + Sha256(package), current["thumbnailVersion"]!.GetValue<string>());

            // The Case picture stored on the Server wins over both Operation thumbnails.
            var picture = Png(120, 240, 160);
            using (var read = await client.GetAsync("/api/v1/cases/case-th"))
            using (var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/cases/case-th/picture?name=part.png") { Content = new ByteArrayContent(picture) })
            {
                put.Headers.IfMatch.Add(new EntityTagHeaderValue(read.Headers.ETag!.Tag));
                using var saved = await client.SendAsync(put);
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            }
            current = await CurrentAsync(client);
            Assert.Equal("case_picture", current["thumbnailSource"]!.GetValue<string>());
            Assert.Equal("1-" + Sha256(picture), current["thumbnailVersion"]!.GetValue<string>());
            Assert.Equal("PN-TH - Bracket", current["thumbnailAlt"]!.GetValue<string>());

            var list = JsonNode.Parse(await client.GetStringAsync($"{OperationsPath}/case-op-10/thumbnails"))!;
            Assert.Equal("case_picture", list["tvSource"]!.GetValue<string>());
            Assert.Equal(new[] { "package", "snapshot" }, list["items"]!.AsArray().Select(item => item!["source"]!.GetValue<string>()).ToArray());

            // A stored file that went missing is skipped: the next source is shown instead of a broken picture.
            var root = application.Services.GetRequiredService<Meimad.Planner.Server.Configuration.GCodeOptions>().ResolvedReleaseRoot;
            File.Delete(Directory.GetFiles(Path.Combine(root, "case-pictures"), "*.png", SearchOption.AllDirectories).Single());
            Assert.Equal("package", (await CurrentAsync(client))["thumbnailSource"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task Picture_belongs_to_the_current_Operation_and_changes_with_it()
    {
        await RunAsync(async (application, client, _) =>
        {
            await SeedAsync(application.Services);
            var next = Png(64, 64, 200);
            await PutThumbnailAsync(client, "case-op-20", "snapshot", next, null);

            // Operation 20's picture is not shown while Operation 10 is current; the row keeps the placeholder.
            var current = await CurrentAsync(client);
            Assert.Equal(10, current["operationNumber"]!.GetValue<int>());
            Assert.Equal("placeholder", current["thumbnailSource"]!.GetValue<string>());
            var machine = await MachineAsync(client);
            Assert.Equal("1-" + Sha256(next), machine["next"]!["thumbnailVersion"]!.GetValue<string>());

            // When Operation 10 finishes, Operation 20 becomes current: number, name and picture change in one projection.
            await ExecuteAsync(application.Services, "UPDATE batch_operations SET status = 'completed' WHERE id = 'op-10';");
            current = await CurrentAsync(client);
            Assert.Equal(20, current["operationNumber"]!.GetValue<int>());
            Assert.Equal("Finish mill", current["operationName"]!.GetValue<string>());
            Assert.Equal("PN-TH", current["partNumber"]!.GetValue<string>());
            Assert.Equal("1-" + Sha256(next), current["thumbnailVersion"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task Picture_route_serves_a_small_versioned_png_read_only_and_only_for_displayed_parts()
    {
        await RunAsync(async (application, client, _) =>
        {
            await SeedAsync(application.Services);
            var large = Png(1600, 900, 30);
            await PutThumbnailAsync(client, "case-op-10", "snapshot", large, null);
            var url = (await CurrentAsync(client))["thumbnailUrl"]!.GetValue<string>();

            // The TV has no account: an unknown session (or none) still reads the picture.
            using (client.SignedOut())
            {
                using var image = await client.GetAsync(url);
                Assert.Equal(HttpStatusCode.OK, image.StatusCode);
                Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
                var bytes = await image.Content.ReadAsByteArrayAsync();
                Assert.Equal((192, 192), PngSize(bytes));
                Assert.True(bytes.Length < large.Length && bytes.Length < 150_000, $"{bytes.Length} bytes");
                Assert.Contains("max-age=3600", image.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
                Assert.Equal("\"tv-thumbnail:" + url[(url.LastIndexOf('/') + 1)..] + "\"", image.Headers.ETag!.Tag);

                using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
                conditional.Headers.IfNoneMatch.Add(image.Headers.ETag!);
                using var unchanged = await client.SendAsync(conditional);
                Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
                Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());

                // Only GET exists on the TV routes.
                foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
                {
                    using var write = new HttpRequestMessage(method, url) { Content = new ByteArrayContent(large) };
                    using var refused = await client.SendAsync(write);
                    Assert.Equal(HttpStatusCode.MethodNotAllowed, refused.StatusCode);
                }

                // Without an account the Operation thumbnail routes refuse every change.
                using var upload = new HttpRequestMessage(HttpMethod.Put, $"{OperationsPath}/case-op-10/thumbnails/package?name=a.png")
                { Content = new ByteArrayContent(large) };
                upload.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                using var unauthorized = await client.SendAsync(upload);
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                using var removal = await client.DeleteAsync($"{OperationsPath}/case-op-10/thumbnails/snapshot");
                Assert.Equal(HttpStatusCode.Unauthorized, removal.StatusCode);

                // Malformed versions and pictures of parts no display-enabled Machine works on are not served.
                foreach (var bad in new[] { "abc", "1-" + new string('z', 64), "2-" + Sha256(large), "1-" + Sha256(Png(5, 5, 1)) })
                {
                    using var missing = await client.GetAsync("/api/v1/tv-dashboard/thumbnails/" + bad);
                    Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                }
            }

            // The same picture of a Machine hidden from the TV is not served any more.
            await ExecuteAsync(application.Services, "UPDATE machines SET display_enabled = 0 WHERE id = 'machine-th';");
            using (var hidden = await client.GetAsync(url))
                Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        });
    }

    [Fact]
    public async Task Operation_thumbnail_routes_need_cases_edit_and_refuse_stale_and_bad_uploads()
    {
        await RunAsync(async (application, client, _) =>
        {
            await SeedAsync(application.Services);
            var path = $"{OperationsPath}/case-op-10/thumbnails/snapshot";
            var png = Png(32, 32, 1);

            using (client.SignedInWithOnly(Permissions.PlanMachines))
            using (var forbidden = await SendAsync(client, HttpMethod.Put, path + "?name=a.png", png, ifNoneMatchAny: true))
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            using (var noPrecondition = await SendAsync(client, HttpMethod.Put, path + "?name=a.png", png))
                Assert.Equal(HttpStatusCode.PreconditionRequired, noPrecondition.StatusCode);
            using (var unknown = await SendAsync(client, HttpMethod.Put, $"{OperationsPath}/case-op-10/thumbnails/other?name=a.png", png, ifNoneMatchAny: true))
                Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            using (var text = await SendAsync(client, HttpMethod.Put, path + "?name=a.txt", "not an image"u8.ToArray(), ifNoneMatchAny: true))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, text.StatusCode);
                Assert.Contains("thumbnail_format_unsupported", await text.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
            var tooLarge = new byte[10 * 1024 * 1024 + 1];
            png.CopyTo(tooLarge, 0);
            using (var large = await SendAsync(client, HttpMethod.Put, path + "?name=big.png", tooLarge, ifNoneMatchAny: true))
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
            using (var otherCase = await SendAsync(client, HttpMethod.Put, "/api/v1/cases/case-none/operations/case-op-10/thumbnails/snapshot?name=a.png", png, ifNoneMatchAny: true))
                Assert.Equal(HttpStatusCode.NotFound, otherCase.StatusCode);

            string tag;
            using (client.SignedInWithOnly(Permissions.EditCases))
            {
                var created = await PutThumbnailAsync(client, "case-op-10", "snapshot", png, null, "snap.png", @"\\srv\PN-TH\_MeimadPlanner\snap.png");
                tag = created.Tag;
                Assert.Equal("\"case-operation-thumbnail:case-op-10:snapshot:v1\"", tag);
            }

            // A second create, or a replace based on an old version, is refused and says who saved the current one.
            using (var again = await SendAsync(client, HttpMethod.Put, path + "?name=b.png", Png(8, 8, 2), ifNoneMatchAny: true))
            {
                Assert.Equal(HttpStatusCode.PreconditionFailed, again.StatusCode);
                var body = JsonNode.Parse(await again.Content.ReadAsStringAsync())!;
                Assert.Equal("resource_version_stale", body["error"]!["code"]!.GetValue<string>());
                Assert.Equal("Operation thumbnail", body["error"]!["conflict"]!["resource"]!.GetValue<string>());
            }
            var replaced = await PutThumbnailAsync(client, "case-op-10", "snapshot", Png(8, 8, 3), tag);
            Assert.Equal("\"case-operation-thumbnail:case-op-10:snapshot:v2\"", replaced.Tag);
            using (var stale = await SendAsync(client, HttpMethod.Put, path + "?name=c.png", Png(8, 8, 4), ifMatch: tag))
                Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            using (var staleDelete = await SendAsync(client, HttpMethod.Delete, path, null, ifMatch: tag))
                Assert.Equal(HttpStatusCode.PreconditionFailed, staleDelete.StatusCode);

            var info = JsonNode.Parse(await client.GetStringAsync($"{OperationsPath}/case-op-10/thumbnails"))!["items"]!.AsArray().Single()!;
            Assert.Equal(2, info["version"]!.GetValue<int>());
            using (var image = await client.GetAsync(path + "/image"))
            {
                Assert.Equal(HttpStatusCode.OK, image.StatusCode);
                Assert.Equal(Png(8, 8, 3), await image.Content.ReadAsByteArrayAsync());
            }

            // Stored below the release root, in the recovery set; removal empties the slot.
            var recovery = await RecoveryDatabase.ReadAsync(application.Services.GetRequiredService<SqliteDatabase>().DatabasePath, CancellationToken.None);
            Assert.Contains(recovery.Files, file => file.Path == $"gcode/operation-thumbnails/case-op-10/{Sha256(Png(8, 8, 3))}.png");
            using (client.SignedInWithOnly(Permissions.PlanMachines))
            using (var forbiddenDelete = await SendAsync(client, HttpMethod.Delete, path, null, ifMatch: replaced.Tag))
                Assert.Equal(HttpStatusCode.Forbidden, forbiddenDelete.StatusCode);
            using (var deleted = await SendAsync(client, HttpMethod.Delete, path, null, ifMatch: replaced.Tag))
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            using (var gone = await client.GetAsync(path + "/image"))
                Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            Assert.Equal("placeholder", (await CurrentAsync(client))["thumbnailSource"]!.GetValue<string>());
            Assert.Equal(3L, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM structured_event_log WHERE event_type LIKE 'case_operation_thumbnail_%';"));
        });
    }

    [Fact]
    public async Task Idle_row_shows_the_last_part_only_when_the_setting_allows_and_an_alarm_is_an_error_with_a_warning()
    {
        foreach (var showLastPart in new[] { false, true })
        {
            await RunAsync(async (application, client, _) =>
            {
                await SeedAsync(application.Services);
                var picture = Png(50, 50, 9);
                await PutThumbnailAsync(client, "case-op-10", "snapshot", picture, null);
                await ExecuteAsync(application.Services, $"""
                    DELETE FROM machine_assignments;
                    UPDATE batch_operations SET status = 'completed', actual_machine_id = 'machine-th',
                        actual_start = '{DateTimeOffset.UtcNow.AddHours(-3):O}', actual_end = '{DateTimeOffset.UtcNow.AddHours(-1):O}'
                    WHERE id = 'op-10';
                    """);

                var machine = await MachineAsync(client);
                Assert.Null(machine["current"]);
                Assert.Equal("IDLE", machine["displayStatus"]!.GetValue<string>());
                if (!showLastPart)
                {
                    Assert.Null(machine["idlePart"]);
                }
                else
                {
                    var idle = machine["idlePart"]!;
                    Assert.Equal("PN-TH", idle["partNumber"]!.GetValue<string>());
                    Assert.Equal(10, idle["operationNumber"]!.GetValue<int>());
                    Assert.Equal("1-" + Sha256(picture), idle["thumbnailVersion"]!.GetValue<string>());
                    // The last part's picture is served while it is shown on the idle row.
                    using var image = await client.GetAsync(idle["thumbnailUrl"]!.GetValue<string>());
                    Assert.Equal(HttpStatusCode.OK, image.StatusCode);
                }

                await ExecuteAsync(application.Services, "UPDATE machine_current_state SET machine_state = 'ALARM' WHERE machine_id = 'machine-th';");
                machine = await MachineAsync(client);
                Assert.Equal("ERROR", machine["displayStatus"]!.GetValue<string>());
                Assert.Equal("cnc_alarm", machine["warning"]!["code"]!.GetValue<string>());
            }, showLastPart);
        }
    }

    [Fact]
    public async Task Version_118_adds_the_operation_thumbnail_table_with_cascade_checks_and_revision_triggers()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        var migrator = new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(117);
        await using (var v117 = await fixture.Database.OpenConnectionAsync())
        {
            await using var command = v117.CreateCommand();
            command.CommandText = """
                INSERT INTO cases (id, part_number, name, working_folder_path) VALUES ('case-m', 'PN-M', 'Part', 'C:\Cases\PN-M');
                INSERT INTO case_operations (id, case_id, operation_number, route_position, name) VALUES ('op-m', 'case-m', 10, 0, 'Mill');
                """;
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        await migrator.MigrateAsync();

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var sql = connection.CreateCommand();
        sql.CommandText = "SELECT name FROM schema_migrations WHERE version = 118;";
        Assert.Equal("case_operation_thumbnails", await sql.ExecuteScalarAsync());
        sql.CommandText = "SELECT group_concat(name, ',') FROM (SELECT name FROM pragma_table_info('case_operation_thumbnails') ORDER BY cid);";
        Assert.Equal("case_operation_id,source,file_name,media_type,file_size,file_hash,stored_relative_path,source_reference,version,updated_at,updated_by",
            await sql.ExecuteScalarAsync());

        sql.CommandText = "SELECT revision, non_planning_revision FROM projection_revision WHERE id = 1;";
        (long All, long NonPlanning) before;
        await using (var reader = await sql.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            before = (reader.GetInt64(0), reader.GetInt64(1));
        }
        var hash = new string('a', 64);
        sql.CommandText = $"""
            INSERT INTO case_operation_thumbnails (case_operation_id, source, file_name, media_type, file_size, file_hash,
                stored_relative_path, version, updated_at)
            VALUES ('op-m', 'package', 'p.png', 'image/png', 10, '{hash}', 'operation-thumbnails/op-m/{hash}.png', 1, '2026-10-12T00:00:00Z');
            """;
        await sql.ExecuteNonQueryAsync();
        sql.CommandText = "SELECT revision, non_planning_revision FROM projection_revision WHERE id = 1;";
        await using (var reader = await sql.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            // Not a planning input: both counters move together, so the planning revision stays.
            Assert.Equal(before.All + 1, reader.GetInt64(0));
            Assert.Equal(before.NonPlanning + 1, reader.GetInt64(1));
        }

        // Unknown sources, a second row per source and bad hashes are refused.
        foreach (var bad in new[]
        {
            $"('op-m', 'stl', 'p.png', 'image/png', 10, '{hash}', 'x', 1, 'now')",
            $"('op-m', 'package', 'p.png', 'image/png', 10, '{hash}', 'x', 1, 'now')",
            "('op-m', 'snapshot', 'p.png', 'image/png', 10, 'XYZ', 'x', 1, 'now')",
            $"('op-m', 'snapshot', 'p.stl', 'model/stl', 10, '{hash}', 'x', 1, 'now')"
        })
        {
            sql.CommandText = "INSERT INTO case_operation_thumbnails (case_operation_id, source, file_name, media_type, file_size, file_hash, stored_relative_path, version, updated_at) VALUES " + bad + ";";
            await Assert.ThrowsAsync<SqliteException>(() => sql.ExecuteNonQueryAsync());
        }

        // Removing the Case Operation removes its thumbnails.
        sql.CommandText = "PRAGMA foreign_keys = ON; DELETE FROM case_operations WHERE id = 'op-m'; SELECT COUNT(*) FROM case_operation_thumbnails;";
        Assert.Equal(0L, await sql.ExecuteScalarAsync());
    }

    // ----- helpers -------------------------------------------------------------------------------

    private static async Task<(string Tag, JsonNode Body)> PutThumbnailAsync(
        HttpClient client, string operationId, string source, byte[] bytes, string? ifMatch,
        string fileName = "thumbnail.png", string? reference = null)
    {
        var path = $"{OperationsPath}/{operationId}/thumbnails/{source}?name={Uri.EscapeDataString(fileName)}"
            + (reference is null ? string.Empty : "&reference=" + Uri.EscapeDataString(reference));
        using var response = await SendAsync(client, HttpMethod.Put, path, bytes, ifMatch: ifMatch, ifNoneMatchAny: ifMatch is null);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, text);
        var body = JsonNode.Parse(text)!;
        Assert.Equal(source, body["source"]!.GetValue<string>());
        Assert.Equal(Sha256(bytes), body["sha256"]!.GetValue<string>());
        if (reference is not null) Assert.Equal(reference, body["sourceReference"]!.GetValue<string>());
        return (response.Headers.ETag!.Tag, body);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, byte[]? body, string? ifMatch = null, bool ifNoneMatchAny = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new ByteArrayContent(body);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (ifNoneMatchAny) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        return await client.SendAsync(request);
    }

    private static async Task<JsonNode> MachineAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/tv-dashboard");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonNode.Parse(text)!["machines"]!.AsArray().Single()!;
    }

    private static async Task<JsonNode> CurrentAsync(HttpClient client) => (await MachineAsync(client))["current"]!;

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static (int Width, int Height) PngSize(byte[] png)
    {
        Assert.True(png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
    }

    /// <summary>A valid RGBA PNG of the given size filled with a gradient seeded by <paramref name="seed"/>.</summary>
    internal static byte[] Png(int width, int height, byte seed)
    {
        var raw = new byte[(width * 4 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 4 + 1);
            raw[row] = 0;
            for (var x = 0; x < width; x++)
            {
                var offset = row + 1 + x * 4;
                raw[offset] = (byte)(seed + x);
                raw[offset + 1] = (byte)(seed + y);
                raw[offset + 2] = (byte)(seed * 3 + x + y);
                raw[offset + 3] = 255;
            }
        }
        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(raw);
            compressed = buffer.ToArray();
        }
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        header[9] = 6;
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32([.. typeBytes, .. data]));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    private static async Task ExecuteAsync(IServiceProvider services, string sql)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(IServiceProvider services, string sql)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    /// <summary>One display-enabled Machine with Operation 10 (in setup) and Operation 20 queued, of Case PN-TH "Bracket".</summary>
    private static async Task SeedAsync(IServiceProvider services)
    {
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var calendarJson = JsonSerializer.Serialize(new
        {
            availability = new[] { new { startsAt = start, endsAt = start.AddDays(7) } }
        });
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
            VALUES ('calendar-th', 'TV calendar', 'UTC', $calendarJson);
            INSERT INTO application_settings (key, value) VALUES ('timeline.setup_calendar_json', $calendarJson);
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active, display_enabled)
            VALUES ('machine-th', 'M-TH', 'Thumbnail Mill', 'mill', 'calendar-th', 'active', 1, 1);
            INSERT INTO machine_connections (
                id, machine_id, adapter_type, enabled, connection_status, polling_interval_ms, connection_timeout_ms,
                maximum_reconnect_backoff_ms, allow_read, allow_write, configuration_json, raw_telemetry_retention_days,
                version, created_at, updated_at)
            VALUES ('connection-th', 'machine-th', 'HAAS_NGC', 1, 'ONLINE', 1000, 1000, 30000, 1, 0, '{}', 14, 1, $now, $now);
            INSERT INTO machine_current_state (
                machine_id, connection_id, adapter_type, observed_at, connection_status, last_seen_at, machine_state,
                component_health_json, capability_health_json, snapshot_json, version)
            VALUES ('machine-th', 'connection-th', 'HAAS_NGC', $now, 'ONLINE', $now, 'ACTIVE', '{}', '{}', '{}', 1);
            INSERT INTO cases (id, part_number, name, working_folder_path) VALUES ('case-th', 'PN-TH', 'Bracket', '');
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-th', 'case-th', 'B-TH', 'waiting', 4);
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name, required_machine_type, setup_seconds, cycle_seconds)
            VALUES ('case-op-10', 'case-th', 10, 0, 'Rough mill', 'mill', 600, 900),
                   ('case-op-20', 'case-th', 20, 1, 'Finish mill', 'mill', 600, 900);
            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id, operation_number, route_position, name,
                required_machine_type, setup_seconds, cycle_seconds, status, actual_start, actual_machine_id)
            VALUES ('op-10', 'batch-th', 'case-op-10', 10, 0, 'Rough mill', 'mill', 600, 900, 'in_progress', $actualStart, 'machine-th'),
                   ('op-20', 'batch-th', 'case-op-20', 20, 1, 'Finish mill', 'mill', 600, 900, 'not_started', NULL, NULL);
            INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
            VALUES ('assignment-10', 'op-10', 'machine-th', 0), ('assignment-20', 'op-20', 'machine-th', 1);
            """;
        command.Parameters.AddWithValue("$calendarJson", calendarJson);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$actualStart", now.AddMinutes(-5).ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, string, Task> test, bool showLastPart = false)
    {
        var path = Path.Combine(Path.GetTempPath(), "MeimadPlanner.TvThumbnail.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5104", $"--Database:Path={Path.Combine(path, "test.db")}",
             $"--GCode:ReleaseRoot={Path.Combine(path, "releases")}",
             $"--TvDashboard:ShowLastPartImageWhenIdle={(showLastPart ? "true" : "false")}"],
            webHost => webHost.UseSignedInTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "tv-thumbnail-client");
            await test(application, client, path);
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
