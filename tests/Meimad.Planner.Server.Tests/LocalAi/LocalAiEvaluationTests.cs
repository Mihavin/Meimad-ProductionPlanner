using System.Diagnostics;
using System.Text.Json;
using Meimad.Planner.Server.Application.LocalAi;
using Meimad.Planner.Server.Configuration;
namespace Meimad.Planner.Server.Tests.LocalAi;

public sealed class LocalAiEvaluationTests
{
    private static string RepositoryFile(string relative)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "AGENTS.md"))) folder = folder.Parent;
        return Path.Combine(folder?.FullName ?? throw new InvalidOperationException("Repository root was not found."), relative);
    }
    private static IReadOnlyList<(string Id, string Language, EvidenceBundle Bundle)> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFile("docs/local-ai-evaluation-cases.json")));
        var rows = new List<(string, string, EvidenceBundle)>();
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        foreach (var language in document.RootElement.GetProperty("languages").EnumerateArray().Select(x => x.GetString()!))
        {
            var id = item.GetProperty("id").GetString()!;
            var source = new EvidenceLink("evaluationFixture", id);
            var fact = new EvidenceFact("E1", item.GetProperty("kind").GetString()!, item.GetProperty("code").GetString()!,
                item.GetProperty("value").Clone(), null, source, "synthetic-v1", null, null, "SYNTHETIC_TEST_ONLY", item.GetProperty(language).GetString()!);
            var limitation = language switch { "he" => "זהו תרחיש בדיקה סינתטי, לא אישור לייצור.", "ru" => "Это синтетический тест, а не разрешение производства.", _ => "This is a synthetic test, not production authorization." };
            var bundle = new EvidenceBundle(1, "ExplainBlockers", DateTimeOffset.Parse("2026-10-08T00:00:00Z"), "synthetic-v1", "RUN-EVAL-" + id,
                null, null, null, [fact], [new("fact", fact.Description, ["E1"]), new("limitation", limitation, ["E1"])], [limitation]);
            rows.Add((id, language, bundle));
        }
        return rows;
    }

    [Fact]
    public void Fifty_four_multilingual_seed_cases_preserve_exact_claims_and_identifiers()
    {
        var rows = Cases(); Assert.Equal(54, rows.Count);
        Assert.Equal(54, rows.Select(x => (x.Id, x.Language)).Distinct().Count());
        foreach (var (_, _, bundle) in rows)
        {
            Assert.Equal(bundle.Claims.Reverse(), ModelClaimValidator.Validate("{\"claimOrder\":[1,0]}", bundle));
            Assert.Throws<ArgumentException>(() => ModelClaimValidator.Validate("{\"claimOrder\":[0,1],\"command\":\"run\"}", bundle));
        }
    }

    [LocalModelEvaluationFact]
    public async Task Evaluate_explicitly_pinned_local_candidate_without_any_factory_database()
    {
        var manifestPath = Environment.GetEnvironmentVariable("MEIMAD_AI_EVALUATION_MANIFEST")!;
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var root = manifest.RootElement;
        Assert.Contains(root.GetProperty("status").GetString(), new[] { "CANDIDATE", "APPROVED" });
        var model = root.GetProperty("selectedModel"); var runtime = root.GetProperty("runtime"); var budgets = root.GetProperty("budgets");
        var options = new LocalAiOptions { Enabled = true, ModelId = model.GetProperty("modelId").GetString()!,
            ModelDigest = model.GetProperty("digest").GetString()!, RuntimeVersion = runtime.GetProperty("version").GetString()!,
            MaxInputTokens = budgets.GetProperty("maxInputTokens").GetInt32(), MaxOutputTokens = budgets.GetProperty("maxOutputTokens").GetInt32() };
        options.Validate(); var client = new OllamaModelClient(options);
        var results = new List<object>(); var failed = 0;
        foreach (var (id, language, bundle) in Cases())
        for (var repetition = 0; repetition < 3; repetition++)
        {
            var start = Stopwatch.GetTimestamp(); string? failure = null; ModelResult? result = null;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
                result = await client.CompleteAsync(bundle, timeout.Token);
                var claims = ModelClaimValidator.Validate(result.Content, bundle);
                Assert.Equal(bundle.Claims.OrderBy(x => x.Code), claims.OrderBy(x => x.Code));
            }
            catch (Exception exception) { failed++; failure = exception.GetType().Name; }
            results.Add(new { id, language, repetition, elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                accepted = failure is null, failure, result?.InputTokens, result?.OutputTokens, result?.DurationNanoseconds,
                humanUseful = (bool?)null, coldStart = (bool?)null });
        }
        var output = Environment.GetEnvironmentVariable("MEIMAD_AI_EVALUATION_OUTPUT")!;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { measuredAt = DateTimeOffset.UtcNow,
            options.ModelId, options.ModelDigest, options.RuntimeVersion, cases = 54, repeats = 3, failed,
            status = "SYNTHETIC_ADAPTER_EVALUATION_NOT_HUMAN_OR_DEPLOYMENT_ACCEPTANCE", results }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(0, failed);
    }
}
internal sealed class LocalModelEvaluationFactAttribute : FactAttribute
{
    public LocalModelEvaluationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEIMAD_AI_EVALUATION_MANIFEST"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEIMAD_AI_EVALUATION_OUTPUT")))
            Skip = "Requires an explicitly configured local candidate manifest and output path; no model is downloaded.";
    }
}
