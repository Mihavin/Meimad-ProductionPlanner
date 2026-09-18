using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class ClientUpdateTests
{
    [Fact]
    public void Same_version_pair_needs_nothing()
    {
        var decision = ClientUpdatePolicy.Evaluate(
            new Version(0, 1, 117, 0), Manifest("0.1.117", "0.1.117"));

        Assert.Equal(ClientUpdateAction.None, decision.Action);
        Assert.Equal("0.1.117", decision.RunningVersion);
        Assert.Equal("0.1.117", decision.ServerClientVersion);
    }

    [Fact]
    public void Newer_client_package_on_the_Server_is_installed()
    {
        var decision = ClientUpdatePolicy.Evaluate(
            new Version(0, 1, 116, 0), Manifest("0.1.117+3ef0a39", "0.1.117"));

        Assert.Equal(ClientUpdateAction.Install, decision.Action);
        Assert.Equal("0.1.117", decision.ServerClientVersion);
        Assert.Contains("New version 0.1.117 is available", decision.Message, StringComparison.Ordinal);
        Assert.Contains("will be installed", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_newer_than_the_Server_package_is_never_downgraded()
    {
        var decision = ClientUpdatePolicy.Evaluate(
            new Version(0, 1, 118), Manifest("0.1.117", "0.1.117"));

        Assert.Equal(ClientUpdateAction.ClientNewerThanServer, decision.Action);
        Assert.Contains("Upgrade the Server", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_mismatch_without_an_installer_asks_the_administrator()
    {
        var decision = ClientUpdatePolicy.Evaluate(
            new Version(0, 1, 116), new ClientInstallerManifest("0.1.117", null, false, null, null, null));

        Assert.Equal(ClientUpdateAction.InstallerMissing, decision.Action);
        Assert.Null(decision.ServerClientVersion);
        Assert.Contains("no client installer", decision.Message, StringComparison.Ordinal);

        var same = ClientUpdatePolicy.Evaluate(
            new Version(0, 1, 117), new ClientInstallerManifest("0.1.117", null, false, null, null, null));
        Assert.Equal(ClientUpdateAction.None, same.Action);
    }

    [Fact]
    public void Installer_script_waits_installs_passively_with_a_log_and_restarts_the_client()
    {
        var script = ClientInstallerLauncher.BuildScript(
            @"C:\Users\me\AppData\Local\MeimadPlanner\updates\Meimad-Planner-Client-Setup.msi",
            @"C:\Users\me\AppData\Local\MeimadPlanner\updates\install-client-update.log",
            @"C:\Program Files\Meimad Production Planner Client\Meimad.Planner.Client.Windows.exe");

        var lines = script.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("@echo off", lines[0]);
        Assert.Contains("ping -n 4 127.0.0.1 >nul", lines);
        Assert.Contains(
            "msiexec.exe /i \"C:\\Users\\me\\AppData\\Local\\MeimadPlanner\\updates\\Meimad-Planner-Client-Setup.msi\" /passive /norestart /l*v \"C:\\Users\\me\\AppData\\Local\\MeimadPlanner\\updates\\install-client-update.log\"",
            lines);
        Assert.Equal(
            "if not errorlevel 1 start \"\" \"C:\\Program Files\\Meimad Production Planner Client\\Meimad.Planner.Client.Windows.exe\"",
            lines[^1]);
        var withoutRelaunch = ClientInstallerLauncher.BuildScript("a.msi", "a.log", null);
        Assert.DoesNotContain("start \"\"", withoutRelaunch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_client_reads_the_installer_manifest()
    {
        var handler = new StubHandler(_ => Json(
            """{"serverVersion":"0.1.117","clientVersion":"0.1.117","installerAvailable":true,"fileName":"Meimad-Planner-Client-Setup.msi","byteLength":12,"sha256":"abc","builtAt":"2026-09-18T12:00:00Z"}"""));
        using var api = new PlannerApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://planner-server:5080/") });

        var manifest = await api.GetClientInstallerManifestAsync();

        Assert.Equal("/api/v1/client-installer", handler.Requests.Single().PathAndQuery);
        Assert.Equal("0.1.117", manifest.ServerVersion);
        Assert.Equal("0.1.117", manifest.ClientVersion);
        Assert.True(manifest.InstallerAvailable);
        Assert.Equal(12, manifest.ByteLength);
        Assert.Equal("abc", manifest.Sha256);
    }

    [Fact]
    public async Task Api_client_downloads_the_installer_and_verifies_its_checksum()
    {
        var payload = Encoding.ASCII.GetBytes("installer bytes");
        var sha256 = Convert.ToHexString(SHA256.HashData(payload));
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            response.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
            { FileName = "\"Meimad-Planner-Client-Setup.msi\"" };
            response.Headers.TryAddWithoutValidation("X-Meimad-Checksum-SHA256", sha256);
            return response;
        });
        using var api = new PlannerApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://planner-server:5080/") });
        var folder = Path.Combine(Path.GetTempPath(), "MeimadPlanner.ClientUpdate.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var reported = new List<long>();
            var download = await api.DownloadClientInstallerAsync(folder, sha256, new SynchronousProgress(reported));

            Assert.Equal("/api/v1/client-installer/download", handler.Requests.Single().PathAndQuery);
            Assert.Equal(Path.Combine(folder, "Meimad-Planner-Client-Setup.msi"), download.LocalPath);
            Assert.Equal(payload, await File.ReadAllBytesAsync(download.LocalPath));
            Assert.Equal(payload.Length, download.ByteLength);
            Assert.Equal(sha256, download.Sha256, ignoreCase: true);
            Assert.Equal(payload.Length, reported.Last());

            await Assert.ThrowsAsync<PlannerProtocolException>(() =>
                api.DownloadClientInstallerAsync(folder, "0000", null));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private static ClientInstallerManifest Manifest(string serverVersion, string clientVersion) =>
        new(serverVersion, clientVersion, true, "Meimad-Planner-Client-Setup.msi", 1024, "abc");

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class SynchronousProgress(List<long> reported) : IProgress<long>
    {
        public void Report(long value) => reported.Add(value);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
