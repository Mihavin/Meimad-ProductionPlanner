using Meimad.Planner.Server.Application.LegacyImport;
using Meimad.Planner.Server.Domain.ToolCatalog;

namespace Meimad.Planner.Server.Application.ToolCatalog;

internal static class CimatronImportActions
{
    internal const string Create = "CREATE";
    internal const string Update = "UPDATE";
    internal const string Unchanged = "UNCHANGED";
    internal const string Skip = "SKIP";
}

/// <summary>What the import does (a preview) or did (applied) with one Cimatron cutter.</summary>
internal sealed record CimatronImportRow(
    int RowNumber,
    string CutterName,
    string Action,
    string? ToolType,
    string? CatalogToolId,
    string? InternalCode,
    string? Message);

internal sealed record CimatronImportResult(string FileName, string Units, bool Applied, IReadOnlyList<CimatronImportRow> Rows)
{
    internal int Count(string action) => Rows.Count(row => row.Action == action);
}

internal sealed record CimatronExportResult(byte[] Workbook, int ExportedCount, IReadOnlyList<string> SkippedTools);

/// <summary>
/// Moves tools between the catalog and Cimatron's cutter workbook. A cutter is matched to the
/// catalog tool that keeps its name under the "Cimatron" external id, else to the one tool with
/// that name; it creates a tool when neither exists. An update replaces only what Cimatron
/// describes (the cutter dimensions, holder, thread, comment, and the type when Cimatron's cutter
/// kind differs from the tool's) and keeps the catalog's own name, other dimensions, attributes
/// and external ids. A preview reports the same rows without saving anything.
/// </summary>
internal sealed class CimatronToolTransferService(
    IToolCatalogRepository repository,
    TimeProvider timeProvider,
    OpenXmlLegacyWorkbookReader reader)
{
    internal async Task<CimatronImportResult> ImportAsync(
        Stream workbook, string fileName, bool apply, string? userId, CancellationToken cancellationToken = default)
    {
        var actor = apply
            ? string.IsNullOrWhiteSpace(userId)
                ? throw new ToolCatalogValidationException("tool_catalog_user_required", "The saving user identity is required.")
                : userId.Trim()
            : null;
        var parsed = CimatronCutterLibrary.Read(await reader.ReadAsync(workbook, fileName, cancellationToken));
        if (parsed.Cutters.Count == 0)
            throw new ToolCatalogValidationException("cimatron_no_cutters", "The Cimatron workbook lists no cutters.");

        var tools = (await repository.ListAsync(null, null, true, cancellationToken)).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<CimatronImportRow>();
        foreach (var cutter in parsed.Cutters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(cutter.Name))
            {
                rows.Add(new(cutter.RowNumber, cutter.Name, CimatronImportActions.Skip, null, null, null, "The cutter name is listed twice in the workbook; the first row is used."));
                continue;
            }

            var values = CimatronCutterLibrary.ToCatalog(cutter);
            var existing = Match(tools, cutter.Name, out var ambiguous);
            if (ambiguous)
            {
                rows.Add(new(cutter.RowNumber, cutter.Name, CimatronImportActions.Skip, values.ToolType, null, null,
                    "Several catalog tools have this name; give the right one the Cimatron id to import it."));
                continue;
            }

            ValidatedCatalogTool validated;
            try
            {
                validated = CatalogToolValidator.Validate(existing is null ? New(cutter, values) : Merge(existing, cutter, values));
            }
            catch (ToolCatalogValidationException exception)
            {
                rows.Add(new(cutter.RowNumber, cutter.Name, CimatronImportActions.Skip, values.ToolType, existing?.CatalogToolId, existing?.InternalCode, exception.Message));
                continue;
            }

            if (existing is not null && Same(existing, validated))
            {
                rows.Add(new(cutter.RowNumber, cutter.Name, CimatronImportActions.Unchanged, validated.ToolType, existing.CatalogToolId, existing.InternalCode, null));
                continue;
            }

            var action = existing is null ? CimatronImportActions.Create : CimatronImportActions.Update;
            if (!apply)
            {
                rows.Add(new(cutter.RowNumber, cutter.Name, action, validated.ToolType, existing?.CatalogToolId, existing?.InternalCode, null));
                continue;
            }

            try
            {
                var saved = existing is null
                    ? await repository.CreateAsync(validated, actor!, timeProvider.GetUtcNow(), cancellationToken)
                    : await repository.UpdateAsync(existing.CatalogToolId, existing.Version, validated, actor!, timeProvider.GetUtcNow(), cancellationToken);
                if (existing is not null) tools.Remove(existing);
                tools.Add(saved);
                rows.Add(new(cutter.RowNumber, cutter.Name, action, saved.ToolType, saved.CatalogToolId, saved.InternalCode, null));
            }
            catch (ToolCatalogConflictException exception)
            {
                rows.Add(new(cutter.RowNumber, cutter.Name, CimatronImportActions.Skip, validated.ToolType, existing?.CatalogToolId, existing?.InternalCode, exception.Message));
            }
        }
        return new CimatronImportResult(fileName, parsed.Units, apply, rows);
    }

    internal async Task<CimatronExportResult> ExportAsync(bool includeInactive, CancellationToken cancellationToken = default)
    {
        var tools = await repository.ListAsync(null, null, includeInactive, cancellationToken);
        var rows = new List<IReadOnlyDictionary<int, object>>();
        var skipped = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            var row = CimatronCutterLibrary.ToCimatron(tool);
            if (row is null || !names.Add((string)row[CimatronCutterLibrary.NameId]))
            {
                skipped.Add($"{tool.InternalCode} {tool.Name}");
                continue;
            }
            rows.Add(row);
        }
        return new CimatronExportResult(CimatronCutterWorkbookWriter.Write(rows), rows.Count, skipped);
    }

    /// <summary>The tool keeping the name as its Cimatron id, else the single tool with that name.</summary>
    private static CatalogTool? Match(IReadOnlyList<CatalogTool> tools, string name, out bool ambiguous)
    {
        ambiguous = false;
        var byId = tools.Where(tool => tool.ExternalIds.Any(entry =>
            entry.System.Equals(CimatronCutterLibrary.ExternalSystem, StringComparison.OrdinalIgnoreCase)
            && entry.Value.Equals(name, StringComparison.OrdinalIgnoreCase))).ToList();
        var candidates = byId.Count > 0
            ? byId
            : tools.Where(tool => tool.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && !tool.ExternalIds.Any(entry => entry.System.Equals(CimatronCutterLibrary.ExternalSystem, StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count > 1)
        {
            ambiguous = true;
            return null;
        }
        return candidates.SingleOrDefault();
    }

    private static CatalogToolUpdate New(CimatronCutter cutter, CimatronCatalogValues values) => new(
        cutter.Name, values.ToolType, null, values.Description, values.Shape, values.Attributes,
        [new CatalogToolExternalId(CimatronCutterLibrary.ExternalSystem, cutter.Name)], true);

    private static CatalogToolUpdate Merge(CatalogTool existing, CimatronCutter cutter, CimatronCatalogValues values)
    {
        var shape = existing.Shape
            .Where(entry => !CimatronCutterLibrary.OwnedShapeKeys.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        foreach (var (key, value) in values.Shape) shape[key] = value;
        var attributes = existing.Attributes.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        foreach (var key in CimatronCutterLibrary.OwnedAttributeKeys)
        {
            if (values.Attributes.TryGetValue(key, out var value)) attributes[key] = value;
        }
        var externalIds = existing.ExternalIds.ToList();
        if (!externalIds.Any(entry => entry.System.Equals(CimatronCutterLibrary.ExternalSystem, StringComparison.OrdinalIgnoreCase)))
            externalIds.Add(new CatalogToolExternalId(CimatronCutterLibrary.ExternalSystem, cutter.Name));
        return new CatalogToolUpdate(
            existing.Name, CimatronCutterLibrary.MergedToolType(existing.ToolType, values.ToolType), existing.Hand,
            values.Description ?? existing.Description, shape, attributes, externalIds, existing.IsActive);
    }

    private static bool Same(CatalogTool existing, ValidatedCatalogTool values) =>
        existing.Name == values.Name
        && existing.ToolType == values.ToolType
        && existing.Hand == values.Hand
        && existing.Description == values.Description
        && existing.IsActive == values.IsActive
        && existing.Shape.Count == values.Shape.Count
        && existing.Shape.All(entry => values.Shape.TryGetValue(entry.Key, out var value) && Math.Abs(value - entry.Value) < 1e-9)
        && existing.Attributes.Count == values.Attributes.Count
        && existing.Attributes.All(entry => values.Attributes.TryGetValue(entry.Key, out var value) && value == entry.Value)
        && existing.ExternalIds.Count == values.ExternalIds.Count
        && existing.ExternalIds.All(entry => values.ExternalIds.Any(other => other.System == entry.System && other.Value == entry.Value));
}
