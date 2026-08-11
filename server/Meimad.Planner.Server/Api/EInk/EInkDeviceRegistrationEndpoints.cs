using Meimad.Planner.Server.Application.EInk;
using Meimad.Planner.Server.Application.EditMode;

namespace Meimad.Planner.Server.Api.EInk;

internal static class EInkDeviceRegistrationEndpoints
{
    internal static void MapEInkDeviceRegistrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/eink/device-registrations", ListAsync);
        endpoints.MapPost("/api/v1/eink/device-registrations", CreateAsync);
        endpoints.MapPatch("/api/v1/eink/device-registrations/{deviceId}", UpdateAsync);
    }

    private static async Task<IResult> ListAsync(
        EInkDeviceRegistrationService service,
        CancellationToken cancellationToken)
    {
        var values = await service.ListAsync(cancellationToken);
        return Results.Ok(new
        {
            items = values.Select(value =>
                EInkDeviceRegistrationResponse.FromApplication(value)).ToArray()
        });
    }

    private static async Task<IResult> CreateAsync(
        CreateEInkDeviceRegistrationRequest request,
        HttpContext context,
        EInkDeviceRegistrationService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(
                context,
                out var editAuthority,
                out var error))
        {
            return error!;
        }

        try
        {
            var result = await service.CreateAsync(
                new CreateEInkDeviceRegistrationCommand(request.DeviceName, request.MachineId),
                editAuthority!,
                cancellationToken);
            return Results.Created(
                $"/api/v1/eink/device-registrations/{result.Registration.DeviceId}",
                EInkDeviceRegistrationResponse.FromApplication(
                    result.Registration,
                    result.RegistrationToken));
        }
        catch (Exception exception) when (Known(exception))
        {
            return Error(context, exception);
        }
    }

    private static async Task<IResult> UpdateAsync(
        string deviceId,
        UpdateEInkDeviceRegistrationRequest request,
        HttpContext context,
        EInkDeviceRegistrationService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(
                context,
                out var editAuthority,
                out var error))
        {
            return error!;
        }

        try
        {
            var result = await service.UpdateAsync(
                deviceId,
                new UpdateEInkDeviceRegistrationCommand(
                    request.MachineId,
                    request.IsEnabled,
                    request.RotateCredential),
                editAuthority!,
                cancellationToken);
            return Results.Ok(EInkDeviceRegistrationResponse.FromApplication(
                result.Registration,
                result.RegistrationToken));
        }
        catch (Exception exception) when (Known(exception))
        {
            return Error(context, exception);
        }
    }

    private static bool Known(Exception exception) => exception is
        EInkDeviceRegistrationValidationException or
        EInkDeviceRegistrationNotFoundException or
        EInkDeviceBindingException or
        EditModeMutationException;

    private static IResult Error(HttpContext context, Exception exception) => exception switch
    {
        EInkDeviceRegistrationValidationException validation => PlanningHttpSupport.Error(
            StatusCodes.Status422UnprocessableEntity,
            "device_registration_invalid",
            validation.Message,
            context),
        EInkDeviceRegistrationNotFoundException notFound => PlanningHttpSupport.Error(
            StatusCodes.Status404NotFound,
            "device_registration_not_found",
            notFound.Message,
            context),
        EInkDeviceBindingException binding => PlanningHttpSupport.Error(
            binding.Code == "machine_not_found"
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status409Conflict,
            binding.Code,
            binding.Message,
            context),
        EditModeMutationException authority => PlanningHttpSupport.Error(
            StatusCodes.Status409Conflict,
            authority.Code,
            authority.Message,
            context),
        _ => throw new ArgumentOutOfRangeException(nameof(exception))
    };
}

internal sealed record CreateEInkDeviceRegistrationRequest(string DeviceName, string? MachineId);

internal sealed record UpdateEInkDeviceRegistrationRequest(
    string? MachineId,
    bool IsEnabled,
    bool RotateCredential);

internal sealed record EInkDeviceRegistrationResponse(
    string DeviceId,
    string DeviceName,
    string? MachineId,
    bool IsEnabled,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? RegistrationToken)
{
    internal static EInkDeviceRegistrationResponse FromApplication(
        EInkDeviceRegistration value,
        string? registrationToken = null) => new(
        value.DeviceId,
        value.DeviceName,
        value.MachineId,
        value.IsEnabled,
        value.Version,
        value.CreatedAt,
        value.UpdatedAt,
        registrationToken);
}
