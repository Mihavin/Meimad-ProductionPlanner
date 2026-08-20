using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Orders;
using Meimad.Planner.Server.Application.ProductionBatches;
using Meimad.Planner.Server.Domain.Orders;
using System.Security.Cryptography;
using System.Text;

namespace Meimad.Planner.Server.Application.Cases;

internal sealed record CaseComponentDetails(
    string CaseComponentId,
    string ParentCaseId,
    string ParentPartNumber,
    string ParentCaseName,
    string ChildCaseId,
    string ChildPartNumber,
    string ChildCaseName,
    double QuantityPerParent,
    int SortOrder,
    string? Notes,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record ComponentGraphEdge(
    string CaseComponentId,
    string ParentCaseId,
    string ParentPartNumber,
    string ChildCaseId,
    string ChildPartNumber,
    double QuantityPerParent);

internal sealed record ComponentDemandRow(
    string ParentCaseId,
    string ChildCaseId,
    string ChildPartNumber,
    double QuantityPerParent,
    IReadOnlyList<double> MultiplierPath,
    double TotalRequiredQuantity,
    int Level,
    IReadOnlyList<string> Path);

internal sealed record ComponentDemandPreview(
    string CaseId,
    string PartNumber,
    double OrderQuantity,
    IReadOnlyList<ComponentDemandRow> Items);

internal sealed record DerivedCaseOrder(
    string DerivedOrderKey,
    string ChildCaseId,
    string SourceOrderId,
    string SourceOrderNumber,
    string SourceParentCaseId,
    string SourceParentPartNumber,
    double QuantityPerParent,
    double DerivedQuantity,
    double AllocatedQuantity,
    double RemainingQuantity,
    string WorkFinishDate,
    string Status,
    int Level,
    IReadOnlyList<string> Path);

internal interface ICaseComponentRepository
{
    Task<IReadOnlyList<CaseComponentDetails>> ListComponentsAsync(string caseId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CaseComponentDetails>> ListWhereUsedAsync(string caseId, CancellationToken cancellationToken);
    Task<CaseComponentDetails?> GetAsync(string componentId, CancellationToken cancellationToken);
    Task<CaseComponentDetails> CreateAsync(
        string componentId, string parentCaseId, string childCaseId, double quantityPerParent,
        int sortOrder, string? notes, DateTimeOffset now, EditAuthority editAuthority,
        CancellationToken cancellationToken);
    Task<CaseComponentDetails?> UpdateAsync(
        string componentId, double quantityPerParent, int sortOrder, string? notes, bool isActive,
        int expectedVersion, DateTimeOffset now, EditAuthority editAuthority,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ComponentGraphEdge>> ReadActiveGraphAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, double>> ReadDerivedAllocatedQuantitiesAsync(
        string childCaseId, CancellationToken cancellationToken);
}

internal sealed class CaseComponentService(
    ICaseComponentRepository repository,
    ICaseRepository caseRepository,
    IProductionBatchRepository productionBatchRepository,
    TimeProvider timeProvider)
{
    internal Task<IReadOnlyList<CaseComponentDetails>> ListComponentsAsync(
        string caseId, CancellationToken cancellationToken) =>
        repository.ListComponentsAsync(caseId, cancellationToken);

    internal Task<IReadOnlyList<CaseComponentDetails>> ListWhereUsedAsync(
        string caseId, CancellationToken cancellationToken) =>
        repository.ListWhereUsedAsync(caseId, cancellationToken);

    internal async Task<CaseComponentDetails> CreateAsync(
        string parentCaseId, string childCaseId, double quantityPerParent,
        int sortOrder, string? notes, EditAuthority editAuthority, CancellationToken cancellationToken)
    {
        Validate(parentCaseId, childCaseId, quantityPerParent, sortOrder, notes);
        if ((await productionBatchRepository.ListByCaseAsync(parentCaseId, cancellationToken)).Count > 0)
            throw new CaseParentBatchesMustBeRemovedException();
        return await repository.CreateAsync(
            $"component-{Guid.NewGuid():N}", parentCaseId, childCaseId, quantityPerParent,
            sortOrder, NormalizeNotes(notes), timeProvider.GetUtcNow(), editAuthority, cancellationToken);
    }

    internal async Task<CaseComponentDetails> UpdateAsync(
        string parentCaseId, string componentId, double quantityPerParent,
        int sortOrder, string? notes, bool isActive, int expectedVersion,
        EditAuthority editAuthority, CancellationToken cancellationToken)
    {
        var current = await repository.GetAsync(componentId, cancellationToken)
            ?? throw new CaseComponentNotFoundException();
        if (!StringComparer.Ordinal.Equals(current.ParentCaseId, parentCaseId))
            throw new CaseComponentNotFoundException();
        Validate(current.ParentCaseId, current.ChildCaseId, quantityPerParent, sortOrder, notes);
        return await repository.UpdateAsync(
            componentId, quantityPerParent, sortOrder, NormalizeNotes(notes), isActive,
            expectedVersion, timeProvider.GetUtcNow(), editAuthority, cancellationToken)
            ?? throw new CaseComponentVersionConflictException();
    }

    internal async Task<ComponentDemandPreview> PreviewDemandAsync(
        string caseId, double orderQuantity, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(orderQuantity) || orderQuantity <= 0)
            throw new CaseComponentValidationException("quantity", "Quantity must be greater than zero.");
        var root = await caseRepository.GetByIdAsync(caseId, cancellationToken)
            ?? throw new CaseComponentNotFoundException();
        var graph = (await repository.ReadActiveGraphAsync(cancellationToken))
            .GroupBy(edge => edge.ParentCaseId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var result = new List<ComponentDemandRow>();
        Explode(caseId, orderQuantity, [], [root.PartNumber], new HashSet<string>(StringComparer.Ordinal) { caseId });
        return new ComponentDemandPreview(caseId, root.PartNumber, orderQuantity, result);

        void Explode(
            string parentId, double parentQuantity, IReadOnlyList<double> multipliers,
            IReadOnlyList<string> path, HashSet<string> ancestors)
        {
            if (!graph.TryGetValue(parentId, out var children)) return;
            foreach (var edge in children)
            {
                if (!ancestors.Add(edge.ChildCaseId))
                    throw new InvalidDataException("Stored Case Component graph contains a cycle.");
                var total = checked(parentQuantity * edge.QuantityPerParent);
                if (!double.IsFinite(total))
                    throw new CaseComponentValidationException("quantity", "Component demand exceeds the supported range.");
                var nextMultipliers = multipliers.Append(edge.QuantityPerParent).ToArray();
                var nextPath = path.Append(edge.ChildPartNumber).ToArray();
                result.Add(new ComponentDemandRow(
                    parentId, edge.ChildCaseId, edge.ChildPartNumber, edge.QuantityPerParent,
                    nextMultipliers, total, nextPath.Length - 1, nextPath));
                Explode(edge.ChildCaseId, total, nextMultipliers, nextPath, ancestors);
                ancestors.Remove(edge.ChildCaseId);
            }
        }
    }

    private static void Validate(
        string parentCaseId, string childCaseId, double quantityPerParent, int sortOrder, string? notes)
    {
        if (string.IsNullOrWhiteSpace(parentCaseId))
            throw new CaseComponentValidationException("parentCaseId", "Parent Case is required.");
        if (string.IsNullOrWhiteSpace(childCaseId))
            throw new CaseComponentValidationException("childCaseId", "Child Case is required.");
        if (StringComparer.Ordinal.Equals(parentCaseId, childCaseId))
            throw new CaseComponentValidationException("childCaseId", "A Case cannot contain itself.");
        if (!double.IsFinite(quantityPerParent) || quantityPerParent <= 0)
            throw new CaseComponentValidationException("quantityPerParent", "Quantity per parent must be greater than zero.");
        if (sortOrder < 0)
            throw new CaseComponentValidationException("sortOrder", "Sort order cannot be negative.");
        if (notes?.Trim().Length > 2000)
            throw new CaseComponentValidationException("notes", "Notes must contain at most 2,000 characters.");
    }

    private static string? NormalizeNotes(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}

internal sealed class DerivedCaseOrderService(
    ICaseComponentRepository componentRepository,
    IOrderRepository orderRepository,
    ICaseRepository caseRepository)
{
    internal async Task<IReadOnlyList<DerivedCaseOrder>> ListAsync(
        string childCaseId, CancellationToken cancellationToken)
    {
        _ = await caseRepository.GetByIdAsync(childCaseId, cancellationToken)
            ?? throw new CaseComponentNotFoundException();
        var graph = await componentRepository.ReadActiveGraphAsync(cancellationToken);
        var byChild = graph.GroupBy(edge => edge.ChildCaseId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var allocated = await componentRepository.ReadDerivedAllocatedQuantitiesAsync(
            childCaseId, cancellationToken);
        var rows = new List<DerivedCaseOrder>();
        await VisitAsync(childCaseId, 1, [], [childCaseId]);
        return rows.OrderBy(row => row.WorkFinishDate, StringComparer.Ordinal)
            .ThenBy(row => row.SourceOrderNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.DerivedOrderKey, StringComparer.Ordinal).ToArray();

        async Task VisitAsync(
            string currentCaseId, double downstreamMultiplier,
            IReadOnlyList<ComponentGraphEdge> downstreamPath,
            IReadOnlyList<string> ancestors)
        {
            if (!byChild.TryGetValue(currentCaseId, out var parents)) return;
            foreach (var edge in parents)
            {
                if (ancestors.Contains(edge.ParentCaseId, StringComparer.Ordinal))
                    throw new InvalidDataException("Stored Case Component graph contains a cycle.");
                var multiplier = checked(downstreamMultiplier * edge.QuantityPerParent);
                var path = new[] { edge }.Concat(downstreamPath).ToArray();
                var orders = await orderRepository.ListByCaseAsync(edge.ParentCaseId, cancellationToken);
                foreach (var order in orders)
                {
                    var derived = checked(order.Quantity * multiplier);
                    var key = DerivedOrderKeys.Create(order.OrderId, childCaseId, path.Select(item => item.CaseComponentId));
                    allocated.TryGetValue(key, out var allocatedQuantity);
                    rows.Add(new DerivedCaseOrder(
                        key, childCaseId, order.OrderId, order.OrderNumber,
                        edge.ParentCaseId, edge.ParentPartNumber, multiplier, derived,
                        allocatedQuantity, Math.Max(0, derived - allocatedQuantity),
                        order.WorkFinishDate.ToString("yyyy-MM-dd"), order.Status.ToContractToken(),
                        path.Length,
                        path.Select(item => item.ParentPartNumber)
                            .Append(path[^1].ChildPartNumber).ToArray()));
                }
                await VisitAsync(
                    edge.ParentCaseId, multiplier, path,
                    ancestors.Append(edge.ParentCaseId).ToArray());
            }
        }
    }
}

internal static class DerivedOrderKeys
{
    internal static string Create(string orderId, string childCaseId, IEnumerable<string> componentIds)
    {
        var source = $"{orderId}|{childCaseId}|{string.Join('|', componentIds)}";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return $"derived:{orderId}:{digest}";
    }
}

internal sealed class CaseComponentValidationException(string field, string message) : Exception(message)
{
    internal string Field { get; } = field;
}

internal sealed class CaseComponentCycleException()
    : Exception("The component would create a circular Case structure.");

internal sealed class CaseComponentDuplicateException()
    : Exception("This child Case is already a component of the parent Case.");

internal sealed class CaseComponentNotFoundException : Exception;

internal sealed class CaseComponentVersionConflictException : Exception;

internal sealed class CaseParentBatchesMustBeRemovedException()
    : Exception("Remove this Case's Production Batches before adding a component. A Case with components is a parent and cannot retain direct Production Batches.");
