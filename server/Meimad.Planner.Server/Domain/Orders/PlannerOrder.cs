namespace Meimad.Planner.Server.Domain.Orders;

internal sealed record PlannerOrder(
    string OrderId,
    string CaseId,
    string OrderNumber,
    int Quantity,
    DateOnly WorkFinishDate,
    OrderStatus Status,
    string? Notes,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
