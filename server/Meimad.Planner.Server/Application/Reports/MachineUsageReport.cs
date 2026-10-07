using Meimad.Planner.Server.Application.Timeline;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Domain.Timeline;

namespace Meimad.Planner.Server.Application.Reports;

/// <summary>
/// The time of one Machine, one day or the whole factory, split into exclusive kinds. Every kind is
/// counted inside the available time only, so <c>Production + Setup + Downtime + NoData + Idle =
/// Available</c>; work outside the available time (unattended running after the shift) is
/// <see cref="OutsideScheduleSeconds"/> and does not raise the percentages.
/// </summary>
internal sealed record MachineUsageMetrics(
    long AvailableSeconds,
    long ProductionSeconds,
    long SetupSeconds,
    long DowntimeSeconds,
    long NoDataSeconds,
    long IdleSeconds,
    long OutsideScheduleSeconds,
    long UsedSeconds,
    decimal? UsagePercent,
    decimal? ProductionPercent,
    decimal? SetupPercent,
    decimal? DowntimePercent,
    decimal? NoDataPercent,
    decimal? IdlePercent)
{
    internal static MachineUsageMetrics From(
        long available, long production, long setup, long downtime, long noData, long outside)
    {
        var idle = Math.Max(0, available - production - setup - downtime - noData);
        var used = production + setup;
        return new MachineUsageMetrics(
            available, production, setup, downtime, noData, idle, outside, used,
            Percent(used, available), Percent(production, available), Percent(setup, available),
            Percent(downtime, available), Percent(noData, available), Percent(idle, available));
    }

    internal static MachineUsageMetrics Sum(IEnumerable<MachineUsageMetrics> values)
    {
        long available = 0, production = 0, setup = 0, downtime = 0, noData = 0, outside = 0;
        foreach (var value in values)
        {
            available += value.AvailableSeconds;
            production += value.ProductionSeconds;
            setup += value.SetupSeconds;
            downtime += value.DowntimeSeconds;
            noData += value.NoDataSeconds;
            outside += value.OutsideScheduleSeconds;
        }
        return From(available, production, setup, downtime, noData, outside);
    }

    private static decimal? Percent(long part, long whole) =>
        whole == 0 ? null : Math.Round(part * 100m / whole, 1, MidpointRounding.AwayFromZero);
}

internal sealed record MachineUsageDay(DateOnly Date, MachineUsageMetrics Metrics);

/// <summary>
/// One Machine's usage. <see cref="DataSource"/> is <c>cnc</c> when production is the CNC
/// running state (time without a CNC signal is <c>noData</c>), or <c>manual</c> when production
/// is the production sessions reported by hand (unreported time is idle).
/// </summary>
internal sealed record MachineUsageRow(
    string MachineId,
    string Number,
    string Name,
    string DataSource,
    MachineUsageMetrics Metrics,
    IReadOnlyList<MachineUsageDay> Days);

internal sealed record MachineUsageReport(
    DateOnly From,
    DateOnly To,
    string Basis,
    string TimeZoneId,
    DateTimeOffset CalculatedAt,
    DateTimeOffset CountedUntil,
    MachineUsageMetrics Totals,
    IReadOnlyList<MachineUsageDay> Days,
    IReadOnlyList<MachineUsageRow> Machines);

internal static class MachineUsageBasis
{
    /// <summary>Available time is the Machine's working calendar, as the Timeline uses it.</summary>
    internal const string Schedule = "schedule";

    /// <summary>Available time is the whole day, 24 hours.</summary>
    internal const string FullDay = "fullDay";
}

internal static class MachineUsageDataSource
{
    internal const string Cnc = "cnc";
    internal const string Manual = "manual";
}

/// <summary>A span of time on one Machine.</summary>
internal sealed record MachineTimeSpan(string MachineId, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>A meaningful CNC change: the state it reports holds until the next change.</summary>
internal sealed record MachineStateChange(
    string MachineId, DateTimeOffset ObservedAt, string? ConnectionStatus, string? MachineState);

/// <summary>A Machine of the report with how its CNC signal is known.</summary>
internal sealed record MachineUsageSourceMachine(
    TimelineSourceMachine Machine, bool HasEnabledCncConnection, DateTimeOffset? LastPolledAt);

internal sealed record MachineUsageSource(
    IReadOnlyList<MachineUsageSourceMachine> Machines,
    string? MasterCalendarJson,
    string? MasterCalendarTimeZoneId,
    IReadOnlyList<TimelineSourceHoliday> Holidays,
    IReadOnlyList<TimelineSourceDowntime> Downtimes,
    IReadOnlyList<MachineStateChange> StateChanges,
    IReadOnlyList<MachineTimeSpan> ProductionSessions,
    IReadOnlyList<MachineTimeSpan> Setups);

internal interface IMachineUsageRepository
{
    /// <summary>
    /// The Machines and their recorded activity that touch [from, to): CNC changes (with the last
    /// one before <paramref name="from"/>), production sessions, setups and downtimes. Spans still
    /// open end at <paramref name="now"/>.
    /// </summary>
    Task<MachineUsageSource> ReadAsync(
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed class MachineUsageValidationException(string message) : Exception(message);

/// <summary>
/// Machine usage report: how each Machine's available time in a period of whole factory days was
/// spent, from what was recorded (it never reads the planned Timeline).
/// <list type="bullet">
/// <item>Available: the Machine's working calendar with holidays and the master calendar, exactly as
/// the Timeline uses it, or the whole day with <see cref="MachineUsageBasis.FullDay"/>. Only time
/// up to now counts.</item>
/// <item>Setup: the measured setups of the Production Runs (an Offset Loader run or a setup run
/// reported by hand to the first SEND_TO_QC, a manual setup start to its end) and a setup still in
/// progress.</item>
/// <item>Production: the CNC <c>ACTIVE</c> (program running) state on a Machine with CNC
/// monitoring; the production sessions reported by hand on a Machine without it. Time in setup is
/// setup, not production.</item>
/// <item>Downtime: planned, active and restored Machine downtimes not covered by work.</item>
/// <item>No data: on a CNC Machine, time with the connection offline or the state unknown.</item>
/// <item>Idle: the rest of the available time (stopped, ready, feed hold, alarm, or unreported).</item>
/// </list>
/// </summary>
internal sealed class MachineUsageReportService(
    IMachineUsageRepository repository,
    TimelineOptions options,
    TimeProvider timeProvider)
{
    internal const int MaximumDays = 92;

    /// <summary>CNC states that mean the program is running (MTConnect Execution and FOCAS).</summary>
    internal static readonly IReadOnlySet<string> RunningStates =
        new HashSet<string>(["ACTIVE"], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> SignalStatuses =
        new HashSet<string>(["ONLINE", "DEGRADED"], StringComparer.OrdinalIgnoreCase);

    internal async Task<MachineUsageReport> CalculateAsync(
        DateOnly from, DateOnly to, string? basis, CancellationToken cancellationToken = default)
    {
        basis = string.IsNullOrWhiteSpace(basis) ? MachineUsageBasis.Schedule : basis.Trim();
        if (basis is not (MachineUsageBasis.Schedule or MachineUsageBasis.FullDay))
            throw new MachineUsageValidationException($"basis is '{MachineUsageBasis.Schedule}' or '{MachineUsageBasis.FullDay}'.");
        if (to < from) throw new MachineUsageValidationException("The period ends before it starts.");
        if (to.DayNumber - from.DayNumber + 1 > MaximumDays)
            throw new MachineUsageValidationException($"The period may be at most {MaximumDays} days.");

        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        var now = timeProvider.GetUtcNow();
        var periodStart = LocalMidnight(from, zone);
        var periodEnd = LocalMidnight(to.AddDays(1), zone);
        var countedUntil = periodEnd < now ? periodEnd : now;
        var source = await repository.ReadAsync(periodStart, periodEnd, now, cancellationToken);
        var dayBounds = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
            .Select(offset => from.AddDays(offset))
            .Select(date => (Date: date, Start: LocalMidnight(date, zone), End: LocalMidnight(date.AddDays(1), zone)))
            .ToArray();

        var windows = TimelineProjectionService.MachineWorkingWindows(
            source.Machines.Select(machine => machine.Machine).ToArray(),
            source.MasterCalendarJson, source.MasterCalendarTimeZoneId, source.Holidays,
            periodStart, periodEnd, new List<TimelineProjectionConflict>());
        var changes = source.StateChanges.ToLookup(change => change.MachineId, StringComparer.Ordinal);
        var sessions = source.ProductionSessions.ToLookup(span => span.MachineId, StringComparer.Ordinal);
        var setups = source.Setups.ToLookup(span => span.MachineId, StringComparer.Ordinal);
        var downtimes = source.Downtimes.ToLookup(downtime => downtime.MachineId, StringComparer.Ordinal);

        var rows = new List<MachineUsageRow>();
        foreach (var entry in source.Machines)
        {
            var machine = entry.Machine;
            var machineChanges = changes[machine.MachineId].OrderBy(change => change.ObservedAt).ToArray();
            var isCnc = entry.HasEnabledCncConnection || machineChanges.Length > 0;
            var counted = new[] { (periodStart, countedUntil) };
            var available = basis == MachineUsageBasis.FullDay
                ? Spans.Normalize(counted)
                : Spans.Intersect(Spans.Normalize(windows[machine.MachineId].Select(window => (window.StartsAt, window.EndsAt))), counted);
            var setup = Spans.Intersect(Spans.Normalize(setups[machine.MachineId].Select(span => (span.StartsAt, span.EndsAt))), counted);
            IReadOnlyList<(DateTimeOffset, DateTimeOffset)> running, observed;
            if (isCnc)
                (running, observed) = CncSpans(machineChanges, Earlier(entry.LastPolledAt ?? countedUntil, countedUntil));
            else
                (running, observed) = (Spans.Normalize(sessions[machine.MachineId].Select(span => (span.StartsAt, span.EndsAt))), []);
            var production = Spans.Subtract(Spans.Intersect(running, counted), setup);
            var work = Spans.Union(setup, production);
            var downtime = Spans.Subtract(Spans.Intersect(
                Spans.Normalize(downtimes[machine.MachineId].Select(value => (value.StartsAt, value.EndsAt))), counted), work);
            var noData = isCnc
                ? Spans.Subtract(Spans.Subtract(available, Spans.Union(work, downtime)), observed)
                : [];

            var days = dayBounds.Select(day =>
            {
                var bounds = new[] { (day.Start, day.End) };
                var dayAvailable = Spans.Intersect(available, bounds);
                return new MachineUsageDay(day.Date, MachineUsageMetrics.From(
                    Spans.Seconds(dayAvailable),
                    Spans.Seconds(Spans.Intersect(production, dayAvailable)),
                    Spans.Seconds(Spans.Intersect(setup, dayAvailable)),
                    Spans.Seconds(Spans.Intersect(downtime, dayAvailable)),
                    Spans.Seconds(Spans.Intersect(noData, dayAvailable)),
                    Spans.Seconds(Spans.Subtract(Spans.Intersect(work, bounds), dayAvailable))));
            }).ToArray();
            rows.Add(new MachineUsageRow(
                machine.MachineId, machine.Number, machine.Name,
                isCnc ? MachineUsageDataSource.Cnc : MachineUsageDataSource.Manual,
                MachineUsageMetrics.Sum(days.Select(day => day.Metrics)), days));
        }

        var totalsByDay = dayBounds.Select((day, index) => new MachineUsageDay(
            day.Date, MachineUsageMetrics.Sum(rows.Select(row => row.Days[index].Metrics)))).ToArray();
        return new MachineUsageReport(
            from, to, basis, options.TimeZoneId, now, countedUntil,
            MachineUsageMetrics.Sum(rows.Select(row => row.Metrics)),
            totalsByDay,
            rows.OrderBy(row => row.Number, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.MachineId, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The running and the observed time of a CNC Machine: each change holds until the next one, and
    /// the last until the last poll. A state is observed while the connection is online or degraded.
    /// </summary>
    private static (IReadOnlyList<(DateTimeOffset, DateTimeOffset)> Running, IReadOnlyList<(DateTimeOffset, DateTimeOffset)> Observed) CncSpans(
        IReadOnlyList<MachineStateChange> changes, DateTimeOffset lastPolledAt)
    {
        var running = new List<(DateTimeOffset, DateTimeOffset)>();
        var observed = new List<(DateTimeOffset, DateTimeOffset)>();
        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            var end = index + 1 < changes.Count ? changes[index + 1].ObservedAt : lastPolledAt;
            if (end <= change.ObservedAt) continue;
            if (change.MachineState is null || change.ConnectionStatus is null || !SignalStatuses.Contains(change.ConnectionStatus)) continue;
            observed.Add((change.ObservedAt, end));
            if (RunningStates.Contains(change.MachineState)) running.Add((change.ObservedAt, end));
        }
        return (Spans.Normalize(running), Spans.Normalize(observed));
    }

    private static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    private static DateTimeOffset LocalMidnight(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}

/// <summary>Sorted, disjoint sets of time spans.</summary>
internal static class Spans
{
    internal static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Normalize(
        IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> spans)
    {
        var result = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var span in spans.Where(span => span.End > span.Start).OrderBy(span => span.Start))
        {
            if (result.Count > 0 && span.Start <= result[^1].End)
            {
                if (span.End > result[^1].End) result[^1] = (result[^1].Start, span.End);
            }
            else result.Add(span);
        }
        return result;
    }

    internal static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Union(
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> left,
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> right) => Normalize(left.Concat(right));

    internal static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Intersect(
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> left,
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> right)
    {
        var result = new List<(DateTimeOffset, DateTimeOffset)>();
        int i = 0, j = 0;
        while (i < left.Count && j < right.Count)
        {
            var start = left[i].Start > right[j].Start ? left[i].Start : right[j].Start;
            var end = left[i].End < right[j].End ? left[i].End : right[j].End;
            if (end > start) result.Add((start, end));
            if (left[i].End < right[j].End) i++;
            else j++;
        }
        return result;
    }

    internal static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Subtract(
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> left,
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> right)
    {
        var result = new List<(DateTimeOffset, DateTimeOffset)>();
        var j = 0;
        foreach (var (spanStart, spanEnd) in left)
        {
            var start = spanStart;
            while (j < right.Count && right[j].End <= start) j++;
            var k = j;
            while (k < right.Count && right[k].Start < spanEnd)
            {
                if (right[k].Start > start) result.Add((start, right[k].Start));
                if (right[k].End > start) start = right[k].End;
                k++;
            }
            if (spanEnd > start) result.Add((start, spanEnd));
        }
        return result;
    }

    internal static long Seconds(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> spans) =>
        (long)Math.Round(spans.Sum(span => (span.End - span.Start).TotalSeconds));
}
