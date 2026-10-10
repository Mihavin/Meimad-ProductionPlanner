using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Machines;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api.Cases;

/// <summary>An X/Y/Z triple of a CAM setup; an omitted component is zero.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CamSetupVectorRequest(double? X = null, double? Y = null, double? Z = null)
{
    internal MachineVector3 ToDomain() => new(X ?? 0, Y ?? 0, Z ?? 0);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PutCaseOperationCamSetupRequest(
    string? Source,
    string? WorkOffset = null,
    CamSetupVectorRequest? OriginMm = null,
    CamSetupVectorRequest? RotationDeg = null,
    string? Notes = null)
{
    internal CaseOperationCamSetupInput ToInput() => new(
        Source, WorkOffset, OriginMm?.ToDomain(), RotationDeg?.ToDomain(), Notes);
}

internal sealed record CaseOperationCamSetupResponse(
    string CaseOperationId,
    string Source,
    string? WorkOffset,
    MachineVector3 OriginMm,
    MachineVector3 RotationDeg,
    string? Notes,
    int Version,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy)
{
    internal static CaseOperationCamSetupResponse FromDomain(CaseOperationCamSetup setup) => new(
        setup.CaseOperationId,
        setup.Source,
        setup.WorkOffset,
        setup.OriginMm,
        setup.RotationDeg,
        setup.Notes,
        setup.Version,
        setup.UpdatedAt,
        setup.UpdatedBy);
}

/// <summary>
/// <c>/api/v1/cases/{caseId}/operations/{operationId}/cam-setup</c> (schema v113): read by every
/// signed-in user; PUT and DELETE need <c>cases.edit</c>. PUT creates with <c>If-None-Match: *</c>
/// and replaces with <c>If-Match</c> carrying the current entity tag; neither header is 428.
/// </summary>
internal static class CaseOperationCamSetupEndpoints
{
    private const string EntityTagKind = "case-operation-cam-setup";

    internal static void MapCaseOperationCamSetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var route = endpoints.MapGroup("/api/v1/cases/{caseId}/operations/{operationId}/cam-setup");
        route.MapGet(string.Empty, GetAsync);
        route.MapPut(string.Empty, PutAsync);
        route.MapDelete(string.Empty, DeleteAsync);
    }

    private static async Task<IResult> GetAsync(
        string caseId,
        string operationId,
        HttpContext httpContext,
        CaseOperationCamSetupService service,
        CancellationToken cancellationToken)
    {
        var read = await service.GetAsync(caseId, operationId, cancellationToken);
        if (!read.OperationExists) return OperationNotFound(httpContext);
        if (read.Setup is null) return SetupNotFound(httpContext);
        SetEntityTag(httpContext.Response, read.Setup);
        return Results.Ok(CaseOperationCamSetupResponse.FromDomain(read.Setup));
    }

    private static async Task<IResult> PutAsync(
        string caseId,
        string operationId,
        PutCaseOperationCamSetupRequest request,
        HttpContext httpContext,
        CaseOperationCamSetupService service,
        IAccountRepository accounts,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(httpContext, Permissions.EditCases, out var authority, out var accessError))
            return accessError!;

        var ifMatch = httpContext.Request.Headers.IfMatch;
        var ifNoneMatch = httpContext.Request.Headers.IfNoneMatch;
        int? expectedVersion;
        if (!StringValues.IsNullOrEmpty(ifMatch))
        {
            if (!StringValues.IsNullOrEmpty(ifNoneMatch))
                return PreconditionRequired(httpContext, "Send either If-None-Match: * to create or If-Match to replace, not both.");
            if (!PlanningHttpSupport.TryReadExpectedVersion(ifMatch, EntityTagKind, operationId, out var version))
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
                $"Send If-None-Match: * to create the CAM setup or If-Match: \"{EntityTagKind}:{operationId}:v<version>\" to replace it.");
        }

        try
        {
            var result = await service.SaveAsync(caseId, operationId, request.ToInput(), expectedVersion, authority!, cancellationToken);
            switch (result.Outcome)
            {
                case CaseOperationCamSetupOutcome.Created:
                    SetEntityTag(httpContext.Response, result.Setup!);
                    return Results.Created(
                        $"/api/v1/cases/{Uri.EscapeDataString(caseId)}/operations/{Uri.EscapeDataString(operationId)}/cam-setup",
                        CaseOperationCamSetupResponse.FromDomain(result.Setup!));
                case CaseOperationCamSetupOutcome.Updated:
                    SetEntityTag(httpContext.Response, result.Setup!);
                    return Results.Ok(CaseOperationCamSetupResponse.FromDomain(result.Setup!));
                case CaseOperationCamSetupOutcome.OperationNotFound:
                    return OperationNotFound(httpContext);
                default:
                    // AlreadyExists, SetupNotFound (removed meanwhile) and Stale: the change was based on old data.
                    return await Stale(httpContext, result.Setup, accounts, cancellationToken);
            }
        }
        catch (CaseOperationCamSetupValidationException exception)
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
        HttpContext httpContext,
        CaseOperationCamSetupService service,
        IAccountRepository accounts,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(httpContext, Permissions.EditCases, out var authority, out var accessError))
            return accessError!;

        var ifMatch = httpContext.Request.Headers.IfMatch;
        if (StringValues.IsNullOrEmpty(ifMatch))
            return PreconditionRequired(httpContext, $"If-Match: \"{EntityTagKind}:{operationId}:v<version>\" is required.");
        if (!PlanningHttpSupport.TryReadExpectedVersion(ifMatch, EntityTagKind, operationId, out var expectedVersion))
            return await Stale(httpContext, null, accounts, cancellationToken);

        try
        {
            var result = await service.DeleteAsync(caseId, operationId, expectedVersion, authority!, cancellationToken);
            return result.Outcome switch
            {
                CaseOperationCamSetupOutcome.Deleted => Results.NoContent(),
                CaseOperationCamSetupOutcome.OperationNotFound => OperationNotFound(httpContext),
                CaseOperationCamSetupOutcome.SetupNotFound => SetupNotFound(httpContext),
                _ => await Stale(httpContext, result.Setup, accounts, cancellationToken)
            };
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, httpContext);
        }
    }

    /// <summary>
    /// 412 <c>resource_version_stale</c>. With the current setup known, the conflict names who saved it
    /// and when from the row itself; otherwise the conflict middleware adds it from its change journal.
    /// </summary>
    private static async Task<IResult> Stale(
        HttpContext httpContext, CaseOperationCamSetup? current, IAccountRepository accounts, CancellationToken cancellationToken)
    {
        const string message = "The Operation CAM setup changed since you opened it, so your change was not saved.";
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
                        resource = "Operation CAM setup",
                        changedBy,
                        changedAt = current.UpdatedAt,
                        advice = "Someone saved this Operation CAM setup after you opened it, so your change was not saved. " +
                                 "Refresh to load the current CAM setup, check what changed, then make your change again."
                    }
                }
            },
            statusCode: StatusCodes.Status412PreconditionFailed);
    }

    private static IResult PreconditionRequired(HttpContext httpContext, string message) =>
        PlanningHttpSupport.Error(StatusCodes.Status428PreconditionRequired, "precondition_required", message, httpContext);

    private static IResult OperationNotFound(HttpContext httpContext) =>
        PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "resource_not_found",
            "The requested Case Operation was not found in this Case.", httpContext);

    private static IResult SetupNotFound(HttpContext httpContext) =>
        PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "cam_setup_not_found",
            "This Operation has no CAM setup yet.", httpContext);

    private static IResult ValidationError(CaseOperationCamSetupValidationException exception, HttpContext httpContext) =>
        PlanningHttpSupport.Error(
            StatusCodes.Status422UnprocessableEntity,
            "validation_failed",
            exception.Message,
            httpContext,
            exception.Issues.Select(issue => (object)new { field = issue.Field, code = issue.Code, message = issue.Message }));

    private static string EntityTag(CaseOperationCamSetup setup) =>
        $"\"{EntityTagKind}:{setup.CaseOperationId}:v{setup.Version}\"";

    private static void SetEntityTag(HttpResponse response, CaseOperationCamSetup setup) =>
        response.Headers.ETag = EntityTag(setup);
}
