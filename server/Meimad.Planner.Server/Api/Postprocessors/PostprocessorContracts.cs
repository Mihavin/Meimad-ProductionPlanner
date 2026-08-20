using System.Text.Json;
using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.Postprocessors;
using Meimad.Planner.Server.Domain.Postprocessors;

namespace Meimad.Planner.Server.Api.Postprocessors;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreatePostprocessorRequest(
    string? Name,
    string? Description,
    bool? IsActive)
{
    internal CreatePostprocessorCommand ToCommand() => new(Name, Description, IsActive);
}

internal sealed class PatchPostprocessorRequest
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Fields { get; init; } = new(StringComparer.Ordinal);

    internal UpdatePostprocessorCommand ToCommand()
    {
        var reader = new Reader(Fields);
        var command = new UpdatePostprocessorCommand(
            reader.String("name"),
            reader.String("description"),
            reader.Boolean("isActive"));
        reader.ThrowIfInvalid();
        return command;
    }

    private sealed class Reader
    {
        private static readonly HashSet<string> Allowed = ["name", "description", "isActive"];
        private readonly IReadOnlyDictionary<string, JsonElement> fields;
        private readonly List<PostprocessorRequestIssue> issues = [];

        internal Reader(IReadOnlyDictionary<string, JsonElement> fields)
        {
            this.fields = fields;
            foreach (var field in fields.Keys.Where(value => !Allowed.Contains(value)))
            {
                issues.Add(new(field, "unknown_field", $"Field '{field}' is not supported."));
            }

            if (fields.Count == 0)
            {
                issues.Add(new(string.Empty, "empty_patch", "At least one Postprocessor field is required."));
            }
        }

        internal PostprocessorField<string?> String(string name)
        {
            if (!fields.TryGetValue(name, out var value))
            {
                return PostprocessorField<string?>.Unspecified;
            }

            if (value.ValueKind == JsonValueKind.Null)
            {
                return PostprocessorField<string?>.Specified(null);
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return PostprocessorField<string?>.Specified(value.GetString());
            }

            issues.Add(new(name, "invalid_type", $"Field '{name}' must be a string or null."));
            return PostprocessorField<string?>.Unspecified;
        }

        internal PostprocessorField<bool?> Boolean(string name)
        {
            if (!fields.TryGetValue(name, out var value))
            {
                return PostprocessorField<bool?>.Unspecified;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return PostprocessorField<bool?>.Specified(value.GetBoolean());
            }

            issues.Add(new(name, "invalid_type", $"Field '{name}' must be a boolean."));
            return PostprocessorField<bool?>.Unspecified;
        }

        internal void ThrowIfInvalid()
        {
            if (issues.Count > 0)
            {
                throw new PostprocessorRequestException(issues);
            }
        }
    }
}

internal sealed record PostprocessorResponse(
    string PostprocessorId,
    string Name,
    string? Description,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static PostprocessorResponse FromDomain(Postprocessor value) => new(
        value.PostprocessorId,
        value.Name,
        value.Description,
        value.IsActive,
        value.Version,
        value.CreatedAt,
        value.UpdatedAt);
}

internal sealed record PostprocessorListResponse(
    IReadOnlyList<PostprocessorResponse> Items,
    string? NextCursor);

internal sealed record PostprocessorRequestIssue(string Field, string Code, string Message);

internal sealed class PostprocessorRequestException(
    IReadOnlyList<PostprocessorRequestIssue> issues) : Exception("Postprocessor request is invalid.")
{
    internal IReadOnlyList<PostprocessorRequestIssue> Issues { get; } = issues;
}
