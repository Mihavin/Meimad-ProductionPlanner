using System.Text.Json;
using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows.Tests.Localization;

// TEMPORARY audit harness (not committed): translates candidate texts collected from the sources
// and writes the ones a language leaves unchanged or only partly translated.
public sealed class TemporaryCoverageAudit
{
    private sealed record Candidate(string text, string key, string source, string origin, string context);

    [Fact]
    public void Write_untranslated_candidates()
    {
        var input = Environment.GetEnvironmentVariable("MEIMAD_L10N_CANDIDATES");
        var output = Environment.GetEnvironmentVariable("MEIMAD_L10N_RESULT");
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var candidates = JsonSerializer.Deserialize<List<Candidate>>(File.ReadAllText(input))!;
        var rows = new List<object>();
        foreach (var candidate in candidates)
        {
            var he = LocalizationService.Current.Translate("he", candidate.text);
            var ru = LocalizationService.Current.Translate("ru", candidate.text);
            rows.Add(new
            {
                candidate.text,
                candidate.key,
                candidate.source,
                candidate.origin,
                candidate.context,
                he,
                ru,
                heExact = LocalizationService.Current.TryTranslateExact("he", candidate.text, out _),
                heChanged = !string.Equals(he, candidate.text, StringComparison.Ordinal),
                ruChanged = !string.Equals(ru, candidate.text, StringComparison.Ordinal)
            });
        }

        File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        }));
    }
}
