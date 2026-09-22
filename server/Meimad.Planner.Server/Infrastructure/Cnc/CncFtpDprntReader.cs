using System.Net;
using System.Text;
using Meimad.Planner.Server.Infrastructure.Haas;

namespace Meimad.Planner.Server.Infrastructure.Cnc;

/// <summary>Where a controller's embedded FTP server serves the DPRNT print file, and how to log into it.</summary>
internal sealed record CncFtpDprntEndpoint(Uri Uri, string? Username, string? Password);

/// <summary>
/// Tails a controller-written DPRNT text file served by the controller's own embedded FTP
/// server (for example a Fanuc control with an Ethernet board). Embedded CNC FTP servers vary
/// in which commands they honour reliably, so unlike <see cref="HaasDprntFileReader"/> this
/// reader does not attempt a byte-range resume: each poll downloads the whole file, and only
/// the text past what the previous poll already returned is treated as new. A file that was
/// rewritten, shortened, or emptied by the controller is read again from the start. Line
/// semantics match <see cref="HaasDprntFileReader"/>. Read errors propagate to the caller,
/// which owns the availability decision.
/// </summary>
internal sealed class CncFtpDprntReader
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private string consumedContent = string.Empty;
    private bool lastDrainConsumedEverything;

    /// <summary>Set when the last requested clear failed; cleared by the next successful clear.</summary>
    internal string? LastTruncateError { get; private set; }

    internal async Task<HaasDprntDrainResult> DrainAsync(
        CncFtpDprntEndpoint endpoint, bool replayExistingContent, CancellationToken token)
    {
        var content = Decode(await DownloadAsync(endpoint, token));
        var added = !replayExistingContent && content.StartsWith(consumedContent, StringComparison.Ordinal)
            ? content[consumedContent.Length..]
            : content;

        consumedContent = content;
        lastDrainConsumedEverything = true;

        string? latest = null;
        var eventLines = new List<string>();
        foreach (var raw in added.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            if (raw.Length == 0) continue;
            var line = HaasDprntPartReader.StripControlCharacters(raw);
            if (HaasDprntPartReader.TryParsePartName(line, out var value)) latest = value;
            else if (line.TrimStart().StartsWith("MEIMAD/", StringComparison.Ordinal))
                eventLines.Add(line.Trim());
        }
        return new(latest, eventLines);
    }

    /// <summary>
    /// Empties the remote file after a drain that consumed it completely, and only when it
    /// still matches what this reader last saw, so a line the controller appended between the
    /// read and the clear is never lost. Returns true when the file was emptied.
    /// </summary>
    internal async Task<bool> TryTruncateConsumedAsync(CncFtpDprntEndpoint endpoint, CancellationToken token)
    {
        if (!lastDrainConsumedEverything || consumedContent.Length == 0) return false;
        try
        {
            var current = Decode(await DownloadAsync(endpoint, token));
            if (current != consumedContent) return false;
            await UploadEmptyAsync(endpoint, token);
            LastTruncateError = null;
            consumedContent = string.Empty;
            lastDrainConsumedEverything = false;
            return true;
        }
        catch (Exception exception) when (exception is WebException or IOException)
        {
            LastTruncateError = "DPRNT file could not be emptied after reading: " + Safe(exception.Message);
            return false;
        }
    }

    private static async Task<byte[]> DownloadAsync(CncFtpDprntEndpoint endpoint, CancellationToken token)
    {
        var request = CreateRequest(endpoint, WebRequestMethods.Ftp.DownloadFile);
        await using var registration = token.Register(request.Abort);
        using var response = (FtpWebResponse)await request.GetResponseAsync();
        await using var stream = response.GetResponseStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, token);
        var bytes = buffer.ToArray();
        return bytes.Length > MaximumFileBytes ? bytes[^MaximumFileBytes..] : bytes;
    }

    private static async Task UploadEmptyAsync(CncFtpDprntEndpoint endpoint, CancellationToken token)
    {
        var request = CreateRequest(endpoint, WebRequestMethods.Ftp.UploadFile);
        await using var registration = token.Register(request.Abort);
        await using (var stream = await request.GetRequestStreamAsync())
        {
            // Writing nothing before closing the request stream uploads a zero-byte file.
        }
        using var response = (FtpWebResponse)await request.GetResponseAsync();
    }

    private static FtpWebRequest CreateRequest(CncFtpDprntEndpoint endpoint, string method)
    {
        // FtpWebRequest is obsolete (SYSLIB0014) but .NET has no first-party replacement that speaks
        // FTP's STOR/DELE/RETR commands; HttpClient's ftp:// support is GET-only and cannot clear the
        // remote file. Embedded CNC FTP servers are the only thing this class talks to.
#pragma warning disable SYSLIB0014
        var request = (FtpWebRequest)WebRequest.Create(endpoint.Uri);
#pragma warning restore SYSLIB0014
        request.Method = method;
        request.UsePassive = true;
        request.UseBinary = true;
        request.KeepAlive = false;
        if (endpoint.Username is not null)
            request.Credentials = new NetworkCredential(endpoint.Username, endpoint.Password ?? string.Empty);
        return request;
    }

    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes)
        .Replace("\0", string.Empty, StringComparison.Ordinal)
        .Replace("﻿", string.Empty, StringComparison.Ordinal);

    private static string Safe(string value) => value.Length <= 400 ? value : value[..400];
}
