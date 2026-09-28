using Meimad.Planner.Server.Application.AdministrativeSetup;
using Meimad.Planner.Server.Application.Timeline;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Domain.Timeline;

namespace Meimad.Planner.Server.Application.Reports;

/// <summary>One day of an Employee's planned load: working time, booked time and their ratio.</summary>
internal sealed record EmployeeWorkloadDay(DateOnly Date, long AvailableSeconds, long BookedSeconds, decimal? LoadPercent);

/// <summary>The time one piece of work books on an Employee in the period.</summary>
internal sealed record EmployeeWorkloadItem(
    string Kind,
    string? BatchNumber,
    string? PartNumber,
    int? OperationNumber,
    string? Name,
    long Seconds,
    DateTimeOffset FirstStart,
    DateTimeOffset LastEnd);

/// <summary>
/// An Employee's planned load in the period. <see cref="LoadLevel"/> is <c>none</c> (no working
/// time), <c>low</c> (under 30 %), <c>normal</c>, <c>high</c> (85 % or more), <c>full</c> (100 %)
/// or <c>over</c> (more work than working time).
/// </summary>
internal sealed record EmployeeWorkloadRow(
    string EmployeeId,
    string EmployeeNumber,
    string Name,
    string Role,
    long AvailableSeconds,
    long SetupSeconds,
    long QaSeconds,
    long LoadUnloadSeconds,
    long StationStepSeconds,
    long BookedSeconds,
    decimal? LoadPercent,
    string LoadLevel,
    IReadOnlyList<EmployeeWorkloadDay> Days,
    IReadOnlyList<EmployeeWorkloadItem> Work);

internal sealed record EmployeeWorkloadReport(
    DateOnly From,
    DateOnly To,
    DateTimeOffset CalculatedAt,
    DateTimeOffset CountedFrom,
    string TimeZoneId,
    IReadOnlyList<EmployeeWorkloadRow> Employees);

internal sealed class EmployeeWorkloadValidationException(string message) : Exception(message);

/// <summary>
/// Employee workload calculator (owner decision 2026-09-28): the planned load of every active
/// Employee in a period of whole factory days, from the calculated Timeline. Booked time is the
/// setup, first-part QA and load/unload the Timeline assigns to the Employee on Machine operations
/// plus the station steps the auxiliary allocator places on them; working time is what the Timeline
/// may book (their calendar with holidays, absences and the master calendar). Only time from now on
/// counts, because the Timeline plans forward from now.
/// </summary>
internal sealed class EmployeeWorkloadService(
    TimelineProjectionService timeline,
    IAdministrativeSetupRepository setup,
    TimelineOptions options,
    TimeProvider timeProvider)
{
    internal const int MaximumDays = 92;

    /// <summary>Days the Timeline is calculated beyond the period, so its placement matches the Timeline view.</summary>
    private const int HorizonMarginDays = 30;

    internal async Task<EmployeeWorkloadReport> CalculateAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from) throw new EmployeeWorkloadValidationException("The period ends before it starts.");
        if (to.DayNumber - from.DayNumber + 1 > MaximumDays)
            throw new EmployeeWorkloadValidationException($"The period may be at most {MaximumDays} days.");

        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        var now = timeProvider.GetUtcNow();
        var periodStart = LocalMidnight(from, zone);
        var periodEnd = LocalMidnight(to.AddDays(1), zone);
        var countedFrom = periodStart > now ? periodStart : now;
        // The Timeline plans from now: a later horizon start would move work into the period.
        var horizonStart = periodStart < now ? periodStart : now;
        var horizonEnd = (periodEnd > now ? periodEnd : now).AddDays(HorizonMarginDays);
        var projection = await timeline.CalculateAsync(horizonStart, horizonEnd, cancellationToken);
        var directory = (await setup.ListResourcesAsync(cancellationToken))
            .ToDictionary(resource => resource.ResourceId, StringComparer.Ordinal);

        var rows = new List<EmployeeWorkloadRow>();
        foreach (var employee in projection.Employees ?? [])
        {
            directory.TryGetValue(employee.EmployeeId, out var resource);
            if (resource is { IsActive: false }) continue;
            var days = new List<EmployeeWorkloadDay>();
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                var dayStart = Later(LocalMidnight(date, zone), countedFrom);
                var dayEnd = LocalMidnight(date.AddDays(1), zone);
                var available = Overlap(employee.Availability.Select(window => (window.StartsAt, window.EndsAt)), dayStart, dayEnd);
                // Each kind counts on its own, so work booked twice at once shows as more than 100 %.
                var booked = employee.Bookings.GroupBy(booking => booking.Kind)
                    .Sum(kind => Overlap(kind.Select(booking => (booking.StartsAt, booking.EndsAt)), dayStart, dayEnd));
                days.Add(new EmployeeWorkloadDay(date, available, booked, Percent(booked, available)));
            }

            long Booked(string kind) => Overlap(
                employee.Bookings.Where(booking => booking.Kind == kind).Select(booking => (booking.StartsAt, booking.EndsAt)),
                countedFrom, periodEnd);
            var availableSeconds = days.Sum(day => day.AvailableSeconds);
            var (setupSeconds, qaSeconds, loadSeconds, stationSeconds) =
                (Booked("setup"), Booked("qa"), Booked("load_unload"), Booked("station_step"));
            var bookedSeconds = setupSeconds + qaSeconds + loadSeconds + stationSeconds;
            var percent = Percent(bookedSeconds, availableSeconds);
            var work = employee.Bookings
                .Where(booking => booking.EndsAt > countedFrom && booking.StartsAt < periodEnd)
                .GroupBy(booking => (booking.Kind, booking.OperationId, booking.Name))
                .Select(group => new EmployeeWorkloadItem(
                    group.Key.Kind,
                    group.First().BatchNumber,
                    group.First().PartNumber,
                    group.First().OperationNumber,
                    group.Key.Name,
                    Overlap(group.Select(booking => (booking.StartsAt, booking.EndsAt)), countedFrom, periodEnd),
                    Later(group.Min(booking => booking.StartsAt), countedFrom),
                    Earlier(group.Max(booking => booking.EndsAt), periodEnd)))
                .OrderBy(item => item.FirstStart)
                .ThenBy(item => item.BatchNumber, StringComparer.Ordinal)
                .ToArray();
            rows.Add(new EmployeeWorkloadRow(
                employee.EmployeeId,
                resource?.EmployeeNumber ?? string.Empty,
                resource?.Name ?? employee.Name ?? employee.EmployeeId,
                resource?.ResourceType ?? employee.Role,
                availableSeconds,
                setupSeconds,
                qaSeconds,
                loadSeconds,
                stationSeconds,
                bookedSeconds,
                percent,
                Level(bookedSeconds, availableSeconds, percent),
                days,
                work));
        }

        return new EmployeeWorkloadReport(
            from, to, now, countedFrom, options.TimeZoneId,
            rows.OrderByDescending(row => row.LoadPercent ?? (row.BookedSeconds > 0 ? decimal.MaxValue : -1))
                .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray());
    }

    private static string Level(long booked, long available, decimal? percent) => available == 0
        ? booked > 0 ? "over" : "none"
        : percent switch
        {
            > 100 => "over",
            >= 100 => "full",
            >= 85 => "high",
            >= 30 => "normal",
            _ => "low"
        };

    private static decimal? Percent(long booked, long available) =>
        available == 0 ? null : Math.Round(booked * 100m / available, 1);

    /// <summary>Seconds of the union of the spans inside [from, to).</summary>
    private static long Overlap(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> spans, DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) return 0;
        long total = 0;
        DateTimeOffset? runStart = null, runEnd = null;
        foreach (var (start, end) in spans
                     .Select(span => (Start: Later(span.Start, from), End: Earlier(span.End, to)))
                     .Where(span => span.End > span.Start)
                     .OrderBy(span => span.Start))
        {
            if (runEnd is not null && start <= runEnd)
            {
                if (end > runEnd) runEnd = end;
                continue;
            }
            if (runStart is not null) total += (long)(runEnd!.Value - runStart.Value).TotalSeconds;
            (runStart, runEnd) = (start, end);
        }
        if (runStart is not null) total += (long)(runEnd!.Value - runStart.Value).TotalSeconds;
        return total;
    }

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    private static DateTimeOffset LocalMidnight(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
