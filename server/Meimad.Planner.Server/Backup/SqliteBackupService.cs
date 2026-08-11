using System.Globalization;
using System.Security.Cryptography;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Backup;

internal sealed class SqliteBackupService
{
    private const string BackupFilePrefix = "meimad-planner-backup-";
    private readonly SqliteDatabase database;
    private readonly BackupOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SqliteBackupService> logger;
    private readonly SemaphoreSlim operationLock = new(1, 1);

    public SqliteBackupService(
        SqliteDatabase database,
        BackupOptions options,
        TimeProvider timeProvider,
        ILogger<SqliteBackupService> logger)
    {
        this.database = database;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    internal async Task<BackupResult> CreateBackupAsync(
        CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            return await CreateBackupCoreAsync(cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    internal async Task VerifyBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var fullBackupPath = Path.GetFullPath(backupPath);
        EnsureNotActiveDatabase(fullBackupPath);
        EnsureManagedBackupPath(fullBackupPath);

        await operationLock.WaitAsync(cancellationToken);
        var workFolder = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner",
            "BackupWork",
            Guid.NewGuid().ToString("N"));
        var testRestorePath = Path.Combine(workFolder, "restore-test.db");
        try
        {
            Directory.CreateDirectory(workFolder);
            await VerifyIntegrityAsync(fullBackupPath, cancellationToken);
            await RestoreToTestLocationAsync(fullBackupPath, testRestorePath, cancellationToken);
            await VerifyIntegrityAsync(testRestorePath, cancellationToken);
        }
        finally
        {
            TryDeleteWorkingFolder(workFolder);
            operationLock.Release();
        }
    }

    private async Task<BackupResult> CreateBackupCoreAsync(CancellationToken cancellationToken)
    {
        var createdAt = timeProvider.GetUtcNow().ToUniversalTime();
        var operationId = Guid.NewGuid().ToString("N");
        var workFolder = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner",
            "BackupWork",
            operationId);
        var localSnapshotPath = Path.Combine(workFolder, "snapshot.db");
        var testRestorePath = Path.Combine(workFolder, "restore-test.db");
        var fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"{BackupFilePrefix}{createdAt:yyyyMMdd'T'HHmmss'.'fff'Z'}-{operationId[..8]}.db");
        var finalPath = Path.Combine(options.BackupFolder, fileName);
        var pendingPath = finalPath + ".pending";
        var published = false;
        var verified = false;

        EnsureNotActiveDatabase(localSnapshotPath);
        EnsureNotActiveDatabase(testRestorePath);
        EnsureNotActiveDatabase(finalPath);
        EnsureNotActiveDatabase(pendingPath);

        try
        {
            Directory.CreateDirectory(workFolder);
            Directory.CreateDirectory(options.BackupFolder);

            await CreateOnlineSnapshotAsync(localSnapshotPath, cancellationToken);
            await VerifyIntegrityAsync(localSnapshotPath, cancellationToken);
            await CopyDurablyAsync(localSnapshotPath, pendingPath, cancellationToken);
            File.Move(pendingPath, finalPath, overwrite: false);
            published = true;

            await VerifyIntegrityAsync(finalPath, cancellationToken);
            await RestoreToTestLocationAsync(finalPath, testRestorePath, cancellationToken);
            await VerifyIntegrityAsync(testRestorePath, cancellationToken);
            verified = true;

            var checksum = await CalculateSha256Async(finalPath, cancellationToken);
            var byteLength = new FileInfo(finalPath).Length;
            var deletedCount = ApplyRetention(finalPath);

            logger.LogInformation(
                "Created and restore-verified SQLite backup {BackupFileName}; retained the newest {RetentionCount} backups.",
                fileName,
                options.RetentionCount);

            return new BackupResult(
                finalPath,
                createdAt,
                byteLength,
                checksum,
                deletedCount,
                IntegrityVerified: true,
                RestoreVerified: true);
        }
        catch
        {
            if (published && !verified)
            {
                TryDeleteFile(finalPath);
            }

            throw;
        }
        finally
        {
            TryDeleteFile(pendingPath);
            TryDeleteWorkingFolder(workFolder);
        }
    }

    private async Task CreateOnlineSnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        await using var source = await database.OpenConnectionAsync(cancellationToken);
        await using var destination = CreateConnection(snapshotPath, SqliteOpenMode.ReadWriteCreate);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private async Task RestoreToTestLocationAsync(
        string backupPath,
        string testRestorePath,
        CancellationToken cancellationToken)
    {
        EnsureNotActiveDatabase(testRestorePath);
        await using var source = CreateConnection(backupPath, SqliteOpenMode.ReadOnly);
        await using var destination = CreateConnection(testRestorePath, SqliteOpenMode.ReadWriteCreate);
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private static async Task VerifyIntegrityAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(databasePath, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(cancellationToken);

        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            await using var reader = await integrity.ExecuteReaderAsync(cancellationToken);
            var rowCount = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                rowCount++;
                if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "SQLite integrity verification failed for a generated backup.");
                }
            }

            if (rowCount != 1)
            {
                throw new InvalidDataException(
                    "SQLite integrity verification returned an unexpected result.");
            }
        }

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        await using var foreignKeyReader = await foreignKeys.ExecuteReaderAsync(cancellationToken);
        if (await foreignKeyReader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "SQLite foreign-key verification failed for a generated backup.");
        }
    }

    private int ApplyRetention(string currentBackupPath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var otherBackups = Directory
            .EnumerateFiles(options.BackupFolder, $"{BackupFilePrefix}*.db", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(currentBackupPath),
                comparison))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        var keepOtherCount = options.RetentionCount - 1;
        var deletedCount = 0;
        foreach (var path in otherBackups.Skip(keepOtherCount))
        {
            File.Delete(path);
            deletedCount++;
        }

        return deletedCount;
    }

    private static async Task CopyDurablyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
    }

    private static async Task<string> CalculateSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static SqliteConnection CreateConnection(string path, SqliteOpenMode mode) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            ForeignKeys = true,
            Pooling = false
        }.ToString());

    private void EnsureNotActiveDatabase(string path)
    {
        if (PathsEqual(path, database.DatabasePath))
        {
            throw new InvalidOperationException(
                "Backup restore verification cannot target the active SQLite database.");
        }
    }

    private void EnsureManagedBackupPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        if (directory is null
            || !PathsEqual(directory, options.BackupFolder)
            || !fileName.StartsWith(BackupFilePrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(".db", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Only managed backup files in the configured backup folder can be verified.");
        }
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void TryDeleteWorkingFolder(string workFolder)
    {
        try
        {
            if (Directory.Exists(workFolder))
            {
                Directory.Delete(workFolder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Could not remove a completed local backup verification work folder.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the original backup failure; startup cleanup can remove stale pending files.
        }
    }
}
