using Meimad.Planner.Server.Application.AdministrativeSetup;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.AdministrativeSetup;
using Microsoft.Extensions.Primitives;
using System.Globalization;

namespace Meimad.Planner.Server.Api.AdministrativeSetup;

internal static class AdministrativeSetupEndpoints
{
    internal static void MapAdministrativeSetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var resources=endpoints.MapGroup("/api/v1/resources");
        resources.MapGet(string.Empty, async (AdministrativeSetupService service,CancellationToken token)=>Results.Ok(new EmployeeResourceListResponse((await service.ListResourcesAsync(token)).Select(EmployeeResourceResponse.FromDomain).ToArray(),null)));
        resources.MapGet("/available", async (AdministrativeSetupService service,CancellationToken token)=>Results.Ok(new EmployeeResourceListResponse((await service.ListAvailableResourcesAsync(token)).Select(EmployeeResourceResponse.FromDomain).ToArray(),null)));
        resources.MapPost(string.Empty, CreateResourceAsync); resources.MapGet("/{resourceId}",GetResourceAsync); resources.MapPatch("/{resourceId}",UpdateResourceAsync); resources.MapDelete("/{resourceId}",DeleteResourceAsync);
        resources.MapGet("/{resourceId}/exceptions", ListEmployeeExceptionsAsync);
        resources.MapPost("/{resourceId}/exceptions", CreateEmployeeExceptionAsync);
        resources.MapPatch("/{resourceId}/exceptions/{exceptionId}", UpdateEmployeeExceptionAsync);
        resources.MapDelete("/{resourceId}/exceptions/{exceptionId}", DeleteEmployeeExceptionAsync);
        resources.MapGet("/{resourceId}/availability", GetEmployeeAvailabilityAsync);
        var holidays=endpoints.MapGroup("/api/v1/israeli-holidays");
        holidays.MapGet(string.Empty, async (AdministrativeSetupService service,CancellationToken token)=>Results.Ok(new IsraeliHolidayListResponse((await service.ListHolidaysAsync(token)).Select(IsraeliHolidayResponse.FromDomain).ToArray(),null)));
        holidays.MapPost(string.Empty,CreateHolidayAsync); holidays.MapPost("/sync",SynchronizeHolidaysAsync); holidays.MapGet("/{holidayId}",GetHolidayAsync); holidays.MapPatch("/{holidayId}",UpdateHolidayAsync); holidays.MapDelete("/{holidayId}",DeleteHolidayAsync);
        endpoints.MapGet("/api/v1/report-email-settings",GetReportSettingsAsync);
        endpoints.MapPut("/api/v1/report-email-settings",UpdateReportSettingsAsync);
    }

    private static async Task<IResult> CreateResourceAsync(CreateEmployeeResourceRequest request,HttpContext context,AdministrativeSetupService service,CancellationToken token)
    { if(!PlanningHttpSupport.TryReadEditAuthority(context,out var authority,out var error))return error!; try{var value=await service.CreateResourceAsync(request.ToCommand(),authority!,token);SetTag(context.Response,"resource",value.ResourceId,value.Version);return Results.Created($"/api/v1/resources/{value.ResourceId}",EmployeeResourceResponse.FromDomain(value));}catch(Exception exception)when(TryMap(exception,context,out var mapped)){return mapped!;} }
    private static async Task<IResult> GetResourceAsync(string resourceId,HttpContext context,AdministrativeSetupService service,CancellationToken token)
    {var value=await service.GetResourceAsync(resourceId,token);if(value is null)return NotFound(context,"Employee Resource");SetTag(context.Response,"resource",value.ResourceId,value.Version);return Results.Ok(EmployeeResourceResponse.FromDomain(value));}
    private static async Task<IResult> UpdateResourceAsync(string resourceId,PatchEmployeeResourceRequest request,HttpContext context,AdministrativeSetupService service,CancellationToken token)
    {if(!PlanningHttpSupport.TryReadEditAuthority(context,out var authority,out var error))return error!;if(!Expected(context,"resource",resourceId,out var version,out var etagError))return etagError!;try{var value=await service.UpdateResourceAsync(resourceId,version,request.ToCommand(),authority!,token);SetTag(context.Response,"resource",value.ResourceId,value.Version);return Results.Ok(EmployeeResourceResponse.FromDomain(value));}catch(Exception exception)when(TryMap(exception,context,out var mapped)){return mapped!;}}
    private static async Task<IResult> DeleteResourceAsync(string resourceId,HttpContext context,AdministrativeSetupService service,CancellationToken token)
    {if(!PlanningHttpSupport.TryReadEditAuthority(context,out var authority,out var error))return error!;try{return await service.DeleteResourceAsync(resourceId,authority!,token)?Results.NoContent():NotFound(context,"Employee Resource");}catch(Exception exception)when(TryMap(exception,context,out var mapped)){return mapped!;}}
    private static async Task<IResult> ListEmployeeExceptionsAsync(string resourceId, string? from, string? to, HttpContext context, AdministrativeSetupService service, CancellationToken token)
    {
        if (!TryDate(from, "from", context, out var fromDate, out var error) || !TryDate(to, "to", context, out var toDate, out error)) return error!;
        try { return Results.Ok(new EmployeeCalendarExceptionListResponse((await service.ListEmployeeExceptionsAsync(resourceId, fromDate, toDate, token)).Select(EmployeeCalendarExceptionResponse.FromDomain).ToArray(), null)); }
        catch (Exception exception) when (TryMap(exception, context, out var mapped)) { return mapped!; }
    }
    private static async Task<IResult> CreateEmployeeExceptionAsync(string resourceId, CreateEmployeeCalendarExceptionRequest request, HttpContext context, AdministrativeSetupService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        try { var value = await service.CreateEmployeeExceptionAsync(resourceId, request.ToCommand(), authority!, token); SetTag(context.Response, "employee-exception", value.ExceptionId, value.Version); return Results.Created($"/api/v1/resources/{resourceId}/exceptions/{value.ExceptionId}", EmployeeCalendarExceptionResponse.FromDomain(value)); }
        catch (Exception exception) when (TryMap(exception, context, out var mapped)) { return mapped!; }
    }
    private static async Task<IResult> UpdateEmployeeExceptionAsync(string resourceId, string exceptionId, PatchEmployeeCalendarExceptionRequest request, HttpContext context, AdministrativeSetupService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        if (!Expected(context, "employee-exception", exceptionId, out var version, out var etagError)) return etagError!;
        try { var value = await service.UpdateEmployeeExceptionAsync(resourceId, exceptionId, version, request.ToCommand(), authority!, token); SetTag(context.Response, "employee-exception", value.ExceptionId, value.Version); return Results.Ok(EmployeeCalendarExceptionResponse.FromDomain(value)); }
        catch (Exception exception) when (TryMap(exception, context, out var mapped)) { return mapped!; }
    }
    private static async Task<IResult> DeleteEmployeeExceptionAsync(string resourceId, string exceptionId, HttpContext context, AdministrativeSetupService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        try { return await service.DeleteEmployeeExceptionAsync(resourceId, exceptionId, authority!, token) ? Results.NoContent() : NotFound(context, "Employee Calendar Exception"); }
        catch (Exception exception) when (TryMap(exception, context, out var mapped)) { return mapped!; }
    }
    private static async Task<IResult> GetEmployeeAvailabilityAsync(string resourceId, string? from, string? to, HttpContext context, AdministrativeSetupService service, CancellationToken token)
    {
        if (!TryInstant(from, "from", context, out var startsAt, out var error) || !TryInstant(to, "to", context, out var endsAt, out error)) return error!;
        try { return Results.Ok(EmployeeAvailabilityResponse.FromDomain(await service.GetEmployeeAvailabilityAsync(resourceId, startsAt, endsAt, token))); }
        catch (Exception exception) when (TryMap(exception, context, out var mapped)) { return mapped!; }
    }
    private static async Task<IResult> CreateHolidayAsync(CreateIsraeliHolidayRequest request,HttpContext context,AdministrativeSetupService service,CancellationToken token)
    {if(!PlanningHttpSupport.TryReadEditAuthority(context,out var authority,out var error))return error!;try{var value=await service.CreateHolidayAsync(request.ToCommand(),authority!,token);SetTag(context.Response,"israeli-holiday",value.IsraeliHolidayId,value.Version);return Results.Created($"/api/v1/israeli-holidays/{value.IsraeliHolidayId}",IsraeliHolidayResponse.FromDomain(value));}catch(Exception exception)when(TryMap(exception,context,out var mapped)){return mapped!;}}
    private static async Task<IResult> GetHolidayAsync(string holidayId,HttpContext context,AdministrativeSetupService service,CancellationToken token)
    {var value=await service.GetHolidayAsync(holidayId,token);if(value is null)return NotFound(context,"Israeli Holiday");SetTag(context.Response,"israeli-holiday",value.IsraeliHolidayId,value.Version);return Results.Ok(IsraeliHolidayResponse.FromDomain(value));}
    private static async Task<IResult> UpdateHolidayAsync(string holidayId,PatchIsraeliHolidayRequest request,HttpContext context,AdministrativeSetupService service,CancellationToken token)
    {if(!PlanningHttpSupport.TryReadEditAuthority(context,out var authority,out var error))return error!;if(!Expected(context,"israeli-holiday",holidayId,out var version,out var etagError))return etagError!;try{var value=await service.UpdateHolidayAsync(holidayId,version,request.ToCommand(),authority!,token);SetTag(context.Response,"israeli-holiday",value.IsraeliHolidayId,value.Version);return Results.Ok(IsraeliHolidayResponse.FromDomain(value));}catch(Exception exception)when(TryMap(exception,context,out var mapped)){return mapped!;}}
    private static async Task<IResult> DeleteHolidayAsync(string holidayId,HttpContext context,AdministrativeSetupService service,CancellationToken token)
    {if(!PlanningHttpSupport.TryReadEditAuthority(context,out var authority,out var error))return error!;try{return await service.DeleteHolidayAsync(holidayId,authority!,token)?Results.NoContent():NotFound(context,"Israeli Holiday");}catch(Exception exception)when(TryMap(exception,context,out var mapped)){return mapped!;}}
    private static async Task<IResult> SynchronizeHolidaysAsync(SyncIsraeliHolidaysRequest request,HttpContext context,AdministrativeSetupService service,CancellationToken token)
    {if(!PlanningHttpSupport.TryReadEditAuthority(context,out var authority,out var error))return error!;try{return Results.Ok(IsraeliHolidaySyncResponse.FromDomain(await service.SynchronizeHolidaysAsync(request.ToCommand(),authority!,token)));}catch(Exception exception)when(TryMap(exception,context,out var mapped)){return mapped!;}}
    private static async Task<IResult> GetReportSettingsAsync(HttpContext context,AdministrativeSetupService service,CancellationToken token)
    {var value=await service.GetReportEmailSettingsAsync(token);SetTag(context.Response,"report-email-settings","1",value.Version);return Results.Ok(ReportEmailSettingsResponse.FromDomain(value));}
    private static async Task<IResult> UpdateReportSettingsAsync(UpdateReportEmailSettingsRequest request,HttpContext context,AdministrativeSetupService service,CancellationToken token)
    {if(!PlanningHttpSupport.TryReadEditAuthority(context,out var authority,out var error))return error!;if(!Expected(context,"report-email-settings","1",out var version,out var etagError))return etagError!;try{var value=await service.UpdateReportEmailSettingsAsync(version,request.ToCommand(),authority!,token);SetTag(context.Response,"report-email-settings","1",value.Version);return Results.Ok(ReportEmailSettingsResponse.FromDomain(value));}catch(Exception exception)when(TryMap(exception,context,out var mapped)){return mapped!;}}

    private static bool Expected(HttpContext context,string kind,string id,out int version,out IResult? error)
    {if(PlanningHttpSupport.TryReadExpectedVersion(context.Request.Headers.IfMatch,kind,id,out version)){error=null;return true;}var missing=StringValues.IsNullOrEmpty(context.Request.Headers.IfMatch);error=PlanningHttpSupport.Error(missing?428:412,missing?"precondition_required":"resource_version_stale",$"A matching {kind} If-Match header is required.",context);return false;}
    private static bool TryMap(Exception exception,HttpContext context,out IResult? result)
    {result=exception switch{AdministrativeRequestException request=>PlanningHttpSupport.Error(400,"invalid_request",request.Message,context,request.Issues.Select(issue=>(object)new{field=string.IsNullOrEmpty(issue.Field)?null:issue.Field,code=issue.Code,message=issue.Message})),AdministrativeSetupValidationException validation=>PlanningHttpSupport.Error(422,"validation_failed",validation.Message,context,validation.Issues.Select(issue=>(object)new{field=issue.Field,code=issue.Code,message=issue.Message})),EmployeeNumberConflictException=>PlanningHttpSupport.Error(409,"employee_number_conflict",exception.Message,context),EmployeeAssignedCalendarNotFoundException=>PlanningHttpSupport.Error(422,"assigned_calendar_not_found",exception.Message,context),EmployeeCalendarUsageException=>PlanningHttpSupport.Error(422,"invalid_assigned_calendar_usage",exception.Message,context),EmployeeAvailabilityHorizonException=>PlanningHttpSupport.Error(400,"invalid_availability_horizon",exception.Message,context),IsraeliHolidaySyncRangeException=>PlanningHttpSupport.Error(400,"invalid_holiday_sync_range",exception.Message,context),HolidayDateConflictException=>PlanningHttpSupport.Error(409,"holiday_date_conflict",exception.Message,context),AdministrativeResourceNotFoundException=>NotFound(context,"Administrative resource"),AdministrativeVersionConflictException=>PlanningHttpSupport.Error(412,"resource_version_stale",exception.Message,context),EditModeMutationException edit=>PlanningHttpSupport.Error(409,edit.Code,edit.Message,context),_=>null};return result is not null;}
    private static bool TryDate(string? value, string field, HttpContext context, out DateOnly? result, out IResult? error)
    { if(string.IsNullOrWhiteSpace(value)){result=null;error=null;return true;} if(DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed)){result=parsed;error=null;return true;} result=null;error=PlanningHttpSupport.Error(400,"invalid_date",$"{field} must use yyyy-MM-dd.",context);return false; }
    private static bool TryInstant(string? value, string field, HttpContext context, out DateTimeOffset result, out IResult? error)
    { if(value is not null&&DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out result)){error=null;return true;} result=default;error=PlanningHttpSupport.Error(400,"invalid_availability_horizon",$"{field} must be an RFC3339 timestamp.",context);return false; }
    private static IResult NotFound(HttpContext context,string kind)=>PlanningHttpSupport.Error(404,"resource_not_found",$"The requested {kind} was not found.",context);
    private static void SetTag(HttpResponse response,string kind,string id,int version)=>response.Headers.ETag=$"\"{kind}:{id}:v{version}\"";
}
