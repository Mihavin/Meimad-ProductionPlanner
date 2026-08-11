using System.Globalization;
using Meimad.Planner.Server.Backup;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Backup;

public sealed class SqliteBackupServiceTests
{
    private static readonly DateTimeOffset StartTime =
        new(2026, 8, 11, 12, 34, 56, 789, TimeSpan.Zero);

    [Fact]
    public async Task Backup_is_timestamped_consistent_integrity_checked_and_restore_verified()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SetApplicationSettingAsync(fixture.Database, "backup-marker", "before-backup");
        var backupFolder = GetBackupFolder(fixture);
        var service = CreateService(fixture, backupFolder, retentionCount: 14);

        var result = await service.CreateBackupAsync();

        Assert.True(File.Exists(result.BackupPath));
        Assert.Matches(
            @"^meimad-planner-backup-20260811T123456\.789Z-[0-9a-f]{8}\.db$",
            Path.GetFileName(result.BackupPath));
        Assert.Equal(StartTime, result.CreatedAt);
        Assert.True(result.ByteLength > 0);
        Assert.Matches("^[0-9a-f]{64}$", result.Sha256);
        Assert.True(result.IntegrityVerified);
        Assert.True(result.RestoreVerified);
        Assert.Equal(0, result.RetentionDeletedCount);
        Assert.Equal("before-backup", await ReadSettingFromFileAsync(
            result.BackupPath,
            "backup-marker"));
        Assert.Equal("before-backup", await ReadApplicationSettingAsync(
            fixture.Database,
            "backup-marker"));
        Assert.Empty(Directory.EnumerateFiles(backupFolder, "*.pending"));
    }

    [Fact]
    public async Task Retention_keeps_current_and_newest_configured_backups_only()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var backupFolder = GetBackupFolder(fixture);
        Directory.CreateDirectory(backupFolder);
        var unrelated = Path.Combine(backupFolder, "operator-copy.db");
        await File.WriteAllTextAsync(unrelated, "unrelated");
        var clock = new ManualTimeProvider(StartTime);
        var service = CreateService(fixture, backupFolder, retentionCount: 2, clock);
        var results = new List<BackupResult>();

        for (var index = 1; index <= 4; index++)
        {
            await SetApplicationSettingAsync(
                fixture.Database,
                "retention-marker",
                index.ToString(CultureInfo.InvariantCulture));
            results.Add(await service.CreateBackupAsync());
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var retained = Directory
            .EnumerateFiles(backupFolder, "meimad-planner-backup-*.db")
            .Select(Path.GetFullPath)
            .ToHashSet(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        Assert.Equal(2, retained.Count);
        Assert.Contains(Path.GetFullPath(results[2].BackupPath), retained);
        Assert.Contains(Path.GetFullPath(results[3].BackupPath), retained);
        Assert.False(File.Exists(results[0].BackupPath));
        Assert.False(File.Exists(results[1].BackupPath));
        Assert.True(File.Exists(unrelated));
        Assert.Equal("4", await ReadSettingFromFileAsync(
            results[3].BackupPath,
            "retention-marker"));
    }

    [Fact]
    public async Task Online_backup_remains_valid_while_server_writes_continue()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SetApplicationSettingAsync(fixture.Database, "counter", "0");
        var service = CreateService(fixture, GetBackupFolder(fixture), retentionCount: 3);
        using var stop = new CancellationTokenSource();
        var writerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = RunWriterAsync(fixture.Database, writerStarted, stop.Token);
        await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        BackupResult result;
        try
        {
            result = await service.CreateBackupAsync();
        }
        finally
        {
            stop.Cancel();
            await writer.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var capturedValue = await ReadSettingFromFileAsync(result.BackupPath, "counter");
        Assert.True(int.Parse(capturedValue, CultureInfo.InvariantCulture) > 0);
        await service.VerifyBackupAsync(result.BackupPath);
    }

    [Fact]
    public async Task Restore_verification_rejects_active_database_without_modifying_it()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SetApplicationSettingAsync(fixture.Database, "active-marker", "must-survive");
        var service = CreateService(fixture, GetBackupFolder(fixture), retentionCount: 3);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.VerifyBackupAsync(fixture.DatabasePath));

        Assert.Contains("cannot target the active", exception.Message, StringComparison.Ordinal);
        Assert.Equal("must-survive", await ReadApplicationSettingAsync(
            fixture.Database,
            "active-marker"));
    }

    [Fact]
    public async Task Verification_rejects_corrupt_managed_backup()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var service = CreateService(fixture, GetBackupFolder(fixture), retentionCount: 3);
        var result = await service.CreateBackupAsync();
        await File.WriteAllTextAsync(result.BackupPath, "not a sqlite database");

        var exception = await Record.ExceptionAsync(() =>
            service.VerifyBackupAsync(result.BackupPath));
        Assert.True(exception is InvalidDataException or SqliteException);
        Assert.True(File.Exists(fixture.DatabasePath));
    }

    private static SqliteBackupService CreateService(
        TemporaryDatabase fixture,
        string backupFolder,
        int retentionCount,
        TimeProvider? timeProvider = null) => new(
        fixture.Database,
        new BackupOptions(backupFolder, retentionCount),
        timeProvider ?? new ManualTimeProvider(StartTime),
        NullLogger<SqliteBackupService>.Instance);

    private static string GetBackupFolder(TemporaryDatabase fixture) => Path.Combine(
        Path.GetDirectoryName(fixture.DatabasePath)!,
        "backups");

    private static async Task RunWriterAsync(
        SqliteDatabase database,
        TaskCompletionSource writerStarted,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE application_settings
                SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT),
                    version = version + 1,
                    updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                WHERE key = 'counter';
                """;
            await command.ExecuteNonQueryAsync();
            writerStarted.TrySetResult();
            await Task.Yield();
        }
    }

    private static async Task SetApplicationSettingAsync(
        SqliteDatabase database,
        string key,
        string value)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO application_settings (key, value)
            VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET
                value = excluded.value,
                version = application_settings.version + 1,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadApplicationSettingAsync(
        SqliteDatabase database,
        string key)
    {
        await using var connection = await database.OpenConnectionAsync();
        return await ReadSettingAsync(connection, key);
    }

    private static async Task<string> ReadSettingFromFileAsync(string path, string key)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        return await ReadSettingAsync(connection, key);
    }

    private static async Task<string> ReadSettingAsync(SqliteConnection connection, string key)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM application_settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;

        internal ManualTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => utcNow;

        internal void Advance(TimeSpan duration)
        {
            utcNow = utcNow.Add(duration);
        }
    }
}
