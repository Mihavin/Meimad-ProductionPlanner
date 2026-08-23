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
            ["he"] = LoadCatalog("he", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Cases"] = "פריטים",
                ["Planning Board"] = "לוח תכנון",
                ["Setup"] = "הגדרות",
                ["Timeline"] = "ציר זמן",
                ["CASE POOL"] = "מאגר פריטים",
                ["Refresh Case"] = "רענון פריט",
                ["Save Machine"] = "שמירת מכונה",
                ["Flip Z"] = "היפוך Z",
                ["Wireframe"] = "תצוגת שלד",
                ["Accepts: {0}"] = "מקבלת: {0}",
                ["{0} queued"] = "{0} בתור",
                ["{operation} is intended for {requiredType}. You selected {machine} ({selectedType}). This does not change the operation route. Confirm only when this exception is intentional; the Server will audit your identity, time, both types, and reason."] =
                    "{operation} מיועדת עבור {requiredType}. בחרת {machine} ({selectedType}). הדבר אינו משנה את מסלול הפעולה. יש לאשר רק כאשר החריגה מכוונת; השרת יתעד את זהותך, הזמן, שני הסוגים והסיבה."
            }),
            ["ru"] = LoadCatalog("ru", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Cases"] = "Изделия",
                ["Planning Board"] = "Доска планирования",
                ["Setup"] = "Настройки",
                ["Timeline"] = "Шкала времени",
                ["Accepts: {0}"] = "Допускает: {0}",
                ["{0} queued"] = "В очереди: {0}",
                ["{operation} is intended for {requiredType}. You selected {machine} ({selectedType}). This does not change the operation route. Confirm only when this exception is intentional; the Server will audit your identity, time, both types, and reason."] =
                    "{operation} предназначена для {requiredType}. Вы выбрали {machine} ({selectedType}). Это не изменяет маршрут операции. Подтверждайте только намеренное исключение; сервер зарегистрирует вашу личность, время, оба типа и причину."
            })
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

        var leadingLength = value.Length - value.TrimStart().Length;
        var trailingLength = value.Length - value.TrimEnd().Length;
        var core = NormalizeUiWhitespace(value.Trim());
        if (!catalogs[normalizedLanguage].TryGetValue(core, out var translated))
        {
            translated = TranslateTemplate(normalizedLanguage, core);
            if (translated is null)
            {
                translated = TranslateSegments(normalizedLanguage, core);
                if (translated is null)
                {
                    CacheTranslation(cache, value, value);
                    return value;
                }
            }
        }

        var result = value[..leadingLength] + translated + value[(value.Length - trailingLength)..];
        CacheTranslation(cache, value, result);
        return result;
    }

    internal int CatalogEntryCount(string language) =>
        catalogs.GetValueOrDefault(NormalizeLanguage(language))?.Count ?? 0;

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

    private static IReadOnlyDictionary<string, string> LoadCatalog(
        string language,
        IReadOnlyDictionary<string, string> overrides)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{assembly.GetName().Name}.Localization.strings.{language}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing localization catalog '{resourceName}'.");
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"Localization catalog '{resourceName}' is invalid.");
        foreach (var item in overrides)
        {
            values[item.Key] = item.Value;
        }
        return values;
    }

    private string? TranslateTemplate(string language, string value)
        => templates[language].Translate(value);

    private string? TranslateSegments(string language, string value)
    {
        // Some UI messages are assembled from several localized string literals plus live
        // identifiers. Translate the sentence-sized literals while leaving the identifiers
        // and user-entered domain values untouched.
        if (value.Length < 12
            || !value.Any(char.IsWhiteSpace)
            || !value.Any(character => character is '.' or ':' or '?' or '!'))
        {
            return null;
        }

        return segmentLookups[language].Translate(value);
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
        return letterCount >= 8 && source.Any(character => character is '.' or ':' or '?' or '!');
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

            var matches = Regex.Matches(item.Key, "\\{[^{}]+\\}");
            if (matches.Count == 0 || matches.Cast<Match>().Any(match => !item.Value.Contains(match.Value, StringComparison.Ordinal)))
            {
                continue;
            }

            var literals = new List<string>();
            var position = 0;
            for (var index = 0; index < matches.Count; index++)
            {
                var match = matches[index];
                var literal = item.Key[position..match.Index];
                literals.Add(literal);
                position = match.Index + match.Length;
            }
            var suffix = item.Key[position..];
            literals.Add(suffix);
            result.Add(new LocalizedTemplate(
                item.Key[..matches[0].Index],
                item.Key.Length,
                literals.Sum(literal => literal.Length) + matches.Count,
                literals.OrderByDescending(literal => literal.Length).FirstOrDefault() ?? string.Empty,
                literals,
                matches.Cast<Match>().Select(match => match.Value).ToArray(),
                item.Value));
        }
        return result
            .OrderByDescending(item => item.Prefix.Length)
            .ThenByDescending(item => item.SourceLength)
            .ToArray();
    }

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

        internal string? Translate(string value)
        {
            if (value.Length > 0
                && prefixedTemplates.TryGetValue(value[0], out var candidates))
            {
                var translated = Translate(candidates, value);
                if (translated is not null)
                {
                    return translated;
                }
            }

            return Translate(prefixlessTemplates, value);
        }

        private static string? Translate(IReadOnlyList<LocalizedTemplate> templates, string value)
        {
            foreach (var template in templates)
            {
                if (value.Length < template.MinimumLength
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

                var result = template.Translation;
                for (var index = 0; index < template.Placeholders.Count; index++)
                {
                    result = result.Replace(
                        template.Placeholders[index],
                        captures[index],
                        StringComparison.Ordinal);
                }
                return result;
            }

            return null;
        }

        private static IReadOnlyList<string>? MatchTemplate(LocalizedTemplate template, string value)
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

        internal string? Translate(string value)
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

                    if (end - start < 12
                        || !translations.TryGetValue(value[start..end], out var translation))
                    {
                        continue;
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
        IReadOnlyList<string> Literals,
        IReadOnlyList<string> Placeholders,
        string Translation);
}
