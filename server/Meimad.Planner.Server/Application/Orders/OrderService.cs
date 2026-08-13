using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Orders;

namespace Meimad.Planner.Server.Application.Orders;

internal sealed class OrderService
{
    private readonly IOrderRepository repository;
    private readonly TimeProvider timeProvider;

    public OrderService(IOrderRepository repository, TimeProvider timeProvider)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
    }

    internal async Task<PlannerOrder> CreateAsync(
        CreateOrderCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var values = OrderValidator.ValidateAndNormalize(ToValues(command));
        if (values.Status is OrderStatus.InProduction or OrderStatus.Complete)
        {
            throw new OrderManualProductionStatusException(
                "A new Order status must be active or cancelled; production status is derived by the Server.");
        }
        var now = timeProvider.GetUtcNow();
        var order = new PlannerOrder(
            Guid.NewGuid().ToString("N"),
            values.CaseId,
            values.OrderNumber,
            values.Quantity,
            values.WorkFinishDate,
            values.Status,
            values.Notes,
            1,
            now,
            now);

        return await repository.CreateAsync(order, editAuthority, cancellationToken);
    }

    internal Task<PlannerOrder?> GetByIdAsync(
        string orderId,
        CancellationToken cancellationToken = default) =>
        repository.GetByIdAsync(orderId, cancellationToken);

    internal Task<IReadOnlyList<PlannerOrder>> ListByCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default) =>
        repository.ListByCaseAsync(caseId, cancellationToken);

    internal async Task<PlannerOrder> UpdateAsync(
        string orderId,
        int expectedVersion,
        UpdateOrderCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var current = await repository.GetByIdAsync(orderId, cancellationToken)
            ?? throw new OrderNotFoundException(orderId);

        var values = OrderValidator.ValidateAndNormalize(new OrderValues(
            current.CaseId,
            Select(command.OrderNumber, current.OrderNumber),
            Select(command.Quantity, current.Quantity) ?? 0,
            Select(command.WorkFinishDate, current.WorkFinishDate.ToString("yyyy-MM-dd")),
            Select(command.Status, current.Status.ToContractToken()),
            Select(command.Notes, current.Notes)));

        var updated = current with
        {
            OrderNumber = values.OrderNumber,
            Quantity = values.Quantity,
            WorkFinishDate = values.WorkFinishDate,
            Status = values.Status,
            Notes = values.Notes,
            Version = expectedVersion + 1,
            UpdatedAt = timeProvider.GetUtcNow()
        };

        return await repository.UpdateAsync(
                updated,
                expectedVersion,
                command.Status.IsSpecified,
                editAuthority,
                cancellationToken)
            ?? throw new OrderVersionConflictException(orderId, expectedVersion);
    }

    private static OrderValues ToValues(CreateOrderCommand command) => new(
        command.CaseId,
        command.OrderNumber,
        command.Quantity,
        command.WorkFinishDate,
        command.Status,
        command.Notes);

    private static T Select<T>(OrderField<T> field, T current) =>
        field.IsSpecified ? field.Value : current;
}

internal sealed class OrderCaseNotFoundException : Exception
{
    internal OrderCaseNotFoundException(string caseId)
        : base($"Case '{caseId}' was not found.")
    {
    }
}

internal sealed class OrderNotFoundException : Exception
{
    internal OrderNotFoundException(string orderId)
        : base($"Order '{orderId}' was not found.")
    {
    }
}

internal sealed class OrderVersionConflictException : Exception
{
    internal OrderVersionConflictException(string orderId, int expectedVersion)
        : base($"Order '{orderId}' is no longer at version {expectedVersion}.")
    {
    }
}

internal sealed class OrderQuantityBelowAllocatedException : Exception
{
    internal OrderQuantityBelowAllocatedException(string orderId, long allocatedQuantity)
        : base($"Order '{orderId}' quantity cannot be lower than its allocated quantity {allocatedQuantity}.")
    {
    }
}

internal sealed class OrderDerivedStatusException : Exception
{
    internal OrderDerivedStatusException(string orderId, OrderStatus expected)
        : base($"Order '{orderId}' status is derived from related production work and must be '{expected.ToContractToken()}'.")
    {
    }
}

internal sealed class OrderManualProductionStatusException(string message) : Exception(message);
