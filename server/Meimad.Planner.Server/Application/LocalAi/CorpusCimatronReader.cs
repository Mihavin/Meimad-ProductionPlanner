using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace Meimad.Planner.Server.Application.LocalAi;

/// <summary>
/// Reads Cimatron files for the approved document index (owner request 2026-10-14) as labelled text:
/// <list type="bullet">
/// <item>native documents (<c>.elt</c>, a ZIP container): the document attributes Cimatron keeps as XML — document type
/// (part, NC, assembly, drawing), title, units, part size, who created and last changed it on which PC and when, the
/// Cimatron build, the template, the file it was imported from — and, for an NC document, its setups and the number of
/// toolpath blocks. Procedure, tool and geometry data are stored in Cimatron's binary format and are not read;</item>
/// <item>Cimatron tool sheets and other single-file web pages (<c>.mht</c>/<c>.mhtml</c>): the page text;</item>
/// <item>Meimad exporter packages (<c>cimatron-export\manifest.json</c>): document, operation, postprocessor, Work CS,
/// files, the tool table and the post runs of <c>metadata\jobs.xml</c>.</item>
/// </list>
/// Original files are only read.
/// </summary>
internal static partial class CorpusCimatronReader
{
    internal const long MaxEltBytes = 1024L * 1024 * 1024;
    internal const long MaxMhtBytes = 64L * 1024 * 1024;
    internal const long MaxPackageBytes = 4L * 1024 * 1024;
    private const long MaxExlBytes = 64L * 1024 * 1024;

    /// <summary>True for a Meimad exporter manifest (<c>manifest.json</c> in a <c>cimatron-export</c> folder).</summary>
    internal static bool IsPackageManifest(string path) =>
        string.Equals(Path.GetFileName(path), "manifest.json", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "cimatron-export", StringComparison.OrdinalIgnoreCase);

    // ----- native documents (.elt) ---------------------------------------------------------------

    internal static string ReadElt(string path, int maxCharacters, CancellationToken token)
    {
        // Read-only and shared, so Cimatron can keep the document open; the stream is closed even when it is no container.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("Not a Cimatron document this version can read (no document container).", exception);
        }
        using (archive)
        {
            var exl = archive.GetEntry(".exl")
                ?? throw new InvalidDataException("Cimatron document without its attribute block (.exl).");
            if (exl.Length > MaxExlBytes) throw new InvalidDataException("The Cimatron attribute block exceeds the indexing size limit.");
            // .exl is one XML document: the document's attribute list first, then one list per data block — an NC
            // document has a list per setup (ELITE_DOC_TYPE NC-Setup, titled by CN_TITLE) and per toolpath (MotNumber).
            var lists = AttributeListPattern().Split(ReadEntry(exl, token)).Skip(1).Select(Attributes).ToList();
            var document = lists.FirstOrDefault(list => list.Count > 0)
                ?? throw new InvalidDataException("Cimatron document attributes could not be read.");
            var setups = lists.Where(list => First(list, "ELITE_DOC_TYPE") == "NC-Setup")
                .Select(list => First(list, "CN_TITLE") ?? "unnamed").ToList();
            var motionBlocks = lists.Count(list => list.Any(attribute => attribute.Id == "MotNumber"));
            return Describe(Path.GetFileName(path), document, setups, motionBlocks, maxCharacters);
        }
    }

    private static string Describe(
        string fileName, List<(string Id, string Value)> attributes, List<string> setups, int motionBlocks, int maxCharacters)
    {
        var text = new StringBuilder();
        text.Append("Cimatron document (native .elt). Document attributes only: procedures, tools and geometry are not readable here.\n");
        text.Append("File name: ").Append(fileName).Append('\n');
        var type = First(attributes, "ELITE_DOC_TYPE");
        if (type is not null) text.Append("Document type: ").Append(DocumentType(type)).Append('\n');
        Line(text, "Title", First(attributes, "CN_TITLE"));
        Line(text, "Description", First(attributes, "CN_DESCRIPTION"));
        Line(text, "Units", First(attributes, "CN_UNITS_OF_MEASURE"));
        if (First(attributes, "TMPL::Visible_Part_Size") is { } size)
            text.Append("Part size: ").Append(size).Append(' ').Append(First(attributes, "CN_UNITS_OF_MEASURE") ?? "").Append('\n');
        Line(text, "Material density", First(attributes, "TMPL::Density"));
        Line(text, "Created", Who(First(attributes, "Cim_E_CREATION_DATE"), First(attributes, "Cim_E_CREATED_BY"),
            First(attributes, "Cim_E_CREATED_FROM"), First(attributes, "Cim_E_CREATED_IN_PASS")));
        Line(text, "Last modified", Who(First(attributes, "Cim_E_MODIFICATION_DATE"), First(attributes, "Cim_E_MODIFIED_BY"),
            First(attributes, "Cim_E_MODIFIED_FROM"), null));
        Line(text, "Cimatron build", First(attributes, "CN_ELITE_PASS"));
        Line(text, "Template", First(attributes, "Cim_E_CREATED_FROM_TMPL"));
        if (First(attributes, "DI::ORIG_FILENAME") is { } imported)
            text.Append("Imported from: ").Append(imported)
                .Append(First(attributes, "DI::ORIG_FILEFORMAT") is { } format ? $" ({format})" : string.Empty).Append('\n');
        if (setups.Count > 0)
            text.Append("NC setups: ").Append(setups.Count).Append(" (").Append(string.Join(", ", setups.Distinct(StringComparer.Ordinal))).Append(")\n");
        if (motionBlocks > 0) text.Append("Toolpath blocks: ").Append(motionBlocks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return Limit(text, maxCharacters);
    }

    private static string DocumentType(string value) => value switch
    {
        "NC" => "NC (CAM program file)",
        "Modeler" => "Part (Modeler)",
        "Assembly" => "Assembly",
        "Drawing" => "Drawing",
        _ => value,
    };

    private static string? Who(string? date, string? user, string? computer, string? build)
    {
        if (date is null && user is null) return null;
        var text = new StringBuilder(date ?? "");
        if (user is not null) text.Append(" by ").Append(user);
        if (computer is not null) text.Append(" on ").Append(computer);
        if (build is not null) text.Append(" (Cimatron build ").Append(build).Append(')');
        return text.ToString().Trim();
    }

    private static string ReadEntry(ZipArchiveEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The <c>&lt;Cim:Attr Id="…"&gt;value&lt;/Cim:Attr&gt;</c> pairs with a value, in document order.</summary>
    internal static List<(string Id, string Value)> Attributes(string xml)
    {
        var result = new List<(string, string)>();
        foreach (Match match in AttributePattern().Matches(xml))
        {
            var value = WebUtility.HtmlDecode(match.Groups[2].Value).Trim();
            if (value.Length > 0) result.Add((WebUtility.HtmlDecode(match.Groups[1].Value), value));
        }
        return result;
    }

    private static string? First(List<(string Id, string Value)> attributes, string id) =>
        attributes.FirstOrDefault(attribute => attribute.Id == id).Value;

    [GeneratedRegex("""<Cim:Attr\s+Id="([^"]+)"[^>]*>([^<]*)</Cim:Attr>""", RegexOptions.CultureInvariant)]
    private static partial Regex AttributePattern();

    [GeneratedRegex("<Cim:Attr_list\\b", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeListPattern();

    // ----- single-file web pages (.mht) ----------------------------------------------------------

    /// <summary>The text of the first HTML part of a MIME web archive (Cimatron tool sheets saved by Word).</summary>
    internal static string ReadMht(byte[] bytes, int maxCharacters, CancellationToken token)
    {
        var raw = Encoding.Latin1.GetString(bytes);
        var html = HtmlPart(raw) ?? throw new InvalidDataException("The web archive has no HTML page.");
        token.ThrowIfCancellationRequested();
        var text = HtmlText(html);
        if (text.Length == 0) throw new InvalidDataException("The web archive page has no text.");
        return Limit(new StringBuilder("Web archive page (.mht), for example a Cimatron tool sheet\n").Append(text), maxCharacters);
    }

    private static string? HtmlPart(string raw)
    {
        var boundary = BoundaryPattern().Match(raw);
        if (!boundary.Success) return raw.Contains("<html", StringComparison.OrdinalIgnoreCase) ? raw : null;
        foreach (var part in raw.Split("--" + boundary.Groups[1].Value, StringSplitOptions.None).Skip(1))
        {
            var split = part.IndexOf("\r\n\r\n", StringComparison.Ordinal) is var crlf and >= 0 ? (crlf, 4)
                : part.IndexOf("\n\n", StringComparison.Ordinal) is var lf and >= 0 ? (lf, 2) : (-1, 0);
            if (split.Item1 < 0) continue;
            var headers = part[..split.Item1];
            if (!headers.Contains("text/html", StringComparison.OrdinalIgnoreCase)) continue;
            var body = part[(split.Item1 + split.Item2)..];
            var encoding = TransferEncodingPattern().Match(headers) is { Success: true } transfer ? transfer.Groups[1].Value.ToLowerInvariant() : "";
            byte[] decoded = encoding switch
            {
                "quoted-printable" => QuotedPrintable(body),
                "base64" => Convert.FromBase64String(Regex.Replace(body, @"[^A-Za-z0-9+/=]", "")),
                _ => Encoding.Latin1.GetBytes(body),
            };
            var charset = CharsetPattern().Match(headers) is { Success: true } declared ? declared.Groups[1].Value : "utf-8";
            return Decode(decoded, charset);
        }
        return null;
    }

    private static string Decode(byte[] bytes, string charset)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var name = charset.Trim().Trim('"').ToLowerInvariant();
            return (name is "us-ascii" or "ascii" ? Encoding.UTF8 : Encoding.GetEncoding(name)).GetString(bytes);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static byte[] QuotedPrintable(string body)
    {
        var output = new List<byte>(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c != '=') { output.Add((byte)c); continue; }
            if (i + 1 < body.Length && body[i + 1] == '\n') { i += 1; continue; }
            if (i + 2 < body.Length && body[i + 1] == '\r' && body[i + 2] == '\n') { i += 2; continue; }
            if (i + 2 < body.Length && Uri.IsHexDigit(body[i + 1]) && Uri.IsHexDigit(body[i + 2]))
            {
                output.Add(Convert.ToByte(body.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }
            output.Add((byte)c);
        }
        return [.. output];
    }

    /// <summary>Page text: head, scripts, styles, comments and Office XML dropped; table cells kept apart; one row per line.</summary>
    internal static string HtmlText(string html)
    {
        var body = HeadPattern().Replace(html, " ");
        body = DroppedPattern().Replace(body, " ");
        // A table row is one line, its cells apart; the paragraphs Word puts inside a cell stay on that line.
        body = TableRowPattern().Replace(body, row => string.Join(" | ", CellPattern().Matches(row.Value)
            .Select(cell => Spaces().Replace(WebUtility.HtmlDecode(TagPattern().Replace(cell.Groups[1].Value, " ")).Replace('\u00A0', ' '), " ").Trim())
            .Where(cell => cell.Length > 0)).Replace("&", "&amp;").Replace("<", "&lt;") + "\n");
        body = RowEndPattern().Replace(body, "\n");
        body = CellEndPattern().Replace(body, " | ");
        body = TagPattern().Replace(body, " ");
        body = WebUtility.HtmlDecode(body).Replace(' ', ' ');
        var lines = body.Split('\n')
            .Select(line => Spaces().Replace(line, " ").Trim(' ', '|').Trim())
            .Where(line => line.Length > 0);
        return string.Join('\n', lines);
    }

    [GeneratedRegex("""boundary="?([^";\r\n]+)"?""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoundaryPattern();

    [GeneratedRegex(@"Content-Transfer-Encoding:\s*([\w-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TransferEncodingPattern();

    [GeneratedRegex("""charset="?([\w-]+)"?""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CharsetPattern();

    [GeneratedRegex(@"<head\b.*?</head>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HeadPattern();

    [GeneratedRegex(@"<(script|style|xml)\b.*?</\1>|<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DroppedPattern();

    [GeneratedRegex(@"</(tr|p|div|h[1-6]|li|table)>|<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RowEndPattern();

    [GeneratedRegex(@"</t[dh]>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CellEndPattern();

    [GeneratedRegex(@"<tr\b[^>]*>.*?</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TableRowPattern();

    [GeneratedRegex(@"<t[dh]\b[^>]*>(.*?)</t[dh]>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CellPattern();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"[ \t\r\f\v]+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    // ----- Meimad exporter packages (cimatron-export\manifest.json) -----------------------------

    /// <summary>
    /// The package as text. The tool table and the post runs are read from the files the manifest names, inside the
    /// package folder; the manifest carries their SHA-256, so its own hash identifies the whole text.
    /// </summary>
    internal static string ReadPackage(string manifestPath, byte[] bytes, int maxCharacters, CancellationToken token)
    {
        using var document = ParseJson(bytes);
        var root = document.RootElement;
        var folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var text = new StringBuilder("Cimatron export package (Meimad Cimatron exporter manifest)\n");
        var source = Object(root, "source");
        var part = Object(root, "part");
        Line(text, "Part", Join(" ", String(part, "number"), String(part, "name")));
        Line(text, "Cimatron file", String(source, "documentPath") ?? String(source, "project"));
        Line(text, "Setup", String(source, "setup"));
        Line(text, "Exported", Join(" ", String(source, "exportedAtUtc"),
            String(source, "user") is { } user ? "by " + user : null, String(source, "host") is { } host ? "on " + host : null));
        var exporter = Object(root, "exporter");
        Line(text, "Exporter", Join(", ", String(exporter, "version") is { } version ? "version " + version : null,
            String(exporter, "cimatronVersion") is { } cimatron ? "Cimatron build " + cimatron : null));
        var operation = Object(root, "operation");
        Line(text, "Operation", Join(" ", String(operation, "number"), String(operation, "name"),
            String(operation, "procedureName") is { } procedure ? "(procedure " + procedure + ")" : null));
        Line(text, "Postprocessor", String(Object(root, "machine"), "postprocessor"));
        Line(text, "NC program", String(source, "ncFileName"));
        var cs = Object(root, "coordinateSystem");
        if (cs is { } coordinates)
            Line(text, "Work CS", Join(" ", String(coordinates, "name"), String(coordinates, "workOffset"),
                Vector(coordinates, "originMm") is { } origin ? "origin " + origin : null,
                Vector(coordinates, "rotationDeg") is { } rotation ? "rotation " + rotation : null));
        // The CAM operations chosen for an Import from Cimatron (manifests since exporter 0.2): kind, name, toolpath.
        foreach (var listed in (Array(source, "operations") ?? []).Take(500))
            text.Append("- CAM operation: ").Append(Join(" ", String(listed, "kind"), String(listed, "name"),
                String(listed, "toolpath") is { } toolpath ? "(toolpath " + toolpath + ")" : null)).Append('\n');

        var files = Object(root, "files");
        foreach (var stock in Array(files, "stock") ?? [])
            Line(text, "Stock file", Join(" ", String(stock, "path"), String(stock, "kind") is { } kind ? "(" + kind + ")" : null));
        foreach (var file in Array(files, "fixtures") ?? [])
            Line(text, "Fixture file", String(file, "path"));
        Line(text, "Finished part file", String(Object(files, "part"), "path"));

        if (PackageFile(folder, String(Object(files, "toolTable"), "path")) is { } toolTable)
            AppendTools(text, toolTable, token);
        if (PackageFile(folder, String(Object(files, "jobsXml"), "path")) is { } jobs)
            AppendPosts(text, jobs, token);
        return Limit(text, maxCharacters);
    }

    private static void AppendTools(StringBuilder text, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (new FileInfo(path).Length > MaxPackageBytes) return;
        try
        {
            using var document = ParseJson(File.ReadAllBytes(path));
            var tools = Array(document.RootElement, "tools") ?? [];
            if (tools.Count == 0) return;
            text.Append("Tools:\n");
            foreach (var tool in tools.Take(500))
            {
                text.Append("- ").Append(Join(" | ",
                    String(tool, "toolIdentifier") ?? (Number(tool, "number") is { } number ? "T" + number : null),
                    String(tool, "name") ?? String(tool, "description"),
                    String(tool, "type"),
                    Number(tool, "diameterMm") is { } diameter ? "diameter " + diameter + " mm" : null,
                    Number(tool, "fluteLengthMm") is { } flute ? "flute " + flute + " mm" : null,
                    Number(tool, "gaugeLengthMm") is { } gauge ? "gauge " + gauge + " mm" : null,
                    String(Object(tool, "holder"), "name") is { } holder ? "holder " + holder : null)).Append('\n');
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            text.Append("Tools: the tool table could not be read.\n");
        }
    }

    private static void AppendPosts(StringBuilder text, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (new FileInfo(path).Length > MaxPackageBytes) return;
        try
        {
            var xml = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                xml.Load(reader);
            var lines = new List<string>();
            foreach (XmlElement job in xml.GetElementsByTagName("Job"))
            {
                foreach (XmlElement post in job.GetElementsByTagName("Post"))
                {
                    var files = post.GetElementsByTagName("File").Cast<XmlElement>()
                        .Select(file => Path.GetFileName(file.GetAttribute("Name"))).Where(name => name.Length > 0);
                    var time = job.GetElementsByTagName("Parameter").Cast<XmlElement>()
                        .Select(parameter => parameter.GetAttribute("MachineTime")).FirstOrDefault(value => value.Length > 0);
                    lines.Add(Join(" | ", post.GetAttribute("Name"),
                        Child(post, "RefernceUcs", "Name") is { Length: > 0 } ucs ? "UCS " + ucs : null,
                        Child(post, "DateAndTime", "Value"),
                        time is { Length: > 0 } ? "machine time " + time : null,
                        string.Join(", ", files)) ?? "");
                }
            }
            if (lines.Count == 0) return;
            text.Append("Post runs in the Cimatron file:\n");
            foreach (var line in lines.Where(line => line.Length > 0).Take(200)) text.Append("- ").Append(line).Append('\n');
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            text.Append("Post runs: metadata/jobs.xml could not be read.\n");
        }
    }

    private static string? Child(XmlElement element, string name, string attribute) =>
        element.GetElementsByTagName(name).Cast<XmlElement>().Select(child => child.GetAttribute(attribute)).FirstOrDefault();

    /// <summary>A package file inside the package folder (a relative path that does not leave it), or null.</summary>
    private static string? PackageFile(string folder, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        var full = Path.GetFullPath(Path.Combine(folder, relative));
        return full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    private static JsonDocument ParseJson(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Cimatron package manifest is not valid JSON: " + exception.Message, exception);
        }
    }

    private static JsonElement? Object(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object
            ? child
            : null;

    private static List<JsonElement>? Array(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Array
            ? child.EnumerateArray().ToList()
            : null;

    private static string? String(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var child)) return null;
        var text = child.ValueKind switch
        {
            JsonValueKind.String => child.GetString(),
            JsonValueKind.Number => child.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? Number(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Number
            ? child.GetRawText()
            : null;

    private static string? Vector(JsonElement? parent, string name)
    {
        var vector = Object(parent, name);
        return vector is null ? null : $"X{Number(vector, "x") ?? "0"} Y{Number(vector, "y") ?? "0"} Z{Number(vector, "z") ?? "0"}";
    }

    // ----- shared ---------------------------------------------------------------------------------

    private static string? Join(string separator, params string?[] parts)
    {
        var present = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return present.Length == 0 ? null : string.Join(separator, present);
    }

    private static void Line(StringBuilder text, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) text.Append(label).Append(": ").Append(value.Trim()).Append('\n');
    }

    private static string Limit(StringBuilder text, int maxCharacters)
    {
        if (text.Length > maxCharacters) text.Length = maxCharacters;
        return text.ToString();
    }
}
