namespace Meimad.Planner.Server.Domain.MachineTypes;

internal static class MachineTypeValidator
{
    private const int NameMaximum = 200;
    private const int CapabilityMaximum = 100;
    private const int CapabilityCountMaximum = 100;

    internal static ValidatedMachineTypeValues ValidateAndNormalize(MachineTypeValues values)
    {
        var issues = new List<MachineTypeValidationIssue>();
        var name = Normalize(values.Name);
        if (name is null)
        {
            issues.Add(new("name", "required", "name is required."));
        }
        else if (name.Length > NameMaximum)
        {
            issues.Add(new("name", "too_long", $"name must contain at most {NameMaximum} characters."));
        }

        var capabilities = new List<string>();
        if (values.Capabilities is { } supplied)
        {
            if (supplied.Count > CapabilityCountMaximum)
            {
                issues.Add(new("capabilities", "too_many", $"capabilities may contain at most {CapabilityCountMaximum} entries."));
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < supplied.Count; index++)
            {
                var capability = Normalize(supplied[index]);
                if (capability is null)
                {
                    issues.Add(new($"capabilities[{index}]", "required", "Capability entries cannot be blank."));
                    continue;
                }

                if (capability.Length > CapabilityMaximum)
                {
                    issues.Add(new($"capabilities[{index}]", "too_long", $"Capability entries may contain at most {CapabilityMaximum} characters."));
                }

                if (!seen.Add(capability))
                {
                    issues.Add(new($"capabilities[{index}]", "duplicate_capability", "Capabilities must be unique ignoring case."));
                }

                capabilities.Add(capability);
            }
        }

        if (issues.Count > 0)
        {
            throw new MachineTypeValidationException(issues);
        }

        return new ValidatedMachineTypeValues(name!, capabilities);
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}

internal sealed record MachineTypeValidationIssue(string Field, string Code, string Message);

internal sealed class MachineTypeValidationException : Exception
{
    internal MachineTypeValidationException(IReadOnlyList<MachineTypeValidationIssue> issues)
        : base("Machine Type validation failed.") => Issues = issues;

    internal IReadOnlyList<MachineTypeValidationIssue> Issues { get; }
}
