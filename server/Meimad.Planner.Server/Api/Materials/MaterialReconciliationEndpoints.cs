using System.Globalization;
using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Materials;
using Meimad.Planner.Server.Domain.Materials;

namespace Meimad.Planner.Server.Api.Materials;

internal static class MaterialReconciliationEndpoints
{
    internal static void MapMaterialReconciliationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/material-receipts", ListReceiptsAsync);
        endpoints.MapPost("/api/v1/material-receipts", CreateReceiptAsync);
        endpoints.MapGet("/api/v1/batches/{batchId}/material", ReadBatchAsync);
        endpoints.MapPut("/api/v1/batches/{batchId}/material/reservations", ReplaceReservationsAsync);
    }

    private static async Task<IResult> ListReceiptsAsync(
        string? caseId,
        HttpContext context,
        MaterialReconciliationService service,
        CancellationToken token)
    {
        try
        {
            var values = await service.ListReceiptsAsync(caseId ?? string.Empty, token);
            return Results.Ok(new { items = values.Select(MaterialReceiptResponse.FromDomain).ToArray(), nextCursor = (string?)null });
        }
        catch (Exception exception) { return Error(exception, context); }
    }

    private static async Task<IResult> CreateReceiptAsync(
        CreateMaterialReceiptRequest request,
        HttpContext context,
        MaterialReconciliationService service,
        CancellationToken token)
    {
        if (!TryAuthority(context, out var authority, out var error)) return error!;
        try
        {
            var value = await service.CreateReceiptAsync(request.ToCommand(), authority!, token);
            return Results.Created($"/api/v1/material-receipts/{value.ReceiptId}", MaterialReceiptResponse.FromDomain(value));
        }
        catch (Exception exception) { return Error(exception, context); }
    }

    private static async Task<IResult> ReadBatchAsync(
        string batchId,
        HttpContext context,
        MaterialReconciliationService service,
        CancellationToken token)
    {
        var value = await service.ReadBatchAsync(batchId, token);
        return value is null
            ? Problem(StatusCodes.Status404NotFound, "resource_not_found", "The Production Batch was not found.", context)
            : Results.Ok(BatchMaterialResponse.FromDomain(value));
    }

    private static async Task<IResult> ReplaceReservationsAsync(
        string batchId,
        ReplaceMaterialReservationsRequest request,
        HttpContext context,
        MaterialReconciliationService service,
        CancellationToken token)
    {
        if (!TryAuthority(context, out var authority, out var error)) return error!;
        try
        {
            var value = await service.ReplaceReservationsAsync(
                batchId, request.Reservations?.Select(item => item.ToValue()).ToArray(), authority!, token);
            return value is null
                ? Problem(StatusCodes.Status404NotFound, "resource_not_found", "The Production Batch was not found.", context)
                : Results.Ok(BatchMaterialResponse.FromDomain(value));
        }
        catch (Exception exception) { return Error(exception, context); }
    }

    private static IResult Error(Exception exception, HttpContext context) => exception switch
    {
        MaterialReconciliationValidationException validation => Problem(
            StatusCodes.Status422UnprocessableEntity, validation.Code, validation.Message, context,
            new[] { new { field = validation.Field, code = validation.Code, message = validation.Message } }),
        MaterialReceiptCaseNotFoundException => Problem(
            StatusCodes.Status404NotFound, "resource_not_found", exception.Message, context),
        EditModeMutationException edit => Problem(
            StatusCodes.Status409Conflict, edit.Code, edit.Message, context),
        _ => throw exception
    };

    private static bool TryAuthority(HttpContext context, out EditAuthority? authority, out IResult? error)
    {
        authority = null;
        error = null;
        var clientId = context.Request.Headers["X-Meimad-Client-Id"].ToString();
        var generationText = context.Request.Headers["X-Meimad-Edit-Generation"].ToString();
        if (string.IsNullOrWhiteSpace(clientId)
            || !long.TryParse(generationText, NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || generation < 0)
        {
            error = Problem(StatusCodes.Status428PreconditionRequired, "precondition_required",
                "X-Meimad-Client-Id and a valid X-Meimad-Edit-Generation are required.", context);
            return false;
        }
        authority = new(clientId, generation);
        return true;
    }

    private static IResult Problem(
        int status, string code, string message, HttpContext context, object? details = null) =>
        Results.Json(new
        {
            error = new
            {
                code,
                message,
                correlationId = context.TraceIdentifier,
                details = details ?? Array.Empty<object>()
            }
        }, statusCode: status);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateMaterialReceiptRequest(
    string? CaseId,
    int Quantity,
    DateTimeOffset ReceivedAt,
    string? ExternalReference,
    string? Comment)
{
    internal CreateVerifiedMaterialReceiptCommand ToCommand() =>
        new(CaseId, Quantity, ReceivedAt, ExternalReference, Comment);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReplaceMaterialReservationsRequest(
    IReadOnlyList<MaterialReservationRequest>? Reservations);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MaterialReservationRequest(string? ReceiptId, int Quantity, string? Comment)
{
    internal MaterialReservationValue ToValue() => new(ReceiptId, Quantity, Comment);
}

internal sealed record MaterialReceiptResponse(
    string ReceiptId,
    string CaseId,
    int Quantity,
    string Unit,
    DateTimeOffset ReceivedAt,
    DateTimeOffset VerifiedAt,
    string VerifiedBy,
    string? ExternalReference,
    string? Comment,
    int ReservedQuantity,
    int AvailableQuantity)
{
    internal static MaterialReceiptResponse FromDomain(VerifiedMaterialReceipt value) => new(
        value.ReceiptId, value.CaseId, value.Quantity, "piece", value.ReceivedAt,
        value.VerifiedAt, value.VerifiedBy, value.ExternalReference, value.Comment,
        value.ReservedQuantity, value.AvailableQuantity);
}

internal sealed record MaterialReservationResponse(
    string ReservationId,
    string ReceiptId,
    string ProductionBatchId,
    int Quantity,
    DateTimeOffset ReservedAt,
    string ReservedBy,
    string? Comment)
{
    internal static MaterialReservationResponse FromDomain(BatchMaterialReservation value) => new(
        value.ReservationId, value.ReceiptId, value.ProductionBatchId, value.Quantity,
        value.ReservedAt, value.ReservedBy, value.Comment);
}

internal sealed record BatchMaterialResponse(
    string ProductionBatchId,
    string CaseId,
    string BatchNumber,
    int PlannedQuantity,
    int RequiredMaterialPieces,
    int ReservedQuantity,
    int VerifiedAvailableToBatch,
    int ShortageQuantity,
    string State,
    string Message,
    IReadOnlyList<MaterialReceiptResponse> Receipts,
    IReadOnlyList<MaterialReservationResponse> Reservations)
{
    internal static BatchMaterialResponse FromDomain(BatchMaterialReconciliation value) => new(
        value.ProductionBatchId, value.CaseId, value.BatchNumber, value.PlannedQuantity,
        value.PlannedQuantity, value.ReservedQuantity, value.VerifiedAvailableToBatch,
        value.ShortageQuantity, value.State, value.Message,
        value.Receipts.Select(MaterialReceiptResponse.FromDomain).ToArray(),
        value.Reservations.Select(MaterialReservationResponse.FromDomain).ToArray());
}
