using System.Text;

namespace Meimad.Planner.NcEngine;

/// <summary>An NC program as text: lines joined with "\n", plus what is needed to save it back.</summary>
public sealed record NcTextDocument(string Text, string LineEnding, bool HasBom, string EncodingName)
{
    public string LineEndingName => LineEnding switch
    {
        "\n" => "LF",
        "\r" => "CR",
        _ => "CRLF"
    };
}

/// <summary>
/// Reads and writes NC program files the way the upstream desktop viewer does: UTF-8 (with or
/// without BOM), dominant line ending kept for saving. Files that are not valid UTF-8 are read
/// as Latin-1, which is what controls and CAM posts write for 8-bit comment characters.
/// </summary>
public static class NcTextFile
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static NcTextDocument Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var start = hasBom ? 3 : 0;
        if (Array.IndexOf(bytes, (byte)0, start) >= 0)
        {
            throw new InvalidDataException("UTF-16 and binary files are not supported; choose a text NC program.");
        }

        string decoded;
        string encodingName;
        try
        {
            decoded = StrictUtf8.GetString(bytes, start, bytes.Length - start);
            encodingName = hasBom ? "UTF-8 with BOM" : "UTF-8";
        }
        catch (DecoderFallbackException)
        {
            decoded = Encoding.Latin1.GetString(bytes, start, bytes.Length - start);
            encodingName = "Latin-1";
        }

        return new NcTextDocument(Normalize(decoded), DominantLineEnding(decoded), hasBom, encodingName);
    }

    public static byte[] Encode(string text, string lineEnding, bool hasBom, string? encodingName = null)
    {
        var normalized = Normalize(text ?? string.Empty);
        var ending = lineEnding is "\n" or "\r" ? lineEnding : "\r\n";
        var withEndings = ending == "\n" ? normalized : normalized.Replace("\n", ending, StringComparison.Ordinal);
        var body = string.Equals(encodingName, "Latin-1", StringComparison.Ordinal)
            ? Encoding.Latin1.GetBytes(withEndings)
            : Encoding.UTF8.GetBytes(withEndings);
        return hasBom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    public static string Normalize(string text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string DominantLineEnding(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    crlf++;
                    index++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[index] == '\n')
            {
                lf++;
            }
        }
        if (crlf == 0 && lf == 0 && cr == 0) return "\r\n";
        if (crlf >= lf && crlf >= cr) return "\r\n";
        return lf >= cr ? "\n" : "\r";
    }
}
