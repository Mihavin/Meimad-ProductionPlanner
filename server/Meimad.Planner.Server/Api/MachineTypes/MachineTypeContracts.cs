using System.Text.Json;
using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.MachineTypes;
using Meimad.Planner.Server.Domain.MachineTypes;

namespace Meimad.Planner.Server.Api.MachineTypes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateMachineTypeRequest(
    string? Name,
    IReadOnlyList<string?>? Capabilities)
{
    internal CreateMachineTypeCommand ToCommand() => new(Name, Capabilities);
}

internal sealed class PatchMachineTypeRequest
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Fields { get; init; } = new(StringComparer.Ordinal);

    internal UpdateMachineTypeCommand ToCommand()
    {
        var issues = new List<MachineTypeRequestIssue>();
        foreach (var field in Fields.Keys.Where(value => value is not ("name" or "capabilities")))
            issues.Add(new(field, "unknown_field", $"Field '{field}' is not supported."));
        if (Fields.Count == 0) issues.Add(new(string.Empty, "empty_patch", "At least one Machine Type field is required."));

        var name = ReadString("name", issues);
        var capabilities = ReadArray("capabilities", issues);
        if (issues.Count > 0) throw new MachineTypeRequestException(issues);
        return new UpdateMachineTypeCommand(name, capabilities);
    }

    private MachineTypeField<string?> ReadString(string name, ICollection<MachineTypeRequestIssue> issues)
    {
        if (!Fields.TryGetValue(name, out var value)) return MachineTypeField<string?>.Unspecified;
        if (value.ValueKind == JsonValueKind.Null) return MachineTypeField<string?>.Specified(null);
        if (value.ValueKind == JsonValueKind.String) return MachineTypeField<string?>.Specified(value.GetString());
        issues.Add(new(name, "invalid_type", $"Field '{name}' must be a string or null."));
        return MachineTypeField<string?>.Unspecified;
    }

    private MachineTypeField<IReadOnlyList<string?>?> ReadArray(string name, ICollection<MachineTypeRequestIssue> issues)
    {
        if (!Fields.TryGetValue(name, out var value)) return MachineTypeField<IReadOnlyList<string?>?>.Unspecified;
        if (value.ValueKind == JsonValueKind.Null) return MachineTypeField<IReadOnlyList<string?>?>.Specified(null);
        if (value.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new(name, "invalid_type", $"Field '{name}' must be a string array or null."));
            return MachineTypeField<IReadOnlyList<string?>?>.Unspecified;
        }

        var values = new List<string?>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                issues.Add(new(name, "invalid_type", $"Field '{name}' must be a string array or null."));
                return MachineTypeField<IReadOnlyList<string?>?>.Unspecified;
            }
            values.Add(item.ValueKind == JsonValueKind.Null ? null : item.GetString());
        }
        return MachineTypeField<IReadOnlyList<string?>?>.Specified(values);
    }
}

internal sealed record MachineTypeResponse(
    string MachineTypeId,
    string Name,
    IReadOnlyList<string> Capabilities,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static MachineTypeResponse FromDomain(MachineType value) => new(
        value.MachineTypeId, value.Name, value.Capabilities, value.Version, value.CreatedAt, value.UpdatedAt);
}

internal sealed record MachineTypeListResponse(IReadOnlyList<MachineTypeResponse> Items, string? NextCursor);
internal sealed record MachineTypeRequestIssue(string Field, string Code, string Message);
internal sealed class MachineTypeRequestException(IReadOnlyList<MachineTypeRequestIssue> issues) : Exception("Machine Type request is invalid.")
{
    internal IReadOnlyList<MachineTypeRequestIssue> Issues { get; } = issues;
}
