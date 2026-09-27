using System.Globalization;
using Meimad.Planner.Server.Domain.ToolPreparations;

namespace Meimad.Planner.Server.Domain.ToolCatalog;

/// <summary>One identifier of a catalog tool in another system: the supplier's, the ERP's, the CAM library's, ...</summary>
internal sealed record CatalogToolExternalId(string System, string Value);

/// <summary>
/// A tool the factory owns as a definition: its Meimad internal id (<c>MT-00001</c>, assigned
/// once and never reused), type, hand, dimensions, textual attributes (ISO insert and holder
/// codes, thread profile, material, coating, manufacturer) and its ids in other systems. The
/// catalog is a description of tools, not an inventory: it records no quantities, locations or
/// stock movements.
/// </summary>
internal sealed record CatalogTool(
    string CatalogToolId,
    int InternalNumber,
    string InternalCode,
    string Name,
    string ToolType,
    string? Hand,
    string? Description,
    IReadOnlyDictionary<string, double> Shape,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<CatalogToolExternalId> ExternalIds,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string UpdatedBy)
{
    internal string Family => ToolShapeTypes.Family(ToolType);

    internal static string InternalCodeFor(int number) => string.Create(CultureInfo.InvariantCulture, $"MT-{number:D5}");
}

/// <summary>What a client sends to create or replace a catalog tool.</summary>
internal sealed record CatalogToolUpdate(
    string? Name,
    string? ToolType,
    string? Hand,
    string? Description,
    IReadOnlyDictionary<string, double>? Shape,
    IReadOnlyDictionary<string, string>? Attributes,
    IReadOnlyList<CatalogToolExternalId>? ExternalIds,
    bool IsActive);

internal sealed record ValidatedCatalogTool(
    string Name,
    string ToolType,
    string? Hand,
    string? Description,
    IReadOnlyDictionary<string, double> Shape,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<CatalogToolExternalId> ExternalIds,
    bool IsActive);

/// <summary>Textual attributes a catalog tool may carry.</summary>
internal static class CatalogToolAttributes
{
    internal static readonly IReadOnlyList<string> Keys =
        ["insertCode", "holderCode", "threadProfile", "material", "coating", "manufacturer"];

    internal const int MaximumLength = 200;
}

internal sealed class ToolCatalogValidationException(string code, string message, string? field = null) : Exception(message)
{
    internal string Code { get; } = code;
    internal string? Field { get; } = field;
}

internal sealed class ToolCatalogNotFoundException(string id) : Exception($"Catalog tool '{id}' was not found.");

internal sealed class ToolCatalogConflictException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

internal static class CatalogToolValidator
{
    private const int MaximumExternalIds = 50;

    internal static ValidatedCatalogTool Validate(CatalogToolUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var name = update.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 200)
            throw new ToolCatalogValidationException("tool_catalog_name_invalid", "name is required (at most 200 characters).", "name");
        var type = update.ToolType?.Trim().ToUpperInvariant();
        if (!ToolShapeTypes.IsSupported(type))
            throw new ToolCatalogValidationException("tool_catalog_tool_type_invalid", $"toolType must be one of {string.Join(", ", ToolShapeTypes.All)}.", "toolType");
        string? hand;
        try
        {
            hand = ToolPreparationValidator.Hand(update.Hand, type!, name, "tool_catalog");
        }
        catch (ToolPreparationValidationException exception)
        {
            throw new ToolCatalogValidationException(exception.Code, exception.Message, exception.Field);
        }
        var description = update.Description?.Trim();
        if (string.IsNullOrEmpty(description)) description = null;
        else if (description.Length > 2000)
            throw new ToolCatalogValidationException("tool_catalog_description_too_long", "description must be at most 2000 characters.", "description");

        IReadOnlyDictionary<string, double> shape;
        try
        {
            shape = ToolPreparationValidator.Shape(update.Shape, name, "tool_catalog");
        }
        catch (ToolPreparationValidationException exception)
        {
            throw new ToolCatalogValidationException(exception.Code, exception.Message, exception.Field);
        }

        var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in update.Attributes ?? new Dictionary<string, string>())
        {
            if (!CatalogToolAttributes.Keys.Contains(key, StringComparer.Ordinal))
                throw new ToolCatalogValidationException("tool_catalog_attribute_unknown", $"Attribute '{key}' is not supported.", "attributes");
            var text = value?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            if (text.Length > CatalogToolAttributes.MaximumLength)
                throw new ToolCatalogValidationException("tool_catalog_attribute_too_long", $"Attribute '{key}' must be at most {CatalogToolAttributes.MaximumLength} characters.", "attributes");
            attributes[key] = text;
        }

        var externalIds = new List<CatalogToolExternalId>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in update.ExternalIds ?? [])
        {
            var system = entry?.System?.Trim() ?? string.Empty;
            var value = entry?.Value?.Trim() ?? string.Empty;
            if (system.Length is 0 or > 60)
                throw new ToolCatalogValidationException("tool_catalog_external_id_system_invalid", "Every external id needs its system name (at most 60 characters).", "externalIds");
            if (value.Length is 0 or > 200)
                throw new ToolCatalogValidationException("tool_catalog_external_id_value_invalid", $"The external id in '{system}' needs a value (at most 200 characters).", "externalIds");
            if (!seen.Add($"{system}\u0001{value}"))
                throw new ToolCatalogValidationException("tool_catalog_external_id_duplicate", $"The external id '{value}' in '{system}' is listed twice.", "externalIds");
            externalIds.Add(new CatalogToolExternalId(system, value));
        }
        if (externalIds.Count > MaximumExternalIds)
            throw new ToolCatalogValidationException("tool_catalog_too_many_external_ids", $"At most {MaximumExternalIds} external ids can be listed.", "externalIds");

        return new ValidatedCatalogTool(name, type!, hand, description, shape, attributes, externalIds, update.IsActive);
    }
}
