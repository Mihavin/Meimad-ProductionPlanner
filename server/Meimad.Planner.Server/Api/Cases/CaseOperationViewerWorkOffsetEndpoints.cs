using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.EditMode;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api.Cases;

/// <summary>The work offsets of one viewer machine: label to axis letter to value (mm or degrees); a null axis is not set.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PutCaseOperationViewerWorkOffsetsRequest(Dictionary<string, Dictionary<string, double?>?>? Offsets);

internal sealed record CaseOperationViewerWorkOffsetsResponse(
    string CaseOperationId,
    string ViewerMachine,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Offsets,
    int Version,
    string EntityTag,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy)
{
    internal static CaseOperationViewerWorkOffsetsResponse FromDomain(CaseOperationViewerWorkOffsets offsets) => new(
        offsets.CaseOperationId,
        offsets.ViewerMachine,
        offsets.Offsets,
        offsets.Version,
        CaseOperationViewerWorkOffsetEndpoints.EntityTag(offsets),
        offsets.UpdatedAt,
        offsets.UpdatedBy);
}

internal sealed record CaseOperationViewerWorkOffsetsListResponse(IReadOnlyList<CaseOperationViewerWorkOffsetsResponse> Items);

/// <summary>
/// <c>/api/v1/cases/{caseId}/operations/{operationId}/viewer-work-offsets</c> (schema v120): the list is read by every
/// signed-in user; PUT and DELETE of <c>…/{viewerMachine}</c> need <c>cases.edit</c>. PUT creates with
/// <c>If-None-Match: *</c> and replaces with <c>If-Match</c> carrying the current entity tag; neither header is 428.
/// </summary>
internal static class CaseOperationViewerWorkOffsetEndpoints
{
    private const string EntityTagKind = "case-operation-viewer-work-offsets";

    internal static void MapCaseOperationViewerWorkOffsetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var route = endpoints.MapGroup("/api/v1/cases/{caseId}/operations/{operationId}/viewer-work-offsets");
        route.MapGet(string.Empty, ListAsync);
        route.MapPut("/{viewerMachine}", PutAsync);
        route.MapDelete("/{viewerMachine}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(
        string caseId,
        string operationId,
        HttpContext httpContext,
        CaseOperationViewerWorkOffsetService service,
        CancellationToken cancellationToken)
    {
        var list = await service.ListAsync(caseId, operationId, cancellationToken);
        if (!list.OperationExists) return OperationNotFound(httpContext);
        return Results.Ok(new CaseOperationViewerWorkOffsetsListResponse(
            list.Items.Select(CaseOperationViewerWorkOffsetsResponse.FromDomain).ToArray()));
    }

    private static async Task<IResult> PutAsync(
        string caseId,
        string operationId,
        string viewerMachine,
        PutCaseOperationViewerWorkOffsetsRequest request,
        HttpContext httpContext,
        CaseOperationViewerWorkOffsetService service,
        IAccountRepository accounts,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(httpContext, Permissions.EditCases, out var authority, out var accessError))
            return accessError!;

        string machine;
        try
        {
            machine = CaseOperationViewerWorkOffsetService.ValidateMachine(viewerMachine);
        }
        catch (CaseOperationViewerWorkOffsetValidationException exception)
        {
            return ValidationError(exception, httpContext);
        }

        var resourceId = ResourceId(operationId, machine);
        var ifMatch = httpContext.Request.Headers.IfMatch;
        var ifNoneMatch = httpContext.Request.Headers.IfNoneMatch;
        int? expectedVersion;
        if (!StringValues.IsNullOrEmpty(ifMatch))
        {
            if (!StringValues.IsNullOrEmpty(ifNoneMatch))
                return PreconditionRequired(httpContext, "Send either If-None-Match: * to create or If-Match to replace, not both.");
            if (!PlanningHttpSupport.TryReadExpectedVersion(ifMatch, EntityTagKind, resourceId, out var version))
                return await Stale(httpContext, null, accounts, cancellationToken);
            expectedVersion = version;
        }
        else if (ifNoneMatch.Count == 1 && ifNoneMatch[0]?.Trim() == "*")
        {
            expectedVersion = null;
        }
        else
        {
            return PreconditionRequired(httpContext,
                $"Send If-None-Match: * to create the work offsets or If-Match: \"{EntityTagKind}:{resourceId}:v<version>\" to replace them.");
        }

        try
        {
            var result = await service.SaveAsync(
                caseId, operationId, machine,
                request.Offsets?.ToDictionary(pair => pair.Key, pair => pair.Value),
                expectedVersion, authority!, cancellationToken);
            switch (result.Outcome)
            {
                case CaseOperationViewerWorkOffsetOutcome.Created:
                    SetEntityTag(httpContext.Response, result.Offsets!);
                    return Results.Created(
                        $"/api/v1/cases/{Uri.EscapeDataString(caseId)}/operations/{Uri.EscapeDataString(operationId)}/viewer-work-offsets/{Uri.EscapeDataString(machine)}",
                        CaseOperationViewerWorkOffsetsResponse.FromDomain(result.Offsets!));
                case CaseOperationViewerWorkOffsetOutcome.Updated:
                    SetEntityTag(httpContext.Response, result.Offsets!);
                    return Results.Ok(CaseOperationViewerWorkOffsetsResponse.FromDomain(result.Offsets!));
                case CaseOperationViewerWorkOffsetOutcome.OperationNotFound:
                    return OperationNotFound(httpContext);
                default:
                    // AlreadyExists, OffsetsNotFound (removed meanwhile) and Stale: the change was based on old data.
                    return await Stale(httpContext, result.Offsets, accounts, cancellationToken);
            }
        }
        catch (CaseOperationViewerWorkOffsetValidationException exception)
        {
            return ValidationError(exception, httpContext);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, httpContext);
        }
    }

    private static async Task<IResult> DeleteAsync(
        string caseId,
        string operationId,
        string viewerMachine,
        HttpContext httpContext,
        CaseOperationViewerWorkOffsetService service,
        IAccountRepository accounts,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(httpContext, Permissions.EditCases, out var authority, out var accessError))
            return accessError!;

        try
        {
            var machine = CaseOperationViewerWorkOffsetService.ValidateMachine(viewerMachine);
            var resourceId = ResourceId(operationId, machine);
            var ifMatch = httpContext.Request.Headers.IfMatch;
            if (StringValues.IsNullOrEmpty(ifMatch))
                return PreconditionRequired(httpContext, $"If-Match: \"{EntityTagKind}:{resourceId}:v<version>\" is required.");
            if (!PlanningHttpSupport.TryReadExpectedVersion(ifMatch, EntityTagKind, resourceId, out var expectedVersion))
                return await Stale(httpContext, null, accounts, cancellationToken);

            var result = await service.DeleteAsync(caseId, operationId, machine, expectedVersion, authority!, cancellationToken);
            return result.Outcome switch
            {
                CaseOperationViewerWorkOffsetOutcome.Deleted => Results.NoContent(),
                CaseOperationViewerWorkOffsetOutcome.OperationNotFound => OperationNotFound(httpContext),
                CaseOperationViewerWorkOffsetOutcome.OffsetsNotFound => PlanningHttpSupport.Error(
                    StatusCodes.Status404NotFound, "viewer_work_offsets_not_found",
                    "This Operation has no work offsets for that viewer machine.", httpContext),
                _ => await Stale(httpContext, result.Offsets, accounts, cancellationToken)
            };
        }
        catch (CaseOperationViewerWorkOffsetValidationException exception)
        {
            return ValidationError(exception, httpContext);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, httpContext);
        }
    }

    /// <summary>
    /// 412 <c>resource_version_stale</c>. With the current set known, the conflict names who saved it and when from the
    /// row itself; otherwise the conflict middleware adds it from its change journal.
    /// </summary>
    private static async Task<IResult> Stale(
        HttpContext httpContext, CaseOperationViewerWorkOffsets? current, IAccountRepository accounts, CancellationToken cancellationToken)
    {
        const string message = "The Operation work offsets changed since you opened them, so your change was not saved.";
        if (current is null)
            return PlanningHttpSupport.Error(StatusCodes.Status412PreconditionFailed, "resource_version_stale", message, httpContext);

        string? changedBy = current.UpdatedBy;
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
                    details = new[] { new { currentVersion = current.Version, currentEntityTag = EntityTag(current) } },
                    conflict = new
                    {
                        resource = "Operation work offsets",
                        changedBy,
                        changedAt = current.UpdatedAt,
                        advice = "Someone saved the work offsets of this Operation after you opened them, so your change was not saved. " +
                                 "Reopen the program to load the current work offsets, check what changed, then make your change again."
                    }
                }
            },
            statusCode: StatusCodes.Status412PreconditionFailed);
    }

    private static string ResourceId(string operationId, string viewerMachine) => $"{operationId.Trim()}/{viewerMachine}";

    internal static string EntityTag(CaseOperationViewerWorkOffsets offsets) =>
        $"\"{EntityTagKind}:{ResourceId(offsets.CaseOperationId, offsets.ViewerMachine)}:v{offsets.Version}\"";

    private static void SetEntityTag(HttpResponse response, CaseOperationViewerWorkOffsets offsets) =>
        response.Headers.ETag = EntityTag(offsets);

    private static IResult PreconditionRequired(HttpContext httpContext, string message) =>
        PlanningHttpSupport.Error(StatusCodes.Status428PreconditionRequired, "precondition_required", message, httpContext);

    private static IResult OperationNotFound(HttpContext httpContext) =>
        PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "resource_not_found",
            "The requested Case Operation was not found in this Case.", httpContext);

    private static IResult ValidationError(CaseOperationViewerWorkOffsetValidationException exception, HttpContext httpContext) =>
        PlanningHttpSupport.Error(
            StatusCodes.Status422UnprocessableEntity,
            "validation_failed",
            exception.Message,
            httpContext,
            exception.Issues.Select(issue => (object)new { field = issue.Field, code = issue.Code, message = issue.Message }));
}
