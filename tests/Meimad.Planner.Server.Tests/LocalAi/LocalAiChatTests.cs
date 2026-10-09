using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Application.LocalAi;
using Meimad.Planner.Server.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;
namespace Meimad.Planner.Server.Tests.LocalAi;

public sealed class LocalAiChatTests
{
    [Fact]
    public async Task Chat_answers_from_the_selection_and_server_lookups_without_changing_planning_data()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = new ScriptedModel(
            _ => Call("get_case", """{"partNumber":"PN-PKX"}"""),
            _ => Call("get_nc_releases", """{"partNumber":"PN-PKG","operationNumber":10}"""),
            _ => new("PN-PKX was not found; PN-PKG operation 10 has release main.nc.", [], null, 10, 10, 1));
        await using var chat = await StartAsync(fixture, model);
        var before = await Revisions(fixture);
        var context = new ChatContext(CaseId: "case-package", GCodeReleaseId: "gcode-1", MachineId: "machine-package");
        var status = await AskAsync(chat, "owner", new([new("user", "Which NC release does PN-PKX use?")], context));

        Assert.Equal("COMPLETED", status.Status);
        Assert.Equal("PN-PKX was not found; PN-PKG operation 10 has release main.nc.", status.Answer);
        var system = model.Requests[0][0].Content;
        Assert.Contains("PN-PKG", system, StringComparison.Ordinal);
        Assert.Contains("Selected NC release", system, StringComparison.Ordinal);
        Assert.Contains("main.nc", system, StringComparison.Ordinal);
        Assert.Contains("M-PKG", system, StringComparison.Ordinal);
        Assert.Contains("Last week:", system, StringComparison.Ordinal);
        // The misspelled part number returns the closest Case instead of a guess.
        var lookup = model.Requests[1].Last(message => message.Role == "tool");
        Assert.Equal("get_case", lookup.ToolName);
        Assert.Contains("notFound", lookup.Content, StringComparison.Ordinal);
        Assert.Contains("PN-PKG", lookup.Content, StringComparison.Ordinal);
        var releases = model.Requests[2].Last(message => message.Role == "tool").Content;
        Assert.Contains("Haas4x", releases, StringComparison.Ordinal);
        Assert.Contains(status.Sources!, source => source.Kind == "CONTEXT" && source.Title == "Case PN-PKG");
        Assert.Contains(status.Sources!, source => source.Kind == "LOOKUP" && source.Title.StartsWith("NC releases of PN-PKG", StringComparison.Ordinal));
        Assert.Equal(before, await Revisions(fixture));
        Assert.Null(chat.Service.Read("someone-else", status.ChatId));
    }

    [Fact]
    public async Task Material_receipts_use_inclusive_factory_local_days()
    {
        await using var fixture = await Fixture.CreateAsync();
        // Asia/Jerusalem is UTC+3 in early October: 2026-09-27 00:30 local is 2026-09-26 21:30Z.
        await fixture.ExecuteAsync("""
            INSERT INTO verified_material_receipts(id,case_id,quantity,received_at,verified_at,verified_by,external_reference,created_at,updated_at) VALUES
              ('in-first','case-package',5,'2026-09-26T21:30:00Z','2026-09-27T00:00:00Z','store','PO-IN-1','2026-09-27T00:00:00Z','2026-09-27T00:00:00Z'),
              ('in-last','case-package',6,'2026-10-03T20:00:00Z','2026-10-03T20:00:00Z','store','PO-IN-2','2026-10-03T20:00:00Z','2026-10-03T20:00:00Z'),
              ('out-before','case-package',7,'2026-09-26T20:30:00Z','2026-09-26T20:30:00Z','store','PO-OUT-1','2026-09-26T20:30:00Z','2026-09-26T20:30:00Z'),
              ('out-after','case-package',8,'2026-10-03T21:30:00Z','2026-10-03T21:30:00Z','store','PO-OUT-2','2026-10-03T21:30:00Z','2026-10-03T21:30:00Z');
            """);
        var tools = fixture.App.Services.GetRequiredService<LocalAiChatTools>();
        var sources = new List<ChatSource>();
        var result = await tools.ExecuteAsync(new("material_receipts", Args("""{"from":"2026-09-27","to":"2026-10-03"}""")), sources, default);
        Assert.Contains("PO-IN-1", result, StringComparison.Ordinal);
        Assert.Contains("PO-IN-2", result, StringComparison.Ordinal);
        Assert.DoesNotContain("PO-OUT", result, StringComparison.Ordinal);
        Assert.Contains("2026-09-27 00:30", result, StringComparison.Ordinal);
        Assert.Single(sources);
        Assert.Contains("must be a date", await tools.ExecuteAsync(new("material_receipts", Args("""{"from":"last week","to":"2026-10-03"}""")), sources, default), StringComparison.Ordinal);
        Assert.StartsWith("Unknown lookup", await tools.ExecuteAsync(new("delete_case", Args("{}")), sources, default), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_ai_and_invalid_conversations_are_refused_over_http()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var disabled = await fixture.Client.PostAsJsonAsync("/api/v1/local-ai/chat", new { messages = new[] { new { role = "user", content = "Hello" } } });
        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
        Assert.Contains("ai_unavailable", await disabled.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var invalid = await fixture.Client.PostAsJsonAsync("/api/v1/local-ai/chat", new { messages = new[] { new { role = "assistant", content = "Hello" } } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var missing = await fixture.Client.GetAsync("/api/v1/local-ai/chat/unknown");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Throws<ArgumentException>(() => LocalAiChatService.Validate(new(Enumerable.Range(0, 21).Select(_ => new ChatTurnInput("user", "x")).ToArray())));
        Assert.Throws<ArgumentException>(() => LocalAiChatService.Validate(new([new("user", new string('x', 4001))])));
    }

    [Fact]
    public async Task One_active_question_per_user_and_cancellation_releases_it()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new ScriptedModel(_ => { entered.TrySetResult(); return null; });
        await using var chat = await StartAsync(fixture, model);
        var first = chat.Service.Submit("owner", new([new("user", "First?")]));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Throws<LocalAiBusyException>(() => chat.Service.Submit("owner", new([new("user", "Second?")])));
        Assert.True(chat.Service.Cancel("owner", first.ChatId));
        Assert.Equal("CANCELLED", (await WaitAsync(chat.Service, "owner", first.ChatId)).Status);
        model.Script.Enqueue(_ => new("Done.", [], null, 1, 1, 1));
        Assert.Equal("Done.", (await WaitAsync(chat.Service, "owner", chat.Service.Submit("owner", new([new("user", "Again?")])).ChatId)).Answer);
    }

    [Fact]
    public async Task Pdf_text_is_indexed_and_found_by_keywords_scoped_to_a_part_number()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "MeimadCorpus", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "PN-PKG"));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "PN-PKG", "PL_PN-PKG.pdf"),
                Pdf("PARTS LIST PN-PKG", "7N GENERAL TOLERANCES FOR 3D MODEL: WALL THICKNESS +/-0.25 MM", "16F DO NOT APPLY EPOXY PRIMER ON HOLES SURFACE"));
            await File.WriteAllBytesAsync(Path.Combine(root, "other.pdf"), Pdf("OTHER PART", "GENERAL TOLERANCES +/-1 MM HOLES"));
            await File.WriteAllBytesAsync(Path.Combine(root, "broken.pdf"), [1, 2, 3]);
            var ocr = new FakeRecognizer(available: false);
            var corpus = Corpus(fixture, ocr);
            var settings = await corpus.UpdateAsync(new(0, [root]), "admin", default);
            var index = await corpus.IndexAsync(settings.Version, default);
            Assert.Equal(2, index.IndexedFiles);
            Assert.Equal(1, index.SkippedFiles);

            var scoped = await corpus.SearchKeywordsAsync("hole tolerances", "PN-PKG-001", 6, default);
            var match = Assert.Single(scoped.DistinctBy(x => x.DocumentId));
            Assert.Equal("PN-PKG/PL_PN-PKG.pdf", match.Title);
            Assert.Equal("PDF_TEXT", match.Extraction);
            Assert.Contains(scoped, x => x.Excerpt.Contains("TOLERANCES", StringComparison.Ordinal));
            Assert.Equal(2, (await corpus.SearchKeywordsAsync("hole tolerances", null, 6, default)).DistinctBy(x => x.DocumentId).Count());
            Assert.Equal(["TOLERANCE", "HOLE", "30P450172200-001"], LocalCorpusService.KeywordTerms("What are the tolerances for holes in 30P450172200-001?"));
            Assert.Contains(await corpus.SearchAsync("EPOXY PRIMER", default), x => x.Extraction == "PDF_TEXT");
            // A changed PDF is hidden until reindexed, exactly like a changed text file.
            await File.WriteAllBytesAsync(Path.Combine(root, "PN-PKG", "PL_PN-PKG.pdf"), Pdf("PARTS LIST PN-PKG", "REVISED"));
            Assert.Empty(await corpus.SearchKeywordsAsync("tolerances", "PN-PKG", 6, default));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Pages_without_text_are_recognized_once_and_cached_by_file_hash()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "MeimadCorpus", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "scan.pdf"), Pdf(null, null));
            await File.WriteAllBytesAsync(Path.Combine(root, "mixed.pdf"), Pdf("TITLE BLOCK DRAWING 30P450172200 SHEET ONE OF TWO WITH TEXT LAYER", null));
            var ocr = new FakeRecognizer(available: true);
            var corpus = Corpus(fixture, ocr);
            var settings = await corpus.UpdateAsync(new(0, [root]), "admin", default);
            await corpus.IndexAsync(settings.Version, default);
            Assert.Equal(2, ocr.Calls);
            var scan = Assert.Single(await corpus.SearchAsync("RECOGNIZED HOLE", default), x => x.Title == "scan.pdf");
            Assert.Equal("PDF_OCR", scan.Extraction);
            Assert.Contains("OCR", scan.MatchReason, StringComparison.Ordinal);
            Assert.Equal("PDF_MIXED", Assert.Single(await corpus.SearchAsync("SHEET ONE", default)).Extraction);
            await corpus.IndexAsync(settings.Version, default);
            Assert.Equal(2, ocr.Calls);
            Assert.Equal(2L, await fixture.ScalarAsync("SELECT COUNT(*) FROM local_ai_extraction_cache"));
            File.Delete(Path.Combine(root, "scan.pdf"));
            await corpus.IndexAsync(settings.Version, default);
            Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM local_ai_extraction_cache"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Windows_ocr_reads_a_rendered_pdf_page()
    {
        var recognizer = new WindowsPageTextRecognizer();
        if (!recognizer.Available) return; // No Windows OCR language on this machine; indexing then uses text layers only.
        var text = await recognizer.RecognizePdfPagesAsync(Pdf("HOLE TOLERANCE 0.05 MM", null, size: 28), [0], default);
        Assert.Contains("TOLERANCE", Assert.Single(text), StringComparison.OrdinalIgnoreCase);
    }

    private static LocalCorpusService Corpus(Fixture fixture, IPageTextRecognizer ocr) =>
        new(fixture.Database, TimeProvider.System, fixture.App.Services.GetRequiredService<IHostApplicationLifetime>(), new CorpusDocumentReader(ocr));

    /// <summary>A one- or two-page PDF; a null line leaves that page without any text layer.</summary>
    private static byte[] Pdf(string? first, string? second, params string[] more) => Pdf(first, second, 12, more);
    private static byte[] Pdf(string? first, string? second, int size, params string[] more)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        var y = 780.0;
        foreach (var line in new[] { first }.Concat(more).OfType<string>()) { page.AddText(line, size, new PdfPoint(40, y), font); y -= size * 2; }
        var next = builder.AddPage(PageSize.A4);
        if (second is not null) next.AddText(second, size, new PdfPoint(40, 780), font);
        return builder.Build();
    }

    private static async Task<string> Revisions(Fixture fixture)
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT epoch, revision FROM projection_revision";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue));
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static ChatModelTurn Call(string name, string arguments) =>
        new("", [new(name, Args(arguments))], Args("[{\"function\":{\"name\":\"" + name + "\",\"arguments\":" + arguments + "}}]"), 1, 1, 1);

    private static async Task<ChatStatus> AskAsync(Chat chat, string owner, ChatRequest request) =>
        await WaitAsync(chat.Service, owner, chat.Service.Submit(owner, request).ChatId);

    private static async Task<ChatStatus> WaitAsync(LocalAiChatService service, string owner, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (service.Read(owner, id)!.Status is "QUEUED" or "RUNNING") await Task.Delay(20, timeout.Token);
        return service.Read(owner, id)!;
    }

    private static async Task<Chat> StartAsync(Fixture fixture, ILocalModelClient model)
    {
        var options = new LocalAiOptions { Enabled = true };
        var control = new LocalAiRequestService(fixture.App.Services.GetRequiredService<LocalEvidenceService>(), model, options,
            fixture.Database, TimeProvider.System, NullLogger<LocalAiRequestService>.Instance);
        var service = new LocalAiChatService(fixture.App.Services.GetRequiredService<LocalAiChatTools>(), model, options, control,
            TimeProvider.System, NullLogger<LocalAiChatService>.Instance);
        await service.StartAsync(default);
        return new(service, control);
    }

    private sealed class Chat(LocalAiChatService service, LocalAiRequestService control) : IAsyncDisposable
    {
        internal LocalAiChatService Service => service;
        public async ValueTask DisposeAsync() { await service.StopAsync(default); service.Dispose(); control.Dispose(); }
    }

    /// <summary>Returns scripted turns; a step returning null waits until the request is cancelled.</summary>
    private sealed class ScriptedModel(params Func<IReadOnlyList<ChatModelMessage>, ChatModelTurn?>[] steps) : ILocalModelClient
    {
        internal readonly Queue<Func<IReadOnlyList<ChatModelMessage>, ChatModelTurn?>> Script = new(steps);
        internal readonly List<IReadOnlyList<ChatModelMessage>> Requests = [];
        public Task<ModelResult> CompleteAsync(EvidenceBundle bundle, CancellationToken token) => throw new InvalidOperationException();
        public async Task<ChatModelTurn> ChatAsync(IReadOnlyList<ChatModelMessage> messages, IReadOnlyList<ChatToolDefinition> tools, int contextTokens, CancellationToken token)
        {
            Requests.Add(messages.ToList());
            var turn = Script.Dequeue()(messages);
            if (turn is null) { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }
            return turn;
        }
    }

    private sealed class FakeRecognizer(bool available) : IPageTextRecognizer
    {
        internal int Calls;
        public bool Available => available;
        public Task<IReadOnlyList<string>> RecognizePdfPagesAsync(byte[] pdf, IReadOnlyList<int> pageIndexes, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<IReadOnlyList<string>>(pageIndexes.Select(index => $"RECOGNIZED HOLE DIAMETER 6.35 PAGE {index + 1} TOLERANCE +0.05/-0").ToList());
        }
    }
}
