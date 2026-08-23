using System.Text;
using System.Text.RegularExpressions;
using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Haas;

namespace Meimad.Planner.Server.Infrastructure.Haas;

/// <summary>
/// Read-only adapter for a Haas Local Net Share path already accessible to the Server service account.
/// It deliberately fails closed when an active O-number cannot be proven to identify exactly one file.
/// </summary>
internal sealed class LocalNetShareHaasProgramReader(TimeProvider timeProvider) : IHaasProgramReader
{
    public async Task<MachineNcHeader> ReadActiveProgramHeaderAsync(
        HaasConnectionSettings settings,
        string programNumber,
        CancellationToken cancellationToken = default)
    {
        if (!settings.LocalNetShareEnabled || string.IsNullOrWhiteSpace(settings.LocalNetSharePath))
            throw new HaasProgramHeaderUnavailableException("Haas Local Net Share header access is not configured.");
        if (!Directory.Exists(settings.LocalNetSharePath))
            throw new HaasProgramHeaderUnavailableException("The configured Haas Local Net Share path is unavailable to the Server service account.");

        var locator = ParseProgramLocator(programNumber);
        var candidates = new List<(string Path, IReadOnlyList<string> Lines)>();
        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(settings.LocalNetSharePath, "*", SearchOption.AllDirectories);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HaasProgramHeaderUnavailableException($"Haas Local Net Share enumeration failed: {exception.Message}");
        }

        foreach (var path in paths.Take(10000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<string> lines;
            try
            {
                lines = await ReadFirstLinesAsync(path, settings.HeaderByteLimit,
                    settings.HeaderLineLimit, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                continue;
            }
            var located = lines.Select(line => Regex.Match(line, @"^\s*O(?<number>\d{1,8})\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .FirstOrDefault(match => match.Success);
            var fileNameMatches = locator.FileName is null
                || string.Equals(Path.GetFileName(path), locator.FileName, StringComparison.OrdinalIgnoreCase);
            if (fileNameMatches && located?.Success == true
                && $"O{located.Groups["number"].Value}" == locator.ProgramNumber)
                candidates.Add((path, lines));
            if (candidates.Count > 1) break;
        }

        return candidates.Count switch
        {
            1 => new MachineNcHeader(locator.ProgramNumber, candidates[0].Lines,
                candidates[0].Path, timeProvider.GetUtcNow()),
            0 => throw new HaasProgramHeaderUnavailableException(
                $"No readable machine-side NC file uniquely mapped to active program {locator.DisplayName}."),
            _ => throw new HaasProgramHeaderUnavailableException(
                $"More than one machine-side NC file contains active program {locator.DisplayName}; header identity is ambiguous.")
        };
    }

    private static async Task<IReadOnlyList<string>> ReadFirstLinesAsync(
        string path, int byteLimit, int lineLimit, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[byteLimit];
        var count = await stream.ReadAsync(bytes, cancellationToken);
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes, 0, count);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes, 0, count);
        }
        return text.TrimStart('\uFEFF')
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Take(lineLimit)
            .ToArray();
    }

    private static ActiveProgramLocator ParseProgramLocator(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (Regex.IsMatch(normalized, @"^O\d{1,8}$", RegexOptions.CultureInvariant))
            return new ActiveProgramLocator(normalized, null, normalized);

        // MTConnect commonly reports the program filename (for example 1500.CNC).
        // It is accepted only as an exact filename locator and only when its numeric
        // stem agrees with the O-number in the bounded machine-side header.
        if (Path.GetFileName(normalized) == normalized
            && Regex.Match(normalized, @"^(?<number>\d{1,8})\.(?:NC|CNC)$",
                RegexOptions.CultureInvariant) is { Success: true } file)
        {
            return new ActiveProgramLocator($"O{file.Groups["number"].Value}", normalized, normalized);
        }

        throw new HaasProgramHeaderUnavailableException(
            "The active Haas program must be an O-number or a numeric .NC/.CNC filename.");
    }

    private sealed record ActiveProgramLocator(string ProgramNumber, string? FileName, string DisplayName);
}
