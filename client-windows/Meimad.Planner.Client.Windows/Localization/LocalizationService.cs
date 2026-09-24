using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Meimad.Planner.Client.Windows.Localization;

internal sealed class LocalizationService
{
    private const string DefaultLanguage = "en";
    private const int TranslationCacheLimit = 4096;
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "he", "ru"
    };

    private static readonly Regex PlaceholderPattern = new("\\{[^{}]+\\}", RegexOptions.CultureInvariant);

    // Whole displayed text: any template may match. When a template starts with a value and
    // that value holds earlier sentences, those sentences are translated on their own and the
    // template applies only to the text after the last sentence break.
    private static readonly TemplateMatchOptions WholeTextTemplates = new(0, CaptureCheck.LeadingSentencesTranslated);

    // Sentence pieces and inserted values: only sentence-like templates, and no value may span
    // a sentence break, so short label templates never rewrite names such as "Machine 01".
    private static readonly TemplateMatchOptions EmbeddedTemplates = new(10, CaptureCheck.RejectSentences);

    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> catalogs;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> reverseCatalogs;
    private readonly IReadOnlyDictionary<string, LocalizedTemplateIndex> templates;
    private readonly IReadOnlyDictionary<string, SegmentLookup> segmentLookups;
    private readonly IReadOnlyDictionary<string, Dictionary<string, string>> translationCaches;
    private readonly object persistenceLock = new();
    private readonly string languagePath;
    private string? pendingLanguageToSave;
    private bool persistenceWorkerRunning;
    private string currentLanguage;

    private LocalizationService()
    {
        var initializationStarted = Stopwatch.GetTimestamp();
        catalogs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["he"] = LoadCatalog("he"),
            ["ru"] = LoadCatalog("ru")
        };
        templates = catalogs.ToDictionary(
            item => item.Key,
            item => new LocalizedTemplateIndex(BuildTemplates(item.Value)),
            StringComparer.OrdinalIgnoreCase);
        segmentLookups = catalogs.ToDictionary(
            item => item.Key,
            item => new SegmentLookup(item.Value.Where(value => IsSafeSegment(value.Key, value.Value))),
            StringComparer.OrdinalIgnoreCase);
        translationCaches = catalogs.ToDictionary(
            item => item.Key,
            _ => new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
        reverseCatalogs = catalogs.ToDictionary(
            item => item.Key,
            item => (IReadOnlyDictionary<string, string>)item.Value
                .Where(value => !string.Equals(value.Key, value.Value, StringComparison.Ordinal))
                .GroupBy(value => value.Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
        languagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Meimad Planner",
            "language.txt");
        currentLanguage = LoadLanguage();
        ApplyCulture(currentLanguage);
        InitializationDuration = Stopwatch.GetElapsedTime(initializationStarted);
    }

    internal static LocalizationService Current { get; } = new();

    internal event EventHandler? LanguageChanged;

    internal string CurrentLanguage => currentLanguage;

    internal TimeSpan InitializationDuration { get; }

    internal bool IsRightToLeft => currentLanguage == "he";

    // English is the catalog source language: its interface text is shown untranslated.
    internal bool IsSourceLanguage => currentLanguage == DefaultLanguage;

    internal string Translate(string value)
        => Translate(currentLanguage, value);

    internal string Translate(string language, string value)
    {
        var normalizedLanguage = NormalizeLanguage(language);
        if (normalizedLanguage == DefaultLanguage || string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var cache = translationCaches[normalizedLanguage];
        lock (cache)
        {
            if (cache.TryGetValue(value, out var cached))
            {
                return cached;
            }
        }

        var result = TranslateText(normalizedLanguage, value) ?? value;
        CacheTranslation(cache, value, result);
        return result;
    }

    internal int CatalogEntryCount(string language) =>
        catalogs.GetValueOrDefault(NormalizeLanguage(language))?.Count ?? 0;

    /// <summary>The source-to-translation entries of one language; empty for English.</summary>
    internal IReadOnlyDictionary<string, string> CatalogEntries(string language) =>
        catalogs.GetValueOrDefault(NormalizeLanguage(language))
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Catalog lookup without templates or sentence composition, for structured text such as
    /// file-dialog filters where only a complete known entry may be replaced.
    /// </summary>
    internal bool TryTranslateExact(string value, out string translation)
        => TryTranslateExact(currentLanguage, value, out translation);

    internal bool TryTranslateExact(string language, string value, out string translation)
    {
        translation = value;
        var normalizedLanguage = NormalizeLanguage(language);
        if (normalizedLanguage == DefaultLanguage || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var leadingLength = value.Length - value.TrimStart().Length;
        var trailingLength = value.Length - value.TrimEnd().Length;
        if (!catalogs[normalizedLanguage].TryGetValue(NormalizeUiWhitespace(value.Trim()), out var translated))
        {
            return false;
        }

        translation = value[..leadingLength] + translated + value[(value.Length - trailingLength)..];
        return true;
    }

    internal void SetLanguage(string language, bool persist = true)
    {
        var normalized = NormalizeLanguage(language);
        if (string.Equals(currentLanguage, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        currentLanguage = normalized;
        ApplyCulture(currentLanguage);
        if (persist)
        {
            QueueLanguageSave(currentLanguage);
        }
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void CacheTranslation(Dictionary<string, string> cache, string source, string translation)
    {
        lock (cache)
        {
            if (cache.Count < TranslationCacheLimit)
            {
                cache.TryAdd(source, translation);
            }
        }
    }

    internal bool HasTranslation(string language, string value)
    {
        var normalizedLanguage = NormalizeLanguage(language);
        return normalizedLanguage != DefaultLanguage
            && !string.Equals(Translate(normalizedLanguage, value), value, StringComparison.Ordinal);
    }

    internal string ResolveSource(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var leadingLength = value.Length - value.TrimStart().Length;
        var trailingLength = value.Length - value.TrimEnd().Length;
        var core = NormalizeUiWhitespace(value.Trim());
        foreach (var reverseCatalog in reverseCatalogs.Values)
        {
            if (reverseCatalog.TryGetValue(core, out var source))
            {
                return value[..leadingLength] + source + value[(value.Length - trailingLength)..];
            }
        }

        return value;
    }

    internal bool IsTranslation(string language, string value)
    {
        var normalized = NormalizeLanguage(language);
        return normalized != DefaultLanguage
            && reverseCatalogs[normalized].ContainsKey(NormalizeUiWhitespace(value.Trim()));
    }

    private string LoadLanguage()
    {
        try
        {
            if (File.Exists(languagePath))
            {
                return NormalizeLanguage(File.ReadAllText(languagePath).Trim());
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return NormalizeLanguage(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
    }

    private void SaveLanguage(string language)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(languagePath)!);
            File.WriteAllText(languagePath, language);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void QueueLanguageSave(string language)
    {
        lock (persistenceLock)
        {
            pendingLanguageToSave = language;
            if (persistenceWorkerRunning)
            {
                return;
            }

            persistenceWorkerRunning = true;
        }

        _ = Task.Run(PersistPendingLanguages);
    }

    private void PersistPendingLanguages()
    {
        while (true)
        {
            string language;
            lock (persistenceLock)
            {
                if (pendingLanguageToSave is null)
                {
                    persistenceWorkerRunning = false;
                    return;
                }

                language = pendingLanguageToSave;
                pendingLanguageToSave = null;
            }

            SaveLanguage(language);
        }
    }

    private static string NormalizeLanguage(string? language) =>
        language is not null && SupportedLanguages.Contains(language) ? language.ToLowerInvariant() : DefaultLanguage;

    private static string NormalizeUiWhitespace(string value)
    {
        var previousWasWhitespace = false;
        var requiresNormalization = false;
        foreach (var character in value)
        {
            if (!char.IsWhiteSpace(character))
            {
                previousWasWhitespace = false;
                continue;
            }

            if (character != ' ' || previousWasWhitespace)
            {
                requiresNormalization = true;
                break;
            }
            previousWasWhitespace = true;
        }

        if (!requiresNormalization)
        {
            return value;
        }

        var result = new StringBuilder(value.Length);
        previousWasWhitespace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    result.Append(' ');
                    previousWasWhitespace = true;
                }
                continue;
            }

            result.Append(character);
            previousWasWhitespace = false;
        }
        return result.ToString();
    }

    private static void ApplyCulture(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
    }

    private static IReadOnlyDictionary<string, string> LoadCatalog(string language)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{assembly.GetName().Name}.Localization.strings.{language}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing localization catalog '{resourceName}'.");
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"Localization catalog '{resourceName}' is invalid.");

        // Lookups use the trimmed, whitespace-collapsed source text, so keys are stored the same way.
        var catalog = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
        foreach (var item in values)
        {
            var key = NormalizeUiWhitespace(item.Key.Trim());
            if (key.Length > 0 && !string.IsNullOrWhiteSpace(item.Value))
            {
                catalog.TryAdd(key, item.Value.Trim());
            }
        }
        return catalog;
    }

    private string? TranslateText(string language, string value)
    {
        // Multi-line messages keep their line structure: every line is translated on its own.
        // Only when no line is known is the whole text tried as one whitespace-normalized key.
        if (value.Contains('\n', StringComparison.Ordinal))
        {
            var lines = value.Split('\n');
            var translatedAnyLine = false;
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var carriageReturn = line.EndsWith('\r');
                var content = carriageReturn ? line[..^1] : line;
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var translatedLine = TranslateLine(language, content, allowSegments: true, depth: 0);
                if (translatedLine is null)
                {
                    continue;
                }

                lines[index] = carriageReturn ? translatedLine + "\r" : translatedLine;
                translatedAnyLine = true;
            }

            if (translatedAnyLine)
            {
                return string.Join('\n', lines);
            }
        }

        return TranslateLine(language, value, allowSegments: true, depth: 0);
    }

    private string? TranslateLine(string language, string value, bool allowSegments, int depth)
    {
        var leadingLength = value.Length - value.TrimStart().Length;
        var trailingLength = value.Length - value.TrimEnd().Length;
        var core = NormalizeUiWhitespace(value.Trim());
        if (core.Length == 0)
        {
            return null;
        }

        if (!catalogs[language].TryGetValue(core, out var translated))
        {
            var options = depth == 0 ? WholeTextTemplates : EmbeddedTemplates;
            translated = templates[language].Translate(
                core,
                options,
                capture => TranslateCapture(language, capture, depth + 1, allowSegments: false),
                sentences => TranslateCapture(language, sentences, depth + 1, allowSegments: true));
            if (translated is null && allowSegments)
            {
                translated = TranslateSegments(language, core);
            }
            if (translated is null)
            {
                return null;
            }
        }

        return value[..leadingLength] + translated + value[(value.Length - trailingLength)..];
    }

    private string TranslateCapture(string language, string capture, int depth, bool allowSegments)
    {
        // Values inserted into a message (status tokens, role labels, short composed phrases)
        // are translated only when they are catalog text; identifiers stay untouched. A leading
        // value that holds whole earlier sentences is translated sentence by sentence.
        if (depth > MaximumCaptureDepth || !capture.Any(char.IsLetter))
        {
            return capture;
        }

        return TranslateLine(language, capture, allowSegments, depth) ?? capture;
    }

    private string? TranslateSegments(string language, string value)
    {
        // Some UI messages are assembled from several localized sentences plus live identifiers.
        // Translate the sentence-sized pieces (exact text or sentence templates) and leave the
        // identifiers and user-entered domain values untouched.
        if (value.Length < 12
            || !value.Any(char.IsWhiteSpace)
            || !value.Any(character => character is '.' or ':' or '?' or '!' or ';'))
        {
            return null;
        }

        return segmentLookups[language].Translate(
            value,
            segment => templates[language].Translate(
                segment,
                EmbeddedTemplates,
                capture => TranslateCapture(language, capture, 1, allowSegments: false),
                sentences => sentences));
    }

    private static bool IsSafeSegment(string source, string translation)
    {
        if (source.Length < 12
            || string.Equals(source, translation, StringComparison.Ordinal)
            || source.Contains('{', StringComparison.Ordinal)
            || !source.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var letterCount = source.Count(char.IsLetter);
        return letterCount >= 8 && source.Any(character => character is '.' or ':' or '?' or '!' or ';');
    }

    private static int LastSentenceBreak(string value)
    {
        for (var index = value.Length - 2; index >= 0; index--)
        {
            var character = value[index];
            if (character == '\n'
                || character is '.' or ':' or ';' or '?' or '!' && char.IsWhiteSpace(value[index + 1]))
            {
                return index + 1;
            }
        }
        return -1;
    }

    private static bool ContainsSentenceBreak(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\n')
            {
                return true;
            }
            if (character is '.' or ':' or ';' or '?' or '!'
                && index + 1 < value.Length
                && char.IsWhiteSpace(value[index + 1]))
            {
                return true;
            }
        }
        return false;
    }

    private static IReadOnlyList<LocalizedTemplate> BuildTemplates(IReadOnlyDictionary<string, string> catalog)
    {
        var result = new List<LocalizedTemplate>();
        foreach (var item in catalog)
        {
            if (string.Equals(item.Key, item.Value, StringComparison.Ordinal))
            {
                continue;
            }

            var matches = PlaceholderPattern.Matches(item.Key);
            if (matches.Count == 0)
            {
                continue;
            }

            var placeholders = matches.Select(match => match.Value).ToArray();
            var translationParts = SplitTranslation(item.Value, placeholders);
            if (translationParts is null)
            {
                continue;
            }

            var literals = new List<string>();
            var position = 0;
            foreach (Match match in matches)
            {
                literals.Add(item.Key[position..match.Index]);
                position = match.Index + match.Length;
            }
            literals.Add(item.Key[position..]);
            result.Add(new LocalizedTemplate(
                item.Key[..matches[0].Index],
                item.Key.Length,
                literals.Sum(literal => literal.Length) + matches.Count,
                literals.OrderByDescending(literal => literal.Length).FirstOrDefault() ?? string.Empty,
                literals.Sum(literal => literal.Count(char.IsLetter)),
                literals,
                placeholders,
                translationParts));
        }
        return result
            .OrderByDescending(item => item.Prefix.Length)
            .ThenByDescending(item => item.SourceLength)
            .ToArray();
    }

    private static IReadOnlyList<TemplatePart>? SplitTranslation(string translation, IReadOnlyList<string> placeholders)
    {
        // Every placeholder of the source must appear in the translation, and the translation
        // may only use the source's placeholders; otherwise the entry is not usable as a template.
        var parts = new List<TemplatePart>();
        var used = new bool[placeholders.Count];
        var position = 0;
        foreach (Match match in PlaceholderPattern.Matches(translation))
        {
            var index = -1;
            for (var candidate = 0; candidate < placeholders.Count; candidate++)
            {
                if (string.Equals(placeholders[candidate], match.Value, StringComparison.Ordinal))
                {
                    index = candidate;
                    break;
                }
            }
            if (index < 0)
            {
                return null;
            }

            for (var candidate = 0; candidate < placeholders.Count; candidate++)
            {
                if (string.Equals(placeholders[candidate], match.Value, StringComparison.Ordinal))
                {
                    used[candidate] = true;
                }
            }
            if (match.Index > position)
            {
                parts.Add(new TemplatePart(translation[position..match.Index], -1));
            }
            parts.Add(new TemplatePart(string.Empty, index));
            position = match.Index + match.Length;
        }
        if (position < translation.Length)
        {
            parts.Add(new TemplatePart(translation[position..], -1));
        }
        return used.All(value => value) ? parts : null;
    }

    private const int MaximumCaptureDepth = 2;

    private enum CaptureCheck
    {
        LeadingSentencesTranslated,
        RejectSentences
    }

    private readonly record struct TemplateMatchOptions(int MinimumLiteralLetters, CaptureCheck Check);

    private readonly record struct TemplatePart(string Literal, int PlaceholderIndex);

    private sealed class LocalizedTemplateIndex
    {
        private readonly IReadOnlyDictionary<char, IReadOnlyList<LocalizedTemplate>> prefixedTemplates;
        private readonly IReadOnlyList<LocalizedTemplate> prefixlessTemplates;

        internal LocalizedTemplateIndex(IReadOnlyList<LocalizedTemplate> templates)
        {
            prefixedTemplates = templates
                .Where(template => template.Prefix.Length > 0)
                .GroupBy(template => template.Prefix[0])
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<LocalizedTemplate>)group.ToArray());
            prefixlessTemplates = templates
                .Where(template => template.Prefix.Length == 0)
                .ToArray();
        }

        internal string? Translate(
            string value,
            TemplateMatchOptions options,
            Func<string, string> translateValue,
            Func<string, string> translateSentences)
        {
            if (value.Length > 0
                && prefixedTemplates.TryGetValue(value[0], out var candidates))
            {
                var translated = Translate(candidates, value, options, translateValue, translateSentences);
                if (translated is not null)
                {
                    return translated;
                }
            }

            return Translate(prefixlessTemplates, value, options, translateValue, translateSentences);
        }

        private static string? Translate(
            IReadOnlyList<LocalizedTemplate> templates,
            string value,
            TemplateMatchOptions options,
            Func<string, string> translateValue,
            Func<string, string> translateSentences)
        {
            foreach (var template in templates)
            {
                if (template.LiteralLetterCount < options.MinimumLiteralLetters
                    || value.Length < template.MinimumLength
                    || template.Prefix.Length > 0
                    && !value.StartsWith(template.Prefix, StringComparison.Ordinal)
                    || template.RequiredLiteral.Length > 0
                    && !value.Contains(template.RequiredLiteral, StringComparison.Ordinal))
                {
                    continue;
                }

                var captures = MatchTemplate(template, value);
                if (captures is null)
                {
                    continue;
                }

                var translatedCaptures = new string?[captures.Length];
                var leading = string.Empty;
                if (options.Check == CaptureCheck.RejectSentences)
                {
                    if (captures.Any(ContainsSentenceBreak))
                    {
                        continue;
                    }
                }
                else if (SpansFollowingSentence(template, captures))
                {
                    continue;
                }
                else if (template.Prefix.Length == 0 && ContainsSentenceBreak(captures[0]))
                {
                    // "{0} Machine backlog(s) affected." can match a whole composed message; the
                    // value then carries the earlier sentences. Unless that value is itself one
                    // known text, only the part after its last sentence break belongs here.
                    var whole = translateValue(captures[0]);
                    if (!string.Equals(whole, captures[0], StringComparison.Ordinal))
                    {
                        translatedCaptures[0] = whole;
                    }
                    else
                    {
                        var split = LastSentenceBreak(captures[0]);
                        var tail = captures[0][split..];
                        var trimmedTail = tail.TrimStart();
                        if (trimmedTail.Length == 0)
                        {
                            continue;
                        }

                        leading = translateSentences(captures[0][..split]) + tail[..(tail.Length - trimmedTail.Length)];
                        captures[0] = trimmedTail;
                    }
                }

                var result = new StringBuilder(leading, template.SourceLength + leading.Length + 16);
                foreach (var part in template.TranslationParts)
                {
                    if (part.PlaceholderIndex < 0)
                    {
                        result.Append(part.Literal);
                        continue;
                    }

                    var index = part.PlaceholderIndex;
                    translatedCaptures[index] ??= translateValue(captures[index]);
                    result.Append(translatedCaptures[index]);
                }
                return result.ToString();
            }

            return null;
        }

        // A value may carry whole sentences only at the end of the text or when real sentence
        // text follows it. "DAYLIGHT HOURS ({0}-{1} {2})" must not swallow "...; DARK HOURS ...".
        private static bool SpansFollowingSentence(LocalizedTemplate template, string[] captures)
        {
            for (var index = 0; index < captures.Length; index++)
            {
                if (index == 0 && template.Prefix.Length == 0 || !ContainsSentenceBreak(captures[index]))
                {
                    continue;
                }

                var following = template.Literals[index + 1];
                if (following.Length > 0 && following.Count(char.IsLetter) < 10)
                {
                    return true;
                }
            }
            return false;
        }

        private static string[]? MatchTemplate(LocalizedTemplate template, string value)
        {
            var captureStarts = new int[template.Placeholders.Count];
            var captureLengths = new int[template.Placeholders.Count];
            if (!MatchPlaceholder(0, template.Literals[0].Length))
            {
                return null;
            }

            var captures = new string[template.Placeholders.Count];
            for (var index = 0; index < captures.Length; index++)
            {
                captures[index] = value.Substring(captureStarts[index], captureLengths[index]);
            }
            return captures;

            bool MatchPlaceholder(int placeholderIndex, int position)
            {
                var nextLiteral = template.Literals[placeholderIndex + 1];
                if (placeholderIndex == template.Placeholders.Count - 1)
                {
                    var literalStart = value.Length - nextLiteral.Length;
                    if (literalStart <= position
                        || !value.AsSpan(literalStart).SequenceEqual(nextLiteral.AsSpan()))
                    {
                        return false;
                    }

                    captureStarts[placeholderIndex] = position;
                    captureLengths[placeholderIndex] = literalStart - position;
                    return true;
                }

                if (nextLiteral.Length == 0)
                {
                    for (var captureEnd = position + 1; captureEnd < value.Length; captureEnd++)
                    {
                        captureStarts[placeholderIndex] = position;
                        captureLengths[placeholderIndex] = captureEnd - position;
                        if (MatchPlaceholder(placeholderIndex + 1, captureEnd))
                        {
                            return true;
                        }
                    }
                    return false;
                }

                var searchPosition = position + 1;
                while (searchPosition <= value.Length - nextLiteral.Length)
                {
                    var literalStart = value.IndexOf(nextLiteral, searchPosition, StringComparison.Ordinal);
                    if (literalStart < 0)
                    {
                        return false;
                    }

                    captureStarts[placeholderIndex] = position;
                    captureLengths[placeholderIndex] = literalStart - position;
                    if (MatchPlaceholder(placeholderIndex + 1, literalStart + nextLiteral.Length))
                    {
                        return true;
                    }
                    searchPosition = literalStart + 1;
                }
                return false;
            }
        }
    }

    private sealed class SegmentLookup
    {
        private readonly IReadOnlyDictionary<string, string> translations;

        internal SegmentLookup(IEnumerable<KeyValuePair<string, string>> segments)
        {
            translations = segments.ToDictionary(
                segment => segment.Key,
                segment => segment.Value,
                StringComparer.Ordinal);
        }

        internal string? Translate(string value, Func<string, string?> translateTemplate)
        {
            var boundaries = new List<int> { 0 };
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] is '.' or ':' or '?' or '!' or ';' or '\n')
                {
                    boundaries.Add(index + 1);
                }
            }
            if (boundaries[^1] != value.Length)
            {
                boundaries.Add(value.Length);
            }

            var matches = new List<SegmentReplacement>();
            for (var startBoundary = 0; startBoundary < boundaries.Count - 1; startBoundary++)
            {
                for (var endBoundary = startBoundary + 1; endBoundary < boundaries.Count; endBoundary++)
                {
                    var start = boundaries[startBoundary];
                    var end = boundaries[endBoundary];
                    while (start < end && char.IsWhiteSpace(value[start]))
                    {
                        start++;
                    }
                    while (end > start && char.IsWhiteSpace(value[end - 1]))
                    {
                        end--;
                    }

                    if (end - start < 12)
                    {
                        continue;
                    }

                    var segment = value[start..end];
                    if (!translations.TryGetValue(segment, out var translation))
                    {
                        translation = translateTemplate(segment);
                        if (translation is null)
                        {
                            continue;
                        }
                    }
                    matches.Add(new SegmentReplacement(start, end - start, translation));
                }
            }

            if (matches.Count == 0)
            {
                return null;
            }

            var occupied = new bool[value.Length];
            var selected = new List<SegmentReplacement>();
            foreach (var match in matches
                         .OrderByDescending(match => match.Length)
                         .ThenBy(match => match.Start))
            {
                var overlaps = false;
                for (var index = match.Start; index < match.Start + match.Length; index++)
                {
                    if (occupied[index])
                    {
                        overlaps = true;
                        break;
                    }
                }
                if (overlaps)
                {
                    continue;
                }

                selected.Add(match);
                for (var index = match.Start; index < match.Start + match.Length; index++)
                {
                    occupied[index] = true;
                }
            }

            var result = new StringBuilder(value.Length);
            var position = 0;
            foreach (var match in selected.OrderBy(match => match.Start))
            {
                result.Append(value, position, match.Start - position);
                result.Append(match.Translation);
                position = match.Start + match.Length;
            }
            result.Append(value, position, value.Length - position);
            return result.ToString();
        }

        private readonly record struct SegmentReplacement(
            int Start,
            int Length,
            string Translation);
    }

    private sealed record LocalizedTemplate(
        string Prefix,
        int SourceLength,
        int MinimumLength,
        string RequiredLiteral,
        int LiteralLetterCount,
        IReadOnlyList<string> Literals,
        IReadOnlyList<string> Placeholders,
        IReadOnlyList<TemplatePart> TranslationParts);
}
