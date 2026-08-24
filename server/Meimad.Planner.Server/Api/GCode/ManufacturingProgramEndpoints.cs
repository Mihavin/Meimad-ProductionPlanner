using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Domain.GCode;
using System.Text.Json;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api.GCode;

internal static class ManufacturingProgramEndpoints
{
    internal static void MapManufacturingProgramEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var programs = endpoints.MapGroup("/api/v1/manufacturing-programs");
        programs.MapGet(string.Empty, ListAsync);
        programs.MapGet("/{programId}", GetAsync);
        programs.MapPost(string.Empty, CreateAsync);
        programs.MapPost("/{programId}/revisions", CreateRevisionAsync);
        programs.MapPost("/{programId}/gcode-releases", ReleaseAsync).DisableAntiforgery();
        programs.MapGet("/{programId}/gcode-releases/{releaseId}/file", DownloadReleaseAsync);
        programs.MapGet("/{programId}/tool-table-releases/{toolTableReleaseId}/file", DownloadToolTableAsync);
    }

    private static async Task<IResult> ListAsync(
        ManufacturingProgramService service, CancellationToken token) =>
        Results.Ok(new ManufacturingProgramListResponse(
            (await service.ListAsync(token)).Select(ManufacturingProgramResponse.FromDomain).ToArray()));

    private static async Task<IResult> GetAsync(
        string programId, HttpContext context,
        ManufacturingProgramService service, CancellationToken token)
    {
        try
        {
            var value = await service.GetAsync(programId, token);
            SetTag(context.Response, value);
            return Results.Ok(ManufacturingProgramResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> CreateAsync(
        CreateManufacturingProgramRequest request,
        HttpContext context,
        ManufacturingProgramService service,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error))
            return error!;
        try
        {
            var value = await service.CreateAsync(request.ToCommand(), authority!, token);
            SetTag(context.Response, value);
            return Results.Created($"/api/v1/manufacturing-programs/{value.ManufacturingProgramId}",
                ManufacturingProgramResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> CreateRevisionAsync(
        string programId,
        CreateManufacturingProgramRevisionRequest request,
        HttpContext context,
        ManufacturingProgramService service,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error))
            return error!;
        if (!PlanningHttpSupport.TryReadExpectedVersion(
                context.Request.Headers.IfMatch, "manufacturing-program", programId, out var expectedVersion))
        {
            var missing = StringValues.IsNullOrEmpty(context.Request.Headers.IfMatch);
            return PlanningHttpSupport.Error(missing ? 428 : 412,
                missing ? "precondition_required" : "resource_version_stale",
                "A matching Manufacturing Program If-Match header is required.", context);
        }
        try
        {
            var value = await service.CreateRevisionAsync(
                programId, expectedVersion, request.ToCommand(), authority!, token);
            SetTag(context.Response, value);
            return Results.Created(
                $"/api/v1/manufacturing-programs/{programId}/revisions/{value.ActiveRevision!.ProcessRevisionId}",
                ManufacturingProgramResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> ReleaseAsync(
        string programId,
        HttpContext context,
        GCodeService service,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error))
            return error!;
        try
        {
            if (!context.Request.HasFormContentType)
                throw new GCodeValidationException(
                    "contentType", "multipart_required", "Release G-code requires multipart/form-data.");
            var form = await context.Request.ReadFormAsync(token);
            IReadOnlyList<ManufacturingProgramRevisionOutput>? outputs = null;
            if (form.TryGetValue("outputsJson", out var outputJson)
                && !string.IsNullOrWhiteSpace(outputJson.ToString()))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<ManufacturingProgramOutputRequest[]>(
                        outputJson.ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
                    outputs = parsed.Select(value => new ManufacturingProgramRevisionOutput(
                        Guid.NewGuid().ToString("N"), value.CaseOperationId ?? string.Empty,
                        value.QuantityPerCycle, value.DisplayOrder,
                        value.ExecutionMetadataJson ?? "{}")).ToArray();
                }
                catch (JsonException)
                {
                    throw new GCodeValidationException(
                        "outputsJson", "invalid_json", "outputsJson must contain a valid output recipe array.");
                }
            }

            var release = await service.ReleaseForProgramAsync(programId, new ReleaseGCodeCommand(
                null, null, Text(form, "postprocessorId"), Text(form, "changeScope"),
                Text(form, "releaseComment"), Text(form, "processChangeDescription"),
                Boolean(form, "confirmNewProcessRevision"), Boolean(form, "reuseActiveToolTable"),
                Boolean(form, "confirmToolTable"), Upload(form.Files.GetFile("gcodeFile")),
                Upload(form.Files.GetFile("toolTableFile")), programId, outputs), authority!, token);
            return Results.Created(
                $"/api/v1/manufacturing-programs/{programId}/gcode-releases/{release.GCodeReleaseId}",
                GCodeReleaseResponse.FromDomain(release));
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> DownloadReleaseAsync(
        string programId,
        string releaseId,
        HttpContext context,
        GCodeService service,
        CancellationToken token)
    {
        try
        {
            var file = await service.OpenProgramReleaseFileAsync(programId, releaseId, token);
            context.Response.Headers["X-Meimad-Checksum-SHA256"] = file.FileHash;
            return Results.File(file.AbsolutePath, "application/octet-stream",
                file.OriginalFileName, enableRangeProcessing: true);
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> DownloadToolTableAsync(
        string programId,
        string toolTableReleaseId,
        HttpContext context,
        GCodeService service,
        CancellationToken token)
    {
        try
        {
            var file = await service.OpenProgramToolTableFileAsync(
                programId, toolTableReleaseId, token);
            context.Response.Headers["X-Meimad-Checksum-SHA256"] = file.FileHash;
            return Results.File(file.AbsolutePath, "application/octet-stream",
                file.OriginalFileName, enableRangeProcessing: true);
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
            ManufacturingProgramValidationException validation => PlanningHttpSupport.Error(
                422, "validation_failed", validation.Message, context,
                [new { field = validation.Field, code = validation.Code, message = validation.Message }]),
            ManufacturingProgramNotFoundException => PlanningHttpSupport.Error(
                404, "resource_not_found", exception.Message, context),
            ManufacturingProgramSourceRevisionNotFoundException => PlanningHttpSupport.Error(
                422, "source_revision_not_found", exception.Message, context),
            ManufacturingProgramOutputOperationNotFoundException => PlanningHttpSupport.Error(
                422, "output_operation_not_found", exception.Message, context),
            ManufacturingProgramVersionConflictException => PlanningHttpSupport.Error(
                412, "resource_version_stale", exception.Message, context),
            GCodeValidationException validation => PlanningHttpSupport.Error(
                422, "validation_failed", validation.Message, context,
                [new { field = validation.Field, code = validation.Code, message = validation.Message }]),
            GCodeOperationNotFoundException or GCodePostprocessorNotFoundException => PlanningHttpSupport.Error(
                422, "invalid_release_context", exception.Message, context),
            GCodeReleaseNotFoundException or GCodeToolTableNotFoundException => PlanningHttpSupport.Error(
                404, "resource_not_found", exception.Message, context),
            GCodeProcessStateException state => PlanningHttpSupport.Error(
                409, state.Code, state.Message, context),
            GCodeFileUnavailableException or GCodeStorageException => PlanningHttpSupport.Error(
                503, "release_storage_unavailable", exception.Message, context),
            EditModeMutationException edit => PlanningHttpSupport.Error(
                409, edit.Code, edit.Message, context),
            _ => null
        };
        return result is not null;
    }

    private static string? Text(IFormCollection form, string name) =>
        form.TryGetValue(name, out var value) ? value.ToString() : null;
    private static bool Boolean(IFormCollection form, string name) =>
        form.TryGetValue(name, out var value)
        && bool.TryParse(value.ToString(), out var parsed) && parsed;
    private static UploadedReleaseFile? Upload(IFormFile? value) => value is null
        ? null : new UploadedReleaseFile(value.FileName, value.OpenReadStream(), value.Length);
    private static void SetTag(HttpResponse response, ManufacturingProgram value) =>
        response.Headers.ETag = $"\"manufacturing-program:{value.ManufacturingProgramId}:v{value.Version}\"";
}

internal sealed record ManufacturingProgramOutputRequest(
    string? CaseOperationId,
    int QuantityPerCycle,
    int DisplayOrder,
    string? ExecutionMetadataJson)
{
    internal ManufacturingProgramOutputInput ToCommand() =>
        new(CaseOperationId, QuantityPerCycle, DisplayOrder, ExecutionMetadataJson);
}

internal sealed record CreateManufacturingProgramRequest(
    string? Name,
    string? SourceProcessRevisionId,
    string? ChangeDescription,
    IReadOnlyList<ManufacturingProgramOutputRequest>? Outputs)
{
    internal CreateManufacturingProgramCommand ToCommand() =>
        new(Name, SourceProcessRevisionId, ChangeDescription,
            Outputs?.Select(value => value.ToCommand()).ToArray());
}

internal sealed record CreateManufacturingProgramRevisionRequest(
    string? SourceProcessRevisionId,
    string? ChangeDescription,
    IReadOnlyList<ManufacturingProgramOutputRequest>? Outputs)
{
    internal CreateManufacturingProgramRevisionCommand ToCommand() =>
        new(SourceProcessRevisionId, ChangeDescription,
            Outputs?.Select(value => value.ToCommand()).ToArray());
}

internal sealed record ManufacturingProgramOutputResponse(
    string OutputId,
    string CaseOperationId,
    int QuantityPerCycle,
    int DisplayOrder,
    string ExecutionMetadataJson)
{
    internal static ManufacturingProgramOutputResponse FromDomain(ManufacturingProgramRevisionOutput value) =>
        new(value.OutputId, value.CaseOperationId, value.QuantityPerCycle,
            value.DisplayOrder, value.ExecutionMetadataJson);
}

internal sealed record ManufacturingProgramRevisionResponse(
    string ProcessRevisionId,
    int RevisionNumber,
    bool IsActive,
    string ToolTableReleaseId,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string ChangeDescription,
    int Version,
    ToolTableReleaseResponse ToolTable,
    IReadOnlyList<ManufacturingProgramOutputResponse> Outputs)
{
    internal static ManufacturingProgramRevisionResponse FromDomain(ProcessRevision value) =>
        new(value.ProcessRevisionId, value.ProcessRevisionNumber, value.IsActive,
            value.ToolTableReleaseId, value.CreatedAt, value.CreatedBy,
            value.ChangeDescription, value.Version,
            ToolTableReleaseResponse.FromDomain(value.ToolTable),
            (value.Outputs ?? []).Select(ManufacturingProgramOutputResponse.FromDomain).ToArray());
}

internal sealed record ManufacturingProgramResponse(
    string ManufacturingProgramId,
    string Name,
    string? DefaultCaseOperationId,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ManufacturingProgramRevisionResponse? ActiveRevision,
    IReadOnlyList<ManufacturingProgramRevisionResponse> Revisions,
    IReadOnlyList<ManufacturingProgramReleaseResponse> Releases)
{
    internal static ManufacturingProgramResponse FromDomain(ManufacturingProgram value) =>
        new(value.ManufacturingProgramId, value.Name, value.DefaultCaseOperationId,
            value.Version, value.CreatedAt, value.UpdatedAt,
            value.ActiveRevision is null ? null : ManufacturingProgramRevisionResponse.FromDomain(value.ActiveRevision),
            value.Revisions.Select(ManufacturingProgramRevisionResponse.FromDomain).ToArray(),
            value.Releases.Select(ManufacturingProgramReleaseResponse.FromDomain).ToArray());
}

internal sealed record ManufacturingProgramReleaseResponse(
    string GCodeReleaseId,
    string ProcessRevisionId,
    string PostprocessorId,
    int PostSpecificRevision,
    string OriginalFileName,
    long FileSize,
    string FileHash,
    DateTimeOffset ReleasedAt,
    string ReleasedBy,
    string ChangeScope,
    string ReleaseComment,
    string ToolTableReleaseId)
{
    internal static ManufacturingProgramReleaseResponse FromDomain(ManufacturingProgramRelease value) =>
        new(value.GCodeReleaseId, value.ProcessRevisionId, value.PostprocessorId,
            value.PostSpecificRevision, value.OriginalFileName, value.FileSize,
            value.FileHash, value.ReleasedAt, value.ReleasedBy, value.ChangeScope,
            value.ReleaseComment, value.ToolTableReleaseId);
}

internal sealed record ManufacturingProgramListResponse(
    IReadOnlyList<ManufacturingProgramResponse> Items);
