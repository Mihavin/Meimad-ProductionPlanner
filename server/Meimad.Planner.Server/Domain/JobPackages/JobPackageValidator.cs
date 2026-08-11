namespace Meimad.Planner.Server.Domain.JobPackages;

internal static class JobPackageValidator
{
    internal static string RequiredIdentifier(string? value, string field, int maximumLength = 120)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximumLength)
        {
            throw new JobPackageValidationException(
                field,
                $"{field} is required and must not exceed {maximumLength} characters.");
        }

        return normalized;
    }

    internal static string? OptionalText(string? value, string field, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.Length > maximumLength)
        {
            throw new JobPackageValidationException(
                field,
                $"{field} must not exceed {maximumLength} characters.");
        }

        return normalized;
    }

    internal static string SafeLogicalPath(string? value, string field)
    {
        var normalized = RequiredIdentifier(value, field, 240).Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (normalized.StartsWith('/')
            || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new JobPackageValidationException(
                field,
                $"{field} must be a safe relative package path.");
        }

        return string.Join('/', segments);
    }

    internal static string SafeSourceRelativePath(string? value, string field)
    {
        var normalized = SafeLogicalPath(value, field);
        if (Path.IsPathRooted(normalized) || normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new JobPackageValidationException(
                field,
                $"{field} must be relative to the Case Working Folder.");
        }

        return normalized;
    }

    internal static JobPackageAssetType SourceAssetType(string? value, string field) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "nc" => JobPackageAssetType.Nc,
            "text" => JobPackageAssetType.Text,
            _ => throw new JobPackageValidationException(
                field,
                $"{field} must be 'nc' or 'text'.")
        };
}

internal sealed class JobPackageValidationException : Exception
{
    internal JobPackageValidationException(string field, string message) : base(message)
    {
        Field = field;
    }

    internal string Field { get; }
}
