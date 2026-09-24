using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows.Tests.Localization;

public sealed class LocalizationCatalogTests
{
    private static readonly Regex Placeholder = new("\\{[^{}]+\\}", RegexOptions.CultureInvariant);
    private static readonly Regex LatinWord = new("[A-Za-z]{2,}", RegexOptions.CultureInvariant);

    // XAML literals that are deliberately shown as-is in every language: protocol and product
    // names, control-panel tokens, axis letters and units.
    private static readonly HashSet<string> UntranslatedXamlLiterals = new(StringComparer.Ordinal)
    {
        "English", "עברית", "Русский", "MEIMAD", "Meimad Production Planner", "SHA-256"
    };

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly string[] LocalizedAttributes =
    [
        "Text", "Content", "Header", "Title", "ToolTip", "AutomationProperties.Name", "AutomationProperties.HelpText"
    ];
    private static readonly HashSet<string> TextContentElements = new(StringComparer.Ordinal)
    {
        "TextBlock", "Run", "Label", "Button", "CheckBox", "RadioButton", "ComboBoxItem", "Hyperlink", "Bold", "Italic", "Span"
    };
    private static readonly HashSet<string> UserInputElements = new(StringComparer.Ordinal)
    {
        "TextBox", "PasswordBox", "RichTextBox"
    };

    [Theory]
    [InlineData("he")]
    [InlineData("ru")]
    public void Catalog_entries_use_normalized_keys_and_keep_every_placeholder(string language)
    {
        var problems = new List<string>();
        foreach (var (key, value) in LoadCatalog(language))
        {
            if (!string.Equals(key, NormalizeWhitespace(key), StringComparison.Ordinal))
            {
                problems.Add($"key is not whitespace-normalized: {key}");
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                problems.Add($"empty translation: {key}");
            }
            if (!Placeholder.Matches(key).Select(match => match.Value).Order(StringComparer.Ordinal)
                    .SequenceEqual(Placeholder.Matches(value).Select(match => match.Value).Order(StringComparer.Ordinal)))
            {
                problems.Add($"placeholders differ: {key} => {value}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(40)));
    }

    [Theory]
    [InlineData("he", "\u0590", "\u05FF")]
    [InlineData("ru", "\u0400", "\u04FF")]
    public void Catalog_translations_are_written_in_the_target_script(string language, string first, string last)
    {
        var problems = LoadCatalog(language)
            .Where(item => LatinWord.IsMatch(Placeholder.Replace(item.Key, string.Empty)))
            .Where(item => !item.Value.Any(character => character >= first[0] && character <= last[0]))
            .Select(item => $"{item.Key} => {item.Value}")
            .ToArray();

        Assert.True(problems.Length == 0, string.Join(Environment.NewLine, problems.Take(40)));
    }

    [Theory]
    [InlineData("he")]
    [InlineData("ru")]
    public void Catalog_contains_no_source_code_fragments(string language)
    {
        string[] codeMarkers = ["=>", "??", "?.", "\" +", "$\"", "();", "string.", ".ToString("];
        var problems = LoadCatalog(language).Keys
            .Where(key => codeMarkers.Any(marker =>
                Placeholder.Replace(key, string.Empty).Contains(marker, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(problems.Length == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [InlineData("he")]
    [InlineData("ru")]
    public void Multi_line_messages_keep_their_line_breaks(string language)
    {
        const string path = @"C:\Models\Bracket-01.step";
        var translated = LocalizationService.Current.Translate(
            language,
            $"Some linked files could not be loaded:\n{path}");

        var lines = translated.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("could not be loaded", lines[0], StringComparison.Ordinal);
        Assert.Equal(path, lines[1]);
    }

    [Theory]
    [InlineData("he")]
    [InlineData("ru")]
    public void Composed_import_receipts_are_translated_sentence_by_sentence(string language)
    {
        const string receipt =
            "Import L-7: created 1 Case(s), 2 Order(s), 3 Batch(es), 4 Batch Operation(s), and 5 assignment(s); "
            + "matched/unchanged 6 Case(s), 0 Order(s), 0 Batch(es), 0 Batch Operation(s), and 0 assignment(s). "
            + "1 selected source row(s) skipped; 2 Operation(s) left in Pool; 3 Machine backlog(s) affected.";

        var translated = LocalizationService.Current.Translate(language, receipt);

        Assert.Contains("L-7", translated, StringComparison.Ordinal);
        foreach (var english in new[] { "created", "matched/unchanged", "skipped", "left in Pool", "affected" })
        {
            Assert.DoesNotContain(english, translated, StringComparison.Ordinal);
        }
        // The last sentence must stay last: its value is "3", not the earlier sentences.
        Assert.True(
            translated.IndexOf("L-7", StringComparison.Ordinal) < translated.LastIndexOf('3'),
            translated);
    }

    [Theory]
    [InlineData("he", "עובד בקרת איכות")]
    [InlineData("ru", "контролёра ОТК")]
    public void Inserted_labels_are_translated_without_touching_identifiers(string language, string role)
    {
        var translated = LocalizationService.Current.Translate(
            language,
            "Waiting for a QA worker; operation 'op-17' received the resource first because its Work Finish Date 2026-03-04 is earlier.");

        Assert.Contains(role, translated, StringComparison.Ordinal);
        Assert.Contains("'op-17'", translated, StringComparison.Ordinal);
        Assert.Contains("2026-03-04", translated, StringComparison.Ordinal);
        Assert.DoesNotContain("Waiting for", translated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("he")]
    [InlineData("ru")]
    public void A_known_leading_message_is_translated_before_its_reason(string language)
    {
        var translated = LocalizationService.Current.Translate(
            language,
            "Offset Loader executed; setup started. Reason: operator restarted the program");

        Assert.DoesNotContain("Offset Loader executed", translated, StringComparison.Ordinal);
        Assert.DoesNotContain("Reason:", translated, StringComparison.Ordinal);
        Assert.EndsWith("operator restarted the program", translated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("he")]
    [InlineData("ru")]
    public void File_dialog_filters_translate_descriptions_and_keep_patterns(string language)
    {
        const string known = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*";
        const string composed = "Unknown data|*.dat|All files|*.*";

        var translatedKnown = LocalizedFileDialogs.TranslateFilter(known, language).Split('|');
        var translatedComposed = LocalizedFileDialogs.TranslateFilter(composed, language).Split('|');

        Assert.Equal(4, translatedKnown.Length);
        Assert.NotEqual("Image files", translatedKnown[0]);
        Assert.Equal("*.png;*.jpg;*.jpeg;*.bmp;*.gif", translatedKnown[1]);
        Assert.Equal("*.*", translatedKnown[3]);
        Assert.Equal(["Unknown data", "*.dat"], translatedComposed[..2]);
        Assert.NotEqual("All files", translatedComposed[2]);
        Assert.Equal("*.*", translatedComposed[3]);
    }

    [Theory]
    [InlineData("he")]
    [InlineData("ru")]
    public void Every_literal_xaml_label_has_a_translation(string language)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var literal in XamlLiterals())
        {
            if (UntranslatedXamlLiterals.Contains(literal)
                || !LocalizationService.Current.HasTranslation(language, literal))
            {
                if (!UntranslatedXamlLiterals.Contains(literal))
                {
                    missing.Add(literal);
                }
            }
        }

        Assert.True(missing.Count == 0, $"{missing.Count} XAML literals have no {language} translation:{Environment.NewLine}"
            + string.Join(Environment.NewLine, missing));
    }

    private static IEnumerable<string> XamlLiterals()
    {
        var clientRoot = Path.Combine(FindRepositoryRoot(), "client-windows", "Meimad.Planner.Client.Windows");
        foreach (var file in Directory.EnumerateFiles(clientRoot, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var document = XDocument.Load(file);
            foreach (var element in document.Descendants())
            {
                if (element.Name.Namespace != Presentation || UserInputElements.Contains(element.Name.LocalName))
                {
                    continue;
                }

                foreach (var attribute in element.Attributes())
                {
                    var name = attribute.Name.LocalName;
                    if (LocalizedAttributes.Contains(name) && Literal(attribute.Value) is { } literal)
                    {
                        yield return literal;
                    }
                }

                if (TextContentElements.Contains(element.Name.LocalName))
                {
                    foreach (var text in element.Nodes().OfType<XText>())
                    {
                        if (Literal(text.Value) is { } literal)
                        {
                            yield return literal;
                        }
                    }
                }
            }
        }
    }

    private static string? Literal(string value)
    {
        if (value.StartsWith("{}", StringComparison.Ordinal))
        {
            value = value[2..];
        }
        else if (value.StartsWith('{'))
        {
            return null;
        }

        var normalized = NormalizeWhitespace(value);
        return normalized.Any(char.IsLetter) ? normalized : null;
    }

    private static string NormalizeWhitespace(string value) =>
        Regex.Replace(value.Trim(), "\\s+", " ");

    private static Dictionary<string, string> LoadCatalog(string language)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "client-windows",
            "Meimad.Planner.Client.Windows",
            "Localization",
            $"strings.{language}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new InvalidDataException(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root containing AGENTS.md was not found.");
    }
}
