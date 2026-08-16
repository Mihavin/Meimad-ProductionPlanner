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
            timeline.Conflicts,
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
        var criticalConflictCount = timeline.Conflicts.Count(conflict => conflict.Severity == "blocking");
        var projection = new TvDashboardProjection(
            1,
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
        IReadOnlyList<TimelineProjectionConflict> allConflicts,
        IReadOnlyDictionary<string, TvSourceBatchDueDate> urgentDueByBatch,
        IReadOnlyDictionary<string, DateTimeOffset?> projectedFinishByOperation)
    {
        var unfinished = machine.Backlog
            .Where(operation => operation.Status is not "complete" and not "cancelled")
            .OrderBy(operation => operation.BacklogPosition)
            .ToArray();
        var currentSource = unfinished.FirstOrDefault();
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
        var conflicts = allConflicts
            .Where(conflict => conflict.MachineIds.Contains(machine.MachineId, StringComparer.Ordinal))
            .Select(conflict => new TvConflict(
                conflict.ConflictId,
                conflict.Code,
                conflict.Severity,
                conflict.Message))
            .ToArray();
        var status = Status(downtime, currentSource, conflicts);
        return new TvMachine(
            machine.MachineId,
            machine.Number,
            machine.Name,
            machine.ProcessType,
            status,
            Job(currentSource, urgentDueByBatch, projectedFinishByOperation),
            Job(nextSource, urgentDueByBatch, projectedFinishByOperation),
            Job(thirdSource, urgentDueByBatch, projectedFinishByOperation),
            downtime,
            conflicts);
    }

    private static TvJob? Job(
        TvSourceOperation? operation,
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
            $"/api/v1/cases/{Uri.EscapeDataString(operation.CaseId)}/preview");
    }

    private static TvStatus Status(
        TvDowntime? downtime,
        TvSourceOperation? current,
        IReadOnlyList<TvConflict> conflicts)
    {
        if (downtime?.IsCurrent == true)
        {
            return new TvStatus("downtime", "Downtime", "●", "#C62828");
        }

        if (conflicts.Any(conflict => conflict.Severity == "blocking"))
        {
            return new TvStatus("conflict", "Blocking conflict", "▲", "#C62828");
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
