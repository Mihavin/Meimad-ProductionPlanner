namespace Meimad.Planner.Server.Application.Orders;

internal sealed record CreateOrderCommand(
    string? CaseId,
    string? OrderNumber,
    int Quantity,
    string? WorkFinishDate,
    string? Status,
    string? Notes);

internal readonly record struct OrderField<T>(bool IsSpecified, T Value)
{
    internal static OrderField<T> Unspecified => new(false, default!);

    internal static OrderField<T> Specified(T value) => new(true, value);
}

internal sealed record UpdateOrderCommand(
    OrderField<string?> OrderNumber,
    OrderField<int?> Quantity,
    OrderField<string?> WorkFinishDate,
    OrderField<string?> Status,
    OrderField<string?> Notes);
