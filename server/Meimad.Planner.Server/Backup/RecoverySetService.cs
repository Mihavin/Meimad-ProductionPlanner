using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace Meimad.Planner.Server.Backup;

internal sealed class RecoverySetService(SqliteDatabase database, SqliteBackupService snapshots, RecoveryOptions options,
    GCodeOptions gcode, ProductionPackageOptions packages, EInkOptions eink, IConfiguration configuration,
    IKeyManager keyManager, IDataProtectionProvider protection, TimeProvider clock)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    private string StatusPath => database.DatabasePath + ".recovery-status.json";
    private static readonly string[] ConfigurationSections = ["Server", "Database", "FileAccess", "Backup", "Recovery",
        "DataProtection", "TvDashboard", "Timeline", "SetupEstimation", "LegacyImport", "EInk", "GCode", "ProductionPackages",
        "ClientInstaller", "ClientPortal", "Logging", "AllowedHosts"];

    internal async Task<RecoveryStatus> StatusAsync(CancellationToken token)
    {
        RecoveryStatus? saved = null;
        if (File.Exists(StatusPath))
            saved = JsonSerializer.Deserialize<RecoveryStatus>(await File.ReadAllTextAsync(StatusPath, token), Json);
        return new(options.Configured, options.Folder, "External Windows Task Scheduler", saved?.LastComplete,
            saved?.LastComplete is { } complete ? (clock.GetUtcNow() - complete.SnapshotAt).TotalHours : null,
            saved?.LastAttemptResult);
    }

    internal async Task<RecoverySetResult> CreateAsync(string actor, CancellationToken token)
    {
        if (!options.Configured) throw new InvalidOperationException("Configure Recovery:Folder, PasswordFile and IndependentDestinationConfirmed on the Server first.");
        if (!Path.IsPathFullyQualified(options.Folder!) || !Path.IsPathFullyQualified(options.PasswordFile!))
            throw new InvalidOperationException("Recovery destination and password file must use absolute paths.");
        await gate.WaitAsync(token);
        var id = Guid.NewGuid().ToString("N");
        var workspaceParent = Path.Combine(Path.GetTempPath(), "MeimadPlanner", "RecoveryWork");
        var workspace = Path.Combine(workspaceParent, id);
        string? pending = null;
        byte[]? exportedKeys = null;
        try
        {
            await SaveStatusAsync(null, "In progress", token);
            RecoveryPaths.RejectLinks(options.Folder!);
            RecoveryPaths.CreatePrivateDirectory(workspace);
            Directory.CreateDirectory(options.Folder!);
            var password = RecoveryKeys.ReadPassword(options.PasswordFile!);
            RecoveryEncryption.ValidatePassword(password);
            var snapshot = Path.Combine(workspace, "planner.db");
            var snapshotAt = clock.GetUtcNow();
            var name = $"meimad-recovery-{snapshotAt:yyyyMMddTHHmmssZ}-{id}.mprb";
            var final = Path.Combine(options.Folder!, name);
            pending = final + ".pending";
            RecoveryManifest? manifest = null;
            using (await RecoveryArtifactLease.AcquireAsync(token))
            {
                await snapshots.CreateOnlineSnapshotAsync(snapshot, token);
                await SqliteBackupService.VerifyIntegrityAsync(snapshot, token);
                var references = await RecoveryDatabase.ReadAsync(snapshot, token);
                var probe = protection.CreateProtector(RecoveryKeys.ProbePurpose).Protect(id);
                exportedKeys = RecoveryKeys.Export(keyManager);
                using (var keys = RecoveryKeys.OpenProvider(exportedKeys))
                    await RecoveryDatabase.VerifySecretsAsync(snapshot, keys.GetRequiredService<IDataProtectionProvider>(), token);
                var settings = ConfigurationSections.SelectMany(section => configuration.GetSection(section).AsEnumerable())
                    .Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                var roots = new Dictionary<string, string> { ["gcode"] = gcode.ResolvedReleaseRoot,
                    ["production"] = packages.ResolvedPackageRoot, ["eink"] = eink.ResolvedPackageRoot };
                await RecoveryEncryption.WriteAsync(pending, password, async stream =>
                {
                    var files = new List<RecoveryFile>();
                    async Task AddFile(string logical, string source, long? size = null, string? hash = null)
                    {
                        RecoveryPaths.RejectLinks(source);
                        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                        if (size is not null && input.Length != size) throw new InvalidDataException("A referenced recovery file has the wrong length.");
                        var file = await RecoveryArchive.WriteEntryAsync(stream, logical, input, input.Length, token);
                        if (hash is not null && !string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("A referenced recovery file failed checksum verification.");
                        files.Add(file);
                    }
                    async Task AddBytes(string logical, byte[] bytes)
                    {
                        using var input = new MemoryStream(bytes, false);
                        files.Add(await RecoveryArchive.WriteEntryAsync(stream, logical, input, bytes.Length, token));
                    }
                    await AddFile("database/planner.db", snapshot);
                    foreach (var reference in references.Files)
                    {
                        var split = reference.Path.IndexOf('/');
                        var source = RecoveryPaths.Resolve(roots[reference.Path[..split]], reference.Path[(split + 1)..]);
                        await AddFile(reference.Path, source, reference.Length, reference.Hash);
                    }
                    await AddBytes("configuration/effective.json", JsonSerializer.SerializeToUtf8Bytes(settings, Json));
                    await AddBytes("keys/export.xml", exportedKeys);
                    manifest = new(1, id, snapshotAt, references.Schema,
                        typeof(RecoverySetService).Assembly.GetName().Version?.ToString() ?? "unknown", actor, files,
                        references.Folders, probe);
                    using var manifestBytes = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, Json), false);
                    await RecoveryArchive.WriteEntryAsync(stream, "manifest.json", manifestBytes, manifestBytes.Length, token);
                    RecoveryArchive.End(stream);
                }, token);
            }
            // Reopen every restored artifact and verify keys/DB before the set is published.
            await RestoreCoreAsync(pending, password, Path.Combine(workspace, "restore-test"), token);
            var result = new RecoverySetResult(name, id, snapshotAt, clock.GetUtcNow(), manifest!.SchemaVersion,
                new FileInfo(pending).Length, await RecoveryArchive.HashAsync(pending, token), manifest.Files.Count,
                manifest.EngineeringDependencies.Count);
            File.Move(pending, final);
            pending = null;
            await WriteJsonDurablyAsync(final + ".complete.json", result, token);
            await SaveStatusAsync(result, "Complete and restore-verified", CancellationToken.None);
            return result;
        }
        catch
        {
            try { await SaveStatusAsync(null, "Failed or interrupted; no new complete set was recorded. Check the destination and Server configuration.", CancellationToken.None); }
            catch (Exception) { /* Preserve the original error; the completion marker remains authoritative. */ }
            throw;
        }
        finally
        {
            if (exportedKeys is not null) CryptographicOperations.ZeroMemory(exportedKeys);
            try
            {
                if (pending is not null && File.Exists(pending)) File.Delete(pending);
                RecoveryPaths.DeleteOwnedDirectory(workspaceParent, workspace);
            }
            finally { gate.Release(); }
        }
    }

    internal static async Task<RecoveryRestoreResult> RestoreAsync(string archive, string password, string destination, CancellationToken token)
    {
        RecoveryPaths.RejectLinks(archive);
        var marker = JsonSerializer.Deserialize<RecoverySetResult>(await File.ReadAllTextAsync(archive + ".complete.json", token), Json)
            ?? throw new InvalidDataException("Recovery completion marker is missing.");
        if (marker.FileName != Path.GetFileName(archive) || marker.ByteLength != new FileInfo(archive).Length
            || marker.Sha256 != await RecoveryArchive.HashAsync(archive, token))
            throw new InvalidDataException("Recovery set failed its published checksum.");
        var result = await RestoreCoreAsync(archive, password, destination, token);
        if (result.Manifest.SetId != marker.SetId || result.Manifest.SchemaVersion != marker.SchemaVersion)
            throw new InvalidDataException("Recovery marker does not match its manifest.");
        return result;
    }

    private static async Task<RecoveryRestoreResult> RestoreCoreAsync(string archive, string password, string destination, CancellationToken token)
    {
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new InvalidOperationException("Restore requires a new, separate destination; existing locations cannot be overwritten.");
        RecoveryPaths.RejectLinks(destination);
        byte[]? keys = null;
        RecoveryManifest? manifest = null;
        var actual = new Dictionary<string, RecoveryFile>(StringComparer.OrdinalIgnoreCase);
        var created = false;
        try
        {
            await RecoveryEncryption.ReadAsync(archive, password, async stream =>
            {
                RecoveryPaths.CreatePrivateDirectory(destination);
                created = true;
                long total = 0;
                while (RecoveryArchive.ReadHeader(stream) is { } entry)
                {
                    if (actual.Count > 500_000 || (total += entry.Length) > RecoveryArchive.MaximumBytes)
                        throw new InvalidDataException("Recovery set exceeds supported extraction bounds.");
                    var path = RecoveryPaths.Resolve(destination, entry.Name);
                    if (actual.ContainsKey(entry.Name) || (entry.Name == "manifest.json" && manifest is not null))
                        throw new InvalidDataException("Duplicate recovery entry.");
                    if (entry.Name is "keys/export.xml" or "manifest.json")
                    {
                        if (entry.Length > 64 * 1024 * 1024) throw new InvalidDataException("Recovery metadata exceeds supported size.");
                        using var bytes = new MemoryStream();
                        var hash = await RecoveryArchive.CopyAsync(stream, bytes, entry.Length, token);
                        if (entry.Name == "keys/export.xml")
                        { keys = bytes.ToArray(); actual.Add(entry.Name, new(entry.Name, entry.Length, hash)); }
                        else manifest = JsonSerializer.Deserialize<RecoveryManifest>(bytes.ToArray(), Json);
                    }
                    else
                    {
                        if (!(entry.Name == "database/planner.db" || entry.Name == "configuration/effective.json"
                            || entry.Name.StartsWith("gcode/", StringComparison.Ordinal) || entry.Name.StartsWith("production/", StringComparison.Ordinal)
                            || entry.Name.StartsWith("eink/", StringComparison.Ordinal))) throw new InvalidDataException("Unsupported recovery entry.");
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                        var hash = await RecoveryArchive.CopyAsync(stream, output, entry.Length, token);
                        await output.FlushAsync(token);
                        output.Flush(true);
                        actual.Add(entry.Name, new(entry.Name, entry.Length, hash));
                    }
                }
            }, token);
            if (manifest is null || keys is null || manifest.FormatVersion != 1 || manifest.SchemaVersion > DatabaseMigrator.LatestVersion
                || manifest.Files.Count != actual.Count || manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != actual.Count || manifest.Files.Any(f => !actual.TryGetValue(f.Path, out var found) || found != f))
                throw new InvalidDataException("Recovery manifest, schema version or inventory is invalid.");
            var db = RecoveryPaths.Resolve(destination, "database/planner.db");
            await SqliteBackupService.VerifyIntegrityAsync(db, token);
            var references = await RecoveryDatabase.ReadAsync(db, token);
            if (references.Schema != manifest.SchemaVersion || !references.Folders.SequenceEqual(manifest.EngineeringDependencies))
                throw new InvalidDataException("Recovery database does not match its manifest.");
            foreach (var reference in references.Files)
                if (!actual.TryGetValue(reference.Path, out var file) || !string.Equals(file.Sha256, reference.Hash, StringComparison.OrdinalIgnoreCase)
                    || (reference.Length is not null && reference.Length != file.Length))
                    throw new InvalidDataException("Recovery set is missing a required immutable artifact.");
            var keyFolder = Path.Combine(destination, "data-protection-keys");
            RecoveryKeys.Install(keys, keyFolder);
            var services = new ServiceCollection();
            services.AddDataProtection().SetApplicationName(RecoveryKeys.ApplicationName).DisableAutomaticKeyGeneration();
            services.Configure<KeyManagementOptions>(o => o.XmlRepository = new RecoveryKeys.DpapiRecoveryKeyRepository(keyFolder));
            using (var provider = services.BuildServiceProvider())
            {
                var recovered = provider.GetRequiredService<IDataProtectionProvider>();
                if (recovered.CreateProtector(RecoveryKeys.ProbePurpose).Unprotect(manifest.KeyProbe) != manifest.SetId)
                    throw new CryptographicException("Recovery key verification failed.");
                await RecoveryDatabase.VerifySecretsAsync(db, recovered, token);
            }
            var settings = JsonSerializer.Deserialize<Dictionary<string, string?>>(await File.ReadAllTextAsync(
                RecoveryPaths.Resolve(destination, "configuration/effective.json"), token), Json) ?? throw new InvalidDataException("Recovery configuration is missing.");
            settings["Database:Path"] = db;
            settings["GCode:ReleaseRoot"] = Path.Combine(destination, "gcode");
            settings["ProductionPackages:PackageRoot"] = Path.Combine(destination, "production");
            settings["EInk:PackageRoot"] = Path.Combine(destination, "eink");
            settings["DataProtection:RecoveryKeyFolder"] = keyFolder;
            settings["Recovery:ActivationPending"] = "true";
            settings["Recovery:PasswordFile"] = ""; // A source-machine DPAPI password file is never reused on a replacement PC.
            await WriteJsonDurablyAsync(Path.Combine(destination, "appsettings.Recovery.json"), settings, token);
            await WriteJsonDurablyAsync(Path.Combine(destination, "recovery-manifest.json"), manifest, token);
            return new(destination, manifest, "Staged and verified only. Keep the service stopped; review configuration, service permissions and external engineering folders before explicit activation.");
        }
        catch
        {
            if (created) RecoveryPaths.DeleteOwnedDirectory(Path.GetDirectoryName(destination)!, destination);
            throw;
        }
        finally { if (keys is not null) CryptographicOperations.ZeroMemory(keys); }
    }

    private async Task SaveStatusAsync(RecoverySetResult? result, string message, CancellationToken token)
    {
        var current = await StatusAsync(token);
        await WriteJsonDurablyAsync(StatusPath, current with { LastComplete = result ?? current.LastComplete, LastAttemptResult = message }, token);
    }

    internal static async Task WriteJsonDurablyAsync<T>(string path, T value, CancellationToken token)
    {
        var pending = path + ".pending";
        try
        {
            await using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            { await JsonSerializer.SerializeAsync(stream, value, Json, token); await stream.FlushAsync(token); stream.Flush(true); }
            File.Move(pending, path, true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
