namespace Meimad.Planner.Server.Domain.Cases;

/// <summary>
/// A CAD file linked to a Case: the part model itself, rest-material stock, a fixture, or any
/// other geometry the planner wants to see next to the part. The file lives on the factory
/// network; only its path is stored.
/// </summary>
internal sealed record CaseModelFile(
    string CaseModelFileId,
    string CaseId,
    string? CaseOperationId,
    string Kind,
    string Format,
    string FilePath,
    string Label,
    bool IsPrimary,
    int SortOrder,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal static class CaseModelFileKinds
{
    public const string Part = "part";
    public const string Stock = "stock";
    public const string Fixture = "fixture";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Part, Stock, Fixture, Other];

    public static bool IsValid(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);
}

internal static class CaseModelFileFormats
{
    public const string Step = "step";
    public const string Stl = "stl";

    /// <summary>Derives the stored format from the file extension; null when unsupported.</summary>
    public static string? FromPath(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        if (string.Equals(extension, ".stp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".step", StringComparison.OrdinalIgnoreCase))
        {
            return Step;
        }

        return string.Equals(extension, ".stl", StringComparison.OrdinalIgnoreCase) ? Stl : null;
    }
}
