using System.Text;

namespace Meimad.Planner.Server.Infrastructure.Haas;

/// <summary>
/// Tails a controller-written DPRNT text file (for example the Mazak Matrix
/// <c>print.txt</c> written when DPR14 = 4) over a network share. Only lines
/// appended since the previous poll are returned; a file that was rewritten,
/// shortened, or emptied by the controller is read again from the start.
/// Read errors propagate to the caller, which owns the availability decision.
/// </summary>
/// <remarks>
/// A fresh reader does not know which part of an existing file was already
/// consumed. When the caller replays existing content (it empties the file after
/// every read, so everything still in it is unconsumed by definition) the whole
/// file is returned. Otherwise the initial catch-up only reads the last
/// <see cref="InitialTailBytes"/> to recover the current PartName and deliberately
/// emits no historical <c>MEIMAD/</c> event lines.
/// </remarks>
internal sealed class HaasDprntFileReader
{
    private const int TailFingerprintLength = 64;
    private const int InitialTailBytes = 64 * 1024;
    private const int ChunkBytes = 256 * 1024;
    private const int MaximumBytesPerPoll = 4 * 1024 * 1024;
    private long offset;
    private byte[] tailFingerprint = [];
    private bool initialized;
    private bool catchingUp;
    private bool skipPartialFirstLine;
    private long lastLength;
    private bool lastDrainConsumedEverything;
    private readonly StringBuilder pending = new();

    /// <summary>Set when the last requested truncation failed; cleared by the next successful truncation.</summary>
    internal string? LastTruncateError { get; private set; }

    internal async Task<HaasDprntDrainResult> DrainAsync(string path, bool replayExistingContent, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await DrainOnceAsync(path, replayExistingContent, token);
            }
            catch (IOException exception) when (attempt < 3 && IsSharingViolation(exception))
            {
                // The controller briefly holds the file exclusively while it appends a line.
                await Task.Delay(100, token);
            }
        }
    }

    /// <summary>
    /// Empties the file after a drain that consumed it completely and ended on a line
    /// terminator, and only when it still ends exactly where this reader stopped, so a line
    /// the controller appended between the read and the truncation is never lost.
    /// Returns true when the file was emptied.
    /// </summary>
    internal bool TryTruncateConsumed(string path)
    {
        if (!lastDrainConsumedEverything || lastLength == 0 || pending.Length > 0) return false;
        try
        {
            using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite | FileShare.Delete
            });
            if (stream.Length != lastLength) return false;
            stream.SetLength(0);
            LastTruncateError = null;
            Reset();
            lastLength = 0;
            lastDrainConsumedEverything = false;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LastTruncateError = "DPRNT file could not be emptied after reading: " + Safe(exception.Message);
            return false;
        }
    }

    private async Task<HaasDprntDrainResult> DrainOnceAsync(string path, bool replayExistingContent, CancellationToken token)
    {
        string? latest = null;
        var eventLines = new List<string>();
        long length;
        await using (var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous
        }))
        {
            length = stream.Length;
            if (!initialized)
            {
                initialized = true;
                if (!replayExistingContent)
                {
                    catchingUp = true;
                    if (length > InitialTailBytes)
                    {
                        offset = length - InitialTailBytes;
                        skipPartialFirstLine = true;
                    }
                }
            }
            else if (length < offset || !await TailMatchesAsync(stream, token))
            {
                // The controller rewrote or emptied the file: its content is new.
                Reset();
            }

            var total = 0;
            while (offset < length && total < MaximumBytesPerPoll)
            {
                var count = (int)Math.Min(Math.Min(length - offset, ChunkBytes), MaximumBytesPerPoll - total);
                var buffer = new byte[count];
                stream.Position = offset;
                await stream.ReadExactlyAsync(buffer, token);
                offset += count;
                total += count;
                RememberTail(buffer);
                AppendText(buffer);
                ParsePendingLines(ref latest, eventLines);
            }
        }

        if (length == 0) Reset();
        lastLength = length;
        lastDrainConsumedEverything = offset == length;
        if (catchingUp)
        {
            // Historical event lines were most likely ingested before this reader existed.
            eventLines.Clear();
            if (lastDrainConsumedEverything) catchingUp = false;
        }
        return new(latest, eventLines);
    }

    private async Task<bool> TailMatchesAsync(FileStream stream, CancellationToken token)
    {
        if (tailFingerprint.Length == 0) return true;
        var current = new byte[tailFingerprint.Length];
        stream.Position = offset - tailFingerprint.Length;
        await stream.ReadExactlyAsync(current, token);
        return current.AsSpan().SequenceEqual(tailFingerprint);
    }

    private void RememberTail(byte[] buffer)
    {
        if (buffer.Length >= TailFingerprintLength)
        {
            tailFingerprint = buffer[^TailFingerprintLength..];
            return;
        }
        var combined = new byte[tailFingerprint.Length + buffer.Length];
        tailFingerprint.CopyTo(combined, 0);
        buffer.CopyTo(combined, tailFingerprint.Length);
        tailFingerprint = combined.Length <= TailFingerprintLength
            ? combined
            : combined[^TailFingerprintLength..];
    }

    private void AppendText(byte[] buffer)
    {
        var text = Encoding.UTF8.GetString(buffer)
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace("﻿", string.Empty, StringComparison.Ordinal);
        if (skipPartialFirstLine)
        {
            // The catch-up window starts mid-line; drop the fragment before the first terminator.
            var terminator = text.IndexOfAny(['\r', '\n']);
            if (terminator < 0) return;
            text = text[terminator..];
            skipPartialFirstLine = false;
        }
        pending.Append(text);
    }

    private void ParsePendingLines(ref string? latest, List<string> eventLines)
    {
        var lines = pending.ToString().Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        pending.Clear();
        pending.Append(lines[^1]);
        foreach (var raw in lines[..^1])
        {
            var line = HaasDprntPartReader.StripControlCharacters(raw);
            if (HaasDprntPartReader.TryParsePartName(line, out var value)) latest = value;
            else if (line.TrimStart().StartsWith("MEIMAD/", StringComparison.Ordinal))
                eventLines.Add(line.Trim());
        }
    }

    private void Reset()
    {
        offset = 0;
        tailFingerprint = [];
        skipPartialFirstLine = false;
        pending.Clear();
    }

    /// <summary>Windows ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33).</summary>
    private static bool IsSharingViolation(IOException exception) => (exception.HResult & 0xFFFF) is 32 or 33;

    private static string Safe(string value) => value.Length <= 400 ? value : value[..400];
}
