using System.Security.Cryptography;
using System.Text.Json;
using Meimad.Planner.Server.Application.Timeline;
using Meimad.Planner.Server.Configuration;

namespace Meimad.Planner.Server.Application.TvDashboard;

internal sealed class TvDashboardService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITvDashboardRepository repository;
    private readonly TimelineProjectionService timelineService;
    private readonly TvDashboardOptions options;
    private readonly TimeProvider timeProvider;
    private readonly OperationThumbnailStore thumbnailStore;

    public TvDashboardService(
        ITvDashboardRepository repository,
        TimelineProjectionService timelineService,
        TvDashboardOptions options,
        TimeProvider timeProvider,
        OperationThumbnailStore thumbnailStore)
    {
        this.repository = repository;
        this.timelineService = timelineService;
        this.options = options;
        this.timeProvider = timeProvider;
        this.thumbnailStore = thumbnailStore;
    }

    /// <summary>The projection's schema; the page requires it (schema 3 adds the part pictures, owner request 2026-10-12).</summary>
    internal const int SchemaVersion = 3;

    /// <summary>The TV page build; app.js, index.html and the tests carry the same value.</summary>
    internal const string DashboardBuild = "0.1.39";

    /// <summary>The route of the small TV picture; the last segment is the opaque thumbnail version.</summary>
    internal const string ThumbnailRoute = "/api/v1/tv-dashboard/thumbnails/";

    internal async Task<TvDashboardResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var horizonStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var sourceTask = repository.ReadAsync(options.ShowLastPartImageWhenIdle, cancellationToken);
        var timelineTask = timelineService.CalculateAsync(
            horizonStart,
            horizonStart.AddDays(options.CalculationHorizonDays),
            cancellationToken);
        await Task.WhenAll(sourceTask, timelineTask);
        var source = await sourceTask;
        var timeline = await timelineTask;
        var dueCutoff = DateOnly.FromDateTime(now.UtcDateTime.AddHours(options.UrgentWithinHours));
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var urgentDueByBatch = source.BatchDueDates
            .Where(value => value.WorkFinishDate <= dueCutoff)
            .GroupBy(value => value.BatchId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(value => value.WorkFinishDate).First(),
                StringComparer.Ordinal);
        var projectedFinishByOperation = timeline.Machines
            .SelectMany(machine => machine.Intervals)
            .Where(interval => interval.OperationId is not null
                && interval.Type is "setup" or "production" or "reserved")
            .GroupBy(interval => interval.OperationId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (DateTimeOffset?)group.Max(interval => interval.EndsAt),
                StringComparer.Ordinal);
        var blockedOperations = timeline.Conflicts
            .Where(conflict => string.Equals(conflict.Severity, "blocking", StringComparison.OrdinalIgnoreCase))
            .SelectMany(conflict => conflict.OperationIds)
            .ToHashSet(StringComparer.Ordinal);
        var machines = source.Machines.Select(machine => ProjectMachine(
            machine,
            now,
            source.Downtimes,
            urgentDueByBatch,
            projectedFinishByOperation,
            blockedOperations)).ToArray();
        var machineNumberByBatch = source.Machines
            .SelectMany(machine => machine.Backlog.Select(operation => (operation.BatchId, machine.Number)))
            .GroupBy(value => value.BatchId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Number, StringComparer.Ordinal);
        var urgent = urgentDueByBatch.Values
            .OrderBy(value => value.WorkFinishDate)
            .ThenBy(value => value.PartNumber, StringComparer.OrdinalIgnoreCase)
            .Select(value => new TvUrgentBatch(
                value.BatchId,
                value.BatchNumber,
                value.PartNumber,
                value.WorkFinishDate.ToString("yyyy-MM-dd"),
                value.WorkFinishDate < today,
                machineNumberByBatch.GetValueOrDefault(value.BatchId)))
            .ToArray();
        var criticalConflictCount = 0;
        var projection = new TvDashboardProjection(
            SchemaVersion,
            DashboardBuild,
            now,
            "current",
            options.RefreshAfterSeconds,
            new TvDashboardSummary(
                machines.Length,
                criticalConflictCount,
                urgent.Length,
                machines.Count(machine => machine.Downtime?.IsCurrent == true)),
            urgent,
            machines);
        return new TvDashboardResult(projection, EntityTag(projection));
    }

    private TvMachine ProjectMachine(
        TvSourceMachine machine,
        DateTimeOffset now,
        IReadOnlyList<TvSourceDowntime> allDowntimes,
        IReadOnlyDictionary<string, TvSourceBatchDueDate> urgentDueByBatch,
        IReadOnlyDictionary<string, DateTimeOffset?> projectedFinishByOperation,
        IReadOnlySet<string> blockedOperations)
    {
        var unfinished = machine.Backlog
            .Where(operation => operation.Status is not "completed" and not "complete" and not "cancelled")
            .OrderBy(operation => operation.BacklogPosition)
            .ToArray();
        var currentSource = unfinished.FirstOrDefault(operation => operation.Status is "in_progress" or "started" or "suspended")
            ?? unfinished.FirstOrDefault()
            ?? machine.Backlog.Where(operation => operation.Status is "completed" or "complete")
                .OrderByDescending(operation => operation.BacklogPosition)
                .FirstOrDefault();
        var nextSource = unfinished.Skip(1).FirstOrDefault();
        var thirdSource = unfinished.Skip(2).FirstOrDefault();
        var downtimeSource = allDowntimes
            .Where(value => value.MachineId == machine.MachineId && value.EndsAt > now)
            .OrderBy(value => value.StartsAt)
            .FirstOrDefault();
        var downtime = downtimeSource is null ? null : new TvDowntime(
            downtimeSource.DowntimeId,
            downtimeSource.StartsAt,
            downtimeSource.EndsAt,
            downtimeSource.Reason,
            downtimeSource.StartsAt <= now && downtimeSource.EndsAt > now);
        var status = Status(null, currentSource);
        var machineStatus = NormalizeMachineStatus(machine.MachineStatus);
        var current = Job(currentSource, now, urgentDueByBatch, projectedFinishByOperation);
        var alarm = machineStatus is "ALARM" or "EMERGENCY_STOP";
        var warning = alarm
            ? new TvWarning("cnc_alarm", "error", "CNC alarm")
            : current is not null && blockedOperations.Contains(current.OperationId)
                ? new TvWarning("planning_blocked", "warning", "Planning conflict")
                : null;
        var idlePart = current is null && options.ShowLastPartImageWhenIdle && machine.LastWorked is { } last
            ? IdlePart(last)
            : null;
        return new TvMachine(
            machine.MachineId,
            machine.Number,
            machine.Name,
            machine.ProcessType,
            Connection(machine.ConnectionStatus),
            machineStatus,
            status,
            current,
            Job(nextSource, now, urgentDueByBatch, projectedFinishByOperation),
            Job(thirdSource, now, urgentDueByBatch, projectedFinishByOperation),
            downtime,
            [],
            DisplayStatus(alarm, current),
            warning,
            idlePart);
    }

    /// <summary>The status word of the row: ERROR on a CNC alarm, IDLE without a current Operation, else from its progress.</summary>
    internal static string DisplayStatus(bool alarm, TvJob? current)
    {
        if (alarm) return "ERROR";
        if (current is null) return "IDLE";
        return current.Progress.StatusCode switch
        {
            "paused" => "PAUSED",
            "completed" => "COMPLETED",
            "waiting" => "WAITING",
            _ => current.Progress.Phase == "setup" ? "SETUP" : "RUNNING"
        };
    }

    private TvIdlePart IdlePart(TvSourceOperation operation)
    {
        var picture = Thumbnail(operation.Thumbnails);
        return new TvIdlePart(
            operation.PartNumber,
            operation.PartName,
            operation.OperationNumber,
            operation.OperationName,
            picture.Url,
            picture.Version,
            ThumbnailAlt(operation),
            picture.Source);
    }

    /// <summary>
    /// The picture a TV row shows for an Operation (owner request 2026-10-12): the Case picture stored on the Server, else
    /// the Operation's package thumbnail, else its snapshot, else the placeholder. A file missing on disk is skipped.
    /// The version is the render revision and the content hash, so the URL never changes meaning.
    /// </summary>
    private (string? Url, string? Version, string Source) Thumbnail(TvSourceThumbnails? thumbnails)
    {
        if (thumbnails is not null)
        {
            foreach (var (source, hash, path) in new[]
            {
                ("case_picture", thumbnails.CasePictureHash, thumbnails.CasePicturePath),
                (OperationThumbnailSources.Package, thumbnails.PackageHash, thumbnails.PackagePath),
                (OperationThumbnailSources.Snapshot, thumbnails.SnapshotHash, thumbnails.SnapshotPath)
            })
            {
                if (hash is null || path is null || thumbnailStore.PathOf(path) is null) continue;
                var version = ThumbnailVersion(hash);
                return (ThumbnailRoute + version, version, source);
            }
        }

        return (null, null, "placeholder");
    }

    internal static string ThumbnailVersion(string sha256) => TvThumbnailRenderer.RenderRevision + "-" + sha256;

    /// <summary>The content hash in a thumbnail version, or null when the value is not one.</summary>
    internal static string? HashOfThumbnailVersion(string? version)
    {
        var prefix = TvThumbnailRenderer.RenderRevision + "-";
        if (version is null || !version.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var hash = version[prefix.Length..];
        return hash.Length == 64 && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ? hash : null;
    }

    private static string ThumbnailAlt(TvSourceOperation operation) =>
        string.IsNullOrWhiteSpace(operation.PartName)
            ? operation.PartNumber
            : $"{operation.PartNumber} - {operation.PartName}";

    private TvJob? Job(
        TvSourceOperation? operation,
        DateTimeOffset now,
        IReadOnlyDictionary<string, TvSourceBatchDueDate> urgentDueByBatch,
        IReadOnlyDictionary<string, DateTimeOffset?> projectedFinishByOperation)
    {
        if (operation is null)
        {
            return null;
        }

        var urgent = urgentDueByBatch.GetValueOrDefault(operation.BatchId);
        var picture = Thumbnail(operation.Thumbnails);
        return new TvJob(
            operation.OperationId,
            operation.BatchId,
            operation.PartNumber,
            operation.BatchNumber,
            operation.OperationNumber,
            operation.OperationName,
            operation.Status,
            projectedFinishByOperation.GetValueOrDefault(operation.OperationId),
            urgent is not null,
            urgent?.WorkFinishDate.ToString("yyyy-MM-dd"),
            Progress(operation, now),
            operation.PartName,
            picture.Url,
            picture.Version,
            ThumbnailAlt(operation),
            picture.Source);
    }

    private static TvConnectionStatus Connection(string sourceCode)
    {
        var online = sourceCode is "ONLINE" or "DEGRADED";
        return new TvConnectionStatus(
            online ? "online" : "offline",
            online ? "Online" : "Offline",
            online,
            sourceCode);
    }

    private static string? NormalizeMachineStatus(string? sourceCode)
    {
        var value = sourceCode?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value.ToUpperInvariant();
    }

    private static TvOperationProgress Progress(
        TvSourceOperation operation,
        DateTimeOffset now)
    {
        var statusCode = operation.Status switch
        {
            "in_progress" or "started" => "started",
            "suspended" => "paused",
            "completed" or "complete" => "completed",
            _ => "waiting"
        };
        var statusLabel = statusCode switch
        {
            "started" => "In production",
            "paused" => "Paused",
            "completed" => "Completed",
            _ => "Waiting"
        };
        var quantity = Math.Max(0, operation.PlannedQuantity);
        if (statusCode == "completed")
        {
            return new TvOperationProgress(
                statusCode, statusLabel, "completed", $"Part {quantity}/{quantity} | 100% of Batch",
                100, null, quantity, quantity, operation.MeasuredAverageCycleSeconds,
                operation.MeasuredCycleSampleCount);
        }

        // A Server-owned Production Run output is authoritative. Once the connected
        // CNC has entered production, never infer that it is still in setup from wall time.
        if (quantity > 0 && operation.CncProductionStarted
            && operation.ProducedQuantity.HasValue)
        {
            var produced = Math.Clamp(operation.ProducedQuantity.Value, 0, quantity);
            var percent = Math.Clamp((int)Math.Round((double)produced / quantity * 100), 0, 99);
            return new TvOperationProgress(statusCode, statusLabel, "production",
                $"Part {produced}/{quantity} | {percent}% of Batch",
                percent, null, produced, quantity, operation.MeasuredAverageCycleSeconds,
                operation.MeasuredCycleSampleCount);
        }

        var setupSeconds = Math.Max(0, operation.SetupSeconds ?? 0);
        var effectiveEnd = statusCode == "paused"
            ? operation.ActivePauseStartedAt ?? now
            : now;
        var elapsedSeconds = operation.ActualStart is { } start
            ? Math.Max(0, (effectiveEnd - start).TotalSeconds - Math.Max(0, operation.ClosedPauseSeconds))
            : 0;
        if (setupSeconds > 0 && elapsedSeconds < setupSeconds)
        {
            var percent = statusCode == "waiting"
                ? 0
                : Math.Clamp((int)Math.Round(elapsedSeconds / setupSeconds * 100), 0, 99);
            return new TvOperationProgress(
                statusCode, statusLabel, "setup", $"Setup {percent}%",
                percent, percent, null, quantity);
        }

        if (quantity > 0 && operation.CycleSeconds is > 0)
            return ProductionProgress(statusCode, statusLabel, quantity, operation.CycleSeconds,
                Math.Max(0, elapsedSeconds - setupSeconds));

        return new TvOperationProgress(
            statusCode, statusLabel, statusCode == "waiting" ? "waiting" : "unknown",
            "Progress unavailable", null, null, null, quantity);
    }

    private static TvOperationProgress ProductionProgress(
        string statusCode, string statusLabel, int quantity, int? cycleSeconds, double productionSeconds)
    {
        if (quantity <= 0 || cycleSeconds is not > 0)
            return new TvOperationProgress(statusCode, statusLabel, "production",
                "Production in progress", null, null, null, quantity);
        var fractionalParts = productionSeconds / cycleSeconds.Value;
        var currentPart = Math.Clamp((int)Math.Floor(fractionalParts) + 1, 1, quantity);
        var percent = Math.Clamp((int)Math.Round(fractionalParts / quantity * 100), 0, 99);
        return new TvOperationProgress(statusCode, statusLabel, "production",
            $"Part {currentPart}/{quantity} | {percent}% of Batch",
            percent, null, currentPart, quantity);
    }

    private static TvStatus Status(
        TvDowntime? downtime,
        TvSourceOperation? current)
    {
        if (downtime?.IsCurrent == true)
        {
            return new TvStatus("downtime", "Downtime", "●", "#C62828");
        }

        if (current is not null)
        {
            return current.Status == "setup"
                ? new TvStatus("setup", "Setup", "◆", "#FBC02D")
                : new TvStatus("current", "Current job", "▶", "#1E88E5");
        }

        return new TvStatus("idle", "Idle / no work", "■", "#9E9E9E");
    }

    private static string EntityTag(TvDashboardProjection projection)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            projection.SchemaVersion,
            projection.DashboardBuild,
            projection.Freshness,
            projection.RefreshAfterSeconds,
            projection.Summary,
            projection.UrgentBatches,
            projection.Machines
        }, JsonOptions);
        return $"\"tv:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}\"";
    }
}

internal sealed record TvDashboardResult(TvDashboardProjection Projection, string EntityTag);
