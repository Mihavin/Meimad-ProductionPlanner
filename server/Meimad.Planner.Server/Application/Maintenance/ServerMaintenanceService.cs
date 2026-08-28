using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Backup;

namespace Meimad.Planner.Server.Application.Maintenance;

internal sealed class ServerMaintenanceService
{
    private const int MaximumIdentifierLength = 200;
    private const int MaximumReasonLength = 500;
    private readonly IServerMaintenanceRepository repository;
    private readonly SqliteBackupService backupService;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim mutationLock = new(1, 1);

    public ServerMaintenanceService(
        IServerMaintenanceRepository repository,
        SqliteBackupService backupService,
        TimeProvider timeProvider)
    {
        this.repository = repository;
        this.backupService = backupService;
        this.timeProvider = timeProvider;
    }

    internal Task<DatabaseStorageStatus> ReadStatusAsync(CancellationToken token = default) =>
        repository.ReadStatusAsync(token);

    internal Task<CollectedDataPreview> PreviewAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        IReadOnlyList<string>? types,
        string? machineId,
        CancellationToken token = default) =>
        repository.PreviewAsync(NormalizeFilter(fromInclusive, toExclusive, types, machineId), token);

    internal async Task<MaintenanceBackupArtifact> CreateHttpBackupAsync(
        string userId,
        EditAuthority authority,
        CancellationToken token = default)
    {
        var normalizedUser = NormalizeRequired(userId, "user ID", MaximumIdentifierLength);
        await mutationLock.WaitAsync(token);
        try
        {
            await repository.ValidateEditAuthorityAsync(authority, token);
            var result = await backupService.CreateBackupAsync(token);
            var info = ToInfo(result);
            var content = new FileStream(
                result.BackupPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                await repository.RecordBackupAsync(
                    info,
                    normalizedUser,
                    authority,
                    timeProvider.GetUtcNow(),
                    token);
                return new(content, info);
            }
            catch
            {
                await content.DisposeAsync();
                throw;
            }
        }
        finally
        {
            mutationLock.Release();
        }
    }

    internal async Task<CollectedDataPurgeResult> PurgeAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        IReadOnlyList<string>? types,
        string? machineId,
        long expectedTotalRows,
        string? reason,
        string userId,
        EditAuthority authority,
        CancellationToken token = default)
    {
        if (expectedTotalRows <= 0)
        {
            throw new ServerMaintenanceValidationException(
                "invalid_expected_rows",
                "expectedTotalRows must be greater than zero and must come from a fresh preview.");
        }

        var filter = NormalizeFilter(fromInclusive, toExclusive, types, machineId);
        var normalizedReason = NormalizeRequired(reason, "reason", MaximumReasonLength);
        if (normalizedReason.Length < 3)
        {
            throw new ServerMaintenanceValidationException(
                "invalid_reason",
                "A deletion reason of at least three characters is required.");
        }
        var normalizedUser = NormalizeRequired(userId, "user ID", MaximumIdentifierLength);

        await mutationLock.WaitAsync(token);
        try
        {
            await repository.ValidateEditAuthorityAsync(authority, token);
            var preview = await repository.PreviewAsync(filter, token);
            if (preview.TotalRows != expectedTotalRows)
            {
                throw new CollectedDataPreviewChangedException(expectedTotalRows, preview.TotalRows);
            }

            var backupResult = await backupService.CreateBackupAsync(token);
            var backup = ToInfo(backupResult);
            var occurredAt = timeProvider.GetUtcNow();
            var deleted = await repository.PurgeAsync(
                filter,
                expectedTotalRows,
                normalizedReason,
                normalizedUser,
                authority,
                backup,
                occurredAt,
                token);
            var totalDeleted = deleted.Sum(item => item.RowCount);
            var status = await repository.ReadStatusAsync(token);
            return new(
                filter,
                deleted,
                totalDeleted,
                normalizedReason,
                normalizedUser,
                occurredAt,
                backup,
                status);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    private static CollectedDataFilter NormalizeFilter(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        IReadOnlyList<string>? types,
        string? machineId)
    {
        var from = fromInclusive.ToUniversalTime();
        var to = toExclusive.ToUniversalTime();
        if (from >= to)
        {
            throw new ServerMaintenanceValidationException(
                "invalid_time_range",
                "fromInclusive must be earlier than toExclusive.");
        }

        var selected = (types ?? [])
            .Select(value => value?.Trim().ToLowerInvariant())
            .Where(value => !string.IsNullOrEmpty(value))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (selected.Length == 0)
        {
            throw new ServerMaintenanceValidationException(
                "collected_data_type_required",
                "Select at least one collected-data type.");
        }
        var unsupported = selected.FirstOrDefault(value => !CollectedDataTypes.All.Contains(value, StringComparer.Ordinal));
        if (unsupported is not null)
        {
            throw new ServerMaintenanceValidationException(
                "unsupported_collected_data_type",
                $"Collected-data type '{unsupported}' is not deletable.");
        }

        string? normalizedMachine = null;
        if (!string.IsNullOrWhiteSpace(machineId))
        {
            normalizedMachine = NormalizeRequired(machineId, "Machine ID", MaximumIdentifierLength);
        }
        return new(from, to, selected, normalizedMachine);
    }

    private static string NormalizeRequired(string? value, string field, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > maximumLength)
        {
            throw new ServerMaintenanceValidationException(
                "invalid_maintenance_request",
                $"The {field} must contain between 1 and {maximumLength} characters.");
        }
        return normalized;
    }

    private static MaintenanceBackupInfo ToInfo(BackupResult result) => new(
        Path.GetFileName(result.BackupPath),
        result.CreatedAt,
        result.ByteLength,
        result.Sha256,
        result.IntegrityVerified,
        result.RestoreVerified);
}
