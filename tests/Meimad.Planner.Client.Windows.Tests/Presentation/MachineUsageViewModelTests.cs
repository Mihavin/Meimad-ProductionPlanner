using System.Globalization;
using System.IO;
using System.Net;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Localization;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class MachineUsageViewModelTests
{
    [Fact]
    public async Task Calculating_shows_overall_performance_the_machines_and_the_chosen_history()
    {
        // Texts use the current culture and the client language (the user's saved choice); both are
        // pinned to English here and the language is restored at the end.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        using var english = new EnglishLanguage();
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
        Assert.Equal("56.3 %", viewModel.Totals!.UsageText);
        Assert.Equal("Normal use", viewModel.Totals.UsageLevelText);
        Assert.Equal("4.0 h (20.0 %)", viewModel.Totals.SetupText);
        Assert.Equal("6.8 h (33.8 %)", viewModel.Totals.IdleText);
        Assert.Equal(["M-1 - Mill <One>", "Lathe"], viewModel.Machines.Select(row => row.DisplayName));
        Assert.Equal(["All machines", "M-1 - Mill <One>", "Lathe"], viewModel.ChartScopes.Select(scope => scope.Label));
        Assert.Same(api.Report!.Days, viewModel.ChartDays);
        Assert.Equal(api.Report.CalculatedAt.ToLocalTime().Date, viewModel.Today);
        Assert.StartsWith("2 machine(s); overall usage 56.3 % of 20.0 h. According to the Timeline", viewModel.Status, StringComparison.Ordinal);

        viewModel.ChartScope = viewModel.ChartScopes[2];
        Assert.Same(api.Report.Machines[1].Days, viewModel.ChartDays);

        await viewModel.CalculateAsync();
        Assert.Equal("m-2", viewModel.ChartScope!.MachineId);   // the chart keeps its machine on recalculation
        Assert.Same(api.Report.Machines[1].Days, viewModel.ChartDays);

        // The chart tooltip names every kind and whether the day is history or forecast.
        var tooltip = MachineUsageHistoryChart.Describe(api.Report.Days[0], new DateOnly(2026, 8, 11));
        Assert.Contains("(History)", tooltip, StringComparison.Ordinal);
        Assert.Contains("Part reload: 0.3 h (1.3 %)", tooltip, StringComparison.Ordinal);
        Assert.Contains("Hold: 1.0 h (5.0 %)", tooltip, StringComparison.Ordinal);
        Assert.Contains("(Forecast)", MachineUsageHistoryChart.Describe(api.Report.Days[0], new DateOnly(2026, 8, 9)), StringComparison.Ordinal);
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
        // Texts use the current culture and the client language (the user's saved choice); both are
        // pinned to English here and the language is restored at the end.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        using var english = new EnglishLanguage();
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
            Assert.Contains("<th>Part reload</th>", html, StringComparison.Ordinal);
            Assert.Contains("fill=\"#7B1FA2\"", html, StringComparison.Ordinal);   // the Timeline's part reload colour

            Assert.EndsWith("machine-usage-2026-08-10-2026-08-11.csv", opened[1], StringComparison.Ordinal);
            var csv = (await File.ReadAllTextAsync(opened[1])).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.StartsWith("Date,Machine number,Machine,Available h,Production h,Setup h,QC h,Part reload h", csv[0], StringComparison.Ordinal);
            Assert.Contains("2026-08-10,M-1,Mill <One>,10.00,2.50,1.00,0.50,0.25,0.00,0.00,1.00,4.75,1.00,4.25,42.5,25.0,10.0,47.5", csv);
            Assert.Equal("2026-08-10..2026-08-11,,All machines,20.00,6.50,4.00,0.50,0.25,0.00,1.00,1.00,6.75,1.00,11.25,56.3,32.5,20.0,33.8", csv[^1]);
        }
        finally
        {
            foreach (var path in opened) File.Delete(path);
        }

        var hebrew = MachineUsageReportDocument.Build(viewModel.Report!, value => "[" + value + "]", rightToLeft: true);
        Assert.Contains("dir=\"rtl\"", hebrew, StringComparison.Ordinal);
        Assert.Contains("<h1>[Machine usage]</h1>", hebrew, StringComparison.Ordinal);
        Assert.Contains("[Hold]", hebrew, StringComparison.Ordinal);
    }

    /// <summary>Switches the client language to English for one test, without saving it.</summary>
    private sealed class EnglishLanguage : IDisposable
    {
        private readonly string previous = LocalizationService.Current.CurrentLanguage;

        internal EnglishLanguage() => LocalizationService.Current.SetLanguage("en", persist: false);

        public void Dispose() => LocalizationService.Current.SetLanguage(previous, persist: false);
    }

    private static MachineUsageMetricsInfo Metrics(
        long available, long production, long setup, long qc, long partReload, long hold, long downtime, long outside)
    {
        decimal? P(long part) => available == 0 ? null : Math.Round(part * 100m / available, 1, MidpointRounding.AwayFromZero);
        var used = production + setup + qc + partReload;
        var idle = available - used - hold - downtime;
        return new MachineUsageMetricsInfo(available, production, setup, qc, partReload, 0, hold, downtime, idle, outside,
            used, P(used), P(production), P(setup), P(qc), P(partReload), P(0), P(hold), P(downtime), P(idle));
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
            var at = new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);
            var none = Metrics(0, 0, 0, 0, 0, 0, 0, 0);
            MachineUsageDayInfo Day(int day, MachineUsageMetricsInfo metrics) => new(new DateOnly(2026, 8, day), metrics);
            var mill = Metrics(36_000, 9_000, 3_600, 1_800, 900, 0, 3_600, 3_600);
            var lathe = Metrics(36_000, 14_400, 10_800, 0, 0, 3_600, 0, 0);
            var total = Metrics(72_000, 23_400, 14_400, 1_800, 900, 3_600, 3_600, 3_600);
            Report = new MachineUsageReportInfo(
                from, to, basis, "UTC", at, total,
                [Day(10, total), Day(11, none)],
                [
                    new MachineUsageRowInfo("m-1", "M-1", "Mill <One>", mill, [Day(10, mill), Day(11, none)]),
                    new MachineUsageRowInfo("m-2", "Lathe", "Lathe", lathe, [Day(10, lathe), Day(11, none)])
                ]);
            return Task.FromResult(Report);
        }
    }
}
