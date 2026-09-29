using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Persistence;

namespace Meimad.Planner.Server.Api.Cases;

/// <summary>
/// A Case Operation's time statistics (owner request 2026-09-29): everyone signed in reads them;
/// applying the NC cycle or a measured time to the Case Operation needs the Cases permission and
/// the Operation version the statistics were read at.
/// </summary>
internal static class OperationTimeStatisticsEndpoints
{
    private const string Route = "/api/v1/cases/{caseId}/operations/{operationId}/time-statistics";

    internal static void MapOperationTimeStatisticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, ReadAsync);
        endpoints.MapPost(Route + "/apply", ApplyAsync);
    }

    private static async Task<IResult> ReadAsync(
        string caseId, string operationId, HttpContext context, SqliteOperationTimeStatisticsRepository repository,
        CancellationToken token)
    {
        try
        {
            return Results.Ok(await repository.ReadAsync(caseId, operationId, token));
        }
        catch (Exception exception) when (TryMapError(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> ApplyAsync(
        string caseId, string operationId, ApplyOperationTimeRequest request, HttpContext context,
        SqliteOperationTimeStatisticsRepository repository, TimeProvider time, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(context, Permissions.EditCases, out var editAuthority, out var error))
            return error!;
        if (string.IsNullOrWhiteSpace(request.MachineId))
            return PlanningHttpSupport.Error(StatusCodes.Status400BadRequest, "machine_required", "machineId is required.", context);
        try
        {
            return Results.Ok(await repository.ApplyAsync(
                caseId, operationId, request.Kind ?? string.Empty, request.Source ?? string.Empty, request.MachineId.Trim(),
                request.ExpectedVersion, editAuthority!, time.GetUtcNow(), token));
        }
        catch (Exception exception) when (TryMapError(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static bool TryMapError(Exception exception, HttpContext context, out IResult? result)
    {
        result = exception switch
        {
            OperationTimeNotFoundException => PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "resource_not_found", exception.Message, context),
            OperationTimeRequestException request => PlanningHttpSupport.Error(request.Status, request.Code, request.Message, context),
            EditModeMutationException edit => PlanningHttpSupport.Error(StatusCodes.Status403Forbidden, edit.Code, edit.Message, context),
            _ => null
        };
        return result is not null;
    }
}

internal sealed record ApplyOperationTimeRequest(string? Kind, string? Source, string? MachineId, int ExpectedVersion);
