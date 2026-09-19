using Meimad.Planner.Server.Application.ClientPortal;

namespace Meimad.Planner.Server.Api.ClientPortal;

/// <summary>
/// Manages which Customers the Server pushes to the cloud customer portal. This maps a Customer
/// name to a portal customer id only; the portal login for that customer id is still created
/// manually in the portal project.
/// </summary>
internal static class ClientPortalCustomerEndpoints
{
    internal static void MapClientPortalCustomerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var customers = endpoints.MapGroup("/api/v1/client-portal/customers");
        customers.MapGet(string.Empty, ListAsync);
        customers.MapPost(string.Empty, CreateAsync);
        customers.MapGet("/{customerId}", GetAsync);
        customers.MapPut("/{customerId}", UpdateAsync);
        customers.MapDelete("/{customerId}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(ClientPortalCustomerService service, CancellationToken token)
    {
        var values = await service.ListAsync(token);
        return Results.Ok(new ClientPortalCustomerListResponse(
            values.Select(ClientPortalCustomerResponse.FromDomain).ToArray(), null));
    }

    private static async Task<IResult> GetAsync(
        string customerId, HttpContext context, ClientPortalCustomerService service, CancellationToken token)
    {
        var value = await service.GetAsync(customerId, token);
        return value is null ? NotFound(context) : Results.Ok(ClientPortalCustomerResponse.FromDomain(value));
    }

    private static async Task<IResult> CreateAsync(
        CreateClientPortalCustomerRequest request,
        HttpContext context,
        ClientPortalCustomerService service,
        CancellationToken token)
    {
        try
        {
            var value = await service.CreateAsync(request.CustomerId, request.Customer, token);
            return Results.Created(
                $"/api/v1/client-portal/customers/{Uri.EscapeDataString(value.CustomerId)}",
                ClientPortalCustomerResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped)) { return mapped!; }
    }

    private static async Task<IResult> UpdateAsync(
        string customerId,
        UpdateClientPortalCustomerRequest request,
        HttpContext context,
        ClientPortalCustomerService service,
        CancellationToken token)
    {
        try
        {
            var value = await service.RenameAsync(customerId, request.Customer, token);
            return Results.Ok(ClientPortalCustomerResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped)) { return mapped!; }
    }

    private static async Task<IResult> DeleteAsync(
        string customerId, HttpContext context, ClientPortalCustomerService service, CancellationToken token) =>
        await service.DeleteAsync(customerId, token) ? Results.NoContent() : NotFound(context);

    private static bool TryMap(Exception exception, HttpContext context, out IResult? result)
    {
        result = exception switch
        {
            ClientPortalCustomerValidationException validation => PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity, "validation_failed", validation.Message, context,
                [new { field = validation.Field, code = validation.Code, message = validation.Message }]),
            ClientPortalCustomerIdConflictException => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict, "client_portal_customer_id_conflict", exception.Message, context),
            ClientPortalCustomerNameConflictException => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict, "client_portal_customer_name_conflict", exception.Message, context),
            ClientPortalCustomerNotFoundException => NotFound(context),
            _ => null
        };
        return result is not null;
    }

    private static IResult NotFound(HttpContext context) => PlanningHttpSupport.Error(
        StatusCodes.Status404NotFound,
        "resource_not_found",
        "The requested client portal customer was not found.",
        context);
}
