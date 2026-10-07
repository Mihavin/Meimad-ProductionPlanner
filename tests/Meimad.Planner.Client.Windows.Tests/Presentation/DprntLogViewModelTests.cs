using System.Globalization;
using System.IO;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class DprntLogViewModelTests
{
    [Fact]
    public async Task Loading_reads_every_page_of_the_chosen_machine_and_period_in_arrival_order()
    {
        // The status uses the current culture; the change stays inside this async test.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        var api = new FakeApiClient(lineCount: 25_000);
        var viewModel = new DprntLogViewModel(_ => { });
        viewModel.AttachSession(api);
        await viewModel.LoadMachinesAsync();
        Assert.Equal(["14 - Haas UMC-500ss", "15 - Haas UMC-500ss"], viewModel.Machines.Select(choice => choice.Label));
        Assert.False(viewModel.LoadCommand.CanExecute(null));   // a machine must be chosen first

        viewModel.Machine = viewModel.Machines[1];
        viewModel.From = new DateTime(2026, 10, 4);
        viewModel.To = new DateTime(2026, 10, 5);
        viewModel.Search = " CST ";
        await viewModel.LoadAsync();

        Assert.Equal(25_000, viewModel.Lines.Count);
        Assert.Equal(Enumerable.Range(1, 25_000).Select(index => (long)index), viewModel.Lines.Select(line => line.Id));
        Assert.Equal([0L, 10_000L, 20_000L], api.Requests.Select(request => request.AfterId));
        var first = api.Requests[0];
        Assert.Equal("machine-15", first.MachineId);
        Assert.Equal(" CST ", first.Search);
        // The days are local: from the start of the first day to the start of the day after the last.
        Assert.Equal(new DateTime(2026, 10, 4), first.From!.Value.LocalDateTime);
        Assert.Equal(new DateTime(2026, 10, 6), first.To!.Value.LocalDateTime);
        Assert.StartsWith("15 - Haas UMC-500ss: 25,000 line(s)", viewModel.Status, StringComparison.Ordinal);
        Assert.True(viewModel.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_reversed_period_is_refused_and_an_empty_period_is_explained()
    {
        var api = new FakeApiClient(lineCount: 0);
        var viewModel = new DprntLogViewModel(_ => { });
        viewModel.AttachSession(api);
        await viewModel.LoadMachinesAsync();
        viewModel.Machine = viewModel.Machines[0];
        viewModel.From = new DateTime(2026, 10, 5);
        viewModel.To = new DateTime(2026, 10, 4);

        await viewModel.LoadAsync();
        Assert.Empty(api.Requests);
        Assert.Equal("The last day is before the first day.", viewModel.Status);

        viewModel.From = null;
        viewModel.To = null;
        await viewModel.LoadAsync();
        var request = Assert.Single(api.Requests);
        Assert.Null(request.From);
        Assert.Null(request.To);
        Assert.Contains("no DPRNT lines", viewModel.Status, StringComparison.Ordinal);
        Assert.False(viewModel.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Export_writes_each_line_with_its_time_and_opens_the_file()
    {
        var api = new FakeApiClient(lineCount: 3);
        string? opened = null;
        var viewModel = new DprntLogViewModel(path => opened = path);
        viewModel.AttachSession(api);
        await viewModel.LoadMachinesAsync();
        viewModel.Machine = viewModel.Machines[1];
        await viewModel.LoadAsync();

        await viewModel.ExportAsync();

        Assert.NotNull(opened);
        try
        {
            Assert.StartsWith("dprnt-15_-_Haas_UMC-500ss-", Path.GetFileName(opened), StringComparison.Ordinal);
            var lines = await File.ReadAllLinesAsync(opened);
            Assert.Equal(3, lines.Length);
            Assert.Equal($"{viewModel.Lines[0].TimeText}\tline 1", lines[0]);
        }
        finally
        {
            File.Delete(opened);
        }
    }

    private sealed class FakeApiClient(int lineCount) : StubPlannerApiClient, IPlannerApiClient
    {
        internal List<(string MachineId, DateTimeOffset? From, DateTimeOffset? To, string? Search, long AfterId)> Requests { get; } = [];

        public Task<IReadOnlyList<PlannerMachine>> ListMachinesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerMachine>>(
            [
                Machine("machine-15", "15"),
                Machine("machine-14", "14")
            ]);

        private static PlannerMachine Machine(string id, string number) => new(
            id, number, "Haas UMC-500ss", "mill", null, [], "calendar", true, true, null, null, 0, 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        public Task<DprntLogPageInfo> GetDprntLogAsync(
            string machineId, DateTimeOffset? from, DateTimeOffset? to, string? search, long afterId, int limit,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((machineId, from, to, search, afterId));
            var start = new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
            var lines = Enumerable.Range((int)afterId + 1, (int)Math.Max(0, Math.Min(limit, lineCount - afterId)))
                .Select(id => new DprntLogLineInfo(id, start.AddSeconds(id), $"line {id}"))
                .ToArray();
            var hasMore = lines.Length > 0 && lines[^1].Id < lineCount;
            return Task.FromResult(new DprntLogPageInfo(machineId, from, to, search, lines, hasMore));
        }
    }
}
