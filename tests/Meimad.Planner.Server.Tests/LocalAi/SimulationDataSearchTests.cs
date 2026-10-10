using Meimad.Planner.Server.Application.LocalAi;
using Meimad.Planner.Server.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;

namespace Meimad.Planner.Server.Tests.LocalAi;

/// <summary>
/// The AI-assisted search for an Operation's simulation data (owner request 2026-10-10): rule-found
/// candidates in the part's folders, re-ranked by the local model, read-only.
/// </summary>
public sealed class SimulationDataSearchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Meimad.SimulationData", Guid.NewGuid().ToString("N"));

    public SimulationDataSearchTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Part_folders_come_from_the_nc_file_and_from_indexed_document_paths()
    {
        var nc = Path.Combine(root, "MALKAM", "30P", "30P410126102-001 REV-NEW", "NC", "OP1", "O1000.NC");
        Assert.Equal(Path.Combine(root, "MALKAM", "30P", "30P410126102-001 REV-NEW"),
            SimulationDataDiscovery.PartFolderOfNcFile(nc, "30P410126102-001"));
        // No folder names the part: the parent of an NC folder.
        Assert.Equal(Path.Combine(root, "job"), SimulationDataDiscovery.PartFolderOfNcFile(Path.Combine(root, "job", "NC", "a.nc"), "X-1"));
        Assert.Null(SimulationDataDiscovery.PartFolderOfNcFile(null, "X-1"));
        Assert.Equal(Path.Combine(@"\\srv\data", "MALKAM", "30P410126102-001_-"),
            SimulationDataDiscovery.PartFolderOfDocument(@"\\srv\data", @"MALKAM\30P410126102-001_-\ENGINEERING\a.pdf", "30P410126102-001"));
        Assert.Null(SimulationDataDiscovery.PartFolderOfDocument(@"\\srv\data", @"MALKAM\other\a.pdf", "30P410126102-001"));
        Assert.True(SimulationDataDiscovery.IsUnder(@"\\srv\data\A\b.stl", [@"\\SRV\data\"]));
        Assert.False(SimulationDataDiscovery.IsUnder(@"\\srv\database\b.stl", [@"\\srv\data"]));
    }

    [Fact]
    public void Files_are_listed_and_classified_from_names_folders_and_the_operation_number()
    {
        var part = Path.Combine(root, "30P410126102-001");
        var nc = Path.Combine(part, "NC");
        foreach (var file in new[]
                 {
                     Path.Combine(nc, "STOCK_OP1.stl"), Path.Combine(nc, "STOCK_OP2.stl"), Path.Combine(nc, "STOCK", "FIN_LAYER_1_.stl"),
                     Path.Combine(nc, "TP_MODEL.TOOLS.mht"), Path.Combine(part, "FIXTURE", "vise jaws.stp"), Path.Combine(part, "30P410126102-001_-.STP"),
                     Path.Combine(nc, "30P410126102-001_NC1.elt"), Path.Combine(nc, "STOCK_OP1.stl.bac"), Path.Combine(part, "_MeimadPlanner", "cache.stl"),
                     Path.Combine(nc, "tool_holder.stl"), Path.Combine(nc, "O1000.NC")
                 })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "x");
        }
        var scope = new SimulationDataScope("30P410126102-001", 10, "Mill side 1", Path.Combine(nc, "O1000.NC"), [part],
            new HashSet<string>([Path.Combine(nc, "STOCK_OP2.stl")], StringComparer.OrdinalIgnoreCase));

        var files = SimulationDataDiscovery.Enumerate([part], default);
        var result = SimulationDataDiscovery.Order(files.Select(file => SimulationDataDiscovery.Classify(file, scope)))
            .ToDictionary(candidate => candidate.Name);

        Assert.Equal(["30P410126102-001_-.STP", "FIN_LAYER_1_.stl", "STOCK_OP1.stl", "STOCK_OP2.stl", "TP_MODEL.TOOLS.mht", "tool_holder.stl", "vise jaws.stp"],
            result.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("stock", result["STOCK_OP1.stl"].Kind);
        Assert.True(result["STOCK_OP1.stl"].Confidence > result["STOCK_OP2.stl"].Confidence);   // OP1 is Operation 10
        Assert.Contains("OP number matches", result["STOCK_OP1.stl"].Reason, StringComparison.Ordinal);
        Assert.True(result["STOCK_OP2.stl"].AlreadyLinked);
        Assert.Equal("stock", result["FIN_LAYER_1_.stl"].Kind);                                  // in NC\STOCK
        Assert.Equal("tool-table", result["TP_MODEL.TOOLS.mht"].Kind);
        Assert.Equal("fixture", result["vise jaws.stp"].Kind);
        Assert.Equal("part-model", result["30P410126102-001_-.STP"].Kind);
        Assert.NotEqual("tool-table", result["tool_holder.stl"].Kind);                          // only MHT/spreadsheets are tool lists
        Assert.All(result.Values, candidate => Assert.Equal("RULE", candidate.Source));
    }

    [Fact]
    public void Model_answers_are_merged_and_unreadable_answers_are_rejected()
    {
        SimulationDataCandidate Candidate(string name, string kind) =>
            new(Path.Combine(root, name), name, kind, 0.5, "rule", "RULE", 1, DateTimeOffset.UnixEpoch, false);
        var reviewed = new[] { Candidate("a.stl", "stock"), Candidate("b.stp", "fixture"), Candidate("c.stl", "stock") };
        var all = reviewed.Append(Candidate("d.stl", "stock")).ToArray();

        var merged = SimulationDataSearchService.Merge("""
            Here you go: {"items":[{"i":0,"kind":"fixture","confidence":1.4,"reason":"vise base"},
              {"i":1,"kind":"ignore","confidence":0.9,"reason":"other part"},{"i":7,"kind":"stock"},{"i":2,"kind":"bogus"}],
             "summary":"One fixture found."}
            """, reviewed, all);

        Assert.NotNull(merged);
        var (candidates, summary) = merged.Value;
        Assert.Equal("One fixture found.", summary);
        Assert.Equal(["a.stl", "c.stl", "d.stl"], candidates.Select(candidate => candidate.Name));
        Assert.Equal(("fixture", 1.0, "vise base", "AI"), (candidates[0].Kind, candidates[0].Confidence, candidates[0].Reason, candidates[0].Source));
        Assert.Equal(("stock", 0.3, "RULE"), (candidates[1].Kind, candidates[1].Confidence, candidates[1].Source));   // not judged
        Assert.Equal(0.25, candidates[2].Confidence);                                                                  // not shown to the model
        Assert.Null(SimulationDataSearchService.Merge("I could not decide.", reviewed, all));
        Assert.Null(SimulationDataSearchService.Merge("""{"items":[]}""", reviewed, all));
    }

    [Fact]
    public async Task Search_reads_the_working_folder_ranks_with_the_model_and_changes_nothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var folder = Path.Combine(root, "case");
        var nc = Path.Combine(folder, "NC");
        Directory.CreateDirectory(nc);
        File.WriteAllText(Path.Combine(nc, "O1000.NC"), "%\nO1000 (PN-PKG OP10)\n(STOCK: STOCK_OP1)\nG0 X0\nM30\n%\n");
        File.WriteAllText(Path.Combine(nc, "STOCK_OP1.stl"), "solid a\nendsolid a\n");
        File.WriteAllText(Path.Combine(nc, "clamp.stp"), "ISO-10303-21;");
        await fixture.ExecuteAsync($"UPDATE cases SET working_folder_path='{folder.Replace("'", "''")}' WHERE id='case-package'");
        var operationId = await OperationIdAsync(fixture);
        var before = await fixture.ScalarAsync("SELECT COUNT(*) FROM case_model_files");
        var model = new JsonModel("""{"items":[{"i":0,"kind":"stock","confidence":0.9,"reason":"Cimatron stock of OP1"},{"i":1,"kind":"fixture","confidence":0.8,"reason":"clamp"}],"summary":"Stock and one clamp."}""");

        await using (var search = await StartAsync(fixture, model, enabled: true))
        {
            var status = await RunAsync(search.Service, new("case-package", operationId, Path.Combine(nc, "O1000.NC")));
            Assert.Equal("COMPLETED", status.Status);
            Assert.True(status.AiRanked);
            Assert.Equal("AI_RANKED", status.ResultCode);
            Assert.Contains("Stock and one clamp.", status.Summary, StringComparison.Ordinal);
            Assert.Equal([folder], status.Folders);
            var stock = Assert.Single(status.Candidates!, candidate => candidate.Kind == "stock");
            Assert.Equal(("STOCK_OP1.stl", "AI", 0.9), (stock.Name, stock.Source, stock.Confidence));
            Assert.Single(status.Candidates!, candidate => candidate.Kind == "fixture");
            var prompt = model.Requests.Single()[1].Content;
            Assert.Contains("(STOCK: STOCK_OP1)", prompt, StringComparison.Ordinal);   // NC header comments reach the model
            Assert.Contains("STOCK_OP1.stl", prompt, StringComparison.Ordinal);
            Assert.Null(search.Service.Read("someone-else", status.SearchId));
        }

        // With the assistant off the rules answer alone.
        await using (var off = await StartAsync(fixture, new JsonModel("unused"), enabled: false))
        {
            var status = await RunAsync(off.Service, new("case-package", operationId));
            Assert.Equal("COMPLETED", status.Status);
            Assert.False(status.AiRanked);
            Assert.Contains("switched off", status.Summary, StringComparison.Ordinal);
            Assert.Equal(2, status.Candidates!.Count);
        }

        // An NC file outside the readable folders is not opened.
        await using (var outside = await StartAsync(fixture, new JsonModel("{}"), enabled: false))
        {
            var status = await RunAsync(outside.Service, new("case-package", operationId, @"C:\Windows\win.ini"));
            Assert.Contains("outside the folders the Server may read", status.Summary, StringComparison.Ordinal);
            var missing = await RunAsync(outside.Service, new("case-package", "no-such-operation"));
            Assert.Equal(("FAILED", "not_found"), (missing.Status, missing.ResultCode));
        }
        Assert.Equal(before, await fixture.ScalarAsync("SELECT COUNT(*) FROM case_model_files"));
        Assert.Throws<ArgumentException>(() => SimulationDataSearchService.Validate(new("", "x")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static async Task<string> OperationIdAsync(Fixture fixture)
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM case_operations WHERE case_id='case-package' ORDER BY operation_number LIMIT 1";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<SimulationDataSearchStatus> RunAsync(SimulationDataSearchService service, SimulationDataSearchRequest request)
    {
        var status = service.Submit("owner", request);
        for (var attempt = 0; attempt < 200 && status.Status is "QUEUED" or "RUNNING"; attempt++)
        {
            await Task.Delay(50);
            status = service.Read("owner", status.SearchId)!;
        }
        return status;
    }

    private static async Task<Search> StartAsync(Fixture fixture, ILocalModelClient model, bool enabled)
    {
        var options = new LocalAiOptions { Enabled = enabled };
        var control = new LocalAiRequestService(fixture.App.Services.GetRequiredService<LocalEvidenceService>(), model, options,
            fixture.Database, TimeProvider.System, NullLogger<LocalAiRequestService>.Instance);
        var service = new SimulationDataSearchService(fixture.Database, fixture.App.Services.GetRequiredService<LocalCorpusService>(), model,
            options, control, TimeProvider.System, NullLogger<SimulationDataSearchService>.Instance);
        await service.StartAsync(default);
        return new(service, control);
    }

    private sealed class Search(SimulationDataSearchService service, LocalAiRequestService control) : IAsyncDisposable
    {
        internal SimulationDataSearchService Service => service;
        public async ValueTask DisposeAsync() { await service.StopAsync(default); service.Dispose(); control.Dispose(); }
    }

    private sealed class JsonModel(string answer) : ILocalModelClient
    {
        internal readonly List<IReadOnlyList<ChatModelMessage>> Requests = [];
        public Task<ModelResult> CompleteAsync(EvidenceBundle bundle, CancellationToken token) => throw new InvalidOperationException();
        public Task<ChatModelTurn> ChatAsync(IReadOnlyList<ChatModelMessage> messages, IReadOnlyList<ChatToolDefinition> tools, int contextTokens, CancellationToken token)
        {
            Assert.Empty(tools);
            Requests.Add(messages.ToList());
            return Task.FromResult(new ChatModelTurn(answer, [], null, 10, 10, 1));
        }
    }
}
