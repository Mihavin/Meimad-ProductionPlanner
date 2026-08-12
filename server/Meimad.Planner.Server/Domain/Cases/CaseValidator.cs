using System.Text.RegularExpressions;

namespace Meimad.Planner.Server.Domain.Cases;

internal static partial class CaseValidator
{
    private const int ShortTextMaximum = 200;
    private const int DetailTextMaximum = 500;
    private const int PathMaximum = 4096;
    private const int NotesMaximum = 8000;

    internal static ValidatedCaseValues ValidateAndNormalize(CaseValues values)
    {
        var issues = new List<CaseValidationIssue>();

        var partNumber = RequiredText(
            values.PartNumber,
            "partNumber",
            ShortTextMaximum,
            issues);
        var name = RequiredText(values.Name, "name", ShortTextMaximum, issues);
        var revision = OptionalText(values.Revision, "revision", ShortTextMaximum, issues);
        var customer = OptionalText(values.Customer, "customer", ShortTextMaximum, issues);
        var customerReference = OptionalText(
            values.CustomerReference,
            "customerReference",
            ShortTextMaximum,
            issues);
        var workingFolderPath = RequiredPath(
            values.WorkingFolderPath,
            "workingFolderPath",
            issues);
        var previewPath = OptionalPath(values.PreviewPath, "previewPath", issues);
        var materialType = OptionalText(
            values.MaterialType,
            "materialType",
            ShortTextMaximum,
            issues);
        var materialSpecification = OptionalText(
            values.MaterialSpecification,
            "materialSpecification",
            DetailTextMaximum,
            issues);
        var rawMaterialForm = OptionalText(
            values.RawMaterialForm,
            "rawMaterialForm",
            ShortTextMaximum,
            issues);
        var rawMaterialDimensions = OptionalText(
            values.RawMaterialDimensions,
            "rawMaterialDimensions",
            DetailTextMaximum,
            issues);
        var notes = OptionalText(values.Notes, "notes", NotesMaximum, issues);

        if (issues.Count > 0)
        {
            throw new CaseValidationException(issues);
        }

        return new ValidatedCaseValues(
            partNumber!,
            name!,
            revision,
            customer,
            customerReference,
            previewPath,
            workingFolderPath!,
            materialType,
            materialSpecification,
            rawMaterialForm,
            rawMaterialDimensions,
            notes);
    }

    private static string? RequiredText(
        string? value,
        string field,
        int maximumLength,
        ICollection<CaseValidationIssue> issues)
    {
        var normalized = Normalize(value);
        if (normalized is null)
        {
            issues.Add(new CaseValidationIssue(field, "required", $"{field} is required."));
            return null;
        }

        ValidateLength(normalized, field, maximumLength, issues);
        return normalized;
    }

    private static string? OptionalText(
        string? value,
        string field,
        int maximumLength,
        ICollection<CaseValidationIssue> issues)
    {
        var normalized = Normalize(value);
        if (normalized is not null)
        {
            ValidateLength(normalized, field, maximumLength, issues);
        }

        return normalized;
    }

    private static string? RequiredPath(
        string? value,
        string field,
        ICollection<CaseValidationIssue> issues)
    {
        var normalized = RequiredText(value, field, PathMaximum, issues);
        if (normalized is not null && !IsAbsoluteFileSystemPath(normalized))
        {
            issues.Add(new CaseValidationIssue(
                field,
                "absolute_path_required",
                $"{field} must be an absolute filesystem path."));
        }

        return normalized;
    }

    private static string? OptionalPath(
        string? value,
        string field,
        ICollection<CaseValidationIssue> issues)
    {
        var normalized = OptionalText(value, field, PathMaximum, issues);
        if (normalized is not null && !IsAbsoluteFileSystemPath(normalized))
        {
            issues.Add(new CaseValidationIssue(
                field,
                "absolute_path_required",
                $"{field} must be an absolute filesystem path when supplied."));
        }

        return normalized;
    }

    private static bool IsAbsoluteFileSystemPath(string value)
    {
        if (value.IndexOf('\0') >= 0)
        {
            return false;
        }

        return Path.IsPathFullyQualified(value)
            || value.StartsWith(@"\\", StringComparison.Ordinal)
            || WindowsDrivePath().IsMatch(value);
    }

    private static void ValidateLength(
        string value,
        string field,
        int maximumLength,
        ICollection<CaseValidationIssue> issues)
    {
        if (value.Length > maximumLength)
        {
            issues.Add(new CaseValidationIssue(
                field,
                "too_long",
                $"{field} must contain at most {maximumLength} characters."));
        }
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsDrivePath();
}

internal sealed record CaseValidationIssue(string Field, string Code, string Message);

internal sealed class CaseValidationException : Exception
{
    internal CaseValidationException(IReadOnlyList<CaseValidationIssue> issues)
        : base("Case validation failed.")
    {
        Issues = issues;
    }

    internal IReadOnlyList<CaseValidationIssue> Issues { get; }
}
