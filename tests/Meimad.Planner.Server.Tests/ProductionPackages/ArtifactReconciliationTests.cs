using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Meimad.Planner.Server.Application.Maintenance;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Meimad.Planner.Server.Backup;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;

namespace Meimad.Planner.Server.Tests.ProductionPackages;

public sealed class ArtifactReconciliationTests
{
    [Fact]
    public async Task Approved_default_is_seven_days_and_exposed_in_status()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(7, fixture.App.Services.GetRequiredService<ProductionPackageOptions>().OrphanGraceDays);
        var report = await Scan(fixture);
        Assert.Equal(7, report.OrphanGraceDays);
    }
    [Fact]
    public async Task Report_only_configuration_disables_cleanup()
    {
        await using var fixture = await Fixture.CreateAsync("--ProductionPackages:OrphanGraceDays=0");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.App.Services.GetRequiredService<ArtifactReconciliationService>().CollectAsync(default));
        using var response = await fixture.Client.PostAsync("/api/v1/server-maintenance/artifacts/cleanup", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("eligible", true)]
    [InlineData("recent", false)]
    [InlineData("extra-file", false)]
    [InlineData("corrupt", false)]
    [InlineData("incomplete", false)]
    public async Task Configured_cleanup_requires_old_complete_verified_exclusively_owned_files(string mode, bool removed)
    {
        await using var fixture = await Fixture.CreateAsync();
        var package = await fixture.App.Services.GetRequiredService<ProductionPackageService>().CreateAsync("operation-package", "test");
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Root(fixture), id);
        Directory.CreateDirectory(directory);
        var bytes = "{}"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), bytes);
        await new PackagePublicationJournal(1, id, DateTimeOffset.UtcNow.AddDays(-30), new string('a', 64),
            mode == "incomplete" ? "STAGING" : "READY",
            [new("manifest.json", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))]).SaveAsync(directory, default);
        if (mode == "extra-file") await File.WriteAllTextAsync(Path.Combine(directory, "keep.txt"), "operator file");
        if (mode == "corrupt") await File.AppendAllTextAsync(Path.Combine(directory, "manifest.json"), "!");
        foreach (var file in Directory.GetFiles(directory)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-30));
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(mode == "recent" ? -1 : -30));
        var collector = Collector(fixture);
        var result = await collector.CollectAsync(default);
        Assert.Equal(removed ? 1 : 0, result.Deleted);
        Assert.Equal(!removed, Directory.Exists(directory));
        Assert.True(File.Exists(Path.Combine(Root(fixture), package.ManifestRelativePath)));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_packages"));
        Assert.Equal(0, (await collector.CollectAsync(default)).Deleted);
    }

    [Fact]
    public async Task Backup_pin_and_active_build_prevent_collection()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Scan(fixture);
        var collector = Collector(fixture);
        using (await PackageStorageActivity.BuildAsync(Root(fixture), default))
            await Assert.ThrowsAsync<InvalidOperationException>(() => collector.CollectAsync(default));
        using (await RecoveryArtifactLease.AcquireAsync(default))
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = collector.CollectAsync(cancellation.Token);
            Assert.False(pending.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.Equal(0, (await collector.CollectAsync(default)).Deleted);
    }

    private static ArtifactReconciliationService Collector(Fixture fixture)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ProductionPackages:PackageRoot"] = Root(fixture), ["ProductionPackages:OrphanGraceDays"] = "7" }).Build();
        return new(new(fixture.Database), ProductionPackageOptions.FromConfiguration(config, Root(fixture)),
            fixture.App.Services.GetRequiredService<GCodeOptions>(), fixture.App.Services.GetRequiredService<EInkOptions>(), TimeProvider.System);
    }

    [Fact]
    public async Task Published_journal_and_database_references_reconcile_active_and_superseded_packages()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.App.Services.GetRequiredService<ProductionPackageService>();
        var first = await service.CreateAsync("operation-package", "test");
        var second = await service.CreateAsync("operation-package", "test");
        var path = Path.Combine(Root(fixture), second.ProductionPackageId, PackagePublicationJournal.Name);
        var journal = JsonSerializer.Deserialize<PackagePublicationJournal>(await File.ReadAllTextAsync(path), PackagePublicationJournal.Json)!;
        Assert.Equal("READY", journal.State);
        Assert.Equal(second.ProductionPackageId, journal.PackageId);
        Assert.Equal(second.Artifacts.Count, journal.Files.Count);
        Assert.Equal(second.ManifestHash, journal.Files.Single(x => x.Path == "manifest.json").Sha256);
        var before = await new SqliteProjectionRevisionReader(fixture.Database).ReadAsync(default);
        var report = await Scan(fixture);
        Assert.Contains(report.Items, x => x.Id == first.ProductionPackageId && x.State == "SUPERSEDED" && x.Integrity == "VERIFIED");
        Assert.Contains(report.Items, x => x.Id == second.ProductionPackageId && x.State == "ACTIVE" && x.Integrity == "VERIFIED");
        Assert.Equal(before, await new SqliteProjectionRevisionReader(fixture.Database).ReadAsync(default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_corrupt_active_artifacts_are_visible_without_changing_authority(bool corrupt)
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.App.Services.GetRequiredService<ProductionPackageService>();
        var package = await service.CreateAsync("operation-package", "test");
        var artifact = package.Artifacts.First(x => x.ArtifactType != "MANIFEST");
        var path = Path.Combine(Root(fixture), artifact.StoredRelativePath);
        if (corrupt) await File.AppendAllTextAsync(path, "corrupt"); else File.Delete(path);
        var report = await Scan(fixture);
        Assert.Contains(report.Items, x => x.Id == package.ProductionPackageId && x.State == "ACTIVE" && x.Integrity == (corrupt ? "CORRUPT" : "MISSING"));
        Assert.True(report.Problems > 0);
        Assert.Equal(package.ProductionPackageId, (await service.ReadCurrentAsync("operation-package"))!.ProductionPackageId);
    }

    [Theory]
    [InlineData("STAGING", true, "STAGED", "INCOMPLETE")]
    [InlineData("READY", true, "STAGED", "VERIFIED")]
    [InlineData("READY", false, "PUBLISHED_UNREFERENCED", "VERIFIED")]
    public async Task Interrupted_publication_is_reported_deterministically_and_never_activated(string state, bool staging, string expectedState, string integrity)
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Root(fixture), staging ? ".staging-" + id : id);
        Directory.CreateDirectory(directory);
        var bytes = "{}"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), bytes);
        var journal = new PackagePublicationJournal(1, id, DateTimeOffset.UtcNow.AddDays(-30), new string('a', 64), state,
            state == "READY" ? [new("manifest.json", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))] : []);
        await journal.SaveAsync(directory, default);
        var first = (await Scan(fixture)).Items.Single(x => x.Id == Path.GetFileName(directory));
        Assert.Equal(expectedState, first.State);
        Assert.Equal(integrity, first.Integrity);
        var restarted = new ArtifactReconciliationService(new(fixture.Database),
            fixture.App.Services.GetRequiredService<ProductionPackageOptions>(),
            fixture.App.Services.GetRequiredService<GCodeOptions>(), fixture.App.Services.GetRequiredService<EInkOptions>(), TimeProvider.System);
        var again = await restarted.ScanAsync(default);
        Assert.Contains(first, again.Items);
        Assert.True(Directory.Exists(directory));
        Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_packages"));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("invalid-json")]
    [InlineData("identity")]
    [InlineData("outside")]
    public async Task Ambiguous_directories_are_retained(string mode)
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(Root(fixture), id);
        Directory.CreateDirectory(path);
        if (mode == "invalid-json") await File.WriteAllTextAsync(Path.Combine(path, PackagePublicationJournal.Name), "{");
        if (mode is "identity" or "outside")
            await new PackagePublicationJournal(1, mode == "identity" ? Guid.NewGuid().ToString("N") : id,
                DateTimeOffset.UtcNow, new string('a', 64), "READY", [new("manifest.json", 2, new string('a', 64)), new("../escape.nc", 2, new string('a', 64))]).SaveAsync(path, default);
        var report = await Scan(fixture);
        Assert.Contains(report.Items, x => x.Id == id && x.Integrity != "VERIFIED");
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task Active_build_prevents_scan_without_serializing_other_builds()
    {
        await using var fixture = await Fixture.CreateAsync();
        using (await PackageStorageActivity.BuildAsync(Root(fixture), default))
        using (await PackageStorageActivity.BuildAsync(Root(fixture), default))
        {
            var report = await fixture.App.Services.GetRequiredService<ArtifactReconciliationService>().ScanAsync(default);
            Assert.Equal("BUILD_IN_PROGRESS", report.Status);
        }
        Assert.Equal("COMPLETE", (await Scan(fixture)).Status);
    }

    [Fact]
    public async Task Health_and_scan_require_setup_and_return_no_store()
    {
        await using var fixture = await Fixture.CreateAsync();
        using (fixture.Client.SignedInWithOnly(Application.Accounts.Permissions.PlanMachines))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.GetAsync("/api/v1/server-maintenance/artifacts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.PostAsync("/api/v1/server-maintenance/artifacts/scan", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.PostAsync("/api/v1/server-maintenance/artifacts/cleanup", null)).StatusCode);
        }
        using var response = await fixture.Client.PostAsync("/api/v1/server-maintenance/artifacts/scan", null);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.NotNull(await response.Content.ReadFromJsonAsync<ArtifactHealthReport>());
    }

    private static string Root(Fixture fixture) => fixture.App.Services.GetRequiredService<ProductionPackageOptions>().ResolvedPackageRoot;
    private static async Task<ArtifactHealthReport> Scan(Fixture fixture)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var report = await fixture.App.Services.GetRequiredService<ArtifactReconciliationService>().ScanAsync(default);
            if (report.Status != "BUSY") { Assert.Equal("COMPLETE", report.Status); return report; }
            await Task.Delay(20);
        }
        throw new TimeoutException("Startup scan did not finish.");
    }
}
