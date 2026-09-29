using Meimad.Planner.Server.Domain.WorkingCalendars;

namespace Meimad.Planner.Server.Tests.WorkingCalendars;

public sealed class ShiftRotationValidatorTests
{
    [Fact]
    public void A_rotation_is_normalized_and_defaults_to_the_worker_usages()
    {
        var values = WorkingCalendarValidator.ValidateAndNormalize(Rotation(new ShiftRotationValues(
            "2026-10-04",
            [Shift("Day", "07:00", "19:00"), Shift("NIGHT", "19:00", "07:00")],
            ["day", "", "night", null],
            [new RotationCrewValues(" A ", null, 0), new RotationCrewValues("B", "Crew B", 2)])));

        Assert.Equal(WorkingCalendarScheduleKind.Rotation, values.ScheduleKind);
        Assert.Equal(WorkingCalendarUsage.Workers, values.Usages);
        Assert.Empty(values.Workdays);
        Assert.Equal(["day", "night"], values.Rotation!.Shifts.Select(shift => shift.Code));
        Assert.Equal(["day", "off", "night", "off"], values.Rotation.Pattern);
        Assert.Equal("A", values.Rotation.Crews[0].Code);
        Assert.Equal("A", values.Rotation.Crews[0].Name);
    }

    [Theory]
    [InlineData("rotation.pattern[0]", "unknown_shift")]
    [InlineData("rotation.crews[0].offsetDays", "out_of_range")]
    [InlineData("usages", "rotation_machine_usage")]
    [InlineData("rotation.shifts[1].code", "reserved_code")]
    [InlineData("rotation.anchorDate", "required")]
    [InlineData("rotation.crews", "crews_require_pattern")]
    [InlineData("workdays", "not_used_by_rotation")]
    public void Invalid_rotations_are_explained(string field, string code)
    {
        var rotation = new ShiftRotationValues("2026-10-04", [Shift("day", "07:00", "19:00")], ["day", "off"], []);
        var values = Rotation(rotation);
        values = (field, code) switch
        {
            (_, "unknown_shift") => Rotation(rotation with { Pattern = ["evening"] }),
            (_, "out_of_range") => Rotation(rotation with { Crews = [new RotationCrewValues("A", null, 2)] }),
            (_, "rotation_machine_usage") => values with { Usages = ["machine", "setup_worker"] },
            (_, "reserved_code") => Rotation(rotation with { Shifts = [Shift("day", "07:00", "19:00"), Shift("off", "19:00", "07:00")] }),
            ("rotation.anchorDate", _) => Rotation(rotation with { AnchorDate = null }),
            (_, "crews_require_pattern") => Rotation(rotation with { Pattern = [], Crews = [new RotationCrewValues("A", null, 0)] }),
            _ => values with { Workdays = ["sunday"] }
        };

        var exception = Assert.Throws<WorkingCalendarValidationException>(() => WorkingCalendarValidator.ValidateAndNormalize(values));

        Assert.Contains(exception.Issues, issue => issue.Field == field && issue.Code == code);
    }

    [Fact]
    public void A_weekly_calendar_cannot_carry_a_rotation()
    {
        var values = new WorkingCalendarValues("Weekly", "UTC", ["sunday"], "07:00", "19:00",
            Rotation: new ShiftRotationValues(null, [Shift("day", "07:00", "19:00")], [], []));

        var exception = Assert.Throws<WorkingCalendarValidationException>(() => WorkingCalendarValidator.ValidateAndNormalize(values));

        Assert.Contains(exception.Issues, issue => issue.Code == "rotation_requires_rotation_kind");
    }

    [Fact]
    public void A_rotation_closure_cannot_define_working_windows()
    {
        var values = Rotation(new ShiftRotationValues(null, [Shift("day", "07:00", "19:00")], [], [])) with
        {
            Exceptions = [new WorkingCalendarException("2026-10-04", [new WorkingCalendarWindow("08:00", "12:00")], [], "Short day")]
        };

        var exception = Assert.Throws<WorkingCalendarValidationException>(() => WorkingCalendarValidator.ValidateAndNormalize(values));

        Assert.Contains(exception.Issues, issue => issue.Code == "rotation_exception_windows_unsupported");
    }

    private static WorkingCalendarValues Rotation(ShiftRotationValues rotation) =>
        new("12h", "Asia/Jerusalem", null, null, null, ScheduleKind: "rotation", Rotation: rotation);

    private static RotationShiftValues Shift(string code, string start, string end) =>
        new(code, null, start, end, null);
}
