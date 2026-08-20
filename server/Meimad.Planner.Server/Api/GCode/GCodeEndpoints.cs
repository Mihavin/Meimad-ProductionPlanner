using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Domain.GCode;

namespace Meimad.Planner.Server.Api.GCode;

internal static class GCodeEndpoints
{
    internal static void MapGCodeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var operations = endpoints.MapGroup("/api/v1/cases/{caseId}/operations/{caseOperationId}");
        operations.MapGet("/gcode", ReadCatalogAsync);
        operations.MapPost("/gcode-releases", ReleaseAsync).DisableAntiforgery();
        operations.MapGet("/gcode-releases/{releaseId}/file", DownloadReleaseAsync);
        operations.MapGet("/tool-table-releases/{toolTableReleaseId}/file", DownloadToolTableAsync);
    }

    private static async Task<IResult> ReadCatalogAsync(
        string caseId,
        string caseOperationId,
        HttpContext context,
        GCodeService service,
        CancellationToken token)
    {
        try
        {
            return Results.Ok(GCodeCatalogResponse.FromDomain(
                await service.ReadCatalogAsync(caseId, caseOperationId, token)));
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> ReleaseAsync(
        string caseId,
        string caseOperationId,
        HttpContext context,
        GCodeService service,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error))
        {
            return error!;
        }

        try
        {
            if (!context.Request.HasFormContentType)
            {
                throw new GCodeValidationException(
                    "contentType", "multipart_required", "Release G-code requires multipart/form-data.");
            }

            var form = await context.Request.ReadFormAsync(token);
            var release = await service.ReleaseAsync(new ReleaseGCodeCommand(
                caseId,
                caseOperationId,
                Text(form, "postprocessorId"),
                Text(form, "changeScope"),
                Text(form, "releaseComment"),
                Text(form, "processChangeDescription"),
                Boolean(form, "confirmNewProcessRevision"),
                Boolean(form, "reuseActiveToolTable"),
                Boolean(form, "confirmToolTable"),
                Upload(form.Files.GetFile("gcodeFile")),
                Upload(form.Files.GetFile("toolTableFile"))), authority!, token);
            return Results.Created(
                $"/api/v1/cases/{caseId}/operations/{caseOperationId}/gcode-releases/{release.GCodeReleaseId}",
                GCodeReleaseResponse.FromDomain(release));
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> DownloadReleaseAsync(
        string caseOperationId,
        string releaseId,
        HttpContext context,
        GCodeService service,
        CancellationToken token)
    {
        try
        {
            var file = await service.OpenReleaseFileAsync(caseOperationId, releaseId, token);
            context.Response.Headers["X-Meimad-Checksum-SHA256"] = file.FileHash;
            return Results.File(
                file.AbsolutePath,
                "application/octet-stream",
                file.OriginalFileName,
                enableRangeProcessing: true);
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> DownloadToolTableAsync(
        string caseOperationId,
        string toolTableReleaseId,
        HttpContext context,
        GCodeService service,
        CancellationToken token)
    {
        try
        {
            var file = await service.OpenToolTableFileAsync(
                caseOperationId, toolTableReleaseId, token);
            context.Response.Headers["X-Meimad-Checksum-SHA256"] = file.FileHash;
            return Results.File(
                file.AbsolutePath,
                "application/octet-stream",
                file.OriginalFileName,
                enableRangeProcessing: true);
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static bool TryMap(Exception exception, HttpContext context, out IResult? result)
    {
        result = exception switch
        {
            GCodeValidationException validation => PlanningHttpSupport.Error(
                422, "validation_failed", validation.Message, context,
                [new { field = validation.Field, code = validation.Code, message = validation.Message }]),
            GCodeOperationNotFoundException or GCodeReleaseNotFoundException or GCodeToolTableNotFoundException =>
                PlanningHttpSupport.Error(404, "resource_not_found", exception.Message, context),
            GCodePostprocessorNotFoundException =>
                PlanningHttpSupport.Error(422, "invalid_postprocessor", exception.Message, context),
            GCodeProcessStateException state =>
                PlanningHttpSupport.Error(409, state.Code, state.Message, context),
            GCodeFileUnavailableException or GCodeStorageException =>
                PlanningHttpSupport.Error(503, "release_storage_unavailable", exception.Message, context),
            EditModeMutationException edit =>
                PlanningHttpSupport.Error(409, edit.Code, edit.Message, context),
            _ => null
        };
        return result is not null;
    }

    private static string? Text(IFormCollection form, string name) =>
        form.TryGetValue(name, out var value) ? value.ToString() : null;

    private static bool Boolean(IFormCollection form, string name) =>
        form.TryGetValue(name, out var value)
        && bool.TryParse(value.ToString(), out var parsed)
        && parsed;

    private static UploadedReleaseFile? Upload(IFormFile? value) => value is null
        ? null
        : new UploadedReleaseFile(value.FileName, value.OpenReadStream(), value.Length);
}

internal sealed record ToolTableReleaseResponse(
    string ToolTableReleaseId,
    int RevisionNumber,
    string OriginalFileName,
    long FileSize,
    string FileHash,
    DateTimeOffset ReleasedAt,
    string ReleasedBy,
    string ReleaseComment,
    int? RequiredToolCount,
    IReadOnlyList<ReleasedToolResponse> Tools)
{
    internal static ToolTableReleaseResponse FromDomain(ToolTableRelease value) => new(
        value.ToolTableReleaseId, value.RevisionNumber, value.OriginalFileName,
        value.FileSize, value.FileHash, value.ReleasedAt, value.ReleasedBy,
        value.ReleaseComment, value.RequiredToolCount,
        value.Tools.Select(ReleasedToolResponse.FromDomain).ToArray());
}

internal sealed record ReleasedToolResponse(
    string ReleasedToolId,
    int RowNumber,
    string ToolIdentifier,
    string Description,
    bool IsRequired,
    bool RequiresMagazinePosition,
    bool IsActive,
    string? MagazinePosition)
{
    internal static ReleasedToolResponse FromDomain(ReleasedTool value) => new(
        value.ReleasedToolId, value.RowNumber, value.ToolIdentifier,
        value.Description, value.IsRequired, value.RequiresMagazinePosition,
        value.IsActive, value.MagazinePosition);
}

internal sealed record ProcessRevisionResponse(
    string ProcessRevisionId,
    int ProcessRevisionNumber,
    bool IsActive,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string ChangeDescription,
    int Version,
    ToolTableReleaseResponse ToolTable)
{
    internal static ProcessRevisionResponse FromDomain(ProcessRevision value) => new(
        value.ProcessRevisionId, value.ProcessRevisionNumber, value.IsActive,
        value.CreatedAt, value.CreatedBy, value.ChangeDescription, value.Version,
        ToolTableReleaseResponse.FromDomain(value.ToolTable));
}

internal sealed record GCodeReleaseResponse(
    string GCodeReleaseId,
    string ProcessRevisionId,
    int ProcessRevisionNumber,
    string PostprocessorId,
    string PostprocessorName,
    int PostSpecificRevision,
    string OriginalFileName,
    long FileSize,
    string FileHash,
    DateTimeOffset ReleasedAt,
    string ReleasedBy,
    string ChangeScope,
    string ReleaseComment,
    string ToolTableReleaseId,
    bool IsCurrentForProcessAndPost,
    bool IsForActiveProcess)
{
    internal static GCodeReleaseResponse FromDomain(GCodeRelease value) => new(
        value.GCodeReleaseId, value.ProcessRevisionId, value.ProcessRevisionNumber,
        value.PostprocessorId, value.PostprocessorName, value.PostSpecificRevision,
        value.OriginalFileName, value.FileSize, value.FileHash, value.ReleasedAt,
        value.ReleasedBy, value.ChangeScope, value.ReleaseComment,
        value.ToolTableReleaseId, value.IsCurrentForProcessAndPost,
        value.IsForActiveProcess);
}

internal sealed record PostprocessorReleaseStatusResponse(
    string PostprocessorId,
    string PostprocessorName,
    bool IsActive,
    string Status,
    GCodeReleaseResponse? CurrentRelease,
    GCodeReleaseResponse? LatestHistoricalRelease)
{
    internal static PostprocessorReleaseStatusResponse FromDomain(PostprocessorReleaseStatus value) => new(
        value.PostprocessorId, value.PostprocessorName, value.IsActive, value.Status,
        value.CurrentRelease is null ? null : GCodeReleaseResponse.FromDomain(value.CurrentRelease),
        value.LatestHistoricalRelease is null
            ? null
            : GCodeReleaseResponse.FromDomain(value.LatestHistoricalRelease));
}

internal sealed record GCodeCatalogResponse(
    string CaseOperationId,
    ProcessRevisionResponse? ActiveProcessRevision,
    IReadOnlyList<ProcessRevisionResponse> ProcessRevisions,
    IReadOnlyList<PostprocessorReleaseStatusResponse> Postprocessors,
    IReadOnlyList<GCodeReleaseResponse> Releases)
{
    internal static GCodeCatalogResponse FromDomain(OperationGCodeCatalog value) => new(
        value.CaseOperationId,
        value.ActiveProcessRevision is null
            ? null
            : ProcessRevisionResponse.FromDomain(value.ActiveProcessRevision),
        value.ProcessRevisions.Select(ProcessRevisionResponse.FromDomain).ToArray(),
        value.Postprocessors.Select(PostprocessorReleaseStatusResponse.FromDomain).ToArray(),
        value.Releases.Select(GCodeReleaseResponse.FromDomain).ToArray());
}
