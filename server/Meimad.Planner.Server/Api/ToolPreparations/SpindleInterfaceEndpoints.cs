using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Domain.ToolPreparations;
using Meimad.Planner.Server.Persistence;

namespace Meimad.Planner.Server.Api.ToolPreparations;

/// <summary>
/// The Setup library of spindle adaptors and pull studs and each Machine's default (schema v91,
/// owner decisions 2026-09-29). Everyone signed in reads it; changes need the Setup permission and
/// the version they were based on.
/// </summary>
internal static class SpindleInterfaceEndpoints
{
    internal static void MapSpindleInterfaceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/spindle-library", ReadAsync);
        endpoints.MapPost("/api/v1/spindle-adaptors", (SpindleAdaptorRequest request, HttpContext context, SqliteSpindleInterfaceRepository repository, TimeProvider time, CancellationToken token) =>
            SaveAdaptorAsync(null, request, context, repository, time, token));
        endpoints.MapPut("/api/v1/spindle-adaptors/{id}", (string id, SpindleAdaptorRequest request, HttpContext context, SqliteSpindleInterfaceRepository repository, TimeProvider time, CancellationToken token) =>
            SaveAdaptorAsync(id, request, context, repository, time, token));
        endpoints.MapDelete("/api/v1/spindle-adaptors/{id}", (string id, int version, HttpContext context, SqliteSpindleInterfaceRepository repository, CancellationToken token) =>
            DeleteAsync("spindle_adaptors", id, version, context, repository, token));
        endpoints.MapPost("/api/v1/pull-studs", (PullStudRequest request, HttpContext context, SqliteSpindleInterfaceRepository repository, TimeProvider time, CancellationToken token) =>
            SavePullStudAsync(null, request, context, repository, time, token));
        endpoints.MapPut("/api/v1/pull-studs/{id}", (string id, PullStudRequest request, HttpContext context, SqliteSpindleInterfaceRepository repository, TimeProvider time, CancellationToken token) =>
            SavePullStudAsync(id, request, context, repository, time, token));
        endpoints.MapDelete("/api/v1/pull-studs/{id}", (string id, int version, HttpContext context, SqliteSpindleInterfaceRepository repository, CancellationToken token) =>
            DeleteAsync("pull_studs", id, version, context, repository, token));
        endpoints.MapPut("/api/v1/machines/{machineId}/spindle-interface", SaveMachineAsync);
    }

    private static async Task<IResult> ReadAsync(SqliteSpindleInterfaceRepository repository, CancellationToken token) =>
        Results.Ok(SpindleLibraryResponse.FromDomain(await repository.ReadAsync(token)));

    private static async Task<IResult> SaveAdaptorAsync(
        string? id, SpindleAdaptorRequest request, HttpContext context, SqliteSpindleInterfaceRepository repository,
        TimeProvider time, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorizeIdentity(context, Permissions.ManageSetup, out _, out var userId, out var error))
            return error!;
        try
        {
            var values = SpindleInterfaceValidator.Validate(new SpindleAdaptorValues(
                request.Name, request.TaperLength, request.GaugeDiameter, request.SmallEndDiameter,
                request.ToolChangerDiameter, request.ToolChangerLength, request.Notes, request.IsActive ?? true));
            var saved = await repository.SaveAdaptorAsync(id, request.ExpectedVersion, values, userId!, time.GetUtcNow(), token);
            return id is null ? Results.Created($"/api/v1/spindle-adaptors/{saved.SpindleAdaptorId}", saved) : Results.Ok(saved);
        }
        catch (Exception exception) when (TryMapError(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> SavePullStudAsync(
        string? id, PullStudRequest request, HttpContext context, SqliteSpindleInterfaceRepository repository,
        TimeProvider time, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorizeIdentity(context, Permissions.ManageSetup, out _, out var userId, out var error))
            return error!;
        try
        {
            var values = SpindleInterfaceValidator.Validate(new PullStudValues(
                request.Name, request.Thread, request.Angle, request.OverallLength, request.ExposedLength,
                request.KnobDiameter, request.NeckDiameter, request.PilotDiameter, request.Notes, request.IsActive ?? true));
            var saved = await repository.SavePullStudAsync(id, request.ExpectedVersion, values, userId!, time.GetUtcNow(), token);
            return id is null ? Results.Created($"/api/v1/pull-studs/{saved.PullStudId}", saved) : Results.Ok(saved);
        }
        catch (Exception exception) when (TryMapError(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> DeleteAsync(
        string table, string id, int version, HttpContext context, SqliteSpindleInterfaceRepository repository, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorizeIdentity(context, Permissions.ManageSetup, out _, out _, out var error))
            return error!;
        try
        {
            await repository.DeleteAsync(table, id, version, token);
            return Results.NoContent();
        }
        catch (Exception exception) when (TryMapError(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> SaveMachineAsync(
        string machineId, MachineSpindleInterfaceRequest request, HttpContext context,
        SqliteSpindleInterfaceRepository repository, TimeProvider time, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorizeIdentity(context, Permissions.ManageSetup, out _, out var userId, out var error))
            return error!;
        try
        {
            return Results.Ok(await repository.SaveMachineAsync(
                machineId, Blank(request.SpindleAdaptorId), Blank(request.PullStudId), request.ExpectedVersion,
                userId!, time.GetUtcNow(), token));
        }
        catch (Exception exception) when (TryMapError(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryMapError(Exception exception, HttpContext context, out IResult? result)
    {
        result = exception switch
        {
            SpindleInterfaceNotFoundException => PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "resource_not_found", exception.Message, context),
            SpindleInterfaceConflictException conflict => PlanningHttpSupport.Error(StatusCodes.Status409Conflict, conflict.Code, conflict.Message, context),
            SpindleInterfaceValidationException validation => PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity, validation.Code, validation.Message, context,
                validation.Field is null ? null : [new { field = validation.Field, code = validation.Code, message = validation.Message }]),
            _ => null
        };
        return result is not null;
    }
}

internal sealed record SpindleAdaptorRequest(
    int ExpectedVersion, string? Name, double TaperLength, double GaugeDiameter, double? SmallEndDiameter,
    double ToolChangerDiameter, double ToolChangerLength, string? Notes, bool? IsActive);

internal sealed record PullStudRequest(
    int ExpectedVersion, string? Name, string? Thread, double? Angle, double? OverallLength, double ExposedLength,
    double KnobDiameter, double? NeckDiameter, double? PilotDiameter, string? Notes, bool? IsActive);

internal sealed record MachineSpindleInterfaceRequest(string? SpindleAdaptorId, string? PullStudId, int ExpectedVersion);

internal sealed record SpindleLibraryResponse(
    IReadOnlyList<SpindleAdaptor> Adaptors,
    IReadOnlyList<PullStud> PullStuds,
    IReadOnlyList<MachineSpindleInterface> Machines)
{
    internal static SpindleLibraryResponse FromDomain(SpindleLibrary library) => new(library.Adaptors, library.PullStuds, library.Machines);
}
