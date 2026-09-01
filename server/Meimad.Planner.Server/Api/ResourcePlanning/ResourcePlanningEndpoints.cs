using Meimad.Planner.Server.Application.ResourcePlanning;
using Meimad.Planner.Server.Domain.ResourcePlanning;

namespace Meimad.Planner.Server.Api.ResourcePlanning;

internal static class ResourcePlanningEndpoints
{
    internal static void MapResourcePlanningEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var root = endpoints.MapGroup("/api/v1/resources");
        root.MapGet("/skills", async (ResourceMasterDataService s, CancellationToken t) => Results.Ok(await s.ListSkillsAsync(t)));
        root.MapPost("/skills", CreateSkillAsync);
        root.MapPut("/employees/{employeeId}/skills", SetEmployeeSkillsAsync);
        root.MapGet("/workstation-types", async (ResourceMasterDataService s, CancellationToken t) => Results.Ok(await s.ListWorkstationTypesAsync(t)));
        root.MapPost("/workstation-types", CreateTypeAsync);
        root.MapGet("/workstations", async (ResourceMasterDataService s, CancellationToken t) => Results.Ok(await s.ListWorkstationsAsync(t)));
        root.MapPost("/workstations", CreateWorkstationAsync);
        root.MapGet("/external", async (ResourceMasterDataService s, CancellationToken t) => Results.Ok(await s.ListExternalResourcesAsync(t)));
        root.MapPost("/external", CreateExternalAsync);
        endpoints.MapPost("/api/v1/resource-plan/preview", Preview);
        endpoints.MapGet("/api/v1/case-operations/{operationId}/resource-requirements",async(string operationId,ResourceMasterDataService s,CancellationToken t)=>Results.Ok(await s.ListRequirementsAsync(operationId,t)));
        endpoints.MapPost("/api/v1/case-operations/{operationId}/resource-requirements",CreateRequirementAsync);
    }

    private static IResult Preview(ResourcePlanningInput input, AutomaticResourceScheduler scheduler)
    {
        try { return Results.Ok(scheduler.Calculate(input)); }
        catch (ResourcePlanningException e) { return Results.UnprocessableEntity(new { code = e.Code, message = e.Message }); }
    }
    private static async Task<IResult> CreateSkillAsync(SkillRequest r, HttpContext c, ResourceMasterDataService s, CancellationToken t) =>
        await Mutate(c, a => s.CreateSkillAsync(r.Name, r.Description, a, t));
    private static async Task<IResult> CreateTypeAsync(WorkstationTypeRequest r, HttpContext c, ResourceMasterDataService s, CancellationToken t) =>
        await Mutate(c, a => s.CreateWorkstationTypeAsync(r.Name, r.Description, r.PropertySchemaJson, a, t));
    private static async Task<IResult> CreateWorkstationAsync(WorkstationRequest r, HttpContext c, ResourceMasterDataService s, CancellationToken t) =>
        await Mutate(c, a => s.CreateWorkstationAsync(r.Name, r.WorkstationTypeId, r.WorkingCalendarId, r.Capacity, r.Capabilities, r.PropertiesJson, a, t));
    private static async Task<IResult> CreateExternalAsync(ExternalResourceRequest r, HttpContext c, ResourceMasterDataService s, CancellationToken t) =>
        await Mutate(c, a => s.CreateExternalResourceAsync(r.Name, r.SupplierName, r.PromisedLeadTimeMinutes, r.SafetyBufferMinutes, r.LeadTimeSemantics, r.WorkingCalendarId, r.PropertiesJson, a, t));
    private static async Task<IResult> SetEmployeeSkillsAsync(string employeeId, EmployeeSkillsRequest r, HttpContext c, ResourceMasterDataService s, CancellationToken t) =>
        await Mutate(c, async a => { await s.SetEmployeeSkillsAsync(employeeId, r.SkillIds, a, t); return new { employeeId, skillIds = r.SkillIds ?? [] }; });
    private static async Task<IResult> CreateRequirementAsync(string operationId,RequirementRequest r,HttpContext c,ResourceMasterDataService s,CancellationToken t)=>
        await Mutate(c,a=>s.CreateRequirementAsync(operationId,r.SequencePosition,r.ResourceClass,r.WorkstationTypeId,r.ExternalResourceId,r.RequiredCapability,r.RequiredSkillId,r.CapacityRequired,r.EstimatedDurationSeconds,r.Direction,r.SimultaneousGroupKey,r.PredecessorRequirementId,a,t));

    private static async Task<IResult> Mutate<T>(HttpContext context, Func<Application.EditMode.EditAuthority,Task<T>> action)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        try { return Results.Ok(await action(authority!)); }
        catch (ResourceMasterDataException e) { return PlanningHttpSupport.Error(422,e.Code,e.Message,context,[new { field=e.Field,code=e.Code,message=e.Message }]); }
        catch (Application.EditMode.EditModeMutationException e) { return PlanningHttpSupport.Error(409,e.Code,e.Message,context); }
    }
}

internal sealed record SkillRequest(string? Name,string? Description);
internal sealed record EmployeeSkillsRequest(IReadOnlyList<string?>? SkillIds);
internal sealed record WorkstationTypeRequest(string? Name,string? Description,string? PropertySchemaJson);
internal sealed record WorkstationRequest(string? Name,string? WorkstationTypeId,string? WorkingCalendarId,int Capacity,IReadOnlyList<string?>? Capabilities,string? PropertiesJson);
internal sealed record ExternalResourceRequest(string? Name,string? SupplierName,int PromisedLeadTimeMinutes,int SafetyBufferMinutes,string? LeadTimeSemantics,string? WorkingCalendarId,string? PropertiesJson);
internal sealed record RequirementRequest(int SequencePosition,string? ResourceClass,string? WorkstationTypeId,string? ExternalResourceId,string? RequiredCapability,string? RequiredSkillId,int CapacityRequired,int EstimatedDurationSeconds,string? Direction,string? SimultaneousGroupKey,string? PredecessorRequirementId);
