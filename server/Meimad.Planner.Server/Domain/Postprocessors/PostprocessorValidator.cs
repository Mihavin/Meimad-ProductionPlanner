namespace Meimad.Planner.Server.Domain.Postprocessors;

internal static class PostprocessorValidator
{
    internal static ValidatedPostprocessorValues ValidateAndNormalize(PostprocessorValues values)
    {
        var issues = new List<PostprocessorValidationIssue>();
        var name = Normalize(values.Name);
        var description = Normalize(values.Description);
        if (name is null)
        {
            issues.Add(new("name", "required", "name is required."));
        }
        else if (name.Length > 200)
        {
            issues.Add(new("name", "too_long", "name must contain at most 200 characters."));
        }

        if (description?.Length > 2_000)
        {
            issues.Add(new("description", "too_long", "description must contain at most 2,000 characters."));
        }

        if (!values.IsActive.HasValue)
        {
            issues.Add(new("isActive", "required", "isActive is required."));
        }

        if (issues.Count > 0)
        {
            throw new PostprocessorValidationException(issues);
        }

        return new ValidatedPostprocessorValues(name!, description, values.IsActive!.Value);
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}

internal sealed record PostprocessorValidationIssue(string Field, string Code, string Message);

internal sealed class PostprocessorValidationException(
    IReadOnlyList<PostprocessorValidationIssue> issues) : Exception("Postprocessor validation failed.")
{
    internal IReadOnlyList<PostprocessorValidationIssue> Issues { get; } = issues;
}
