using System.Globalization;
using System.IO;
using System.Net;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class MachineUsageViewModelTests
{
    [Fact]
    public async Task Calculating_shows_overall_performance_the_machines_and_the_chosen_history()
    {
        // Texts use the current culture; the change stays inside this async test.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        var api = new FakeApiClient();
        var viewModel = new MachineUsageViewModel(_ => { });
        Assert.False(viewModel.CalculateCommand.CanExecute(null));
        Assert.False(viewModel.HasReport);

        viewModel.AttachSession(api);
        viewModel.From = new DateTime(2026, 8, 10);
        viewModel.To = new DateTime(2026, 8, 11);
        viewModel.Basis = viewModel.BasisOptions[1];
        await viewModel.CalculateAsync();

        Assert.Equal((new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 11), "fullDay"), api.Requested);
        Assert.True(viewModel.HasReport);
        Assert.Equal("52.5 %", viewModel.Totals!.UsageText);
        Assert.Equal("Normal use", viewModel.Totals.UsageLevelText);
        Assert.Equal("4.0 h (20.0 %)", viewModel.Totals.SetupText);
        Assert.Equal(["M-1 - Mill <One>", "Lathe"], viewModel.Machines.Select(row => row.DisplayName));
        Assert.Equal(["CNC monitoring", "Manual reports"], viewModel.Machines.Select(row => row.DataSourceText));
        Assert.Equal(["All machines", "M-1 - Mill <One>", "Lathe"], viewModel.ChartScopes.Select(scope => scope.Label));
        Assert.Same(api.Report!.Days, viewModel.ChartDays);
        Assert.StartsWith("2 machine(s); overall usage 52.5 % of 20.0 h.", viewModel.Status, StringComparison.Ordinal);

        viewModel.ChartScope = viewModel.ChartScopes[2];
        Assert.Same(api.Report.Machines[1].Days, viewModel.ChartDays);

        await viewModel.CalculateAsync();
        Assert.Equal("m-2", viewModel.ChartScope!.MachineId);   // the chart keeps its machine on recalculation
        Assert.Same(api.Report.Machines[1].Days, viewModel.ChartDays);
    }

    [Fact]
    public async Task A_reversed_period_is_refused_before_asking_the_Server_and_a_refusal_is_explained()
    {
        var api = new FakeApiClient();
        var viewModel = new MachineUsageViewModel(_ => { });
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
        Assert.Empty(viewModel.Machines);
    }

    [Fact]
    public async Task The_printable_report_and_the_csv_spell_out_every_kind_and_level()
    {
        // Texts use the current culture; the change stays inside this async test.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        var api = new FakeApiClient();
        var opened = new List<string>();
        var viewModel = new MachineUsageViewModel(opened.Add);
        viewModel.AttachSession(api);
        viewModel.From = new DateTime(2026, 8, 10);
        viewModel.To = new DateTime(2026, 8, 11);
        await viewModel.CalculateAsync();

        await viewModel.PrintReportAsync();
        await viewModel.ExportCsvAsync();

        Assert.Equal(2, opened.Count);
        try
        {
            Assert.EndsWith("machine-usage-2026-08-10-2026-08-11.html", opened[0], StringComparison.Ordinal);
            var html = await File.ReadAllTextAsync(opened[0]);
            Assert.Contains("M-1 - Mill &lt;One&gt;", html, StringComparison.Ordinal);   // names are encoded
            Assert.Contains("class=\"level normal\">Normal use</td>", html, StringComparison.Ordinal);
            Assert.Contains("<svg", html, StringComparison.Ordinal);
            Assert.Contains("Total setup time", html, StringComparison.Ordinal);
            Assert.Contains("Total idle time", html, StringComparison.Ordinal);

            Assert.EndsWith("machine-usage-2026-08-10-2026-08-11.csv", opened[1], StringComparison.Ordinal);
            var csv = (await File.ReadAllTextAsync(opened[1])).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.StartsWith("Date,Machine number,Machine,Data source,Available h", csv[0], StringComparison.Ordinal);
            Assert.Contains("2026-08-10,M-1,Mill <One>,cnc,10.00,2.50,1.00,1.00,1.00,4.50,1.00,3.50,35.0,25.0,10.0,45.0", csv);
            Assert.Equal("2026-08-10..2026-08-11,,All machines,,20.00,6.50,4.00,1.00,1.00,7.50,7.00,10.50,52.5,32.5,20.0,37.5", csv[^1]);
        }
        finally
        {
            foreach (var path in opened) File.Delete(path);
        }

        var hebrew = MachineUsageReportDocument.Build(viewModel.Report!, value => "[" + value + "]", rightToLeft: true);
        Assert.Contains("dir=\"rtl\"", hebrew, StringComparison.Ordinal);
        Assert.Contains("<h1>[Machine usage]</h1>", hebrew, StringComparison.Ordinal);
        Assert.Contains("[Manual reports]", hebrew, StringComparison.Ordinal);
    }

    private static MachineUsageMetricsInfo Metrics(
        long available, long production, long setup, long downtime, long noData, long outside)
    {
        decimal? P(long part) => available == 0 ? null : Math.Round(part * 100m / available, 1, MidpointRounding.AwayFromZero);
        var idle = available - production - setup - downtime - noData;
        return new MachineUsageMetricsInfo(available, production, setup, downtime, noData, idle, outside,
            production + setup, P(production + setup), P(production), P(setup), P(downtime), P(noData), P(idle));
    }

    private sealed class FakeApiClient : StubPlannerApiClient, IPlannerApiClient
    {
        internal (DateOnly From, DateOnly To, string Basis)? Requested { get; private set; }
        internal bool Refuse { get; set; }
        internal MachineUsageReportInfo? Report { get; private set; }

        public Task<MachineUsageReportInfo> GetMachineUsageAsync(
            DateOnly from, DateOnly to, string basis, CancellationToken cancellationToken = default)
        {
            if (Refuse)
            {
                throw new PlannerApiException(HttpStatusCode.UnprocessableEntity, "validation_failed",
                    "The period may be at most 92 days.");
            }
            Requested = (from, to, basis);
            var at = new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);
            MachineUsageDayInfo Day(int day, MachineUsageMetricsInfo metrics) => new(new DateOnly(2026, 8, day), metrics);
            var cncDays = new[] { Day(10, Metrics(36_000, 9_000, 3_600, 3_600, 3_600, 3_600)), Day(11, Metrics(0, 0, 0, 0, 0, 0)) };
            var manualDays = new[] { Day(10, Metrics(36_000, 14_400, 10_800, 0, 0, 21_600)), Day(11, Metrics(0, 0, 0, 0, 0, 0)) };
            Report = new MachineUsageReportInfo(
                from, to, basis, "UTC", at, at,
                Metrics(72_000, 23_400, 14_400, 3_600, 3_600, 25_200),
                [Day(10, Metrics(72_000, 23_400, 14_400, 3_600, 3_600, 25_200)), Day(11, Metrics(0, 0, 0, 0, 0, 0))],
                [
                    new MachineUsageRowInfo("m-1", "M-1", "Mill <One>", "cnc", Metrics(36_000, 9_000, 3_600, 3_600, 3_600, 3_600), cncDays),
                    new MachineUsageRowInfo("m-2", "Lathe", "Lathe", "manual", Metrics(36_000, 14_400, 10_800, 0, 0, 21_600), manualDays)
                ]);
            return Task.FromResult(Report);
        }
    }
}
