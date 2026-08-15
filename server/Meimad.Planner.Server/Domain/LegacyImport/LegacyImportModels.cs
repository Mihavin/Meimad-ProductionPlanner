namespace Meimad.Planner.Server.Domain.LegacyImport;

internal enum LegacyImportIssueSeverity
{
    Blocking,
    Warning
}

internal sealed record LegacyImportIssue(
    LegacyImportIssueSeverity Severity,
    string Code,
    string Message,
    string? SheetName = null,
    int? RowNumber = null,
    string? Field = null,
    string? SectionKey = null,
    string? Scope = null);

internal static class LegacyImportIssueSeverities
{
    internal static string ToToken(this LegacyImportIssueSeverity severity) => severity switch
    {
        LegacyImportIssueSeverity.Blocking => "blocking",
        LegacyImportIssueSeverity.Warning => "warning",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown import issue severity.")
    };
}

internal sealed class LegacyImportValidationException : Exception
{
    internal LegacyImportValidationException(IReadOnlyList<LegacyImportIssue> issues)
        : base("Legacy working-plan import validation failed.")
    {
        Issues = issues;
    }

    internal IReadOnlyList<LegacyImportIssue> Issues { get; }
}

internal sealed class LegacyImportTokenExpiredException : Exception
{
    internal LegacyImportTokenExpiredException()
        : base("The import preview token is missing or expired. Upload the workbook again.")
    {
    }
}

internal sealed class LegacyWorkbookAlreadyImportedException : Exception
{
    internal LegacyWorkbookAlreadyImportedException(string workbookSha256)
        : base($"Workbook '{workbookSha256}' was already committed with different approved mappings or selections.")
    {
    }
}

internal sealed class LegacyWorkbookFormatException : Exception
{
    internal LegacyWorkbookFormatException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
}
