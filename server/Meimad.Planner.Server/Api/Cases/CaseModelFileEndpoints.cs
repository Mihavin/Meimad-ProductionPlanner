using System.Globalization;
using System.Text.RegularExpressions;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Cases;

namespace Meimad.Planner.Server.Api.Cases;

internal sealed record CaseModelFileResponse(
    string CaseModelFileId,
    string CaseId,
    string? CaseOperationId,
    string Kind,
    string Format,
    string FilePath,
    string Label,
    bool IsPrimary,
    int SortOrder,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static CaseModelFileResponse FromDomain(CaseModelFile file) => new(
        file.CaseModelFileId,
        file.CaseId,
        file.CaseOperationId,
        file.Kind,
        file.Format,
        file.FilePath,
        file.Label,
        file.IsPrimary,
        file.SortOrder,
        file.Version,
        file.CreatedAt,
        file.UpdatedAt);
}

internal sealed record CaseModelFileListResponse(IReadOnlyList<CaseModelFileResponse> Items);

internal sealed record CreateCaseModelFileRequest(
    string? FilePath,
    string? Kind = null,
    string? Label = null,
    string? CaseOperationId = null,
    bool IsPrimary = false);

internal sealed record UpdateCaseModelFileRequest(
    string? Kind = null,
    string? Label = null,
    string? CaseOperationId = null,
    bool ClearCaseOperation = false,
    bool? IsPrimary = null,
    int? SortOrder = null);

internal static class CaseModelFileEndpoints
{
    private const string ClientIdHeader = "X-Meimad-Client-Id";
    private const string EditGenerationHeader = "X-Meimad-Edit-Generation";
    private static readonly Regex EntityTagPattern = new(
        "^\"case-model-file:(?<id>[^:\"]+):v(?<version>\\d+)\"$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static void MapCaseModelFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var files = endpoints.MapGroup("/api/v1/cases/{caseId}/model-files");
        files.MapGet(string.Empty, ListAsync);
        files.MapPost(string.Empty, CreateAsync);
        files.MapPatch("/{fileId}", UpdateAsync);
        files.MapDelete("/{fileId}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(
        string caseId,
        HttpContext httpContext,
        CaseService cases,
        CaseModelFileService service,
        CancellationToken cancellationToken)
    {
        if (await cases.GetByIdAsync(caseId, cancellationToken) is null)
        {
            return CaseNotFound(httpContext);
        }

        var items = await service.ListAsync(caseId, cancellationToken);
        return Results.Ok(new CaseModelFileListResponse(items.Select(CaseModelFileResponse.FromDomain).ToArray()));
    }

    private static async Task<IResult> CreateAsync(
        string caseId,
        CreateCaseModelFileRequest request,
        HttpContext httpContext,
        CaseService cases,
        CaseModelFileService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadEditAuthority(httpContext, out var authority, out var error))
        {
            return error!;
        }

        if (await cases.GetByIdAsync(caseId, cancellationToken) is null)
        {
            return CaseNotFound(httpContext);
        }

        try
        {
            var created = await service.CreateAsync(
                caseId,
                new CaseModelFileCreate(request.FilePath ?? string.Empty, request.Kind, request.Label, request.CaseOperationId, request.IsPrimary),
                authority!,
                cancellationToken);
            SetEntityTag(httpContext.Response, created);
            return Results.Created(
                $"/api/v1/cases/{created.CaseId}/model-files/{created.CaseModelFileId}",
                CaseModelFileResponse.FromDomain(created));
        }
        catch (CaseModelFileValidationException exception)
        {
            return ValidationError(exception, httpContext);
        }
        catch (EditModeMutationException exception)
        {
            return Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, httpContext);
        }
    }

    private static async Task<IResult> UpdateAsync(
        string caseId,
        string fileId,
        UpdateCaseModelFileRequest request,
        HttpContext httpContext,
        CaseModelFileService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadEditAuthority(httpContext, out var authority, out var error))
        {
            return error!;
        }

        if (!TryReadExpectedVersion(httpContext, fileId, out var expectedVersion, out var preconditionError))
        {
            return preconditionError!;
        }

        try
        {
            var updated = await service.UpdateAsync(
                caseId,
                fileId,
                expectedVersion,
                new CaseModelFileUpdate(
                    request.Kind,
                    request.Label,
                    request.CaseOperationId,
                    request.ClearCaseOperation,
                    request.IsPrimary,
                    request.SortOrder),
                authority!,
                cancellationToken);
            SetEntityTag(httpContext.Response, updated);
            return Results.Ok(CaseModelFileResponse.FromDomain(updated));
        }
        catch (CaseModelFileNotFoundException)
        {
            return Error(StatusCodes.Status404NotFound, "resource_not_found", "The requested Case model file was not found.", httpContext);
        }
        catch (CaseModelFileVersionConflictException exception)
        {
            return Error(
                StatusCodes.Status412PreconditionFailed,
                "precondition_failed",
                $"Case model file '{exception.CaseModelFileId}' changed since version {exception.ExpectedVersion}. Reload and retry.",
                httpContext);
        }
        catch (CaseModelFileValidationException exception)
        {
            return ValidationError(exception, httpContext);
        }
        catch (EditModeMutationException exception)
        {
            return Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, httpContext);
        }
    }

    private static async Task<IResult> DeleteAsync(
        string caseId,
        string fileId,
        HttpContext httpContext,
        CaseModelFileService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadEditAuthority(httpContext, out var authority, out var error))
        {
            return error!;
        }

        try
        {
            return await service.DeleteAsync(caseId, fileId, authority!, cancellationToken)
                ? Results.NoContent()
                : Error(StatusCodes.Status404NotFound, "resource_not_found", "The requested Case model file was not found.", httpContext);
        }
        catch (CaseModelFileValidationException exception)
        {
            return ValidationError(exception, httpContext);
        }
        catch (EditModeMutationException exception)
        {
            return Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, httpContext);
        }
    }

    private static bool TryReadExpectedVersion(
        HttpContext httpContext,
        string fileId,
        out int expectedVersion,
        out IResult? error)
    {
        expectedVersion = 0;
        error = null;
        var header = httpContext.Request.Headers.IfMatch.ToString();
        var match = EntityTagPattern.Match(header);
        if (!match.Success
            || !string.Equals(match.Groups["id"].Value, fileId, StringComparison.Ordinal)
            || !int.TryParse(match.Groups["version"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out expectedVersion))
        {
            error = Error(
                StatusCodes.Status428PreconditionRequired,
                "precondition_required",
                $"If-Match must carry the current \"case-model-file:{fileId}:v<version>\" entity tag.",
                httpContext);
            return false;
        }

        return true;
    }

    private static bool TryReadEditAuthority(
        HttpContext httpContext,
        out EditAuthority? editAuthority,
        out IResult? error)
    {
        editAuthority = null;
        error = null;
        var clientId = httpContext.Request.Headers[ClientIdHeader].ToString();
        var generationValue = httpContext.Request.Headers[EditGenerationHeader].ToString();
        if (string.IsNullOrWhiteSpace(clientId)
            || !long.TryParse(generationValue, NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || generation < 0)
        {
            error = Error(
                StatusCodes.Status428PreconditionRequired,
                "precondition_required",
                $"{ClientIdHeader} and a valid {EditGenerationHeader} are required.",
                httpContext);
            return false;
        }

        editAuthority = new EditAuthority(clientId, generation);
        return true;
    }

    private static void SetEntityTag(HttpResponse response, CaseModelFile file) =>
        response.Headers.ETag = $"\"case-model-file:{file.CaseModelFileId}:v{file.Version}\"";

    private static IResult CaseNotFound(HttpContext httpContext) =>
        Error(StatusCodes.Status404NotFound, "resource_not_found", "The requested Case was not found.", httpContext);

    private static IResult ValidationError(CaseModelFileValidationException exception, HttpContext httpContext) =>
        Results.Json(
            new
            {
                error = new
                {
                    code = "validation_failed",
                    message = "Case model file validation failed.",
                    correlationId = httpContext.TraceIdentifier,
                    details = new[] { new { field = exception.Field, code = exception.Code, message = exception.Message } }
                }
            },
            statusCode: StatusCodes.Status422UnprocessableEntity);

    private static IResult Error(int status, string code, string message, HttpContext httpContext) =>
        Results.Json(
            new
            {
                error = new
                {
                    code,
                    message,
                    correlationId = httpContext.TraceIdentifier,
                    details = Array.Empty<object>()
                }
            },
            statusCode: status);
}
