using Meimad.Planner.Server.Application.Timeline;
using Meimad.Planner.Server.Configuration;

namespace Meimad.Planner.Server.Application.Reports;

/// <summary>
/// The time of one Machine, one day or the whole factory as the Timeline shows it, split into
/// exclusive kinds inside the available time: <c>Production + Setup + Qc + PartReload + Reserved +
/// Hold + Downtime + Idle = Available</c>. Used time is the Machine occupancy (production, setup,
/// QC, part reload and reserved). Work the Timeline shows outside the available time is
/// <see cref="OutsideScheduleSeconds"/> and never raises a percentage.
/// </summary>
internal sealed record MachineUsageMetrics(
    long AvailableSeconds,
    long ProductionSeconds,
    long SetupSeconds,
    long QcSeconds,
    long PartReloadSeconds,
    long ReservedSeconds,
    long HoldSeconds,
    long DowntimeSeconds,
    long IdleSeconds,
    long OutsideScheduleSeconds,
    long UsedSeconds,
    decimal? UsagePercent,
    decimal? ProductionPercent,
    decimal? SetupPercent,
    decimal? QcPercent,
    decimal? PartReloadPercent,
    decimal? ReservedPercent,
    decimal? HoldPercent,
    decimal? DowntimePercent,
    decimal? IdlePercent)
{
    internal static MachineUsageMetrics From(
        long available, long production, long setup, long qc, long partReload, long reserved,
        long hold, long downtime, long outside)
    {
        var used = production + setup + qc + partReload + reserved;
        var idle = Math.Max(0, available - used - hold - downtime);
        return new MachineUsageMetrics(
            available, production, setup, qc, partReload, reserved, hold, downtime, idle, outside, used,
            Percent(used, available), Percent(production, available), Percent(setup, available),
            Percent(qc, available), Percent(partReload, available), Percent(reserved, available),
            Percent(hold, available), Percent(downtime, available), Percent(idle, available));
    }

    internal static MachineUsageMetrics Sum(IEnumerable<MachineUsageMetrics> values)
    {
        long available = 0, production = 0, setup = 0, qc = 0, partReload = 0, reserved = 0, hold = 0, downtime = 0, outside = 0;
        foreach (var value in values)
        {
            available += value.AvailableSeconds;
            production += value.ProductionSeconds;
            setup += value.SetupSeconds;
            qc += value.QcSeconds;
            partReload += value.PartReloadSeconds;
            reserved += value.ReservedSeconds;
            hold += value.HoldSeconds;
            downtime += value.DowntimeSeconds;
            outside += value.OutsideScheduleSeconds;
        }
        return From(available, production, setup, qc, partReload, reserved, hold, downtime, outside);
    }

    private static decimal? Percent(long part, long whole) =>
        whole == 0 ? null : Math.Round(part * 100m / whole, 1, MidpointRounding.AwayFromZero);
}

internal sealed record MachineUsageDay(DateOnly Date, MachineUsageMetrics Metrics);

internal sealed record MachineUsageRow(
    string MachineId,
    string Number,
    string Name,
    MachineUsageMetrics Metrics,
    IReadOnlyList<MachineUsageDay> Days);

/// <summary>
/// The report. Time before <see cref="CalculatedAt"/> is what the Timeline shows as history (the
/// recorded actual work); time after it is the Timeline forecast.
/// </summary>
internal sealed record MachineUsageReport(
    DateOnly From,
    DateOnly To,
    string Basis,
    string TimeZoneId,
    DateTimeOffset CalculatedAt,
    MachineUsageMetrics Totals,
    IReadOnlyList<MachineUsageDay> Days,
    IReadOnlyList<MachineUsageRow> Machines);

internal static class MachineUsageBasis
{
    /// <summary>Available time is the Machine's working calendar: the Timeline minus its non-working columns.</summary>
    internal const string Schedule = "schedule";

    /// <summary>Available time is the whole day, 24 hours.</summary>
    internal const string FullDay = "fullDay";
}

/// <summary>The kinds of Machine time, in the order a moment shown twice is counted.</summary>
internal enum MachineTimeKind
{
    Setup,
    Qc,
    PartReload,
    Production,
    Reserved,
    Hold,
    Downtime
}

internal sealed class MachineUsageValidationException(string message) : Exception(message);

/// <summary>
/// Machine usage report: how each Machine's time in a period of whole factory days is used
/// according to the calculated Timeline, exactly as its bars show it.
/// <list type="bullet">
/// <item>Available: the period minus the Machine's non-working Timeline columns (its working calendar
/// with holidays and the master calendar), or the whole day with <see cref="MachineUsageBasis.FullDay"/>.</item>
/// <item>Each operation block counts its phases: production (blue; also the recorded actual work
/// before now), setup, QC, part reload and reserved; the gaps between phases are not used. A block
/// without phases (actual history) is production. A paused block is hold.</item>
/// <item>Downtime: the Timeline's downtime bars. Idle: the rest of the available time, including
/// waiting and blocked time, which the Timeline leaves empty.</item>
/// </list>
/// The Timeline is calculated with the same horizon rule as the Employee workload, so the forecast
/// part of the period matches the Timeline view.
/// </summary>
internal sealed class MachineUsageReportService(
    TimelineProjectionService timeline,
    TimelineOptions options,
    TimeProvider timeProvider)
{
    internal const int MaximumDays = 92;

    /// <summary>Days the Timeline is calculated beyond the period, so its placement matches the Timeline view.</summary>
    private const int HorizonMarginDays = 30;

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
        // The Timeline plans from now: a later horizon start would move work into the period.
        var horizonStart = periodStart < now ? periodStart : now;
        var horizonEnd = (periodEnd > now ? periodEnd : now).AddDays(HorizonMarginDays);
        var projection = await timeline.CalculateAsync(horizonStart, horizonEnd, cancellationToken);
        var dayBounds = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
            .Select(offset => from.AddDays(offset))
            .Select(date => (Date: date, Start: LocalMidnight(date, zone), End: LocalMidnight(date.AddDays(1), zone)))
            .ToArray();
        var period = new[] { (periodStart, periodEnd) };

        var rows = new List<MachineUsageRow>();
        foreach (var machine in projection.Machines)
        {
            var available = basis == MachineUsageBasis.FullDay
                ? Spans.Normalize(period)
                : Spans.Subtract(Spans.Normalize(period), Spans.Normalize(
                    (machine.NonWorkingWindows ?? []).Select(window => (window.StartsAt, window.EndsAt))));
            var kinds = Classify(machine, period);
            var used = Spans.Normalize(kinds
                .Where(kind => kind.Key is not (MachineTimeKind.Hold or MachineTimeKind.Downtime))
                .SelectMany(kind => kind.Value));
            var days = dayBounds.Select(day =>
            {
                var bounds = new[] { (day.Start, day.End) };
                var dayAvailable = Spans.Intersect(available, bounds);
                long Seconds(MachineTimeKind kind) => Spans.Seconds(Spans.Intersect(kinds[kind], dayAvailable));
                return new MachineUsageDay(day.Date, MachineUsageMetrics.From(
                    Spans.Seconds(dayAvailable),
                    Seconds(MachineTimeKind.Production), Seconds(MachineTimeKind.Setup), Seconds(MachineTimeKind.Qc),
                    Seconds(MachineTimeKind.PartReload), Seconds(MachineTimeKind.Reserved), Seconds(MachineTimeKind.Hold),
                    Seconds(MachineTimeKind.Downtime),
                    Spans.Seconds(Spans.Subtract(Spans.Intersect(used, bounds), dayAvailable))));
            }).ToArray();
            rows.Add(new MachineUsageRow(
                machine.MachineId, machine.Number, machine.Name,
                MachineUsageMetrics.Sum(days.Select(day => day.Metrics)), days));
        }

        var totalsByDay = dayBounds.Select((day, index) => new MachineUsageDay(
            day.Date, MachineUsageMetrics.Sum(rows.Select(row => row.Days[index].Metrics)))).ToArray();
        return new MachineUsageReport(
            from, to, basis, options.TimeZoneId, now,
            MachineUsageMetrics.Sum(rows.Select(row => row.Metrics)),
            totalsByDay,
            rows.OrderBy(row => row.Number, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.MachineId, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The exclusive time of each kind on one Timeline Machine row inside <paramref name="period"/>:
    /// a moment shown by more than one bar counts once, for the first kind in
    /// <see cref="MachineTimeKind"/> order.
    /// </summary>
    internal static IReadOnlyDictionary<MachineTimeKind, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>> Classify(
        TimelineProjectionMachine machine, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> period)
    {
        var raw = Enum.GetValues<MachineTimeKind>().ToDictionary(kind => kind, _ => new List<(DateTimeOffset, DateTimeOffset)>());
        foreach (var interval in machine.Intervals)
        {
            if (interval.Type == "downtime")
            {
                raw[MachineTimeKind.Downtime].Add((interval.StartsAt, interval.EndsAt));
                continue;
            }
            if (interval.Type is not ("operation" or "actual_history")) continue;
            var hold = interval.TimingKind == "hold";
            if (interval.Phases is not { Count: > 0 } phases)
            {
                raw[hold ? MachineTimeKind.Hold : MachineTimeKind.Production].Add((interval.StartsAt, interval.EndsAt));
                continue;
            }
            foreach (var phase in phases)
            {
                MachineTimeKind? kind = phase.Type switch
                {
                    "setup" => MachineTimeKind.Setup,
                    "qa" => MachineTimeKind.Qc,
                    "loadunload" => MachineTimeKind.PartReload,
                    "production" => MachineTimeKind.Production,
                    "reserved" => MachineTimeKind.Reserved,
                    "waiting" when hold => MachineTimeKind.Hold,
                    _ => null
                };
                if (kind is { } value) raw[value].Add((phase.StartsAt, phase.EndsAt));
            }
        }

        var result = new Dictionary<MachineTimeKind, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>>();
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> taken = [];
        foreach (var kind in Enum.GetValues<MachineTimeKind>())
        {
            var spans = Spans.Subtract(Spans.Intersect(Spans.Normalize(raw[kind]), period), taken);
            result[kind] = spans;
            taken = Spans.Union(taken, spans);
        }
        return result;
    }

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
