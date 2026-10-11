using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Backup;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Cases;

public sealed class CasePictureTests
{
    // A 1x1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task A_picture_uploaded_to_the_Server_replaces_the_link_and_is_served_from_the_Server()
    {
        await RunAsync(async (application, client, folder) =>
        {
            var (caseId, tag) = await CreateCaseAsync(client, Path.Combine(folder, "missing-link.png"));

            using (var noTag = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/cases/{caseId}/picture") { Content = new ByteArrayContent(Png) })
            using (var refused = await client.SendAsync(noTag))
                Assert.Equal(HttpStatusCode.PreconditionRequired, refused.StatusCode);
            using (var text = await PutAsync(client, caseId, tag, "notes.txt", "not a picture"u8.ToArray()))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, text.StatusCode);
                Assert.Contains("picture_format_unsupported", await text.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
            using (client.SignedInWithOnly("planning.board"))
            using (var forbidden = await PutAsync(client, caseId, tag, "snap.png", Png))
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            using var saved = await PutAsync(client, caseId, tag, "PN-PIC-step-preview.png&source=" + Uri.EscapeDataString(@"\\srv\cases\PN-PIC\_MeimadPlanner\PN-PIC-step-preview.png"), Png);
            var savedText = await saved.Content.ReadAsStringAsync();
            Assert.True(saved.StatusCode == HttpStatusCode.OK, savedText);
            Assert.NotEqual(tag, saved.Headers.ETag!.Tag);
            Assert.Null(JsonNode.Parse(savedText)!["previewPath"]);

            using (var stale = await PutAsync(client, caseId, tag, "again.png", Png))
                Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

            using var preview = await client.GetAsync($"/api/v1/cases/{caseId}/preview");
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Equal("image/png", preview.Content.Headers.ContentType!.MediaType);
            Assert.Equal(Png, await preview.Content.ReadAsByteArrayAsync());
            var info = JsonNode.Parse(await client.GetStringAsync($"/api/v1/cases/{caseId}/picture"))!;
            Assert.Equal("PN-PIC-step-preview.png", info["fileName"]!.GetValue<string>());
            Assert.Equal(Png.Length, info["sizeBytes"]!.GetValue<long>());
            Assert.Equal(@"\\srv\cases\PN-PIC\_MeimadPlanner\PN-PIC-step-preview.png", info["sourceReference"]!.GetValue<string>());

            // The file is below the release root and in the recovery set.
            var root = application.Services.GetRequiredService<GCodeOptions>().ResolvedReleaseRoot;
            var stored = Path.Combine(root, "case-pictures", caseId, info["sha256"]!.GetValue<string>() + ".png");
            Assert.True(File.Exists(stored));
            var recovery = await RecoveryDatabase.ReadAsync(application.Services.GetRequiredService<SqliteDatabase>().DatabasePath, CancellationToken.None);
            Assert.Contains(recovery.Files, file => file.Path == $"gcode/case-pictures/{caseId}/{info["sha256"]!.GetValue<string>()}.png");

            // Removing it leaves the Case without a picture.
            using (var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/cases/{caseId}/picture"))
            {
                delete.Headers.IfMatch.Add(new EntityTagHeaderValue(saved.Headers.ETag!.Tag));
                using var removed = await client.SendAsync(delete);
                Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            }
            using var gone = await client.GetAsync($"/api/v1/cases/{caseId}/picture");
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            using var noPreview = await client.GetAsync($"/api/v1/cases/{caseId}/preview");
            Assert.Equal(HttpStatusCode.NotFound, noPreview.StatusCode);
        });
    }

    [Fact]
    public async Task A_linked_picture_the_Server_can_read_is_copied_without_changing_the_Case()
    {
        await RunAsync(async (application, client, folder) =>
        {
            var link = Path.Combine(folder, "linked.png");
            await File.WriteAllBytesAsync(link, Png);
            var (caseId, tag) = await CreateCaseAsync(client, link);
            var (otherId, _) = await CreateCaseAsync(client, Path.Combine(folder, "nowhere.png"), "PN-PIC-2");

            var (copied, unreadable) = await application.Services.GetRequiredService<CasePictureCopyService>().RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, copied);
            Assert.Equal(1, unreadable);
            using var read = await client.GetAsync($"/api/v1/cases/{caseId}");
            Assert.Equal(tag, read.Headers.ETag!.Tag);
            Assert.Equal(link, JsonNode.Parse(await read.Content.ReadAsStringAsync())!["previewPath"]!.GetValue<string>());
            var info = JsonNode.Parse(await client.GetStringAsync($"/api/v1/cases/{caseId}/picture"))!;
            Assert.Equal(link, info["sourceReference"]!.GetValue<string>());

            // The Server's copy is shown even when the link breaks.
            File.Delete(link);
            using var preview = await client.GetAsync($"/api/v1/cases/{caseId}/preview");
            Assert.Equal(Png, await preview.Content.ReadAsByteArrayAsync());
            using var other = await client.GetAsync($"/api/v1/cases/{otherId}/picture");
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
            Assert.Equal((0, 1), await application.Services.GetRequiredService<CasePictureCopyService>().RunOnceAsync(CancellationToken.None));
        });
    }

    [Fact]
    public async Task Version_115_turns_the_factory_Z_drive_links_into_network_folder_links()
    {
        foreach (var (root, converted) in new[] { (@"\\192.168.0.240\data", true), (@"\\other-server\share", false) })
        {
            await using var fixture = TemporaryDatabase.CreateUnmigrated();
            var migrator = new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance);
            await migrator.MigrateAsync(114);
            await using (var v114 = await fixture.Database.OpenConnectionAsync())
            {
                await ExecuteAsync(v114, $$"""
                    UPDATE network_folder_settings SET root_path = '{{root}}', aliases_json = '[]' WHERE id = 1;
                    INSERT INTO cases (id, part_number, name, working_folder_path, preview_reference)
                    VALUES ('case-z', 'TNK1105650-008', 'Bracket', 'Z:\customers files\DPD\MALKAM\TNK\TNK1105650-008',
                            'Z:\customers files\DPD\MALKAM\TNK\TNK1105650-008\3d-view.png'),
                           ('case-j', 'PN-J', 'Other', 'J:\work\PN-J', 'J:\work\PN-J\pic.png');
                    INSERT INTO case_model_files (id, case_id, kind, format, file_path, label, created_at, updated_at)
                    VALUES ('model-z', 'case-z', 'part', 'step', 'Z:\customers files\DPD\MALKAM\TNK\TNK1105650-008\part.stp', 'Part', 'x', 'x');
                    """);
            }
            SqliteConnection.ClearAllPools();

            await migrator.MigrateAsync();

            await using var connection = await fixture.Database.OpenConnectionAsync();
            var settings = await SqliteNetworkFolderSettings.ReadAsync(connection, null, CancellationToken.None);
            var working = (string)(await ScalarAsync(connection, "SELECT working_folder_path FROM cases WHERE id = 'case-z';"))!;
            var picture = (string)(await ScalarAsync(connection, "SELECT preview_reference FROM cases WHERE id = 'case-z';"))!;
            var model = (string)(await ScalarAsync(connection, "SELECT file_path FROM case_model_files WHERE id = 'model-z';"))!;
            if (converted)
            {
                Assert.Contains(@"Z:\", settings.Aliases);
                Assert.Equal(@"customers files\DPD\MALKAM\TNK\TNK1105650-008", working);
                Assert.Equal(@"\\192.168.0.240\data\customers files\DPD\MALKAM\TNK\TNK1105650-008\3d-view.png", settings.ToAbsolute(picture));
                Assert.Equal(@"customers files\DPD\MALKAM\TNK\TNK1105650-008\part.stp", model);
            }
            else
            {
                Assert.Empty(settings.Aliases);
                Assert.StartsWith(@"Z:\", working, StringComparison.Ordinal);
                Assert.StartsWith(@"Z:\", picture, StringComparison.Ordinal);
            }
            // Other drives are not changed.
            Assert.Equal(@"J:\work\PN-J\pic.png", await ScalarAsync(connection, "SELECT preview_reference FROM cases WHERE id = 'case-j';"));
            Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM case_pictures;"));
        }
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string caseId, string tag, string name, byte[] bytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/cases/{caseId}/picture?name={(name.Contains('&') ? name[..name.IndexOf('&')] : Uri.EscapeDataString(name))}{(name.Contains('&') ? name[name.IndexOf('&')..] : string.Empty)}")
        {
            Content = new ByteArrayContent(bytes)
        };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(tag));
        return await client.SendAsync(request);
    }

    private static async Task<(string Id, string Tag)> CreateCaseAsync(HttpClient client, string previewPath, string partNumber = "PN-PIC-1")
    {
        using var created = await client.PostAsJsonAsync("/api/v1/cases", new
        {
            partNumber,
            name = "Picture test",
            previewPath,
            workingFolderPath = Path.GetDirectoryName(previewPath)
        });
        var text = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, text);
        return (JsonNode.Parse(text)!["caseId"]!.GetValue<string>(), created.Headers.ETag!.Tag);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, string, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "MeimadPlanner.CasePicture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5103", $"--Database:Path={Path.Combine(path, "test.db")}",
             $"--GCode:ReleaseRoot={Path.Combine(path, "releases")}"],
            webHost => webHost.UseSignedInTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "case-picture-client");
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
