using System.Net;
using System.Security.Cryptography;
using System.Text;
using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;

namespace Meimad.Planner.Server.Tests.ProductionPackages;

public sealed class PackagePublicationTests
{
    [Fact]
    public async Task Upgrade_from_v99_preserves_current_package_and_initializes_publication_version()
    {
        await using var fixture = await Fixture.CreateAsync();
        var baseline = await Service(fixture).CreateAsync("operation-package", "test");
        await fixture.ExecuteAsync("""
            DROP TRIGGER package_publication_insert;
            DROP TRIGGER package_publication_update;
            DROP TRIGGER package_publication_delete;
            DROP TABLE production_package_publication_versions;
            DROP TABLE production_package_requests;
            DELETE FROM schema_migrations WHERE version=100;
            PRAGMA user_version=99;
            """);
        await fixture.App.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        Assert.Equal(101L, await fixture.ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT version FROM production_package_publication_versions;"));
        Assert.Equal(baseline.ManifestHash, (await Service(fixture).ReadCurrentAsync("operation-package"))!.ManifestHash);
        var replacement = await Service(fixture).CreateAsync("operation-package", "test", requestId: "after-upgrade");
        Assert.Equal(baseline.ProductionPackageId, replacement.SupersedesPackageId);
        Assert.Equal(2L, await fixture.ScalarAsync("SELECT version FROM production_package_publication_versions;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Competing_builds_from_one_predecessor_publish_once(bool identicalRequest)
    {
        await using var fixture = await Fixture.CreateAsync();
        var baseline = await Service(fixture).CreateAsync("operation-package", "test");
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        var repository = new InterceptingRepository(new SqliteProductionPackageRepository(fixture.Database))
        {
            Before = async () =>
            {
                if (Interlocked.Increment(ref arrived) == 2) barrier.SetResult();
                await barrier.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
        };
        var service = Service(fixture, repository);
        async Task<(ProductionPackageRecord? Package, Exception? Error)> Build(string key)
        {
            try { return (await service.CreateAsync("operation-package", "test", requestId: key), null); }
            catch (Exception error) { return (null, error); }
        }
        var results = await Task.WhenAll(Build("request-1"), Build(identicalRequest ? "request-1" : "request-2"));
        if (identicalRequest)
        {
            Assert.All(results, result => Assert.Null(result.Error));
            Assert.Equal(results[0].Package!.ProductionPackageId, results[1].Package!.ProductionPackageId);
        }
        else
        {
            var failed = Assert.Single(results, result => result.Error is not null);
            Assert.Equal("production_package_publication_conflict", Assert.IsType<ProductionPackageBuildException>(failed.Error).Code);
        }
        var current = await service.ReadCurrentAsync("operation-package");
        Assert.NotNull(current);
        Assert.Equal(baseline.ProductionPackageId, current.SupersedesPackageId);
        Assert.Equal(2L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_packages;"));
        Assert.Equal(2L, await fixture.ScalarAsync("SELECT COUNT(*) FROM offset_loader_releases;"));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_package_requests;"));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_package_invalidations;"));
        Assert.Equal(2, Directory.GetDirectories(PackageRoot(fixture)).Length);
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT offset_loader_release_id FROM production_run_current_offset_loaders;";
        Assert.Equal(current.OffsetLoaderReleaseId, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Lost_response_preserves_committed_files_and_replays_original_result_after_assignment_changes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repository = new InterceptingRepository(new SqliteProductionPackageRepository(fixture.Database))
        {
            After = () => throw new IOException("Connection lost after commit")
        };
        var service = Service(fixture, repository);
        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync("operation-package", "test", requestId: "lost-response"));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_packages;"));
        var original = await Service(fixture).ReadCurrentAsync("operation-package");
        Assert.NotNull(original);
        Assert.True(File.Exists(Path.Combine(PackageRoot(fixture), original.ManifestRelativePath)));
        await fixture.ExecuteAsync("UPDATE machine_assignments SET released_at='2026-10-08T12:00:00Z',version=version+1;");
        // A fresh service/repository has no in-memory knowledge of the earlier request.
        var retry = await Service(fixture).CreateAsync("operation-package", "test", requestId: "lost-response");
        Assert.Equal(original.ProductionPackageId, retry.ProductionPackageId);
        Assert.Equal(original.CreatedAt, retry.CreatedAt);
        Assert.Equal(original.ManifestHash, retry.ManifestHash);
        Assert.Equal(1, repository.PublishCalls);
        var mismatch = await Assert.ThrowsAsync<ProductionPackageBuildException>(() => service.CreateAsync(
            "operation-package", "test", "MANUAL_DUMMY", requestId: "lost-response"));
        Assert.Equal("production_package_request_conflict", mismatch.Code);
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_package_requests;"));
    }

    [Theory]
    [InlineData("UPDATE machine_assignments SET version=version+1;")]
    [InlineData("UPDATE machines SET number='CHANGED' WHERE id='machine-package';")]
    [InlineData("UPDATE machines SET tool_diameter_offset_kind='DIAMETER' WHERE id='machine-package';")]
    [InlineData("UPDATE cnc_verification_settings SET verify_program_number=9099 WHERE machine_id='machine-package';")]
    [InlineData("UPDATE cases SET name='Renamed part' WHERE id='case-package';")]
    [InlineData("INSERT INTO tool_preparations(id,batch_operation_id,machine_id,tool_table_release_id,version_number,saved_at,saved_by,content_hash) VALUES('new-measurements','operation-package','machine-package','tools-1',1,'2026-10-08T10:00:00Z','tool-room','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');")]
    public async Task Input_changes_before_activation_keep_the_previous_package_and_source(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        var baseline = await Service(fixture).CreateAsync("operation-package", "test");
        var source = Directory.GetFiles(fixture.ReleaseRoot, "main.nc", SearchOption.AllDirectories).Single();
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var repository = new InterceptingRepository(new SqliteProductionPackageRepository(fixture.Database))
        { Before = () => fixture.ExecuteAsync(change) };
        var error = await Assert.ThrowsAsync<ProductionPackageBuildException>(() => Service(fixture, repository)
            .CreateAsync("operation-package", "test", requestId: "changed-input"));
        Assert.Equal("production_package_context_changed", error.Code);
        await AssertOldPointerAsync(fixture, baseline);
        Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.Single(Directory.GetDirectories(PackageRoot(fixture)));
    }

    [Fact]
    public async Task Pointer_delete_and_restore_cannot_pass_the_observed_version_check()
    {
        await using var fixture = await Fixture.CreateAsync();
        var baseline = await Service(fixture).CreateAsync("operation-package", "test");
        var repository = new InterceptingRepository(new SqliteProductionPackageRepository(fixture.Database))
        {
            Before = () => fixture.ExecuteAsync("""
                CREATE TEMP TABLE previous_pointer AS SELECT * FROM production_package_context_current;
                DELETE FROM production_package_context_current;
                INSERT INTO production_package_context_current SELECT * FROM previous_pointer;
                """)
        };
        var error = await Assert.ThrowsAsync<ProductionPackageBuildException>(() => Service(fixture, repository)
            .CreateAsync("operation-package", "test", requestId: "aba"));
        Assert.Equal("production_package_publication_conflict", error.Code);
        await AssertOldPointerAsync(fixture, baseline);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("disk-full")]
    [InlineData("activation")]
    public async Task Failed_build_does_not_change_pointers_or_leave_a_receipt(string failure)
    {
        await using var fixture = await Fixture.CreateAsync();
        var baseline = await Service(fixture).CreateAsync("operation-package", "test");
        var repository = new InterceptingRepository(new SqliteProductionPackageRepository(fixture.Database));
        if (failure == "activation") repository.Before = () => throw new IOException("Injected activation failure");
        var exception = await Record.ExceptionAsync(() => Service(fixture, repository, new FaultingFiles(failure))
            .CreateAsync("operation-package", "test", requestId: "failed-build"));
        Assert.NotNull(exception);
        if (failure == "corrupt")
            Assert.Equal("production_package_staged_corrupt", Assert.IsType<ProductionPackageBuildException>(exception).Code);
        else Assert.IsType<IOException>(exception);
        await AssertOldPointerAsync(fixture, baseline);
        Assert.Single(Directory.GetDirectories(PackageRoot(fixture)));
    }

    [Fact]
    public async Task Http_keys_are_actor_scoped_and_changed_commands_conflict()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.DefaultRequestHeaders.Add("Idempotency-Key", "http-request");
        using var first = await fixture.Client.PostAsync(Fixture.Path, Fixture.Body());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var json = await first.Content.ReadAsStringAsync();
        using var replay = await fixture.Client.PostAsync(Fixture.Path, Fixture.Body());
        Assert.Equal(json, await replay.Content.ReadAsStringAsync());
        using var changed = await fixture.Client.PostAsync(Fixture.Path + "?toolOffsetMode=MANUAL_DUMMY", Fixture.Body());
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Contains("production_package_request_conflict", await changed.Content.ReadAsStringAsync());
        var service = Service(fixture);
        var otherActor = await service.CreateAsync("operation-package", "another-user", requestId: "http-request");
        Assert.Equal(2L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_package_requests;"));
        Assert.Equal("another-user", otherActor.CreatedBy);
    }

    private static async Task AssertOldPointerAsync(Fixture fixture, ProductionPackageRecord baseline)
    {
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_packages;"));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_package_requests;"));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM offset_loader_releases;"));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_package_invalidations;"));
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT production_package_id FROM production_package_context_current;";
        Assert.Equal(baseline.ProductionPackageId, await command.ExecuteScalarAsync());
    }

    private static string PackageRoot(Fixture fixture) => fixture.App.Services.GetRequiredService<ProductionPackageOptions>().ResolvedPackageRoot;
    private static ProductionPackageService Service(Fixture fixture, IProductionPackageRepository? repository = null, ProductionPackageFiles? files = null)
        => new(repository ?? new SqliteProductionPackageRepository(fixture.Database),
            fixture.App.Services.GetRequiredService<ProductionPackageOptions>(),
            fixture.App.Services.GetRequiredService<GCodeArtifactStore>(), TimeProvider.System, files);

    private sealed class FaultingFiles(string failure) : ProductionPackageFiles
    {
        internal override Task WriteAsync(string path, byte[] bytes, CancellationToken token)
            => failure == "disk-full" ? throw new IOException("Injected disk full", unchecked((int)0x80070070)) : base.WriteAsync(path, bytes, token);
        internal override void Move(string source, string destination)
        {
            base.Move(source, destination);
            if (failure == "corrupt") File.AppendAllText(Directory.GetFiles(destination, "*.nc", SearchOption.AllDirectories).First(), "CORRUPT", Encoding.ASCII);
        }
    }

    private sealed class InterceptingRepository(IProductionPackageRepository inner) : IProductionPackageRepository
    {
        internal Func<Task>? Before { get; set; }
        internal Action? After { get; set; }
        internal int PublishCalls;
        public async Task<ProductionPackageRecord> PublishAsync(ProductionPackageRecord package, OffsetLoaderPublication? loader,
            ProductionPackageBuildContext observed, ProductionPackageRequest? request, CancellationToken token)
        {
            Interlocked.Increment(ref PublishCalls);
            if (Before is not null) await Before();
            var result = await inner.PublishAsync(package, loader, observed, request, token);
            After?.Invoke();
            return result;
        }
        public Task<ProductionPackageBuildContext?> ReadBuildContextAsync(string id, CancellationToken token) => inner.ReadBuildContextAsync(id, token);
        public Task<ProductionPackageBuildContext?> ReadBuildContextAsync(string id, ProductionPackageSelection? selection, CancellationToken token) => inner.ReadBuildContextAsync(id, selection, token);
        public Task<int> AllocatePackageNumberAsync(CancellationToken token) => inner.AllocatePackageNumberAsync(token);
        public Task<ProductionPackageRecord?> ReadCurrentAsync(string id, CancellationToken token) => inner.ReadCurrentAsync(id, token);
        public Task<ProductionPackageRecord?> ReadCurrentAsync(string id, ProductionPackageSelection? selection, CancellationToken token) => inner.ReadCurrentAsync(id, selection, token);
        public Task<ProductionPackageRecord?> ReadHistoricalAsync(string id, CancellationToken token) => inner.ReadHistoricalAsync(id, token);
        public Task<ProductionPackageRecord?> ReadRequestAsync(ProductionPackageRequest request, CancellationToken token) => inner.ReadRequestAsync(request, token);
        public Task<bool> IsReferencedAsync(string id, CancellationToken token) => inner.IsReferencedAsync(id, token);
    }
}
