using Meimad.Planner.Server.Domain.AdministrativeSetup;
using Meimad.Planner.Server.Domain.WorkingCalendars;

namespace Meimad.Planner.Server.Tests.WorkingCalendars;

public sealed class EmployeeShiftCalculatorTests
{
    // 2026-10-04 is a Sunday; Israel keeps UTC+3 until 2026-10-25.
    private static readonly DateOnly Sunday = new(2026, 10, 4);

    [Fact]
    public void Crews_follow_the_pattern_at_their_offset()
    {
        var calendar = Rotation(["day", "day", "night", "night", "off", "off", "off", "off"],
            [new RotationCrew("A", "Crew A", 0), new RotationCrew("B", "Crew B", 2)]);

        var crewA = Codes(calendar, "A");
        var crewB = Codes(calendar, "B");

        Assert.Equal(["day", "day", "night", "night", "off", "off", "off", "off", "day"], crewA);
        Assert.Equal(["off", "off", "day", "day", "night", "night", "off", "off", "off"], crewB);
    }

    [Fact]
    public void A_night_shift_belongs_to_the_evening_it_starts_and_runs_into_the_next_morning()
    {
        var calendar = Rotation(["night"], []);

        var day = Single(calendar, null, [], []);

        var interval = Assert.Single(day.Intervals);
        Assert.Equal(At("2026-10-04T16:00:00Z"), interval.StartsAt);
        Assert.Equal(At("2026-10-05T04:00:00Z"), interval.EndsAt);
    }

    [Fact]
    public void A_roster_entry_replaces_the_pattern_day()
    {
        var calendar = Rotation(["day"], []);

        var day = Single(calendar, null, [Entry(Sunday, "night")], []);

        Assert.Equal("day", day.PatternShiftCode);
        Assert.Equal("night", day.RosterShiftCode);
        Assert.Equal("night", day.EffectiveShiftCode);
        Assert.Equal(At("2026-10-04T16:00:00Z"), Assert.Single(day.Intervals).StartsAt);
    }

    [Fact]
    public void Without_a_pattern_only_roster_days_are_worked()
    {
        var calendar = Rotation([], []);

        var days = EmployeeShiftCalculator.Expand(calendar, null, [Entry(Sunday.AddDays(1), "day")], [], [], Sunday, Sunday.AddDays(2));

        Assert.Equal(["off", "day", "off"], days.Select(day => day.EffectiveShiftCode));
        Assert.Empty(days[0].Intervals);
        Assert.Single(days[1].Intervals);
    }

    [Fact]
    public void A_full_day_absence_removes_the_whole_night_shift_that_starts_that_day()
    {
        var calendar = Rotation(["night"], []);

        var intervals = EmployeeShiftCalculator.Intervals(calendar, null, [],
            [new ShiftAbsence(Sunday.AddDays(1), true, null, null, "vacation")], [],
            At("2026-10-04T00:00:00Z"), At("2026-10-07T00:00:00Z"));

        // Saturday's night reaches into the horizon; Sunday's night runs to Monday 07:00 untouched;
        // Monday's night is gone; Tuesday's night remains.
        Assert.Equal(
            [
                (At("2026-10-04T00:00:00Z"), At("2026-10-04T04:00:00Z")),
                (At("2026-10-04T16:00:00Z"), At("2026-10-05T04:00:00Z")),
                (At("2026-10-06T16:00:00Z"), At("2026-10-07T00:00:00Z"))
            ],
            intervals.Select(value => (value.StartsAt, value.EndsAt)));
    }

    [Fact]
    public void A_partial_absence_before_the_night_shift_start_is_in_its_after_midnight_part()
    {
        var calendar = Rotation(["night"], []);

        var day = Single(calendar, null, [], [new ShiftAbsence(Sunday, false, "02:00", "04:00", "personal_day")]);

        Assert.Equal("personal_day", day.AbsenceType);
        Assert.Equal(
            [(At("2026-10-04T16:00:00Z"), At("2026-10-04T23:00:00Z")), (At("2026-10-05T01:00:00Z"), At("2026-10-05T04:00:00Z"))],
            day.Intervals.Select(value => (value.StartsAt, value.EndsAt)));
    }

    [Fact]
    public void Shift_breaks_are_subtracted_including_after_midnight()
    {
        var calendar = Rotation(["night"], [], nightBreaks: [new WorkingCalendarWindow("00:00", "00:30")]);

        var day = Single(calendar, null, [], []);

        Assert.Equal(
            [(At("2026-10-04T16:00:00Z"), At("2026-10-04T21:00:00Z")), (At("2026-10-04T21:30:00Z"), At("2026-10-05T04:00:00Z"))],
            day.Intervals.Select(value => (value.StartsAt, value.EndsAt)));
    }

    [Fact]
    public void A_closure_stops_pattern_days_but_an_explicit_roster_entry_still_works()
    {
        var calendar = Rotation(["day"], []) with
        {
            Exceptions = [new WorkingCalendarException("2026-10-04", [], [], "Factory closed")]
        };

        var patternDay = Single(calendar, null, [], []);
        var rosterDay = Single(calendar, null, [Entry(Sunday, "day")], []);

        Assert.Equal("off", patternDay.EffectiveShiftCode);
        Assert.Equal("Factory closed", patternDay.ClosureName);
        Assert.Empty(patternDay.Intervals);
        Assert.Equal("day", rosterDay.EffectiveShiftCode);
        Assert.Single(rosterDay.Intervals);
    }

    [Fact]
    public void A_non_working_holiday_closes_pattern_days_of_an_opted_in_rotation()
    {
        var calendar = Rotation(["day"], []) with { UseIsraeliHolidays = true };
        var holiday = new ShiftHoliday(Sunday, "Holiday", "non_working", null, null);

        var days = EmployeeShiftCalculator.Expand(calendar, null, [], [], [holiday], Sunday, Sunday);

        Assert.Equal("Holiday", days[0].ClosureName);
        Assert.Empty(days[0].Intervals);
    }

    [Fact]
    public void A_crew_member_without_a_crew_has_no_pattern_days()
    {
        var calendar = Rotation(["day", "night"], [new RotationCrew("A", "A", 0)]);

        var day = Single(calendar, null, [], []);

        Assert.Null(day.PatternShiftCode);
        Assert.Equal("off", day.EffectiveShiftCode);
    }

    [Fact]
    public void Weekly_overnight_employee_calendar_yields_availability_and_absence_removes_the_whole_night()
    {
        var calendar = new WorkingCalendar("weekly", "Nights", "Asia/Jerusalem",
            ["sunday", "monday", "tuesday", "wednesday", "thursday"], "19:00", "07:00",
            [new WorkingCalendarWindow("19:00", "07:00")], [], [], WorkingCalendarUsage.Workers,
            WorkingCalendarScheduleKind.Weekly, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var employee = new EmployeeResource("e1", "1", "Night Worker", "regular_worker", null, "Night", "Worker",
            [], "weekly", null, null, true, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var vacation = new EmployeeCalendarException("x1", "e1", Sunday.AddDays(1), "vacation", true, null, null, null,
            1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        var availability = EmployeeAvailabilityCalculator.Calculate(employee, calendar, [vacation], [],
            At("2026-10-04T00:00:00Z"), At("2026-10-06T12:00:00Z"));

        Assert.Equal(
            [(At("2026-10-04T16:00:00Z"), At("2026-10-05T04:00:00Z"))],
            availability.Windows.Select(value => (value.StartsAt, value.EndsAt)));
    }

    private static WorkingCalendar Rotation(
        IReadOnlyList<string> pattern,
        IReadOnlyList<RotationCrew> crews,
        IReadOnlyList<WorkingCalendarWindow>? nightBreaks = null) =>
        new("rotation", "12h rotation", "Asia/Jerusalem", [], null, null, [], [], [], WorkingCalendarUsage.Workers,
            WorkingCalendarScheduleKind.Rotation, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false,
            new ShiftRotation(pattern.Count == 0 ? null : "2026-10-04",
                [
                    new RotationShift("day", "Day", "07:00", "19:00", []),
                    new RotationShift("night", "Night", "19:00", "07:00", nightBreaks ?? [])
                ],
                pattern,
                crews));

    private static string[] Codes(WorkingCalendar calendar, string crew) =>
        EmployeeShiftCalculator.Expand(calendar, crew, [], [], [], Sunday, Sunday.AddDays(8))
            .Select(day => day.EffectiveShiftCode).ToArray();

    private static EmployeeShiftDay Single(
        WorkingCalendar calendar, string? crew, IReadOnlyList<ShiftRosterEntry> roster, IReadOnlyList<ShiftAbsence> absences) =>
        Assert.Single(EmployeeShiftCalculator.Expand(calendar, crew, roster, absences, [], Sunday, Sunday));

    private static ShiftRosterEntry Entry(DateOnly date, string code) =>
        new("r-" + date, "e1", date, code, null, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "planner");

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
