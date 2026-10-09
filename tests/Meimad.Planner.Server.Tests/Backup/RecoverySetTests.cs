using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Backup;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Tests.Persistence;
using Meimad.Planner.Server.Tests.ProductionPackages;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Backup;

public sealed class RecoverySetTests
{
    private const string Password = "Recovery-test-only-passphrase-12345";

    [Theory]
    [InlineData("database")]
    [InlineData("schema")]
    public async Task Authenticated_but_invalid_contents_are_rejected_and_cleaned(string damage)
    {
        await using var fixture = await Fixture.CreateAsync();
        var receipt = await fixture.Service.CreateAsync("test", default);
        var entries = new Dictionary<string, byte[]>();
        await RecoveryEncryption.ReadAsync(fixture.Archive(receipt), Password, async stream =>
        {
            while (RecoveryArchive.ReadHeader(stream) is { } header)
            {
                using var bytes = new MemoryStream();
                await RecoveryArchive.CopyAsync(stream, bytes, header.Length, default);
                entries.Add(header.Name, bytes.ToArray());
            }
        }, default);
        try
        {
            var manifest = JsonSerializer.Deserialize<RecoveryManifest>(entries["manifest.json"], RecoverySetService.Json)!;
            if (damage == "database")
            {
                entries["database/planner.db"] = Encoding.ASCII.GetBytes("damaged sqlite database");
                manifest = manifest with { Files = manifest.Files.Select(f => f.Path == "database/planner.db"
                    ? f with { Length = entries[f.Path].Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(entries[f.Path])) } : f).ToArray() };
            }
            else manifest = manifest with { SchemaVersion = int.MaxValue };
            entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, RecoverySetService.Json);
            var path = Path.Combine(fixture.Destination, "invalid.mprb");
            await RecoveryEncryption.WriteAsync(path, Password, async stream =>
            {
                foreach (var entry in entries)
                {
                    using var input = new MemoryStream(entry.Value);
                    await RecoveryArchive.WriteEntryAsync(stream, entry.Key, input, input.Length, default);
                }
                RecoveryArchive.End(stream);
            }, default);
            receipt = receipt with { FileName = "invalid.mprb", ByteLength = new FileInfo(path).Length,
                Sha256 = await RecoveryArchive.HashAsync(path, default), SchemaVersion = manifest.SchemaVersion };
            await RecoverySetService.WriteJsonDurablyAsync(path + ".complete.json", receipt, default);
            var destination = Path.Combine(fixture.Root, "invalid-contents");
            Assert.NotNull(await Record.ExceptionAsync(() => RecoverySetService.RestoreAsync(path, Password, destination, default)));
            Assert.False(Directory.Exists(destination));
        }
        finally { foreach (var bytes in entries.Values) CryptographicOperations.ZeroMemory(bytes); }
    }

    [Fact]
    public async Task Published_package_and_all_its_artifacts_survive_a_complete_restore()
    {
        await using var packageFixture = await ProductionPackageContextTests.Fixture.CreateAsync();
        var services = packageFixture.App.Services;
        await services.GetRequiredService<ProductionPackageService>().CreateAsync("operation-package", "test");
        var root = Path.GetDirectoryName(packageFixture.Database.DatabasePath)!;
        var passwordFile = Path.Combine(root, "password.dpapi");
        await File.WriteAllBytesAsync(passwordFile, RecoveryKeys.Protect(Encoding.UTF8.GetBytes(Password)));
        var recovery = new RecoverySetService(packageFixture.Database, services.GetRequiredService<SqliteBackupService>(),
            new RecoveryOptions { Folder = Path.Combine(root, "recovery"), PasswordFile = passwordFile, IndependentDestinationConfirmed = true },
            services.GetRequiredService<GCodeOptions>(), services.GetRequiredService<ProductionPackageOptions>(),
            services.GetRequiredService<EInkOptions>(), services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<IKeyManager>(), services.GetRequiredService<IDataProtectionProvider>(),
            TimeProvider.System, NullLogger<RecoverySetService>.Instance);
        var result = await recovery.CreateAsync("test", default);
        var restored = await RecoverySetService.RestoreAsync(Path.Combine(root, "recovery", result.FileName), Password,
            Path.Combine(root, "restored-package"), default);
        Assert.Contains(restored.Manifest.Files, f => f.Path.StartsWith("production/") && f.Path.EndsWith(".json"));
        Assert.True(restored.Manifest.Files.Count(f => f.Path.StartsWith("production/")) >= 3);
        foreach (var file in restored.Manifest.Files.Where(f => f.Path.StartsWith("production/")))
            Assert.Equal(file.Sha256, await RecoveryArchive.HashAsync(Path.Combine(restored.Directory, file.Path), default));
    }

    [Fact]
    public async Task Concurrent_requests_publish_distinct_complete_sets()
    {
        await using var fixture = await Fixture.CreateAsync();
        var results = await Task.WhenAll(fixture.Service.CreateAsync("first", default), fixture.Service.CreateAsync("second", default));
        Assert.NotEqual(results[0].SetId, results[1].SetId);
        Assert.Equal(2, Directory.GetFiles(fixture.Destination, "*.complete.json").Length);
        Assert.Equal(results[1], (await fixture.Service.StatusAsync(default)).LastComplete);
    }

    [Fact]
    public async Task Export_preserves_revoked_keys_and_refuses_their_ciphertext()
    {
        await using var fixture = await Fixture.CreateAsync();
        var manager = fixture.Provider.GetRequiredService<IKeyManager>();
        var key = Assert.Single(manager.GetAllKeys());
        manager.RevokeKey(key.KeyId, "test revocation");
        var bytes = RecoveryKeys.Export(manager);
        try
        {
            using var restored = RecoveryKeys.OpenProvider(bytes);
            Assert.True(Assert.Single(restored.GetRequiredService<IKeyManager>().GetAllKeys()).IsRevoked);
            await Assert.ThrowsAsync<CryptographicException>(() => RecoveryDatabase.VerifySecretsAsync(
                fixture.Database.DatabasePath, restored.GetRequiredService<IDataProtectionProvider>(), default));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public async Task Snapshot_referenced_releases_are_required_and_hash_checked(string state)
    {
        await using var fixture = await Fixture.CreateAsync();
        var releases = Path.Combine(fixture.Root, "gcode-releases");
        await ProductionPackageApiTests.SeedAsync(fixture.Provider, releases, false);
        var extraBytes = Encoding.ASCII.GetBytes("O0002\nM99\n");
        var extraHash = Convert.ToHexStringLower(SHA256.HashData(extraBytes));
        await File.WriteAllBytesAsync(Path.Combine(releases, "sub.nc"), extraBytes);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "eink-packages"));
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "eink-packages", "old.txt"), extraBytes);
        await using (var sql = await fixture.Database.Database.OpenConnectionAsync())
        {
            await using var cmd = sql.CreateCommand();
            cmd.CommandText = """
                INSERT INTO gcode_release_subprograms(id,gcode_release_id,position,original_file_name,program_number,stored_relative_path,file_size,file_hash)
                VALUES('sub','gcode-1',0,'sub.nc',2,'sub.nc',$size,$hash);
                INSERT INTO eink_package_revisions(id,batch_operation_id,revision,published_at)
                VALUES('old-eink','operation-package','old','2026-10-08T00:00:00Z');
                INSERT INTO eink_package_files(id,package_revision_id,logical_path,storage_relative_path,media_type,byte_length,sha256,modified_at)
                VALUES('old-eink-file','old-eink','old.txt','old.txt','text/plain',$size,$hash,'2026-10-08T00:00:00Z');
                """;
            cmd.Parameters.AddWithValue("$size", extraBytes.Length);
            cmd.Parameters.AddWithValue("$hash", extraHash);
            await cmd.ExecuteNonQueryAsync();
        }
        var nc = Directory.GetFiles(releases, "main.nc", SearchOption.AllDirectories).Single();
        if (state == "missing") File.Delete(nc);
        if (state == "corrupt")
        {
            var bytes = await File.ReadAllBytesAsync(nc);
            bytes[10] ^= 1;
            await File.WriteAllBytesAsync(nc, bytes);
        }
        if (state != "valid")
        {
            var failure = await Record.ExceptionAsync(() => fixture.Service.CreateAsync("operator", default));
            Assert.True(failure is IOException or InvalidDataException, failure?.ToString());
            Assert.Empty(Directory.GetFiles(fixture.Destination, "*.complete.json"));
            return;
        }
        var result = await fixture.Service.CreateAsync("operator", default);
        var restored = await RecoverySetService.RestoreAsync(fixture.Archive(result), Password,
            Path.Combine(fixture.Root, "artifact-restore"), default);
        Assert.Equal(3, restored.Manifest.Files.Count(f => f.Path.StartsWith("gcode/")));
        Assert.Contains(restored.Manifest.Files, f => f.Path == "eink/old.txt" && f.Sha256 == extraHash);
        Assert.Single(restored.Manifest.EngineeringDependencies);
        Assert.False(Directory.Exists(restored.Manifest.EngineeringDependencies[0].WorkingFolderPath));
    }

    [Fact]
    public async Task Complete_set_restores_database_and_real_protected_secrets_with_portable_keys()
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Service.CreateAsync("operator", default);
        var restored = await RecoverySetService.RestoreAsync(fixture.Archive(result), Password,
            Path.Combine(fixture.Root, "replacement"), default);
        Assert.Equal(result.SetId, restored.Manifest.SetId);
        Assert.Contains(restored.Manifest.Files, x => x.Path == "keys/export.xml");
        Assert.False(File.Exists(Path.Combine(restored.Directory, "keys", "export.xml")));
        var config = JsonSerializer.Deserialize<Dictionary<string, string>>(
            await File.ReadAllTextAsync(Path.Combine(restored.Directory, "appsettings.Recovery.json")))!;
        Assert.Equal("true", config["Recovery:ActivationPending"]);
        Assert.StartsWith(restored.Directory, config["Database:Path"]);
        Assert.StartsWith(restored.Directory, config["Backup:Folder"]);
        Assert.Empty(config["Recovery:PasswordFile"]);
        var status = await fixture.Service.StatusAsync(default);
        Assert.Equal(result, status.LastComplete);
        Assert.False(status.RecoveryPointOverdue);
        fixture.Clock.Now += TimeSpan.FromHours(4).Add(TimeSpan.FromSeconds(1));
        Assert.True((await fixture.Service.StatusAsync(default)).RecoveryPointOverdue);
        // The source database is still live and the replacement key repository can decrypt its secrets.
        await using var sql = await fixture.Database.Database.OpenConnectionAsync();
        await using var cmd = sql.CreateCommand();
        cmd.CommandText = "SELECT protected_password FROM kitaron_connection_settings";
        Assert.Equal("erp-secret", fixture.Provider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Meimad.Planner.Kitaron.SqlPassword.v1").Unprotect((string)(await cmd.ExecuteScalarAsync())!));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("marker")]
    [InlineData("missing-marker")]
    public async Task Invalid_set_never_leaves_a_staged_restore(string failure)
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Service.CreateAsync("operator", default);
        var path = fixture.Archive(result);
        var password = Password;
        if (failure == "password") password += "wrong";
        if (failure is "corrupt" or "truncated")
        {
            var bytes = await File.ReadAllBytesAsync(path);
            if (failure == "corrupt") bytes[80] ^= 1;
            else bytes = bytes[..100];
            await File.WriteAllBytesAsync(path, bytes);
            // Even a recomputed outer checksum cannot bypass authentication.
            result = result with { ByteLength = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        }
        if (failure == "marker") result = result with { SetId = "wrong-set" };
        await RecoverySetService.WriteJsonDurablyAsync(path + ".complete.json", result, default);
        if (failure == "missing-marker") File.Delete(path + ".complete.json");
        var destination = Path.Combine(fixture.Root, "invalid-restore");
        Assert.NotNull(await Record.ExceptionAsync(() => RecoverySetService.RestoreAsync(path, password, destination, default)));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Existing_destination_is_never_overwritten()
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Service.CreateAsync("operator", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecoverySetService.RestoreAsync(
            fixture.Archive(result), Password, fixture.Root, default));
        Assert.True(File.Exists(fixture.Database.DatabasePath));
    }

    [Fact]
    public async Task Wrong_key_ring_blocks_publication_and_preserves_last_good_set()
    {
        await using var fixture = await Fixture.CreateAsync();
        var good = await fixture.Service.CreateAsync("operator", default);
        await fixture.SetSecretAsync("not-a-protected-value");
        await Assert.ThrowsAsync<CryptographicException>(() => fixture.Service.CreateAsync("operator", default));
        Assert.Single(Directory.GetFiles(fixture.Destination, "*.complete.json"));
        Assert.Empty(Directory.GetFiles(fixture.Destination, "*.pending"));
        Assert.Equal(good, (await fixture.Service.StatusAsync(default)).LastComplete);
    }

    [Fact]
    public async Task Cancellation_and_unwritable_destination_publish_nothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.CreateAsync("operator", stop.Token));
        await File.WriteAllTextAsync(fixture.Destination, "a file prevents directory creation");
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Service.CreateAsync("operator", default));
        Assert.Equal("a file prevents directory creation", await File.ReadAllTextAsync(fixture.Destination));
        Assert.Null((await fixture.Service.StatusAsync(default)).LastComplete);
    }

    [Fact]
    public async Task Artifact_cleanup_waits_for_snapshot_lease_and_waiters_can_cancel()
    {
        using var held = await RecoveryArtifactLease.AcquireAsync(default);
        using var stop = new CancellationTokenSource();
        var waiting = RecoveryArtifactLease.AcquireAsync(stop.Token);
        Assert.False(waiting.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../../escape")]
    [InlineData("a:stream")]
    [InlineData("a/CON.txt")]
    [InlineData("a/LPT1")]
    [InlineData("a./b")]
    [InlineData("/absolute")]
    public void Restore_paths_reject_traversal_and_windows_aliases(string relative) =>
        Assert.Throws<InvalidDataException>(() => RecoveryPaths.Resolve(Path.GetTempPath(), relative));

    [Fact]
    public async Task Authenticated_encryption_refuses_partial_writes_and_wrong_password_before_callback()
    {
        await using var fixture = await Fixture.CreateAsync();
        var path = Path.Combine(fixture.Root, "encryption.mprb");
        await Assert.ThrowsAsync<IOException>(() => RecoveryEncryption.WriteAsync(path, Password, async stream =>
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("partial"));
            throw new IOException("simulated disk-full failure");
        }, default));
        var called = false;
        Assert.NotNull(await Record.ExceptionAsync(() => RecoveryEncryption.ReadAsync(path, Password,
            stream => { called = true; return Task.CompletedTask; }, default)));
        Assert.False(called);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal required TemporaryDatabase Database { get; init; }
        internal required ServiceProvider Provider { get; init; }
        internal required RecoverySetService Service { get; init; }
        internal required TestClock Clock { get; init; }
        internal string Root => Path.GetDirectoryName(Database.DatabasePath)!;
        internal string Destination => Path.Combine(Root, "recovery");
        internal string Archive(RecoverySetResult result) => Path.Combine(Destination, result.FileName);

        internal static async Task<Fixture> CreateAsync()
        {
            var db = await TemporaryDatabase.CreateAsync();
            var root = Path.GetDirectoryName(db.DatabasePath)!;
            var services = new ServiceCollection();
            services.AddSingleton(db.Database);
            services.AddDataProtection().SetApplicationName(RecoveryKeys.ApplicationName)
                .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "original-keys")));
            var provider = services.BuildServiceProvider();
            var config = new ConfigurationBuilder().Build();
            var clock = new TestClock();
            var passwordFile = Path.Combine(root, "password.dpapi");
            await File.WriteAllBytesAsync(passwordFile, RecoveryKeys.Protect(Encoding.UTF8.GetBytes(Password)));
            var fixture = new Fixture
            {
                Database = db, Provider = provider, Clock = clock,
                Service = new RecoverySetService(db.Database,
                    new(db.Database, new BackupOptions(Path.Combine(root, "backups"), 14), clock, NullLogger<SqliteBackupService>.Instance),
                    new RecoveryOptions { Folder = Path.Combine(root, "recovery"), PasswordFile = passwordFile, IndependentDestinationConfirmed = true },
                    GCodeOptions.FromConfiguration(config, root), ProductionPackageOptions.FromConfiguration(config, root),
                    EInkOptions.FromConfiguration(config, root), config, provider.GetRequiredService<IKeyManager>(),
                    provider.GetRequiredService<IDataProtectionProvider>(), clock, NullLogger<RecoverySetService>.Instance)
            };
            await fixture.SetSecretAsync(provider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("Meimad.Planner.Kitaron.SqlPassword.v1").Protect("erp-secret"));
            return fixture;
        }

        internal async Task SetSecretAsync(string value)
        {
            await using var connection = await Database.Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE kitaron_connection_settings SET protected_password=$value";
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync() { await Provider.DisposeAsync(); await Database.DisposeAsync(); }
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
