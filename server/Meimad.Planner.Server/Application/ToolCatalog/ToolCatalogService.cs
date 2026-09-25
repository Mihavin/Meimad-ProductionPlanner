using Meimad.Planner.Server.Domain.ToolCatalog;

namespace Meimad.Planner.Server.Application.ToolCatalog;

internal interface IToolCatalogRepository
{
    /// <summary>Catalog tools by internal number; `query` matches the internal code, name, description and external ids.</summary>
    Task<IReadOnlyList<CatalogTool>> ListAsync(string? query, string? toolType, bool includeInactive, CancellationToken cancellationToken);

    Task<CatalogTool?> GetAsync(string catalogToolId, CancellationToken cancellationToken);

    /// <summary>Assigns the next internal number and code inside the insert transaction.</summary>
    Task<CatalogTool> CreateAsync(ValidatedCatalogTool values, string updatedBy, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Replaces the tool at the expected version; throws a conflict when the version moved.</summary>
    Task<CatalogTool> UpdateAsync(string catalogToolId, int expectedVersion, ValidatedCatalogTool values, string updatedBy, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Deletes an unreferenced tool; false when it does not exist; a conflict when a prepared tool refers to it.</summary>
    Task<bool> DeleteAsync(string catalogToolId, CancellationToken cancellationToken);
}

/// <summary>
/// The factory's tool catalog: definitions with a stable internal id and their ids in other
/// systems. Like the Tool Room's measurements it is Tool Room master data, so writes need the
/// client identity headers and no planning Edit Mode; concurrent editors are guarded by the
/// expected version.
/// </summary>
internal sealed class ToolCatalogService(IToolCatalogRepository repository, TimeProvider timeProvider)
{
    internal Task<IReadOnlyList<CatalogTool>> ListAsync(string? query, string? toolType, bool includeInactive, CancellationToken cancellationToken = default)
    {
        var type = toolType?.Trim().ToUpperInvariant();
        return repository.ListAsync(string.IsNullOrWhiteSpace(query) ? null : query.Trim(), string.IsNullOrEmpty(type) ? null : type, includeInactive, cancellationToken);
    }

    internal async Task<CatalogTool> GetAsync(string catalogToolId, CancellationToken cancellationToken = default) =>
        await repository.GetAsync(Required(catalogToolId), cancellationToken)
        ?? throw new ToolCatalogNotFoundException(catalogToolId);

    internal Task<CatalogTool> CreateAsync(CatalogToolUpdate update, string userId, CancellationToken cancellationToken = default) =>
        repository.CreateAsync(CatalogToolValidator.Validate(update), Actor(userId), timeProvider.GetUtcNow(), cancellationToken);

    internal Task<CatalogTool> UpdateAsync(string catalogToolId, int expectedVersion, CatalogToolUpdate update, string userId, CancellationToken cancellationToken = default) =>
        repository.UpdateAsync(Required(catalogToolId), expectedVersion, CatalogToolValidator.Validate(update), Actor(userId), timeProvider.GetUtcNow(), cancellationToken);

    internal Task<bool> DeleteAsync(string catalogToolId, CancellationToken cancellationToken = default) =>
        repository.DeleteAsync(Required(catalogToolId), cancellationToken);

    private static string Required(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ToolCatalogValidationException("tool_catalog_id_required", "The catalog tool id is required.")
            : value.Trim();

    private static string Actor(string? userId) =>
        string.IsNullOrWhiteSpace(userId)
            ? throw new ToolCatalogValidationException("tool_catalog_user_required", "The saving user identity is required.")
            : userId.Trim();
}
