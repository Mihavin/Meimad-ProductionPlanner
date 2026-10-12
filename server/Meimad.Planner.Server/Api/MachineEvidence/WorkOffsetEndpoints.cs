using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.MachineEvidence;

namespace Meimad.Planner.Server.Api.MachineEvidence;

/// <summary>
/// Measured work offsets (schema v116, docs/api-contract.md "Measured work offsets"). Reading needs a
/// signed-in account; the ingest endpoint for external DPRNT readers needs "Setup and integrations";
/// accepting or rejecting a REVIEW_REQUIRED candidate needs "Run operations". The Server's own DPRNT
/// readers call <see cref="WorkOffsetIngestService"/> directly.
/// </summary>
internal static class WorkOffsetEndpoints
{
    internal static void MapWorkOffsetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/machines/{machineId}/operations/{operationId}/work-offsets", ReadOperationAsync);
        endpoints.MapGet("/api/v1/machines/{machineId}/work-offset-report", ReadReportAsync);
        endpoints.MapGet("/api/v1/machine-evidence/work-offsets", ListAsync);
        endpoints.MapGet("/api/v1/machine-evidence/work-offsets/{workOffsetId}", GetAsync);
        endpoints.MapPost("/api/v1/machine-evidence/work-offset", IngestAsync);
        endpoints.MapPost("/api/v1/machine-evidence/work-offsets/{workOffsetId}/accept",
            (string workOffsetId, WorkOffsetReviewRequest? request, HttpContext context, WorkOffsetService service, CancellationToken token) =>
                ReviewAsync(workOffsetId, accept: true, request, context, service, token));
        endpoints.MapPost("/api/v1/machine-evidence/work-offsets/{workOffsetId}/reject",
            (string workOffsetId, WorkOffsetReviewRequest? request, HttpContext context, WorkOffsetService service, CancellationToken token) =>
                ReviewAsync(workOffsetId, accept: false, request, context, service, token));
    }

    /// <summary>GET .../machines/{machineId}/operations/{operationId}/work-offsets?offset=G54&amp;limit=: current records and history.</summary>
    private static async Task<IResult> ReadOperationAsync(
        string machineId, string operationId, string? offset, int? limit, HttpContext context,
        WorkOffsetService service, CancellationToken token)
    {
        try
        {
            var view = await service.ReadOperationAsync(machineId, operationId, offset, limit, token);
            return view is null
                ? PlanningHttpSupport.Error(404, "resource_not_found", "The Machine or the Case Operation does not exist.", context)
                : Results.Ok(view);
        }
        catch (WorkOffsetValidationException exception) { return Invalid(exception, context); }
    }

    /// <summary>
    /// GET .../machines/{machineId}/work-offset-report?operationId=&amp;releaseId=&amp;offsets=G54,G55&amp;axes=XYZ:
    /// the program that prints the measured offsets, in the Machine's NC dialect. Read-only.
    /// </summary>
    private static async Task<IResult> ReadReportAsync(
        string machineId, string? operationId, string? releaseId, string? offsets, string? axes,
        HttpContext context, WorkOffsetService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorize(context, null, out _, out var error)) return error!;
        try
        {
            var report = await service.RenderReportAsync(machineId, operationId ?? string.Empty, releaseId, offsets, axes, token);
            return report is null
                ? PlanningHttpSupport.Error(404, "resource_not_found", "The Machine or the Case Operation does not exist.", context)
                : Results.Ok(new { ncDialect = report.Dialect, fileName = report.FileName, text = report.Text, lines = report.Lines });
        }
        catch (WorkOffsetValidationException exception) { return Invalid(exception, context); }
    }

    /// <summary>GET /api/v1/machine-evidence/work-offsets?status=REVIEW_REQUIRED,UNMATCHED&amp;machineId=&amp;caseOperationId=&amp;limit=</summary>
    private static async Task<IResult> ListAsync(
        string? status, string? machineId, string? caseOperationId, int? limit, HttpContext context,
        WorkOffsetService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorize(context, null, out _, out var error)) return error!;
        try
        {
            return Results.Ok(new { items = await service.ListAsync(status, machineId, caseOperationId, limit, token) });
        }
        catch (WorkOffsetValidationException exception) { return Invalid(exception, context); }
    }

    private static async Task<IResult> GetAsync(
        string workOffsetId, HttpContext context, WorkOffsetService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorize(context, null, out _, out var error)) return error!;
        var value = await service.GetAsync(workOffsetId, token);
        return value is null
            ? PlanningHttpSupport.Error(404, "resource_not_found", "The measured work offset does not exist.", context)
            : Results.Ok(value);
    }

    /// <summary>
    /// POST /api/v1/machine-evidence/work-offset: one measured work offset from an external reader.
    /// Idempotent by <c>rawMessageHash</c> (or the hash of the normalized body): a repeat answers 200
    /// with <c>duplicate: true</c> and the stored record; a new record answers 201.
    /// </summary>
    private static async Task<IResult> IngestAsync(
        WorkOffsetIngestRequest? request, HttpContext context, WorkOffsetIngestService service, TimeProvider timeProvider,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorize(context, Permissions.ManageSetup, out var user, out var error)) return error!;
        if (request is null) return Invalid("validation_failed", "A JSON body is required.", context);
        if (string.IsNullOrWhiteSpace(request.MachineId)) return Invalid("machine_missing", "machineId is required.", context);
        if (string.IsNullOrWhiteSpace(request.OperationNumber)) return Invalid("operation_missing", "operationNumber is required.", context);
        if (string.IsNullOrWhiteSpace(request.ReleaseId) && string.IsNullOrWhiteSpace(request.Program))
            return Invalid("release_missing", "releaseId (or a program carrying the NC identity) is required.", context);
        if (string.IsNullOrWhiteSpace(request.WorkOffset)) return Invalid("offset_missing", "workOffset is required.", context);
        if (string.IsNullOrWhiteSpace(request.SourceSession)) return Invalid("session_missing", "sourceSession is required.", context);
        if (request.PositionMm is not { X: not null, Z: not null })
            return Invalid("axis_missing", "positionMm needs x and z (and y on a Machine that has it).", context);
        var source = string.IsNullOrWhiteSpace(request.Source) ? "API" : request.Source.Trim().ToUpperInvariant();
        if (source.Length > 32 || !source.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            return Invalid("invalid_source", "source is up to 32 letters, digits and underscores, for example DPRNT_TCP.", context);
        if (request.Units is { } units && !string.Equals(units.Trim(), "mm", StringComparison.OrdinalIgnoreCase))
            return Invalid("invalid_units", "positionMm is in millimetres; units may only be mm.", context);
        if (request.RawMessageHash is { } suppliedHash
            && (suppliedHash.Trim().Length is < 8 or > 128 || !suppliedHash.Trim().All(char.IsAsciiLetterOrDigit)))
            return Invalid("invalid_hash", "rawMessageHash is 8 to 128 letters and digits.", context);
        if (request.MeasuredAt is { } measuredAt && measuredAt > timeProvider.GetUtcNow() + WorkOffsetIngestService.FutureTolerance)
            return Invalid("measured_at_in_future", "measuredAt is in the future.", context);
        if (request.SourceSession.Trim().Length > 128)
            return Invalid("invalid_session", "sourceSession is limited to 128 characters.", context);

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        void Add(string axis, double? value) { if (value is { } number) values[axis] = number; }
        Add("X", request.PositionMm.X); Add("Y", request.PositionMm.Y); Add("Z", request.PositionMm.Z);
        Add("A", request.RotationDeg?.A); Add("B", request.RotationDeg?.B); Add("C", request.RotationDeg?.C);
        if (values.Values.Any(value => !double.IsFinite(value)))
            return Invalid("invalid_number", "Positions and rotations are finite numbers.", context);

        static string? Upper(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
        var canonical = JsonSerializer.Serialize(new
        {
            machineId = request.MachineId.Trim(),
            operationNumber = request.OperationNumber.Trim(),
            program = request.Program?.Trim(),
            releaseId = request.ReleaseId?.Trim(),
            workOffset = request.WorkOffset.Trim(),
            positionMm = new { x = request.PositionMm.X, y = request.PositionMm.Y, z = request.PositionMm.Z },
            rotationDeg = new { a = request.RotationDeg?.A, b = request.RotationDeg?.B, c = request.RotationDeg?.C },
            source,
            sourceSession = request.SourceSession.Trim(),
            sequence = request.Sequence,
            measuredAt = request.MeasuredAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        });
        var raw = string.IsNullOrWhiteSpace(request.RawMessage) ? canonical : request.RawMessage.Trim();
        if (raw.Length > 16384) return Invalid("raw_message_too_long", "rawMessage is limited to 16384 characters.", context);
        var hash = request.RawMessageHash?.Trim().ToLowerInvariant()
                   ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var name = WorkOffsetNames.Parse(request.WorkOffset);

        var result = await service.IngestAsync(new WorkOffsetEvidence(
            null,
            source,
            user!.UserName,
            request.SourceSession.Trim(),
            request.Sequence,
            Upper(request.MachineId),
            Upper(request.Program),
            Upper(request.OperationNumber),
            Upper(request.ReleaseId),
            name?.Canonical ?? Upper(request.WorkOffset),
            "MM",
            values,
            raw,
            hash,
            request.MeasuredAt,
            name is null ? "invalid_work_offset" : null), token);
        return result.Duplicate
            ? Results.Ok(new { duplicate = true, record = result.Record })
            : Results.Created($"/api/v1/machine-evidence/work-offsets/{result.Record.WorkOffsetId}",
                new { duplicate = false, record = result.Record });
    }

    private static async Task<IResult> ReviewAsync(
        string workOffsetId, bool accept, WorkOffsetReviewRequest? request, HttpContext context,
        WorkOffsetService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorize(context, Permissions.RunOperations, out var user, out var error)) return error!;
        if (request is null) return Invalid("validation_failed", "A JSON body with the version you reviewed is required.", context);
        try
        {
            var value = await service.ReviewAsync(new WorkOffsetReviewCommand(
                workOffsetId, accept, request.Version,
                CheckCurrent: accept && request.ExpectedCurrentId is not null,
                request.ExpectedCurrentId, request.Comment?.Trim(), user!.UserName), token);
            return Results.Ok(value);
        }
        catch (WorkOffsetValidationException exception) { return Invalid(exception, context); }
        catch (WorkOffsetNotFoundException exception)
        {
            return PlanningHttpSupport.Error(404, "resource_not_found", exception.Message, context);
        }
    }

    private static IResult Invalid(WorkOffsetValidationException exception, HttpContext context) =>
        Invalid(exception.Code, exception.Message, context);

    private static IResult Invalid(string code, string message, HttpContext context) =>
        PlanningHttpSupport.Error(422, "validation_failed", message, context, [new { code, message }]);
}

/// <summary>The ingest body of exporter definition §20.12.</summary>
internal sealed record WorkOffsetIngestRequest(
    string? MachineId,
    string? OperationNumber,
    string? Program,
    string? ReleaseId,
    string? WorkOffset,
    WorkOffsetPositionRequest? PositionMm,
    WorkOffsetRotationRequest? RotationDeg,
    string? Units,
    string? Source,
    string? SourceSession,
    long? Sequence,
    string? RawMessage,
    string? RawMessageHash,
    DateTimeOffset? MeasuredAt);

internal sealed record WorkOffsetPositionRequest(double? X, double? Y, double? Z);

internal sealed record WorkOffsetRotationRequest(double? A, double? B, double? C);

/// <summary>
/// A review decision: <c>version</c> of the candidate as it was read; when accepting,
/// <c>expectedCurrentId</c> is the current record the reviewer compared with ("" for none, omitted to skip that check).
/// </summary>
internal sealed record WorkOffsetReviewRequest(int Version, string? ExpectedCurrentId, string? Comment);
