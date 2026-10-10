using System.Globalization;
using System.Text;

namespace Meimad.Planner.Server.Application.LocalAi;

/// <summary>
/// Extracts the plain text of an RTF document (CMM inspection reports, Word RTF). Formatting, font and colour
/// tables, pictures, embedded objects, headers, footers and other destinations are dropped; paragraphs become
/// lines and table cells are separated by " | ". \'hh and raw 8-bit characters use the document's \ansicpg
/// code page and \uN escapes give Unicode directly. CMM software on Hebrew Windows writes Hebrew bytes while
/// declaring code page 1252; when every raw 8-bit byte of such a document is a Hebrew letter in code page 1255
/// (0xE0–0xFA), the raw bytes are read as 1255.
/// </summary>
internal static class CorpusRtfReader
{
    internal const long MaxRtfBytes = 64L * 1024 * 1024;

    private static readonly HashSet<string> Destinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "objdata", "themedata", "colorschememapping",
        "datastore", "latentstyles", "xmlnstbl", "listtable", "listoverridetable", "rsidtbl", "generator", "filetbl",
        "revtbl", "pgdsctbl", "header", "headerl", "headerr", "headerf", "footer", "footerl", "footerr", "footerf",
        "fldinst", "nonshppict", "xe", "tc", "bkmkstart", "bkmkend", "atnauthor", "annotation", "comment",
    };

    static CorpusRtfReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal static string Read(byte[] rtf, int maxCharacters, CancellationToken token)
    {
        if (rtf.Length < 6 || Encoding.ASCII.GetString(rtf, 0, 5) != "{\\rtf") throw new InvalidDataException("Not an RTF document.");
        var declared = 1252;
        var head = Encoding.ASCII.GetString(rtf, 0, Math.Min(rtf.Length, 2048));
        var cpg = head.IndexOf("\\ansicpg", StringComparison.Ordinal);
        if (cpg >= 0)
        {
            var end = cpg + 8;
            while (end < head.Length && char.IsAsciiDigit(head[end])) end++;
            if (int.TryParse(head.AsSpan(cpg + 8, end - cpg - 8), NumberStyles.None, CultureInfo.InvariantCulture, out var page)) declared = page;
        }
        var escaped = CodePage(declared);
        var raw = declared == 1252 && rtf.Any(b => b >= 0x80) && rtf.All(b => b < 0x80 || b is >= 0xE0 and <= 0xFA) ? CodePage(1255) : escaped;

        var text = new StringBuilder();
        var pending = new List<byte>();
        Encoding? pendingEncoding = null;
        void Flush()
        {
            if (pending.Count > 0) text.Append(pendingEncoding!.GetString(pending.ToArray()));
            pending.Clear();
        }
        void Byte(byte value, Encoding encoding)
        {
            if (!ReferenceEquals(encoding, pendingEncoding)) { Flush(); pendingEncoding = encoding; }
            pending.Add(value);
        }
        void Append(string value) { Flush(); text.Append(value); }

        var stack = new Stack<(bool Skip, int Uc)>();
        bool skip = false, groupStart = false;
        int uc = 1, fallback = 0;
        for (var i = 0; i < rtf.Length; i++)
        {
            if ((i & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
            if (text.Length > maxCharacters) break;
            var b = rtf[i];
            if (b == '{') { Flush(); stack.Push((skip, uc)); groupStart = true; continue; }
            if (b == '}') { Flush(); if (stack.Count > 0) (skip, uc) = stack.Pop(); groupStart = false; fallback = 0; continue; }
            if (b is (byte)'\r' or (byte)'\n') continue;
            var wasGroupStart = groupStart;
            groupStart = false;
            if (b != '\\')
            {
                if (fallback > 0) { fallback--; continue; }
                if (skip) continue;
                if (b < 0x80) Append(((char)b).ToString());
                else Byte(b, raw);
                continue;
            }
            if (i + 1 >= rtf.Length) break;
            var next = rtf[i + 1];
            if (char.IsAsciiLetter((char)next))
            {
                var start = ++i;
                while (i < rtf.Length && char.IsAsciiLetter((char)rtf[i])) i++;
                var word = Encoding.ASCII.GetString(rtf, start, i - start);
                var parameterStart = i;
                if (i < rtf.Length && rtf[i] == '-') i++;
                while (i < rtf.Length && char.IsAsciiDigit((char)rtf[i])) i++;
                int? parameter = i > parameterStart && int.TryParse(Encoding.ASCII.GetString(rtf, parameterStart, i - parameterStart),
                    NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;
                if (i >= rtf.Length || rtf[i] != ' ') i--; // A space ends the control word and is part of it.
                if (word == "bin" && parameter is > 0) { i += parameter.Value; continue; }
                if (wasGroupStart && Destinations.Contains(word)) { skip = true; continue; }
                if (word == "uc" && parameter is >= 0) { uc = parameter.Value; continue; }
                if (skip) continue;
                if (word == "u" && parameter is { } code)
                {
                    Append(((char)(code < 0 ? code + 65536 : code)).ToString());
                    fallback = uc;
                    continue;
                }
                var mapped = word switch
                {
                    "par" or "line" or "row" or "sect" or "page" => "\n",
                    "tab" => "\t",
                    "cell" => " | ",
                    "emdash" => "—",
                    "endash" => "–",
                    "bullet" => "•",
                    "lquote" or "rquote" => "'",
                    "ldblquote" or "rdblquote" => "\"",
                    "emspace" or "enspace" or "qmspace" => " ",
                    _ => null,
                };
                if (mapped is not null) Append(mapped);
                continue;
            }
            i++;
            switch ((char)next)
            {
                case '\'':
                    if (i + 2 < rtf.Length && byte.TryParse(Encoding.ASCII.GetString(rtf, i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                    {
                        i += 2;
                        if (fallback > 0) { fallback--; break; }
                        if (!skip) Byte(hex, escaped);
                    }
                    break;
                case '*': skip = true; break;
                case '\\' or '{' or '}': if (fallback > 0) fallback--; else if (!skip) Append(((char)next).ToString()); break;
                case '~': if (!skip) Append(" "); break;
                case '_': if (!skip) Append("-"); break;
                case '\r' or '\n': if (!skip) Append("\n"); break;
            }
        }
        Flush();
        var lines = text.ToString().Split('\n').Select(line => line.TrimEnd()).ToList();
        var result = new StringBuilder();
        var blank = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0) { if (++blank > 1 || result.Length == 0) continue; }
            else blank = 0;
            result.Append(line).Append('\n');
        }
        if (result.Length > maxCharacters) result.Length = maxCharacters;
        if (!result.ToString().Any(char.IsLetterOrDigit)) throw new InvalidDataException("RTF document has no text.");
        return result.ToString();
    }

    private static Encoding CodePage(int page)
    {
        try { return Encoding.GetEncoding(page); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return Encoding.GetEncoding(1252); }
    }
}
