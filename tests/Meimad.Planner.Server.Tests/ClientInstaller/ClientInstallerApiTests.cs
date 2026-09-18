using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.ClientInstaller;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Tests.ClientInstaller;

public sealed class ClientInstallerApiTests
{
    [Fact]
    public async Task Server_offers_the_bundled_client_installer_with_its_version_and_checksum()
    {
        var root = NewRoot();
        var folder = Path.Combine(root, "client-installer");
        Directory.CreateDirectory(folder);
        var payload = Encoding.ASCII.GetBytes("not an MSI, but the bytes must travel unchanged\n");
        var sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        await File.WriteAllBytesAsync(Path.Combine(folder, "Meimad-Planner-Client-Setup.msi"), payload);
        await File.WriteAllTextAsync(
            Path.Combine(folder, "Meimad-Planner-Client-Setup.json"),
            $$"""{"fileName":"Meimad-Planner-Client-Setup.msi","version":"0.1.117","sha256":"{{sha256}}","byteLength":{{payload.Length}},"builtAt":"2026-09-18T12:00:00Z"}""");
        await using var application = Build(root, folder, 5099);
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();

            using var manifest = await client.GetAsync("/api/v1/client-installer");
            Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
            using var json = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("installerAvailable").GetBoolean());
            Assert.Equal("0.1.117", json.RootElement.GetProperty("clientVersion").GetString());
            Assert.Equal(sha256, json.RootElement.GetProperty("sha256").GetString());
            Assert.Equal(payload.Length, json.RootElement.GetProperty("byteLength").GetInt64());
            Assert.Equal("Meimad-Planner-Client-Setup.msi", json.RootElement.GetProperty("fileName").GetString());
            var serverVersion = json.RootElement.GetProperty("serverVersion").GetString();
            Assert.Equal(ClientInstallerService.ServerVersion, serverVersion);
            Assert.Matches(@"^\d+\.\d+\.\d+$", serverVersion);

            using var download = await client.GetAsync("/api/v1/client-installer/download");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(payload, await download.Content.ReadAsByteArrayAsync());
            Assert.Equal(sha256, download.Headers.GetValues("X-Meimad-Checksum-SHA256").Single(), ignoreCase: true);
            Assert.Equal("0.1.117", download.Headers.GetValues("X-Meimad-Client-Version").Single());
            Assert.Equal(
                "Meimad-Planner-Client-Setup.msi",
                download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
            Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            await application.StopAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Server_without_a_bundled_installer_reports_unavailable_and_refuses_the_download()
    {
        var root = NewRoot();
        var folder = Path.Combine(root, "client-installer");
        await using var application = Build(root, folder, 5100);
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();

            using var manifest = await client.GetAsync("/api/v1/client-installer");
            Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
            using var json = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("installerAvailable").GetBoolean());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("clientVersion").ValueKind);
            Assert.Matches(@"^\d+\.\d+\.\d+$", json.RootElement.GetProperty("serverVersion").GetString());

            using var download = await client.GetAsync("/api/v1/client-installer/download");
            Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
            using var error = JsonDocument.Parse(await download.Content.ReadAsStringAsync());
            Assert.Equal(
                "client_installer_unavailable",
                error.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            await application.StopAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Installer_whose_manifest_does_not_describe_the_file_is_not_offered()
    {
        var root = NewRoot();
        var folder = Path.Combine(root, "client-installer");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "Meimad-Planner-Client-Setup.msi"), [1, 2, 3, 4]);
        await File.WriteAllTextAsync(
            Path.Combine(folder, "Meimad-Planner-Client-Setup.json"),
            """{"fileName":"Meimad-Planner-Client-Setup.msi","version":"0.1.117","sha256":"00ff","byteLength":4}""");
        await using var application = Build(root, folder, 5101);
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            using var manifest = await client.GetAsync("/api/v1/client-installer");
            using var json = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("installerAvailable").GetBoolean());
            using var download = await client.GetAsync("/api/v1/client-installer/download");
            Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        }
        finally
        {
            await application.StopAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("0.1.117", "0.1.117")]
    [InlineData("0.1.117+3ef0a39", "0.1.117")]
    [InlineData("0.1.117.0", "0.1.117")]
    [InlineData("0.1.117-beta", "0.1.117")]
    [InlineData("1.2", "1.2.0")]
    [InlineData(null, "0.0.0")]
    public void Server_and_client_versions_compare_on_major_minor_patch(string? value, string expected) =>
        Assert.Equal(expected, ClientInstallerService.NormalizeVersion(value));

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.ClientInstaller.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static WebApplication Build(string root, string installerFolder, int port) => ServerApplication.Build(
        [
            "--Server:Host=127.0.0.1",
            $"--Server:Port={port}",
            $"--Database:Path={Path.Combine(root, "test.db")}",
            $"--GCode:ReleaseRoot={Path.Combine(root, "releases")}",
            $"--ProductionPackages:PackageRoot={Path.Combine(root, "packages")}",
            $"--ClientInstaller:Folder={installerFolder}"
        ],
        webHost => webHost.UseTestServer());
}
