using System.Globalization;
using System.Text;

namespace Meimad.Planner.Server.Application.LocalAi;

/// <summary>
/// Reads the searchable metadata of a STEP model (ISO 10303-21, .stp/.step) as text: the file header
/// (file name, description, author, organization, originating CAD system, schema), the products with
/// their versions, the assembly structure (which product contains which, and how many times), length
/// units, solid-body count, and descriptive properties such as material. Geometry is not interpreted,
/// so the text carries no dimensions. The file is streamed once; records longer than 64 KB (geometry
/// lists) are skipped without being buffered.
/// </summary>
internal static class CorpusStepReader
{
    internal const long MaxStepBytes = 512L * 1024 * 1024;
    private const int MaxRecordCharacters = 64 * 1024;
    private const int MaxListedItems = 2000;

    private static readonly HashSet<string> Kept = new(StringComparer.Ordinal)
    {
        "PRODUCT", "PRODUCT_DEFINITION_FORMATION", "PRODUCT_DEFINITION_FORMATION_WITH_SPECIFIED_SOURCE",
        "PRODUCT_DEFINITION", "PRODUCT_DEFINITION_WITH_ASSOCIATED_DOCUMENTS", "NEXT_ASSEMBLY_USAGE_OCCURRENCE",
        "DESCRIPTIVE_REPRESENTATION_ITEM", "MATERIAL_DESIGNATION",
    };

    private sealed record Reference(int Id);
    private sealed record Typed(string Name, List<object?> Arguments);
    private sealed record Entity(string Name, List<object?> Arguments);

    private sealed class Model
    {
        internal readonly Dictionary<string, List<object?>> Header = new(StringComparer.Ordinal);
        internal readonly Dictionary<int, Entity> Entities = [];
        internal readonly List<string> LengthUnits = [];
        internal int Solids;
        internal bool InData;
    }

    internal static async Task<string> ReadAsync(string path, int maxCharacters, CancellationToken token)
    {
        var model = new Model();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        // Part 21 text is ASCII with escapes for other characters; stray UTF-8 from some exporters is kept.
        using var reader = new StreamReader(stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true, 1 << 16);
        var buffer = new char[1 << 16];
        var record = new StringBuilder();
        bool inString = false, inComment = false, overflow = false, first = true;
        var previous = '\0';
        int read;
        while ((read = await reader.ReadAsync(buffer, token)) > 0)
        {
            token.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (inComment)
                {
                    if (previous == '*' && c == '/') { inComment = false; c = '\0'; }
                    previous = c;
                    continue;
                }
                if (c is '\r' or '\n') continue; // Line breaks carry no meaning, even inside strings.
                if (!inString && previous == '/' && c == '*')
                {
                    inComment = true; previous = '\0';
                    if (record.Length > 0 && record[^1] == '/') record.Length--;
                    continue;
                }
                previous = c;
                if (c == '\'') inString = !inString; // A doubled '' toggles out and back in.
                if (!inString && c == ';')
                {
                    var text = record.ToString().Trim();
                    if (first)
                    {
                        if (!string.Equals(text, "ISO-10303-21", StringComparison.Ordinal)) throw new InvalidDataException("Not a STEP (ISO 10303-21) file.");
                        first = false;
                    }
                    else if (!overflow) Handle(model, text);
                    record.Clear(); overflow = false;
                    continue;
                }
                if (record.Length < MaxRecordCharacters) record.Append(c);
                else overflow = true;
            }
        }
        if (first) throw new InvalidDataException("Not a STEP (ISO 10303-21) file.");
        return Describe(model, maxCharacters);
    }

    private static void Handle(Model model, string record)
    {
        if (record.StartsWith("DATA", StringComparison.Ordinal) && !record.StartsWith("DATA_", StringComparison.Ordinal)) { model.InData = true; return; }
        if (record is "HEADER" or "ENDSEC" or "END-ISO-10303-21") return;
        try
        {
            if (!model.InData)
            {
                var open = record.IndexOf('(');
                if (open > 0) model.Header[record[..open].Trim()] = Arguments(record, open);
                return;
            }
            if (!record.StartsWith('#')) return;
            var equals = record.IndexOf('=');
            if (equals < 2 || !int.TryParse(record.AsSpan(1, equals - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return;
            var body = record[(equals + 1)..].TrimStart();
            if (body.StartsWith('('))
            {
                if (body.Contains("LENGTH_UNIT", StringComparison.Ordinal) && LengthUnit(Complex(body)) is { } unit && !model.LengthUnits.Contains(unit))
                    model.LengthUnits.Add(unit);
                return;
            }
            var paren = body.IndexOf('(');
            if (paren <= 0) return;
            var name = body[..paren].Trim();
            if (name is "MANIFOLD_SOLID_BREP" or "BREP_WITH_VOIDS") model.Solids++;
            else if (Kept.Contains(name)) model.Entities[id] = new(name, Arguments(body, paren));
        }
        // A malformed record is ignored; the rest of the model is still described.
        catch (Exception exception) when (exception is FormatException or ArgumentException or IndexOutOfRangeException or InvalidCastException) { }
    }

    private static string? LengthUnit(List<Typed> parts)
    {
        if (!parts.Any(part => part.Name == "LENGTH_UNIT")) return null;
        if (parts.FirstOrDefault(part => part.Name == "CONVERSION_BASED_UNIT") is { } converted)
            return (converted.Arguments.ElementAtOrDefault(0) as string)?.ToLowerInvariant();
        if (parts.FirstOrDefault(part => part.Name == "SI_UNIT") is not { } si) return null;
        var prefix = si.Arguments.ElementAtOrDefault(0) is Typed { Name: var p } ? p : "";
        var unit = si.Arguments.ElementAtOrDefault(1) is Typed { Name: var u } ? u : "METRE";
        return (prefix + unit).ToLowerInvariant();
    }

    private static string Describe(Model model, int maxCharacters)
    {
        var text = new StringBuilder("STEP 3D model metadata (ISO 10303-21). Geometry and dimensions are not indexed; open the model to measure.\n");
        void Line(string label, string? value) { if (Meaningful(value)) text.Append(label).Append(": ").Append(value!.Trim()).Append('\n'); }
        var description = model.Header.GetValueOrDefault("FILE_DESCRIPTION");
        var file = model.Header.GetValueOrDefault("FILE_NAME");
        var schema = model.Header.GetValueOrDefault("FILE_SCHEMA");
        Line("File name", Text(file, 0));
        Line("Description", Join(description?.ElementAtOrDefault(0)));
        Line("Created", Text(file, 1));
        Line("Author", Join(file?.ElementAtOrDefault(2)));
        Line("Organization", Join(file?.ElementAtOrDefault(3)));
        Line("Originating system", Text(file, 5));
        Line("Exporting software", Text(file, 4));
        Line("Schema", Join(schema?.ElementAtOrDefault(0)) is { } names ? Schema(names) : null);
        Line("Length unit", string.Join(", ", model.LengthUnits));
        if (model.Solids > 0) text.Append("Solid bodies: ").Append(model.Solids.ToString(CultureInfo.InvariantCulture)).Append('\n');

        var products = model.Entities.Where(pair => pair.Value.Name == "PRODUCT").OrderBy(pair => pair.Key).ToList();
        var versions = model.Entities.Values.Where(entity => entity.Name.StartsWith("PRODUCT_DEFINITION_FORMATION", StringComparison.Ordinal))
            .Select(entity => (Product: (entity.Arguments.ElementAtOrDefault(2) as Reference)?.Id, Version: entity.Arguments.ElementAtOrDefault(0) as string))
            .Where(item => item.Product is not null && Meaningful(item.Version)).ToLookup(item => item.Product!.Value, item => item.Version!);
        if (products.Count > 0) text.Append("Products:\n");
        foreach (var (id, product) in products.Take(MaxListedItems))
        {
            text.Append("- ").Append(Label(product));
            var name = product.Arguments.ElementAtOrDefault(1) as string;
            var detail = product.Arguments.ElementAtOrDefault(2) as string;
            if (Meaningful(name) && name != product.Arguments.ElementAtOrDefault(0) as string) text.Append(" | name: ").Append(name);
            if (Meaningful(detail) && detail != name) text.Append(" | description: ").Append(detail);
            foreach (var version in versions[id].Distinct()) text.Append(" | version: ").Append(version);
            text.Append('\n');
        }

        var usages = model.Entities.Values.Where(entity => entity.Name == "NEXT_ASSEMBLY_USAGE_OCCURRENCE")
            .Select(entity => (Parent: ProductOf(model, entity.Arguments.ElementAtOrDefault(3)), Child: ProductOf(model, entity.Arguments.ElementAtOrDefault(4))))
            .Where(usage => usage.Parent is not null && usage.Child is not null)
            .GroupBy(usage => (usage.Parent, usage.Child)).ToList();
        if (usages.Count > 0) text.Append("Assembly structure:\n");
        foreach (var usage in usages.Take(MaxListedItems))
            text.Append("- ").Append(usage.Key.Parent).Append(" contains ").Append(usage.Key.Child)
                .Append(usage.Count() > 1 ? " (x" + usage.Count().ToString(CultureInfo.InvariantCulture) + ")" : "").Append('\n');

        var properties = model.Entities.Values
            .Select(entity => entity.Name switch
            {
                "MATERIAL_DESIGNATION" => Meaningful(entity.Arguments.ElementAtOrDefault(0) as string) ? "material: " + entity.Arguments[0] : null,
                "DESCRIPTIVE_REPRESENTATION_ITEM" when Meaningful(entity.Arguments.ElementAtOrDefault(1) as string) =>
                    (Meaningful(entity.Arguments.ElementAtOrDefault(0) as string) ? entity.Arguments[0] + ": " : "") + entity.Arguments[1],
                _ => null,
            }).OfType<string>().Distinct(StringComparer.Ordinal).Take(MaxListedItems).ToList();
        if (properties.Count > 0) text.Append("Properties:\n");
        foreach (var property in properties) text.Append("- ").Append(property).Append('\n');

        if (text.Length > maxCharacters) text.Length = maxCharacters;
        return text.ToString();
    }

    private static string Label(Entity product) =>
        product.Arguments.ElementAtOrDefault(0) as string is { } id && Meaningful(id) ? id
            : product.Arguments.ElementAtOrDefault(1) as string ?? "(unnamed product)";

    private static string? ProductOf(Model model, object? definition)
    {
        if (definition is not Reference d || !model.Entities.TryGetValue(d.Id, out var pd) || !pd.Name.StartsWith("PRODUCT_DEFINITION", StringComparison.Ordinal)) return null;
        if (pd.Arguments.ElementAtOrDefault(2) is not Reference f || !model.Entities.TryGetValue(f.Id, out var formation)) return null;
        if (formation.Arguments.ElementAtOrDefault(2) is not Reference p || !model.Entities.TryGetValue(p.Id, out var product) || product.Name != "PRODUCT") return null;
        return Label(product);
    }

    private static string Schema(string names)
    {
        var name = names.Split('{')[0].Trim();
        var protocol = name.ToUpperInvariant() switch
        {
            "CONFIG_CONTROL_DESIGN" => "AP203",
            "AUTOMOTIVE_DESIGN" => "AP214",
            var other when other.StartsWith("AP242", StringComparison.Ordinal) => "AP242",
            var other when other.StartsWith("AP203", StringComparison.Ordinal) => "AP203",
            _ => null,
        };
        return protocol is null ? name : name + " (" + protocol + ")";
    }

    private static string? Text(List<object?>? arguments, int index) => arguments?.ElementAtOrDefault(index) as string;

    private static string? Join(object? value) => value switch
    {
        string text => text,
        List<object?> list => string.Join(", ", list.OfType<string>().Where(Meaningful)),
        _ => null,
    };

    private static bool Meaningful(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().ToLowerInvariant() is not ("none" or "unknown" or "undefined" or "n/a");

    // ---- Part 21 values ----

    private static List<object?> Arguments(string text, int open)
    {
        var position = open;
        return Value(text, ref position) as List<object?> ?? throw new FormatException();
    }

    private static List<Typed> Complex(string body)
    {
        var parts = new List<Typed>();
        var position = 1;
        while (true)
        {
            SkipSpace(body, ref position);
            if (position >= body.Length) throw new FormatException();
            if (body[position] == ')') return parts;
            var start = position;
            while (position < body.Length && (char.IsLetterOrDigit(body[position]) || body[position] == '_')) position++;
            var name = body[start..position];
            SkipSpace(body, ref position);
            if (name.Length == 0 || position >= body.Length || body[position] != '(') throw new FormatException();
            parts.Add(new(name, (List<object?>)Value(body, ref position)!));
        }
    }

    private static object? Value(string s, ref int p)
    {
        SkipSpace(s, ref p);
        if (p >= s.Length) throw new FormatException();
        var c = s[p];
        if (c == '\'')
        {
            var raw = new StringBuilder();
            for (p++; ; p++)
            {
                if (p >= s.Length) throw new FormatException();
                if (s[p] != '\'') { raw.Append(s[p]); continue; }
                if (p + 1 < s.Length && s[p + 1] == '\'') { raw.Append('\''); p++; continue; }
                p++;
                return Decode(raw.ToString());
            }
        }
        if (c == '(')
        {
            var list = new List<object?>();
            p++;
            while (true)
            {
                SkipSpace(s, ref p);
                if (p >= s.Length) throw new FormatException();
                if (s[p] == ')') { p++; return list; }
                list.Add(Value(s, ref p));
                SkipSpace(s, ref p);
                if (p < s.Length && s[p] == ',') p++;
            }
        }
        if (c == '#')
        {
            var start = ++p;
            while (p < s.Length && char.IsAsciiDigit(s[p])) p++;
            return int.TryParse(s.AsSpan(start, p - start), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? new Reference(id) : throw new FormatException();
        }
        if (c is '$' or '*') { p++; return null; }
        if (c == '.')
        {
            var end = s.IndexOf('.', p + 1);
            if (end < 0) throw new FormatException();
            var name = s[(p + 1)..end];
            p = end + 1;
            return new Typed(name, []);
        }
        if (c == '"')
        {
            var end = s.IndexOf('"', p + 1);
            if (end < 0) throw new FormatException();
            p = end + 1;
            return null;
        }
        if (char.IsLetter(c))
        {
            var start = p;
            while (p < s.Length && (char.IsLetterOrDigit(s[p]) || s[p] == '_')) p++;
            var name = s[start..p];
            SkipSpace(s, ref p);
            return p < s.Length && s[p] == '(' ? new Typed(name, (List<object?>)Value(s, ref p)!) : new Typed(name, []);
        }
        var begin = p;
        while (p < s.Length && s[p] is not (',' or ')')) p++;
        return s[begin..p].Trim();
    }

    private static void SkipSpace(string s, ref int p) { while (p < s.Length && char.IsWhiteSpace(s[p])) p++; }

    /// <summary>Decodes Part 21 string escapes: \\, \X\hh (ISO 8859-1), \S\c, \X2\…\X0\ (UTF-16) and \X4\…\X0\ (code points).</summary>
    internal static string Decode(string raw)
    {
        if (!raw.Contains('\\')) return raw;
        var text = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\') { text.Append(raw[i]); continue; }
            if (At(raw, i, "\\\\")) { text.Append('\\'); i += 1; }
            else if (At(raw, i, "\\X2\\") || At(raw, i, "\\X4\\"))
            {
                var width = raw[i + 2] == '2' ? 4 : 8;
                var end = raw.IndexOf("\\X0\\", i + 4, StringComparison.Ordinal);
                if (end < 0) { text.Append(raw[i]); continue; }
                for (var h = i + 4; h + width <= end; h += width)
                    if (int.TryParse(raw.AsSpan(h, width), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        text.Append(width == 4 ? ((char)code).ToString() : code is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(code) : "�");
                i = end + 3;
            }
            else if (At(raw, i, "\\X\\") && i + 5 <= raw.Length
                && int.TryParse(raw.AsSpan(i + 3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var latin))
            { text.Append((char)latin); i += 4; }
            else if (At(raw, i, "\\S\\") && i + 3 < raw.Length) { text.Append((char)(raw[i + 3] + 128)); i += 3; }
            else if (i + 3 < raw.Length && raw[i + 1] == 'P' && raw[i + 3] == '\\') i += 3; // Code-page directive.
            else text.Append(raw[i]);
        }
        return text.ToString();
    }

    private static bool At(string text, int index, string token) => string.CompareOrdinal(text, index, token, 0, token.Length) == 0;
}
