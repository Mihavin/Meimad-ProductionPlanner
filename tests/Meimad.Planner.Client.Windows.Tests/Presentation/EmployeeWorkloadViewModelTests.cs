using System.IO;
using System.Net;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class EmployeeWorkloadViewModelTests
{
    [Fact]
    public async Task Calculating_lists_the_employees_and_selecting_one_shows_its_days_and_work()
    {
        var api = new FakeApiClient();
        var viewModel = new EmployeeWorkloadViewModel(_ => { });
        Assert.False(viewModel.CalculateCommand.CanExecute(null));
        Assert.False(viewModel.PrintReportCommand.CanExecute(null));

        viewModel.AttachSession(api);
        viewModel.From = new DateTime(2026, 8, 10);
        viewModel.To = new DateTime(2026, 8, 12);
        await viewModel.CalculateAsync();

        Assert.Equal((new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 12)), api.Requested);
        Assert.Equal(["E-1", "E-2"], viewModel.Employees.Select(row => row.EmployeeId));
        Assert.Same(viewModel.Employees[0], viewModel.SelectedEmployee);
        Assert.Equal(2, viewModel.SelectedDays.Count);
        var work = Assert.Single(viewModel.SelectedWork);
        Assert.Equal("Machine setup", work.KindText);
        Assert.Equal("WO 41448 30P450171100-001 OP80 Mill", work.WorkText);
        Assert.StartsWith("2 employee(s), 1 highly loaded.", viewModel.Status, StringComparison.Ordinal);
        Assert.True(viewModel.PrintReportCommand.CanExecute(null));

        viewModel.SelectedEmployee = viewModel.Employees[1];
        Assert.Empty(viewModel.SelectedWork);
        Assert.Equal("No working time", viewModel.Employees[1].LevelText);

        await viewModel.CalculateAsync();
        Assert.Same(viewModel.Employees[1], viewModel.SelectedEmployee);   // the selection survives a recalculation
    }

    [Fact]
    public async Task A_reversed_period_is_refused_before_asking_the_Server_and_a_refusal_is_explained()
    {
        var api = new FakeApiClient();
        var viewModel = new EmployeeWorkloadViewModel(_ => { });
        viewModel.AttachSession(api);
        viewModel.From = new DateTime(2026, 8, 12);
        viewModel.To = new DateTime(2026, 8, 10);

        await viewModel.CalculateAsync();
        Assert.Null(api.Requested);
        Assert.Equal("The last day is before the first day.", viewModel.Status);

        api.Refuse = true;
        viewModel.To = new DateTime(2026, 12, 31);
        await viewModel.CalculateAsync();
        Assert.Equal("The period may be at most 92 days.", viewModel.Status);
        Assert.Empty(viewModel.Employees);
    }

    [Fact]
    public async Task The_printable_report_spells_out_every_level_and_lists_the_work()
    {
        var api = new FakeApiClient();
        string? opened = null;
        var viewModel = new EmployeeWorkloadViewModel(path => opened = path);
        viewModel.AttachSession(api);
        viewModel.From = new DateTime(2026, 8, 10);
        viewModel.To = new DateTime(2026, 8, 12);
        await viewModel.CalculateAsync();

        await viewModel.PrintReportAsync();

        Assert.NotNull(opened);
        try
        {
            Assert.EndsWith("employee-workload-2026-08-10-2026-08-12.html", opened, StringComparison.Ordinal);
            var html = await File.ReadAllTextAsync(opened);
            Assert.Contains("David &lt;Setup&gt;", html, StringComparison.Ordinal);   // names are encoded
            Assert.Contains("class=\"level high\">", html, StringComparison.Ordinal);
            Assert.Contains("WO 41448 30P450171100-001 OP80 Mill", html, StringComparison.Ordinal);
            Assert.StartsWith("Report opened: ", viewModel.Status, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(opened);
        }

        // The document itself, in English and in Hebrew: every level is written next to its colour.
        var report = viewModel.Report!;
        var english = EmployeeWorkloadReportDocument.Build(report, value => value, rightToLeft: false);
        Assert.Contains("<h1>Employee workload</h1>", english, StringComparison.Ordinal);
        Assert.Contains("dir=\"ltr\"", english, StringComparison.Ordinal);
        Assert.Contains("class=\"level high\">High</td>", english, StringComparison.Ordinal);
        Assert.Contains("class=\"level none\">No working time</td>", english, StringComparison.Ordinal);
        Assert.Contains("<th>Setup time</th>", english, StringComparison.Ordinal);
        var hebrew = EmployeeWorkloadReportDocument.Build(report, value => "[" + value + "]", rightToLeft: true);
        Assert.Contains("dir=\"rtl\"", hebrew, StringComparison.Ordinal);
        Assert.Contains("<h1>[Employee workload]</h1>", hebrew, StringComparison.Ordinal);
        Assert.Contains("[Machine setup]", hebrew, StringComparison.Ordinal);
    }

    private sealed class FakeApiClient : StubPlannerApiClient, IPlannerApiClient
    {
        internal (DateOnly From, DateOnly To)? Requested { get; private set; }
        internal bool Refuse { get; set; }

        public Task<EmployeeWorkloadReportInfo> GetEmployeeWorkloadAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            if (Refuse)
            {
                throw new PlannerApiException(HttpStatusCode.UnprocessableEntity, "validation_failed",
                    "The period may be at most 92 days.");
            }
            Requested = (from, to);
            var start = new DateTimeOffset(2026, 8, 10, 7, 0, 0, TimeSpan.FromHours(3));
            return Task.FromResult(new EmployeeWorkloadReportInfo(
                from, to, start, start, "Asia/Jerusalem",
                [
                    new EmployeeWorkloadRowInfo(
                        "E-1", "101", "David <Setup>", "setup_worker", 72_000, 64_800, 0, 0, 0, 64_800, 90.0m, "high",
                        [
                            new EmployeeWorkloadDayInfo(new DateOnly(2026, 8, 10), 36_000, 36_000, 100.0m),
                            new EmployeeWorkloadDayInfo(new DateOnly(2026, 8, 11), 36_000, 28_800, 80.0m)
                        ],
                        [
                            new EmployeeWorkloadItemInfo("setup", "41448", "30P450171100-001", 80, "Mill", 64_800,
                                start, start.AddHours(20))
                        ]),
                    new EmployeeWorkloadRowInfo(
                        "E-2", "102", "Nadav", "qa_worker", 0, 0, 0, 0, 0, 0, null, "none", [], [])
                ]));
        }
    }
}
