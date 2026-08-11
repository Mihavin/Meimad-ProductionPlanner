using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Orders;

namespace Meimad.Planner.Server.Application.Orders;

internal interface IOrderRepository
{
    Task<PlannerOrder> CreateAsync(
        PlannerOrder order,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<PlannerOrder?> GetByIdAsync(
        string orderId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PlannerOrder>> ListByCaseAsync(
        string caseId,
        CancellationToken cancellationToken);

    Task<PlannerOrder?> UpdateAsync(
        PlannerOrder order,
        int expectedVersion,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);
}
