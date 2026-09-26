using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class NetworkFolderPathTests
{
    private static readonly NetworkFolderSettings Share = new(
        @"\\fileserver\data\customers files", [@"J:\customers files", @"Z:\customers files"], "Meimad Cases", 2,
        DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(@"J:\customers files\DPD\30P\NC", @"DPD\30P\NC")]
    [InlineData(@"z:/customers files/DPD/16W1120-13", @"DPD\16W1120-13")]
    [InlineData(@"\\fileserver\data\customers files\DPD\preview.png", @"DPD\preview.png")]
    [InlineData(@"J:\customers files", ".")]
    [InlineData(@"J:\customers files2\other", @"J:\customers files2\other")]
    [InlineData(@"C:\Local\folder", @"C:\Local\folder")]
    [InlineData(@"DPD\already relative", @"DPD\already relative")]
    public void Links_under_the_share_or_an_alias_are_stored_relative(string entered, string stored)
    {
        Assert.Equal(stored, Share.ToStored(entered));
    }

    [Fact]
    public void Stored_links_resolve_to_the_network_path_and_rooted_ones_pass_through()
    {
        Assert.Equal(@"\\fileserver\data\customers files\DPD\30P", Share.ToAbsolute(@"DPD\30P"));
        Assert.Equal(@"\\fileserver\data\customers files", Share.ToAbsolute("."));
        Assert.Equal(@"C:\Local\folder", Share.ToAbsolute(@"C:\Local\folder"));
        Assert.Equal(@"DPD\30P", NetworkFolderSettings.None.ToAbsolute(@"DPD\30P"));
        Assert.Equal(@"Meimad Cases\PN-1", Share.KitaronCaseRelativeFolder("PN-1"));
    }

    [Fact]
    public async Task Saving_the_network_folder_converts_links_and_the_api_returns_network_paths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.NetworkFolder.Tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "network.db");
        var generated = Path.Combine(directory, "KitaronCases", "PN-KIT");
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={databasePath}"],
            webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO cases (id, part_number, name, working_folder_path, preview_reference)
                    VALUES ('case-j', 'PN-J', 'Part J', 'J:\customers files\DPD\PN-J', 'Z:\customers files\DPD\PN-J\preview.png'),
                           ('case-k', 'PN-KIT', 'Kitaron part', $generated, NULL),
                           ('case-l', 'PN-L', 'Local part', 'C:\Local\PN-L', NULL);
                    UPDATE edit_tokens SET holder_client_id = 'setup-editor', holder_user_id = 'planner', generation = 1,
                        acquired_at = '2026-09-26T00:00:00Z', version = version + 1 WHERE id = 1;
                    """;
                seed.Parameters.AddWithValue("$generated", generated);
                await seed.ExecuteNonQueryAsync();
            }

            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "setup-editor");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
            using var saved = await client.PutAsJsonAsync("/api/v1/network-folder", new
            {
                rootPath = @"\\fileserver\data\customers files",
                aliases = new[] { @"J:\customers files", @"Z:\customers files" },
                kitaronCaseFolder = "Meimad Cases",
                expectedVersion = 1
            });
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            using (var document = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()))
                Assert.Equal(3, document.RootElement.GetProperty("convertedLinks").GetInt32());

            await using (var connection = await database.OpenConnectionAsync())
            await using (var verify = connection.CreateCommand())
            {
                verify.CommandText = "SELECT group_concat(id || '=' || working_folder_path || '|' || COALESCE(preview_reference, ''), ';') FROM (SELECT * FROM cases ORDER BY id);";
                Assert.Equal(@"case-j=DPD\PN-J|DPD\PN-J\preview.png;case-k=Meimad Cases\PN-KIT|;case-l=C:\Local\PN-L|",
                    await verify.ExecuteScalarAsync());
            }

            // Every client receives the network path, whatever drive letter it maps.
            using var read = await client.GetAsync("/api/v1/cases/case-j");
            using (var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync()))
            {
                Assert.Equal(@"\\fileserver\data\customers files\DPD\PN-J",
                    document.RootElement.GetProperty("workingFolderPath").GetString());
                Assert.Equal(@"\\fileserver\data\customers files\DPD\PN-J\preview.png",
                    document.RootElement.GetProperty("previewPath").GetString());
            }

            // A path saved later through a drive-letter alias is stored relative as well.
            using var current = await client.GetAsync("/api/v1/cases/case-l");
            var tag = current.Headers.ETag!.Tag;
            using var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/cases/case-l")
            {
                Content = JsonContent.Create(new { workingFolderPath = @"J:\customers files\DPD\PN-L" })
            };
            patch.Headers.TryAddWithoutValidation("If-Match", tag);
            using var patched = await client.SendAsync(patch);
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
            await using (var connection = await database.OpenConnectionAsync())
            await using (var verify = connection.CreateCommand())
            {
                verify.CommandText = "SELECT working_folder_path FROM cases WHERE id = 'case-l';";
                Assert.Equal(@"DPD\PN-L", await verify.ExecuteScalarAsync());
            }
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            for (var attempt = 0; Directory.Exists(directory); attempt++)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch when (attempt < 5) { await Task.Delay(100); }
            }
        }
    }
}
