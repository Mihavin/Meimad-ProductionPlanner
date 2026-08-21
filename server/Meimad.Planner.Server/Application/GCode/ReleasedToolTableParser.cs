using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meimad.Planner.Server.Domain.GCode;

namespace Meimad.Planner.Server.Application.GCode;

internal static class ReleasedToolTableParser
{
    private const int MaximumRows = 2000;
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(5);

    internal static async Task<ReleasedToolTableDefinition> ParseAsync(
        string absolutePath,
        string originalFileName,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        IReadOnlyList<ReleasedTool> tools = extension switch
        {
            ".json" => await ParseJsonAsync(absolutePath, cancellationToken),
            ".csv" or ".txt" => await ParseCsvAsync(absolutePath, cancellationToken),
            ".mht" or ".mhtml" => await ParseCimatronMhtAsync(absolutePath, cancellationToken),
            _ => throw Validation(
                "unsupported_tool_table_format",
                "Released tool tables must use structured CSV/JSON or Cimatron MHT format.")
        };

        var requiredToolCount = tools
            .Where(tool => tool.IsActive && tool.IsRequired && tool.RequiresMagazinePosition)
            .Select(tool => tool.ToolIdentifier.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return new ReleasedToolTableDefinition(tools, requiredToolCount);
    }

    private static async Task<IReadOnlyList<ReleasedTool>> ParseCimatronMhtAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var archive = await File.ReadAllTextAsync(path, cancellationToken);
        var html = ExtractHtmlPart(archive);
        var tools = new List<ReleasedTool>();
        var rows = Regex.Matches(
            html,
            @"<tr\b[^>]*>(?<content>.*?)</tr\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            PatternTimeout);
        foreach (Match row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cells = Regex.Matches(
                row.Groups["content"].Value,
                @"<t[dh]\b[^>]*>(?<content>.*?)</t[dh]\s*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                PatternTimeout);
            if (cells.Count < 2)
            {
                continue;
            }

            var identifier = HtmlText(cells[0].Groups["content"].Value);
            if (!Regex.IsMatch(
                    identifier,
                    @"^T\s*\d+[\p{L}\p{N}._/-]*$",
                    RegexOptions.IgnoreCase,
                    PatternTimeout))
            {
                continue;
            }

            if (tools.Count >= MaximumRows)
            {
                throw Validation("too_many_tool_rows", $"A released tool table may contain at most {MaximumRows} rows.");
            }

            identifier = Required(identifier, "toolIdentifier", 80, tools.Count + 1);
            var description = Optional(HtmlText(cells[1].Groups["content"].Value), 240) ?? identifier;
            tools.Add(new ReleasedTool(
                Guid.NewGuid().ToString("N"),
                tools.Count + 1,
                identifier,
                description,
                IsRequired: true,
                RequiresMagazinePosition: true,
                IsActive: true,
                MagazinePosition: null));
        }

        if (tools.Count == 0)
        {
            throw Validation(
                "cimatron_tool_rows_required",
                "The Cimatron MHT tool table does not contain recognizable tool rows.");
        }

        return tools;
    }

    private static string ExtractHtmlPart(string archive)
    {
        var boundary = Regex.Match(
            archive,
            """boundary\s*=\s*(?:"(?<value>[^"]+)"|(?<value>[^;\r\n]+))""",
            RegexOptions.IgnoreCase,
            PatternTimeout);
        if (!boundary.Success)
        {
            throw Validation("invalid_cimatron_mht", "The Cimatron MHT file has no MIME boundary.");
        }

        var marker = "--" + boundary.Groups["value"].Value.Trim();
        foreach (var part in archive.Split(marker, StringSplitOptions.None))
        {
            var separator = Regex.Match(part, @"\r?\n\r?\n", RegexOptions.None, PatternTimeout);
            if (!separator.Success)
            {
                continue;
            }

            var headers = part[..separator.Index];
            if (!Regex.IsMatch(headers, @"Content-Type\s*:\s*text/html\b", RegexOptions.IgnoreCase, PatternTimeout))
            {
                continue;
            }

            var body = part[(separator.Index + separator.Length)..].TrimEnd('\r', '\n');
            if (Regex.IsMatch(
                    headers,
                    @"Content-Transfer-Encoding\s*:\s*quoted-printable\b",
                    RegexOptions.IgnoreCase,
                    PatternTimeout))
            {
                return DecodeQuotedPrintable(body);
            }

            if (Regex.IsMatch(
                    headers,
                    @"Content-Transfer-Encoding\s*:\s*base64\b",
                    RegexOptions.IgnoreCase,
                    PatternTimeout))
            {
                try
                {
                    return Encoding.UTF8.GetString(Convert.FromBase64String(
                        Regex.Replace(body, @"\s+", string.Empty, RegexOptions.None, PatternTimeout)));
                }
                catch (FormatException)
                {
                    throw Validation("invalid_cimatron_mht", "The Cimatron MHT HTML part contains invalid Base64 data.");
                }
            }

            return body;
        }

        throw Validation("invalid_cimatron_mht", "The Cimatron MHT file has no HTML tool-table part.");
    }

    private static string DecodeQuotedPrintable(string value)
    {
        using var bytes = new MemoryStream(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '=' && index + 1 < value.Length && value[index + 1] == '\n')
            {
                index++;
                continue;
            }

            if (character == '=' && index + 2 < value.Length
                                 && value[index + 1] == '\r' && value[index + 2] == '\n')
            {
                index += 2;
                continue;
            }

            if (character == '=' && index + 2 < value.Length
                                 && Hex(value[index + 1]) is var high && high >= 0
                                 && Hex(value[index + 2]) is var low && low >= 0)
            {
                bytes.WriteByte((byte)((high << 4) | low));
                index += 2;
                continue;
            }

            if (character <= 0x7f)
            {
                bytes.WriteByte((byte)character);
            }
            else
            {
                bytes.Write(Encoding.UTF8.GetBytes(character.ToString()));
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static int Hex(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1
    };

    private static string HtmlText(string value)
    {
        var withoutTags = Regex.Replace(
            value,
            @"<[^>]+>",
            " ",
            RegexOptions.Singleline,
            PatternTimeout);
        return Regex.Replace(
                WebUtility.HtmlDecode(withoutTags).Replace('\u00a0', ' '),
                @"\s+",
                " ",
                RegexOptions.None,
                PatternTimeout)
            .Trim();
    }

    private static async Task<IReadOnlyList<ReleasedTool>> ParseCsvAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        var nonEmpty = lines
            .Select((value, index) => (Value: value, Line: index + 1))
            .Where(value => !string.IsNullOrWhiteSpace(value.Value))
            .ToArray();
        if (nonEmpty.Length == 0)
        {
            return [];
        }

        var headers = ParseCsvLine(nonEmpty[0].Value)
            .Select((value, index) => (Name: NormalizeName(value), Index: index))
            .Where(value => value.Name.Length > 0)
            .GroupBy(value => value.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.Ordinal);
        var identifierIndex = FindIndex(headers, "toolidentifier", "toolid", "tool", "identifier")
            ?? throw Validation(
                "tool_identifier_column_required",
                "The CSV tool table requires a ToolIdentifier, ToolId, or Tool column.");
        var descriptionIndex = FindIndex(headers, "description", "tooldescription", "desc");
        var requiredIndex = FindIndex(headers, "isrequired", "required", "requirementstate", "requirement");
        var activeIndex = FindIndex(headers, "isactive", "active", "status");
        var magazineRequiredIndex = FindIndex(
            headers,
            "requiresmagazineposition",
            "magazinepositionrequired",
            "requiresmagazine",
            "magazinerequired");
        var positionIndex = FindIndex(headers, "magazineposition", "position", "pocket");

        var result = new List<ReleasedTool>();
        foreach (var source in nonEmpty.Skip(1))
        {
            if (result.Count >= MaximumRows)
            {
                throw Validation("too_many_tool_rows", $"A released tool table may contain at most {MaximumRows} rows.");
            }

            var columns = ParseCsvLine(source.Value);
            var identifier = Required(Value(columns, identifierIndex), "toolIdentifier", 80, source.Line);
            var description = Optional(Value(columns, descriptionIndex), 240) ?? identifier;
            var isRequired = ParseState(Value(columns, requiredIndex), defaultValue: true, "required", "optional", source.Line);
            var isActive = ParseState(Value(columns, activeIndex), defaultValue: true, "active", "inactive", source.Line);
            var position = Optional(Value(columns, positionIndex), 80);
            var requiresMagazine = ParseMagazineRequirement(
                Value(columns, magazineRequiredIndex), position, source.Line);
            result.Add(new ReleasedTool(
                Guid.NewGuid().ToString("N"),
                result.Count + 1,
                identifier,
                description,
                isRequired,
                requiresMagazine,
                isActive,
                position));
        }

        return result;
    }

    private static async Task<IReadOnlyList<ReleasedTool>> ParseJsonAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(
            input,
            new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = true },
            cancellationToken);
        var array = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement,
            JsonValueKind.Object when TryProperty(document.RootElement, out var tools, "tools", "toolTable")
                                      && tools.ValueKind == JsonValueKind.Array => tools,
            _ => throw Validation(
                "invalid_tool_table_json",
                "JSON tool tables must be an array or an object containing a tools array.")
        };

        if (array.GetArrayLength() > MaximumRows)
        {
            throw Validation("too_many_tool_rows", $"A released tool table may contain at most {MaximumRows} rows.");
        }

        var result = new List<ReleasedTool>();
        foreach (var item in array.EnumerateArray())
        {
            var row = result.Count + 1;
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Validation("invalid_tool_row", $"Tool row {row} must be a JSON object.");
            }

            var identifier = Required(Text(item, "toolIdentifier", "toolId", "tool", "identifier"), "toolIdentifier", 80, row);
            var description = Optional(Text(item, "description", "toolDescription", "desc"), 240) ?? identifier;
            var isRequired = JsonState(item, true, "required", "optional", row, "isRequired", "required", "requirementState");
            var isActive = JsonState(item, true, "active", "inactive", row, "isActive", "active", "status");
            var position = Optional(Text(item, "magazinePosition", "position", "pocket"), 80);
            var magazineValue = Element(item, "requiresMagazinePosition", "magazinePositionRequired", "requiresMagazine");
            var requiresMagazine = magazineValue.HasValue
                ? ParseBooleanLike(magazineValue.Value, "magazine-position requirement", row)
                : PositionRequiresMagazine(position);
            result.Add(new ReleasedTool(
                Guid.NewGuid().ToString("N"), row, identifier, description,
                isRequired, requiresMagazine, isActive, position));
        }

        return result;
    }

    private static IReadOnlyList<string> ParseCsvLine(string value)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '"')
            {
                if (quoted && index + 1 < value.Length && value[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        if (quoted)
        {
            throw Validation("invalid_tool_table_csv", "The CSV tool table contains an unterminated quoted value.");
        }

        result.Add(current.ToString().Trim());
        return result;
    }

    private static bool JsonState(
        JsonElement item,
        bool defaultValue,
        string trueWord,
        string falseWord,
        int row,
        params string[] names)
    {
        var value = Element(item, names);
        return value.HasValue
            ? ParseBooleanLike(value.Value, $"{trueWord}/{falseWord} state", row, trueWord, falseWord)
            : defaultValue;
    }

    private static bool ParseMagazineRequirement(string? value, string? position, int row) =>
        string.IsNullOrWhiteSpace(value)
            ? PositionRequiresMagazine(position)
            : ParseBooleanLike(value, "magazine-position requirement", row);

    private static bool PositionRequiresMagazine(string? position) =>
        position?.Trim().ToLowerInvariant() is not ("none" or "external" or "n/a" or "na" or "-");

    private static bool ParseState(
        string? value,
        bool defaultValue,
        string trueWord,
        string falseWord,
        int row) => string.IsNullOrWhiteSpace(value)
        ? defaultValue
        : ParseBooleanLike(value, $"{trueWord}/{falseWord} state", row, trueWord, falseWord);

    private static bool ParseBooleanLike(
        JsonElement value,
        string field,
        int row,
        string trueWord = "required",
        string falseWord = "optional") => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt32(out var number) && number is 0 or 1 => number == 1,
        JsonValueKind.String => ParseBooleanLike(value.GetString(), field, row, trueWord, falseWord),
        _ => throw Validation("invalid_tool_state", $"Tool row {row} has an invalid {field}.")
    };

    private static bool ParseBooleanLike(
        string? value,
        string field,
        int row,
        string trueWord = "required",
        string falseWord = "optional") => value?.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "y" or "1" => true,
        "false" or "no" or "n" or "0" => false,
        var word when word == trueWord => true,
        var word when word == falseWord => false,
        _ => throw Validation("invalid_tool_state", $"Tool row {row} has an invalid {field}.")
    };

    private static string? Text(JsonElement item, params string[] names)
    {
        var value = Element(item, names);
        if (!value.HasValue || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.Value.ValueKind == JsonValueKind.String
            ? value.Value.GetString()
            : value.Value.ToString();
    }

    private static JsonElement? Element(JsonElement item, params string[] names) =>
        TryProperty(item, out var value, names) ? value : null;

    private static bool TryProperty(JsonElement item, out JsonElement value, params string[] names)
    {
        var normalized = names.Select(NormalizeName).ToHashSet(StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
        {
            if (normalized.Contains(NormalizeName(property.Name)))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string Required(string? value, string field, int maximum, int row)
    {
        var normalized = Optional(value, maximum);
        return normalized ?? throw Validation(
            "required_tool_value_missing", $"Tool row {row} requires {field}.");
    }

    private static string? Optional(string? value, int maximum)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > maximum)
        {
            throw Validation("tool_value_too_long", $"A tool-table value exceeds {maximum} characters.");
        }

        return normalized;
    }

    private static string? Value(IReadOnlyList<string> values, int? index) =>
        index.HasValue && index.Value < values.Count ? values[index.Value] : null;

    private static int? FindIndex(IReadOnlyDictionary<string, int> values, params string[] names)
    {
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var index))
            {
                return index;
            }
        }

        return null;
    }

    private static string NormalizeName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static GCodeValidationException Validation(string code, string message) =>
        new("toolTableFile", code, message);
}
