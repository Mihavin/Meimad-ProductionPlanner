using System.Text.RegularExpressions;

namespace Meimad.Planner.Server.Domain.Machines;

internal static partial class MachineValidator
{
    private const int ShortTextMaximum = 200;
    private const int CapabilityMaximum = 100;
    private const int CapabilityCountMaximum = 100;
    private const int PathMaximum = 4096;

    internal static ValidatedMachineValues ValidateAndNormalize(MachineValues values)
    {
        var issues = new List<MachineValidationIssue>();
        var number = RequiredText(values.Number, "number", ShortTextMaximum, issues);
        var name = RequiredText(values.Name, "name", ShortTextMaximum, issues);
        var processType = RequiredText(
            values.ProcessType,
            "processType",
            ShortTextMaximum,
            issues);
        var axisType = OptionalText(values.AxisType, "axisType", ShortTextMaximum, issues);
        var workingCalendarId = RequiredText(
            values.WorkingCalendarId,
            "workingCalendarId",
            ShortTextMaximum,
            issues);
        var picturePath = OptionalPath(values.PicturePath, "picturePath", issues);
        var capabilities = NormalizeCapabilities(values.Capabilities, issues);
        if (!values.IsActive.HasValue)
        {
            issues.Add(new MachineValidationIssue(
                "isActive",
                "required",
                "isActive is required."));
        }

        if (!values.DisplayEnabled.HasValue)
        {
            issues.Add(new MachineValidationIssue(
                "displayEnabled",
                "required",
                "displayEnabled is required."));
        }

        if (issues.Count > 0)
        {
            throw new MachineValidationException(issues);
        }

        return new ValidatedMachineValues(
            number!,
            name!,
            processType!,
            axisType,
            capabilities,
            workingCalendarId!,
            values.IsActive!.Value,
            values.DisplayEnabled!.Value,
            picturePath,
            Normalize(values.MachineTypeId));
    }

    private static IReadOnlyList<string> NormalizeCapabilities(
        IReadOnlyList<string?>? values,
        ICollection<MachineValidationIssue> issues)
    {
        if (values is null)
        {
            return [];
        }

        if (values.Count > CapabilityCountMaximum)
        {
            issues.Add(new MachineValidationIssue(
                "capabilities",
                "too_many",
                $"capabilities may contain at most {CapabilityCountMaximum} entries."));
        }

        var normalized = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < values.Count; index++)
        {
            var value = Normalize(values[index]);
            if (value is null)
            {
                issues.Add(new MachineValidationIssue(
                    $"capabilities[{index}]",
                    "required",
                    "Capability entries cannot be blank."));
                continue;
            }

            if (value.Length > CapabilityMaximum)
            {
                issues.Add(new MachineValidationIssue(
                    $"capabilities[{index}]",
                    "too_long",
                    $"Capability entries may contain at most {CapabilityMaximum} characters."));
            }

            if (!seen.Add(value))
            {
                issues.Add(new MachineValidationIssue(
                    $"capabilities[{index}]",
                    "duplicate_capability",
                    "Capabilities must be unique ignoring case."));
            }

            normalized.Add(value);
        }

        return normalized;
    }

    private static string? RequiredText(
        string? value,
        string field,
        int maximumLength,
        ICollection<MachineValidationIssue> issues)
    {
        var normalized = Normalize(value);
        if (normalized is null)
        {
            issues.Add(new MachineValidationIssue(field, "required", $"{field} is required."));
            return null;
        }

        ValidateLength(normalized, field, maximumLength, issues);
        return normalized;
    }

    private static string? OptionalText(
        string? value,
        string field,
        int maximumLength,
        ICollection<MachineValidationIssue> issues)
    {
        var normalized = Normalize(value);
        if (normalized is not null)
        {
            ValidateLength(normalized, field, maximumLength, issues);
        }

        return normalized;
    }

    private static string? OptionalPath(
        string? value,
        string field,
        ICollection<MachineValidationIssue> issues)
    {
        var normalized = OptionalText(value, field, PathMaximum, issues);
        if (normalized is not null && normalized.IndexOf('\0') >= 0)
        {
            issues.Add(new MachineValidationIssue(
                field,
                "invalid_path",
                $"{field} contains an invalid path character."));
        }
        else if (normalized is not null
                 && !(Path.IsPathFullyQualified(normalized)
                     || normalized.StartsWith(@"\\", StringComparison.Ordinal)
                     || WindowsDrivePath().IsMatch(normalized)))
        {
            issues.Add(new MachineValidationIssue(
                field,
                "absolute_path_required",
                $"{field} must be an absolute filesystem path when supplied."));
        }

        return normalized;
    }

    private static void ValidateLength(
        string value,
        string field,
        int maximumLength,
        ICollection<MachineValidationIssue> issues)
    {
        if (value.Length > maximumLength)
        {
            issues.Add(new MachineValidationIssue(
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

internal static class MachineCompatibility
{
    internal static bool IsCompatible(Machine machine, string? requiredMachineType)
    {
        if (!machine.IsActive)
        {
            return false;
        }

        var required = requiredMachineType?.Trim();
        return string.IsNullOrEmpty(required)
            || string.Equals(machine.ProcessType, required, StringComparison.OrdinalIgnoreCase)
            || string.Equals(machine.AxisType, required, StringComparison.OrdinalIgnoreCase)
            || machine.Capabilities.Contains(required, StringComparer.OrdinalIgnoreCase)
            || (machine.MachineTypeCapabilities?.Contains(
                    required,
                    StringComparer.OrdinalIgnoreCase) ?? false);
    }
}

internal sealed record MachineValidationIssue(string Field, string Code, string Message);

internal sealed class MachineValidationException : Exception
{
    internal MachineValidationException(IReadOnlyList<MachineValidationIssue> issues)
        : base("Machine validation failed.")
    {
        Issues = issues;
    }

    internal IReadOnlyList<MachineValidationIssue> Issues { get; }
}
