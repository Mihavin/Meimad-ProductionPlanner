using System.Net;
using System.Text.Json;
using Meimad.Planner.Server.Backup;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests;

public sealed class ServerApplicationTests
{
    [Fact]
    public async Task Application_starts_and_stops_cleanly()
    {
        await using var application = CreateTestApplication();

        await application.StartAsync();

        Assert.True(application.Lifetime.ApplicationStarted.IsCancellationRequested);

        await application.StopAsync();

        Assert.True(application.Lifetime.ApplicationStopped.IsCancellationRequested);
    }

    [Fact]
    public async Task Health_endpoint_returns_healthy_response()
    {
        await using var application = CreateTestApplication();
        await application.StartAsync();

        try
        {
            using var client = application.GetTestClient();
            using var response = await client.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

            await using var responseStream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(responseStream);
            var root = document.RootElement;

            Assert.Equal("healthy", root.GetProperty("status").GetString());
            Assert.Equal("Meimad Planner Server", root.GetProperty("service").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));
            Assert.Equal(JsonValueKind.String, root.GetProperty("serverTimeUtc").ValueKind);
        }
        finally
        {
            await application.StopAsync();
        }
    }

    [Fact]
    public void Configuration_overrides_host_and_port()
    {
        using var application = ServerApplication.Build(
            ["--Server:Host=0.0.0.0", "--Server:Port=6200"],
            webHost => webHost.UseTestServer());

        var options = application.Services.GetRequiredService<ServerOptions>();

        Assert.Equal("0.0.0.0", options.Host);
        Assert.Equal(6200, options.Port);
        Assert.Equal("http://0.0.0.0:6200", options.GetListenUrl());
    }

    [Fact]
    public void Configuration_overrides_edit_mode_timeout()
    {
        using var application = ServerApplication.Build(
            ["--EditMode:TransferTimeoutSeconds=12"],
            webHost => webHost.UseTestServer());

        var options = application.Services.GetRequiredService<EditModeOptions>();
        Assert.Equal(12, options.TransferTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(12), options.TransferTimeout);
    }

    [Fact]
    public void Configuration_rejects_invalid_edit_mode_timeout()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ServerApplication.Build(
            ["--EditMode:TransferTimeoutSeconds=0"],
            webHost => webHost.UseTestServer()));

        Assert.Contains("between 1 and 3600", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_overrides_tv_dashboard_refresh_and_urgency()
    {
        using var application = ServerApplication.Build(
            ["--TvDashboard:RefreshAfterSeconds=20", "--TvDashboard:UrgentWithinHours=72"],
            webHost => webHost.UseTestServer());

        var options = application.Services.GetRequiredService<TvDashboardOptions>();
        Assert.Equal(20, options.RefreshAfterSeconds);
        Assert.Equal(72, options.UrgentWithinHours);
    }

    [Fact]
    public void Configuration_rejects_invalid_tv_dashboard_refresh()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ServerApplication.Build(
            ["--TvDashboard:RefreshAfterSeconds=1"],
            webHost => webHost.UseTestServer()));

        Assert.Contains("between 5 and 300", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_overrides_backup_folder_and_retention()
    {
        var folder = Path.Combine(Path.GetTempPath(), "MeimadPlanner.ConfiguredBackups");
        using var application = ServerApplication.Build(
            [$"--Backup:Folder={folder}", "--Backup:RetentionCount=9"],
            webHost => webHost.UseTestServer());

        var options = application.Services.GetRequiredService<BackupOptions>();
        Assert.Equal(Path.GetFullPath(folder), options.BackupFolder);
        Assert.Equal(9, options.RetentionCount);
        Assert.NotNull(application.Services.GetRequiredService<SqliteBackupService>());
    }

    [Fact]
    public void Installed_server_resolves_relative_mutable_paths_below_program_data()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string programFiles = @"C:\Program Files";
        const string programFilesX86 = @"C:\Program Files (x86)";
        const string programData = @"C:\ProgramData";
        var installedRoot = ServerStoragePathResolver.InstalledStorageRoot(
            @"C:\Program Files\Meimad Production Planner Server",
            programFiles,
            programFilesX86,
            programData);

        Assert.Equal(@"C:\ProgramData\MeimadPlanner\Server", installedRoot);
        Assert.Null(ServerStoragePathResolver.InstalledStorageRoot(
            @"C:\VisualCodeWork\Meimad-ProductionPlanner\server",
            programFiles,
            programFilesX86,
            programData));
    }

    [Fact]
    public void Configuration_rejects_invalid_backup_retention()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ServerApplication.Build(
            ["--Backup:RetentionCount=0"],
            webHost => webHost.UseTestServer()));

        Assert.Contains("between 1 and 3650", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_applies_database_migrations()
    {
        await using var application = CreateTestApplication();
        await application.StartAsync();

        try
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";

            Assert.Equal(54L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            await application.StopAsync();
        }
    }

    private static WebApplication CreateTestApplication()
    {
        return ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099"],
            webHost => webHost.UseTestServer());
    }
}
