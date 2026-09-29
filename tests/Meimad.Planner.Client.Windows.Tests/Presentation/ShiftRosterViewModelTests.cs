using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class ShiftRosterViewModelTests
{
    [Fact]
    public async Task Changing_a_day_saves_only_that_day_with_the_version_it_was_read_at()
    {
        var api = new FakeApi();
        var viewModel = new ShiftRosterViewModel();
        viewModel.AttachSession(api, "client-1", editor: true);
        await viewModel.RefreshAsync();

        var row = Assert.Single(viewModel.Rows);
        Assert.Equal("7 - Dana Levi", row.Name);
        Assert.Contains("Crew A", row.Detail, StringComparison.Ordinal);
        var sunday = row.Cells[0];
        Assert.Equal("Day", sunday.ShiftText);
        Assert.Equal("day", sunday.Kind);
        Assert.Equal("Pattern (Day)", sunday.SelectedChoice.Label);
        var monday = row.Cells[1];
        Assert.Equal("Night", monday.ShiftText);
        Assert.Equal("Night 19:00–07:00", monday.SelectedChoice.Label);
        Assert.False(viewModel.HasChanges);

        sunday.SelectedChoice = sunday.Choices.Single(choice => choice.Code == "night");
        monday.SelectedChoice = monday.Choices[0];

        Assert.True(viewModel.HasChanges);
        Assert.Equal("Night *", sunday.ShiftText);
        Assert.Equal("night", sunday.Kind);
        Assert.False(viewModel.NextWeekCommand.CanExecute(null));
        await viewModel.SaveAsync();

        Assert.Equal(
            [
                new ShiftRosterChange("employee-1", Text(viewModel.WeekStart), "night", null, null),
                new ShiftRosterChange("employee-1", Text(viewModel.WeekStart.AddDays(1)), null, null, 3)
            ],
            api.Saved);
        Assert.Contains("2 roster day(s) saved", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeating_last_week_only_makes_entries_where_the_pattern_differs()
    {
        var api = new FakeApi();
        var viewModel = new ShiftRosterViewModel();
        viewModel.AttachSession(api, "client-1", editor: true);
        await viewModel.RefreshAsync();

        await viewModel.CopyPreviousWeekAsync();

        var cells = viewModel.Rows[0].Cells;
        // Last week: Sunday night (pattern here: day) and Monday night (already this week's roster night).
        Assert.Equal("night", cells[0].SelectedChoice.Code);
        Assert.True(cells[0].IsChanged);
        Assert.Equal("night", cells[1].SelectedChoice.Code);
        Assert.False(cells[1].IsChanged);
        Assert.Equal(viewModel.WeekStart.AddDays(-7), api.PreviousFrom);
    }

    [Fact]
    public async Task A_viewer_sees_the_roster_but_cannot_save()
    {
        var viewModel = new ShiftRosterViewModel();
        viewModel.AttachSession(new FakeApi(), "client-1", editor: false);
        await viewModel.RefreshAsync();

        viewModel.Rows[0].Cells[0].SelectedChoice = viewModel.Rows[0].Cells[0].Choices[^1];

        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.Contains("may not change", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void The_factory_week_starts_on_sunday()
    {
        Assert.Equal(new DateOnly(2026, 9, 27), ShiftRosterViewModel.WeekOf(new DateOnly(2026, 9, 29)));
        Assert.Equal(new DateOnly(2026, 10, 4), ShiftRosterViewModel.WeekOf(new DateOnly(2026, 10, 4)));
    }

    [Fact]
    public void The_rotation_form_reads_shifts_pattern_and_crews()
    {
        var rotation = SetupViewModel.ParseRotation(
            "day | Day | 07:00-19:00 | 12:00-12:30\r\nnight |  | 19:00-07:00",
            "day day night night - - - -",
            "2026-10-04",
            "A | Crew A | 0\r\nB |  | 2");

        Assert.Equal("2026-10-04", rotation.AnchorDate);
        Assert.Equal(["day", "night"], rotation.Shifts.Select(shift => shift.Code));
        Assert.Equal("night", rotation.Shifts[1].Name);
        Assert.Equal(new WorkingCalendarWindow("12:00", "12:30"), Assert.Single(rotation.Shifts[0].BreakWindows!));
        Assert.Equal(["day", "day", "night", "night", "off", "off", "off", "off"], rotation.Pattern);
        Assert.Equal(new RotationCrew("B", "B", 2), rotation.Crews[1]);

        Assert.Throws<FormatException>(() => SetupViewModel.ParseRotation("day | Day | 7-19", "", "", ""));
        Assert.Throws<FormatException>(() => SetupViewModel.ParseRotation("day | Day | 07:00-19:00", "", "04/10/2026", ""));
        Assert.Throws<FormatException>(() => SetupViewModel.ParseRotation("day | Day | 07:00-19:00", "", "", "A | Crew A"));
    }

    private static string Text(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class FakeApi : StubPlannerApiClient, IPlannerApiClient
    {
        private static readonly ShiftRosterCalendar Calendar = new("calendar-1", "12h", "Asia/Jerusalem",
            [new ShiftRosterShift("day", "Day", "07:00", "19:00"), new ShiftRosterShift("night", "Night", "19:00", "07:00")],
            [new RotationCrew("A", "Crew A", 0)], 4);

        internal IReadOnlyList<ShiftRosterChange>? Saved { get; private set; }

        internal DateOnly? PreviousFrom { get; private set; }

        private DateOnly? firstFrom;

        public Task<ShiftRosterSnapshot> GetShiftRosterAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            firstFrom ??= from;
            var previous = from < firstFrom;
            if (previous) PreviousFrom = from;
            var days = Enumerable.Range(0, 7).Select(index =>
            {
                var date = from.AddDays(index).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var pattern = index % 2 == 0 ? "day" : "off";
                return previous
                    ? Day(date, pattern, index < 2 ? "night" : null, index < 2 ? "night" : pattern, index < 2 ? 1 : null)
                    : Day(date, pattern, index == 1 ? "night" : null, index == 1 ? "night" : pattern, index == 1 ? 3 : null);
            }).ToArray();
            return Task.FromResult(new ShiftRosterSnapshot(
                from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                [Calendar],
                [new ShiftRosterEmployee("employee-1", "7", "Dana Levi", "setup_worker", "calendar-1", "A", days)]));
        }

        public Task SaveShiftRosterAsync(IReadOnlyList<ShiftRosterChange> changes, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            Saved = changes;
            return Task.CompletedTask;
        }

        private static ShiftRosterDay Day(string date, string pattern, string? roster, string effective, int? version) =>
            new(date, pattern, roster, effective, null, null, false, version, null, roster is null ? null : "planner", null, null, null);
    }
}
