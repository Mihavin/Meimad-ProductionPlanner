using System.Text.Json;
using System.Text.Json.Nodes;
using Meimad.Planner.NcEngine.MachineDefinitions;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Machines;
using Meimad.Planner.Server.Domain.Machines;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api.Machines;

/// <summary>
/// Machine Designer definitions and meshes (schema v114, docs/machine-simulation-definition.md §4). Every signed-in user
/// reads; writes need <c>setup.manage</c>. The draft is saved with <c>If-Match: "machine-simulation:{id}:v{draftVersion}"</c>
/// and published with the same tag; publishing refuses a definition with errors (422 <c>machine_definition_invalid</c>).
/// </summary>
internal static class MachineSimulationEndpoints
{
    private const string EntityTagKind = "machine-simulation";

    internal static void MapMachineSimulationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/machines/simulation-definitions", ListPublishedAsync);
        var machine = endpoints.MapGroup("/api/v1/machines/{machineId}");
        machine.MapGet("/simulation-definition", GetAsync);
        machine.MapPut("/simulation-definition", PutAsync);
        machine.MapPost("/simulation-definition/validate", ValidateAsync);
        machine.MapPost("/simulation-definition/publish", PublishAsync);
        machine.MapGet("/simulation-assets", ListAssetsAsync);
        machine.MapPost("/simulation-assets", UploadAssetAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MachineSimulationAssetStore.MaximumBytes + 1024 * 1024));
        machine.MapGet("/simulation-assets/{assetId}/content", GetAssetContentAsync);
    }

    private static async Task<IResult> GetAsync(
        string machineId, HttpContext httpContext, MachineSimulationService service, CancellationToken cancellationToken)
    {
        var record = await service.GetAsync(machineId, cancellationToken);
        if (record is null) return NotFound(httpContext);
        var issues = await service.ValidateAsync(machineId, record.Draft, cancellationToken) ?? [];
        SetEntityTag(httpContext.Response, record);
        return Response(record, issues);
    }

    /// <summary>Every Machine's published definition, for the NC viewer (one request instead of one per Machine).</summary>
    private static async Task<IResult> ListPublishedAsync(MachineSimulationService service, CancellationToken cancellationToken)
    {
        var published = await service.ListPublishedAsync(cancellationToken);
        return Results.Ok(new
        {
            items = published.Select(item => new
            {
                machineId = item.MachineId,
                publishedVersion = item.PublishedVersion,
                published = JsonNode.Parse(item.Published.ToJson())
            })
        });
    }

    private static async Task<IResult> PutAsync(
        string machineId,
        HttpContext httpContext,
        MachineSimulationService service,
        IAccountRepository accounts,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(httpContext, Permissions.ManageSetup, out var authority, out var accessError))
            return accessError!;
        if (!TryReadVersion(httpContext, machineId, out var expectedVersion, out var preconditionError))
            return preconditionError ?? await StaleAsync(httpContext, await service.GetAsync(machineId, cancellationToken), accounts, cancellationToken);
        var (definition, bodyError) = await ReadDefinitionAsync(httpContext, cancellationToken);
        if (definition is null) return bodyError!;

        try
        {
            var result = await service.SaveDraftAsync(machineId, definition, expectedVersion, authority!, cancellationToken);
            return await WriteResultAsync(result, httpContext, accounts, cancellationToken);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, httpContext);
        }
    }

    private static async Task<IResult> ValidateAsync(
        string machineId, HttpContext httpContext, MachineSimulationService service, CancellationToken cancellationToken)
    {
        var (definition, bodyError) = await ReadDefinitionAsync(httpContext, cancellationToken);
        if (definition is null) return bodyError!;
        var issues = await service.ValidateAsync(machineId, definition, cancellationToken);
        return issues is null ? NotFound(httpContext) : Results.Ok(new { issues = issues.Select(Issue) });
    }

    private static async Task<IResult> PublishAsync(
        string machineId,
        HttpContext httpContext,
        MachineSimulationService service,
        IAccountRepository accounts,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(httpContext, Permissions.ManageSetup, out var authority, out var accessError))
            return accessError!;
        if (!TryReadVersion(httpContext, machineId, out var expectedVersion, out var preconditionError))
            return preconditionError ?? await StaleAsync(httpContext, await service.GetAsync(machineId, cancellationToken), accounts, cancellationToken);
        try
        {
            var result = await service.PublishAsync(machineId, expectedVersion, authority!, cancellationToken);
            return await WriteResultAsync(result, httpContext, accounts, cancellationToken);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, httpContext);
        }
    }

    private static async Task<IResult> ListAssetsAsync(
        string machineId, HttpContext httpContext, MachineSimulationService service, CancellationToken cancellationToken)
    {
        var assets = await service.ListAssetsAsync(machineId, cancellationToken);
        return assets is null ? NotFound(httpContext) : Results.Ok(new { items = assets.Select(Asset) });
    }

    private static async Task<IResult> UploadAssetAsync(
        string machineId,
        string? name,
        HttpContext httpContext,
        MachineSimulationAssetStore store,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(httpContext, Permissions.ManageSetup, out var authority, out var accessError))
            return accessError!;
        try
        {
            var asset = await store.AddAsync(machineId, name, httpContext.Request.Body, authority!, cancellationToken);
            if (asset is null) return NotFound(httpContext);
            return Results.Created(
                $"/api/v1/machines/{Uri.EscapeDataString(asset.MachineId)}/simulation-assets/{asset.AssetId}/content",
                Asset(asset));
        }
        catch (MachineSimulationAssetException exception)
        {
            return PlanningHttpSupport.Error(
                exception.Code == "asset_too_large" ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status422UnprocessableEntity,
                exception.Code, exception.Message, httpContext);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, httpContext);
        }
    }

    private static async Task<IResult> GetAssetContentAsync(
        string machineId,
        string assetId,
        HttpContext httpContext,
        MachineSimulationService service,
        MachineSimulationAssetStore store,
        CancellationToken cancellationToken)
    {
        var asset = await service.GetAssetAsync(machineId, assetId, cancellationToken);
        if (asset is null)
            return PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "resource_not_found", "The Machine mesh was not found.", httpContext);
        var path = store.PathOf(asset);
        if (path is null)
            return PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "asset_file_missing",
                "The Machine mesh is recorded but its file is missing on the Server; import it again.", httpContext);
        httpContext.Response.Headers.ETag = $"\"{asset.Sha256}\"";
        return Results.File(path, asset.MediaType, asset.FileName);
    }

    private static async Task<IResult> WriteResultAsync(
        MachineSimulationWriteResult result, HttpContext httpContext, IAccountRepository accounts, CancellationToken cancellationToken)
    {
        switch (result.Outcome)
        {
            case MachineSimulationOutcome.Saved:
            case MachineSimulationOutcome.Published:
                SetEntityTag(httpContext.Response, result.Record!);
                return Response(result.Record!, result.Issues);
            case MachineSimulationOutcome.MachineNotFound:
                return NotFound(httpContext);
            case MachineSimulationOutcome.Invalid:
                return PlanningHttpSupport.Error(
                    StatusCodes.Status422UnprocessableEntity,
                    "machine_definition_invalid",
                    "The machine definition has errors; fix them before saving or publishing.",
                    httpContext,
                    result.Issues.Select(Issue));
            default:
                return await StaleAsync(httpContext, result.Record, accounts, cancellationToken);
        }
    }

    private static bool TryReadVersion(HttpContext httpContext, string machineId, out int version, out IResult? error)
    {
        error = null;
        var ifMatch = httpContext.Request.Headers.IfMatch;
        if (StringValues.IsNullOrEmpty(ifMatch))
        {
            version = 0;
            error = PlanningHttpSupport.Error(StatusCodes.Status428PreconditionRequired, "precondition_required",
                $"If-Match: \"{EntityTagKind}:{machineId}:v<draftVersion>\" is required.", httpContext);
            return false;
        }
        return PlanningHttpSupport.TryReadExpectedVersion(ifMatch, EntityTagKind, machineId.Trim(), out version);
    }

    private static async Task<(MachineSimulationDefinition? Definition, IResult? Error)> ReadDefinitionAsync(
        HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (httpContext.Request.ContentLength > MachineSimulationDefinitions.MaximumDefinitionBytes)
            return (null, TooLarge(httpContext));
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await httpContext.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MachineSimulationDefinitions.MaximumDefinitionBytes) return (null, TooLarge(httpContext));
            buffer.Write(chunk, 0, read);
        }
        try
        {
            return (MachineSimulationDefinition.Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray())), null);
        }
        catch (JsonException exception)
        {
            return (null, PlanningHttpSupport.Error(StatusCodes.Status400BadRequest, "invalid_request",
                $"The machine definition is not valid JSON of schema version {MachineSimulationDefinition.CurrentSchemaVersion}: {exception.Message}",
                httpContext));
        }
    }

    private static IResult TooLarge(HttpContext httpContext) => PlanningHttpSupport.Error(
        StatusCodes.Status413PayloadTooLarge, "definition_too_large",
        $"A machine definition may be at most {MachineSimulationDefinitions.MaximumDefinitionBytes / (1024 * 1024)} MB.", httpContext);

    /// <summary>412 <c>resource_version_stale</c> naming who saved the definition last and when.</summary>
    private static async Task<IResult> StaleAsync(
        HttpContext httpContext, MachineSimulationRecord? current, IAccountRepository accounts, CancellationToken cancellationToken)
    {
        const string message = "The machine definition changed since you opened it, so your change was not saved.";
        if (current is null)
            return PlanningHttpSupport.Error(StatusCodes.Status412PreconditionFailed, "resource_version_stale", message, httpContext);
        var changedBy = current.UpdatedBy;
        if (!string.IsNullOrWhiteSpace(changedBy)
            && await accounts.FindDisplayNameAsync(changedBy, cancellationToken) is { } displayName
            && !string.Equals(displayName, changedBy, StringComparison.OrdinalIgnoreCase))
        {
            changedBy = $"{displayName} ({changedBy})";
        }
        return Results.Json(
            new
            {
                error = new
                {
                    code = "resource_version_stale",
                    message,
                    correlationId = httpContext.TraceIdentifier,
                    details = new[] { new { currentVersion = current.DraftVersion, currentEntityTag = EntityTag(current) } },
                    conflict = new
                    {
                        resource = "Machine definition",
                        changedBy,
                        changedAt = current.UpdatedAt,
                        advice = "Someone saved this machine definition after you opened it, so your change was not saved. " +
                                 "Reload the definition, check what changed, then make your change again."
                    }
                }
            },
            statusCode: StatusCodes.Status412PreconditionFailed);
    }

    private static IResult Response(MachineSimulationRecord record, IReadOnlyList<MachineSimulationIssue> issues) => Results.Ok(new
    {
        machineId = record.MachineId,
        draft = JsonNode.Parse(record.Draft.ToJson()),
        draftVersion = record.DraftVersion,
        published = record.Published is null ? null : JsonNode.Parse(record.Published.ToJson()),
        publishedVersion = record.PublishedVersion,
        publishedAt = record.PublishedAt,
        publishedBy = record.PublishedBy,
        updatedAt = record.UpdatedAt,
        updatedBy = record.UpdatedBy,
        hasUnpublishedChanges = record.Published is null || record.Published.ToJson() != record.Draft.ToJson(),
        issues = issues.Select(Issue)
    });

    private static object Issue(MachineSimulationIssue issue) =>
        new { path = issue.Path, code = issue.Code, message = issue.Message, severity = issue.Severity };

    private static object Asset(MachineSimulationAsset asset) => new
    {
        assetId = asset.AssetId,
        fileName = asset.FileName,
        mediaType = asset.MediaType,
        sizeBytes = asset.SizeBytes,
        sha256 = asset.Sha256,
        createdAt = asset.CreatedAt,
        createdBy = asset.CreatedBy
    };

    private static IResult NotFound(HttpContext context) => PlanningHttpSupport.Error(
        StatusCodes.Status404NotFound, "resource_not_found", "The requested Machine was not found.", context);

    private static string EntityTag(MachineSimulationRecord record) => $"\"{EntityTagKind}:{record.MachineId}:v{record.DraftVersion}\"";

    private static void SetEntityTag(HttpResponse response, MachineSimulationRecord record) => response.Headers.ETag = EntityTag(record);
}
