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

    public TvDashboardService(
        ITvDashboardRepository repository,
        TimelineProjectionService timelineService,
        TvDashboardOptions options,
        TimeProvider timeProvider)
    {
        this.repository = repository;
        this.timelineService = timelineService;
        this.options = options;
        this.timeProvider = timeProvider;
    }

    internal async Task<TvDashboardResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var horizonStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var sourceTask = repository.ReadAsync(cancellationToken);
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
        var machines = source.Machines.Select(machine => ProjectMachine(
            machine,
            now,
            source.Downtimes,
            urgentDueByBatch,
            projectedFinishByOperation)).ToArray();
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
            2,
            "0.1.33",
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

    private static TvMachine ProjectMachine(
        TvSourceMachine machine,
        DateTimeOffset now,
        IReadOnlyList<TvSourceDowntime> allDowntimes,
        IReadOnlyDictionary<string, TvSourceBatchDueDate> urgentDueByBatch,
        IReadOnlyDictionary<string, DateTimeOffset?> projectedFinishByOperation)
    {
        var unfinished = machine.Backlog
            .Where(operation => operation.Status is not "completed" and not "complete" and not "cancelled")
            .OrderBy(operation => operation.BacklogPosition)
            .ToArray();
        var currentSource = unfinished.FirstOrDefault(operation => operation.Status is "in_progress" or "suspended")
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
        return new TvMachine(
            machine.MachineId,
            machine.Number,
            machine.Name,
            machine.ProcessType,
            Connection(machine.ConnectionStatus),
            NormalizeMachineStatus(machine.MachineStatus),
            status,
            Job(currentSource, now, urgentDueByBatch, projectedFinishByOperation),
            Job(nextSource, now, urgentDueByBatch, projectedFinishByOperation),
            Job(thirdSource, now, urgentDueByBatch, projectedFinishByOperation),
            downtime,
            []);
    }

    private static TvJob? Job(
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
            PreviewUrl(operation),
            Progress(operation, now));
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

    private static string? PreviewUrl(TvSourceOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.PreviewPath))
        {
            return null;
        }

        if (Path.GetExtension(operation.PreviewPath).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif"))
        {
            return null;
        }

        return $"/api/v1/cases/{Uri.EscapeDataString(operation.CaseId)}/preview?v=2";
    }

    private static TvOperationProgress Progress(
        TvSourceOperation operation,
        DateTimeOffset now)
    {
        var statusCode = operation.Status switch
        {
            "in_progress" => "started",
            "suspended" => "paused",
            "completed" or "complete" => "completed",
            _ => "waiting"
        };
        var statusLabel = statusCode switch
        {
            "started" => "Started",
            "paused" => "Paused",
            "completed" => "Completed",
            _ => "Waiting"
        };
        var quantity = Math.Max(0, operation.PlannedQuantity);
        if (statusCode == "completed")
        {
            return new TvOperationProgress(
                statusCode, statusLabel, "completed", $"Part {quantity}/{quantity} | 100% of Batch",
                100, null, quantity, quantity);
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
