using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// The AI-assisted simulation-data search of the Operation's Cimatron setup card (owner request
/// 2026-10-10): suggestions are shown, likely ones are ticked, and nothing is linked until the planner
/// chooses Link selected.
/// </summary>
public sealed class OperationCamSetupSearchTests
{
    [Fact]
    public async Task Choosing_an_nc_file_searches_once_and_linking_needs_the_planners_choice()
    {
        var api = new FakeApi();
        var viewModel = new OperationCamSetupViewModel();
        string? toolTable = null;
        viewModel.ToolTableChosen = path => toolTable = path;
        viewModel.AttachSession(api, "windows-01", 4, editor: true);
        await viewModel.LoadAsync("case-1", "op-10");

        await viewModel.FindSimulationDataAsync(@"J:\DPD\30P\NC\O1000.NC", null, automatic: true);
        await viewModel.FindSimulationDataAsync(@"J:\DPD\30P\NC\O1000.NC", null, automatic: true);

        Assert.Equal(1, api.Starts);
        Assert.Equal(("case-1", "op-10", @"J:\DPD\30P\NC\O1000.NC"), api.LastStart);
        Assert.False(viewModel.IsSearching);
        Assert.StartsWith("AI: ", viewModel.SearchStatus, StringComparison.Ordinal);
        Assert.Equal(["STOCK_OP1.stl", "vise.stp", "TP_MODEL.TOOLS.mht", "part.stp", "old.stl"], viewModel.Suggestions.Select(row => row.Name));
        // Likely files are ticked; a part model, an unlikely file and a linked file are not.
        Assert.Equal([true, false, true, false, false], viewModel.Suggestions.Select(row => row.IsSelected));
        Assert.Equal("90 %", viewModel.Suggestions[0].ConfidenceText);
        Assert.Equal("Linked", viewModel.Suggestions[4].LinkedText);
        Assert.Empty(api.Added);

        viewModel.Suggestions[3].IsSelected = true;   // the planner also takes the part model
        Assert.True(viewModel.LinkSuggestionsCommand.CanExecute(null));
        viewModel.LinkSuggestionsCommand.Execute(null);
        for (var attempt = 0; attempt < 100 && toolTable is null; attempt++) await Task.Delay(20);

        Assert.Equal(@"J:\DPD\30P\NC\TP_MODEL.TOOLS.mht", toolTable);
        Assert.Equal([("stock", "op-10"), ("part", (string?)null)], api.Added.Select(file => (file.Kind!, file.CaseOperationId)));
        Assert.All(api.Added, file => Assert.Equal(4, api.Generation));
        Assert.True(viewModel.Suggestions[0].AlreadyLinked);
        Assert.DoesNotContain(viewModel.Suggestions, row => row.IsSelected);

        // The button searches again; another Operation clears the suggestions.
        await viewModel.FindSimulationDataAsync(viewModel.LastNcFilePath, null, automatic: false);
        Assert.Equal(2, api.Starts);
        Assert.Equal(@"J:\DPD\30P\NC\O1000.NC", api.LastStart.NcFilePath);
        await viewModel.LoadAsync("case-1", "op-20");
        Assert.Empty(viewModel.Suggestions);
    }

    [Fact]
    public async Task A_failed_search_explains_itself_and_offers_nothing_to_link()
    {
        var api = new FakeApi { Fail = true };
        var viewModel = new OperationCamSetupViewModel();
        viewModel.AttachSession(api, "windows-01", 4, editor: false);
        await viewModel.LoadAsync("case-1", "op-10");
        await viewModel.FindSimulationDataAsync(null, null, automatic: false);
        Assert.Equal("The Case Operation was not found.", viewModel.SearchStatus);
        Assert.Empty(viewModel.Suggestions);
        Assert.False(viewModel.LinkSuggestionsCommand.CanExecute(null));
    }

    private sealed class FakeApi : StubPlannerApiClient, IPlannerApiClient
    {
        internal int Starts;
        internal (string CaseId, string OperationId, string? NcFilePath) LastStart;
        internal List<CaseModelFileCreate> Added { get; } = [];
        internal long Generation;
        internal bool Fail { get; init; }

        public Task<CaseOperationCamSetupResource?> GetCaseOperationCamSetupAsync(string caseId, string caseOperationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaseOperationCamSetupResource?>(null);

        public Task<IReadOnlyList<CaseModelFile>> ListCaseModelFilesAsync(string caseId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaseModelFile>>([]);

        public Task<CaseModelFile> AddCaseModelFileAsync(string caseId, CaseModelFileCreate create, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            Added.Add(create);
            Generation = editGeneration;
            var now = DateTimeOffset.UnixEpoch;
            return Task.FromResult(new CaseModelFile("file-" + Added.Count, caseId, create.CaseOperationId, create.Kind!, "stl", create.FilePath, create.Label!, false, 0, 1, now, now));
        }

        public Task<SimulationDataSearchStatus> StartSimulationDataSearchAsync(string caseId, string caseOperationId, string? ncFilePath, string? gCodeReleaseId, CancellationToken cancellationToken = default)
        {
            Starts++;
            LastStart = (caseId, caseOperationId, ncFilePath);
            return Task.FromResult(new SimulationDataSearchStatus("search-" + Starts, "QUEUED", DateTimeOffset.UnixEpoch, "Waiting…"));
        }

        public Task<SimulationDataSearchStatus> GetSimulationDataSearchAsync(string id, CancellationToken cancellationToken = default)
        {
            if (Fail) return Task.FromResult(new SimulationDataSearchStatus(id, "FAILED", DateTimeOffset.UnixEpoch, Summary: "The Case Operation was not found.", ResultCode: "not_found"));
            SimulationDataCandidate Candidate(string name, string kind, double confidence, bool linked = false) =>
                new(@"J:\DPD\30P\NC\" + name, name, kind, confidence, "reason", "AI", 1024, DateTimeOffset.UnixEpoch, linked);
            return Task.FromResult(new SimulationDataSearchStatus(id, "COMPLETED", DateTimeOffset.UnixEpoch,
                Candidates:
                [
                    Candidate("STOCK_OP1.stl", "stock", 0.9), Candidate("vise.stp", "fixture", 0.4), Candidate("TP_MODEL.TOOLS.mht", "tool-table", 0.8),
                    Candidate("part.stp", "part-model", 0.7), Candidate("old.stl", "stock", 0.95, linked: true)
                ],
                Summary: "5 file(s).", AiRanked: true, ResultCode: "AI_RANKED"));
        }

        public Task CancelSimulationDataSearchAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
