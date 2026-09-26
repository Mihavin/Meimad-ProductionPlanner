using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Configuration;
using Microsoft.AspNetCore.DataProtection;

namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed class KitaronSyncService
{
    private readonly IKitaronConnectionRepository connectionRepository;
    private readonly KitaronMappingService mappingService;
    private readonly IKitaronSourceReader sourceReader;
    private readonly IKitaronSyncRepository syncRepository;
    private readonly IKitaronStationRepository stationRepository;
    private readonly IDataProtector passwordProtector;
    private readonly TimeProvider timeProvider;
    private readonly string workingFolderRoot;
    private readonly ILogger<KitaronSyncService> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private TaskCompletionSource runRequestSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int runRequested;

    public KitaronSyncService(
        IKitaronConnectionRepository connectionRepository,
        KitaronMappingService mappingService,
        IKitaronSourceReader sourceReader,
        IKitaronSyncRepository syncRepository,
        IDataProtectionProvider dataProtectionProvider,
        DatabaseOptions databaseOptions,
        TimeProvider timeProvider,
        ILogger<KitaronSyncService> logger,
        IKitaronStationRepository stationRepository)
    {
        this.connectionRepository = connectionRepository;
        this.mappingService = mappingService;
        this.sourceReader = sourceReader;
        this.syncRepository = syncRepository;
        this.stationRepository = stationRepository;
        this.timeProvider = timeProvider;
        this.logger = logger;
        passwordProtector = dataProtectionProvider.CreateProtector("Meimad.Planner.Kitaron.SqlPassword.v1");
        var dataDirectory = Path.GetDirectoryName(databaseOptions.DatabasePath)
            ?? throw new InvalidOperationException("The Planner database path has no parent directory.");
        workingFolderRoot = Path.GetFullPath(Path.Combine(dataDirectory, "KitaronCases"));
    }

    internal Task<KitaronSyncStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        syncRepository.GetStatusAsync(cancellationToken);

    /// <summary>
    /// Asks the periodic synchronization to run now instead of at its next interval, for example
    /// after a planner changed a Kitaron station decision. Requests that arrive while a run is going
    /// coalesce into one more run. `wake` false only records the request for the next regular pass.
    /// </summary>
    internal void RequestRun(bool wake = true)
    {
        Volatile.Write(ref runRequested, 1);
        if (wake)
        {
            Interlocked.Exchange(
                ref runRequestSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
        }
    }

    /// <summary>Takes a pending run request; true when one was waiting.</summary>
    internal bool TakeRunRequest() => Interlocked.Exchange(ref runRequested, 0) == 1;

    /// <summary>Completes when a run is requested after this task was read.</summary>
    internal Task RunRequested => Volatile.Read(ref runRequestSignal).Task;

    internal async Task<KitaronSyncStatus> RunAsync(CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken))
            throw new KitaronSyncBlockedException("A Kitaron synchronization is already running.");
        try
        {
            var mapping = await mappingService.GetAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            await syncRepository.MarkStartedAsync(mapping.Version, now, cancellationToken);
            try
            {
                if (mapping.Status != "ready_for_implementation")
                    throw new KitaronSyncBlockedException("Save the mapping as Ready before synchronization.");
                var connection = await connectionRepository.GetAsync(cancellationToken);
                if (!connection.Enabled)
                    throw new KitaronSyncBlockedException("Enable the Kitaron connector before synchronization.");
                if (connection.LastTestStatus != "succeeded")
                    throw new KitaronSyncBlockedException("Run a successful read-only connection test first.");
                if (string.IsNullOrWhiteSpace(connection.ProtectedPassword))
                    throw new KitaronSyncBlockedException("No Kitaron password is configured.");
                if (!mapping.DetectedColumns.Any(column =>
                        StringComparer.OrdinalIgnoreCase.Equals(column.Name, "RecordID")))
                {
                    throw new KitaronSyncBlockedException(
                        "The configured planning view must expose RecordID for canonical Kitaron order synchronization.");
                }

                string password;
                try { password = passwordProtector.Unprotect(connection.ProtectedPassword); }
                catch (Exception exception)
                {
                    throw new KitaronSyncBlockedException(
                        "The stored password cannot be decrypted on this Server. Save it again.") { Source = exception.Source };
                }

                var active = mapping.Fields
                    .Where(field => field.Enabled
                        && field.ModelModes.Contains(mapping.ModelMode, StringComparer.Ordinal)
                        && field.TargetEntity is "cases" or "orders" or "case_operations" or "material_orders")
                    .ToArray();
                var workFields = active.Where(field => field.TargetEntity != "material_orders").ToArray();
                var materialFields = active.Where(field => field.TargetEntity == "material_orders").ToArray();
                var columns = workFields
                    .Where(field => !field.ConnectorManaged
                        && field.SourceColumn is not null
                        && !StringComparer.OrdinalIgnoreCase.Equals(field.SourceColumn, "auto")
                        && field.Transform != "generated_working_folder")
                    .Select(field => field.SourceColumn!)
                    .Append("RecordID")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var materialColumns = materialFields
                    .Where(field => field.SourceColumn is not null)
                    .Select(field => field.SourceColumn!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var snapshot = await sourceReader.ReadAsync(
                    connection, password, columns, materialColumns, cancellationToken);
                var existingCasePartNumbers = await syncRepository.GetExistingCasePartNumbersAsync(cancellationToken);
                var stations = (await stationRepository.ListAsync(cancellationToken))
                    .ToDictionary(station => station.KitaronStationId);
                var plan = BuildPlan(snapshot, active, existingCasePartNumbers, mapping.Version, stations);
                return await syncRepository.ApplyAsync(plan, timeProvider.GetUtcNow(), cancellationToken);
            }
            catch (KitaronSyncBlockedException exception)
            {
                return await syncRepository.MarkFailedAsync(
                    "blocked", exception.Message, timeProvider.GetUtcNow(), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Kitaron one-way synchronization failed.");
                return await syncRepository.MarkFailedAsync(
                    "failed", SafeMessage(exception), timeProvider.GetUtcNow(), cancellationToken);
            }
        }
        finally { gate.Release(); }
    }

    internal KitaronSyncPlan BuildPlan(
        KitaronSourceSnapshot snapshot,
        IReadOnlyList<KitaronMappingField> fields,
        IReadOnlySet<string> existingCasePartNumbers,
        int mappingVersion,
        IReadOnlyDictionary<int, KitaronStationRecord>? stations = null)
    {
        var byTarget = fields.ToDictionary(
            field => $"{field.TargetEntity}.{field.TargetField}", StringComparer.Ordinal);
        KitaronMappingField Field(string entity, string field) =>
            byTarget.GetValueOrDefault($"{entity}.{field}")
            ?? throw new KitaronSyncBlockedException($"The ready mapping is missing {entity}.{field}.");

        var warnings = new List<string>();
        var parsed = new List<ParsedRow>(snapshot.WorkRows.Count);
        foreach (var row in snapshot.WorkRows)
        {
            var part = Text(row, Field("cases", "part_number"));
            if (part is null) { AddWarning(warnings, "A source row without a Part Number was skipped."); continue; }
            var name = Text(row, Field("cases", "name")) ?? part;
            var orderNumber = OptionalText(row, byTarget, "orders.order_reference");
            if (IsIgnoredOrderNumber(orderNumber))
            {
                AddWarning(warnings, $"Known Kitaron test Order {orderNumber} was skipped with its source row.");
                continue;
            }
            parsed.Add(new ParsedRow(
                RawText(row, "RecordID"), part, name,
                OptionalText(row, byTarget, "cases.revision"),
                OptionalText(row, byTarget, "cases.customer"),
                orderNumber,
                OptionalInt(row, byTarget, "orders.quantity"),
                OptionalDate(row, byTarget, "orders.work_finish_date"),
                OptionalInt(row, byTarget, "case_operations.operation_number"),
                OptionalInt(row, byTarget, "case_operations.route_position"),
                OptionalText(row, byTarget, "case_operations.name"),
                OptionalText(row, byTarget, "case_operations.required_machine_type", manualLookupAsNull: true),
                OptionalSeconds(row, byTarget, "case_operations.setup_seconds"),
                OptionalSeconds(row, byTarget, "case_operations.cycle_seconds")));
        }

        var reachableParts = parsed.Select(row => row.Part)
            .Concat(snapshot.Orders.Select(order => order.PartNumber))
            .Concat((snapshot.WorkOrders ?? []).Select(workOrder => workOrder.PartNumber))
            .Concat(existingCasePartNumbers)
            .Concat(snapshot.Components.Select(component => component.ParentPartNumber))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var componentsByParent = snapshot.Components
            .GroupBy(item => item.ParentPartNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var selectedComponents = new List<KitaronSourceComponent>();
        var pendingParts = new Queue<string>(reachableParts);
        var expandedParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pendingParts.TryDequeue(out var parentPart))
        {
            if (!expandedParts.Add(parentPart) || !componentsByParent.TryGetValue(parentPart, out var children))
                continue;
            foreach (var component in children)
            {
                selectedComponents.Add(component);
                if (reachableParts.Add(component.ChildPartNumber)) pendingParts.Enqueue(component.ChildPartNumber);
            }
        }

        var caseCandidates = parsed.Select(row => new CaseCandidate(
                row.Part, row.Name, row.Revision, row.Customer))
            .Concat(snapshot.Orders.Select(order => new CaseCandidate(
                order.PartNumber, order.Name, order.Revision, null)))
            // Every open work order's part is a synchronized Case, so its batch is never skipped
            // because the part is outside the planning view and the BOM trees.
            .Concat((snapshot.WorkOrders ?? []).Select(workOrder => new CaseCandidate(
                workOrder.PartNumber, workOrder.PartName ?? workOrder.PartNumber, workOrder.PartRevision, null)))
            .Concat(selectedComponents.SelectMany(component => new[]
            {
                new CaseCandidate(component.ParentPartNumber, component.ParentName, component.ParentRevision, null),
                new CaseCandidate(component.ChildPartNumber, component.ChildName, component.ChildRevision, null)
            }));
        Directory.CreateDirectory(workingFolderRoot);
        var cases = caseCandidates.GroupBy(row => row.Part, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var name = ChooseText(group.Select(item => item.Name)) ?? group.Key;
                var revision = Consistent(group.Select(item => item.Revision), group.Key, "revision", warnings);
                var customer = Consistent(group.Select(item => item.Customer), group.Key, "customer", warnings);
                var folder = Path.Combine(workingFolderRoot, SafeFolder(group.Key));
                return new KitaronSyncCase(group.Key, group.Key, name, revision, customer, folder,
                    Hash(group.Key, name, revision, customer, folder));
            }).OrderBy(item => item.PartNumber, StringComparer.OrdinalIgnoreCase).ToArray();

        var canonicalOrders = BuildOrders(snapshot.Orders, warnings);
        var canonicalSourceRows = canonicalOrders
            .Select(order => OrderRowIdentity(order.CaseSourceKey, order.SourceKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var canonicalFacts = canonicalOrders.Select(OrderFact)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var workOrders = parsed
            .Where(row => row.SourceRecordId is not null
                && row.OrderNumber is not null
                && !IsIgnoredOrderNumber(row.OrderNumber)
                && row.Quantity is > 0
                && row.WorkFinishDate is not null)
            .GroupBy(row => $"{row.Part}\u001f{row.OrderNumber!.Trim()}\u001f{row.Quantity}\u001f{row.WorkFinishDate:yyyy-MM-dd}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(row => row.SourceRecordId, StringComparer.OrdinalIgnoreCase).First())
            .Where(row => !canonicalSourceRows.Contains(OrderRowIdentity(row.Part, row.SourceRecordId!))
                && !canonicalFacts.Contains(OrderFact(
                row.Part, row.OrderNumber!, row.Quantity!.Value, row.WorkFinishDate!.Value, "active")))
            .Select(row =>
            {
                var canonical = row.OrderNumber!.Trim();
                var sourceKey = $"work:{row.SourceRecordId}";
                var reference = $"{canonical}/{row.SourceRecordId}";
                return new KitaronSyncOrder(
                    sourceKey, row.Part, reference, row.Quantity!.Value, row.WorkFinishDate!.Value,
                    "active", Hash(sourceKey, reference, row.Quantity.Value, row.WorkFinishDate.Value, "active"))
                {
                    CanonicalOrderNumber = canonical
                };
            });
        var orders = canonicalOrders.Concat(workOrders)
            .OrderBy(item => item.SourceKey, StringComparer.OrdinalIgnoreCase).ToArray();

        var components = selectedComponents
            .Where(item => item.QuantityPerParent > 0 && double.IsFinite(item.QuantityPerParent))
            .GroupBy(item => item.SourceKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.SortOrder).First())
            .Select(item => new KitaronSyncComponent(
                item.SourceKey, item.ParentPartNumber, item.ChildPartNumber,
                item.QuantityPerParent, item.SortOrder,
                Hash(item.SourceKey, item.ParentPartNumber, item.ChildPartNumber,
                    item.QuantityPerParent, item.SortOrder)))
            .OrderBy(item => item.SourceKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var parentParts = selectedComponents.Select(component => component.ParentPartNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The route master (OD-038) is the authoritative route for every synchronized Case that has
        // one; the planning view's open-work rows remain only a fallback for parts without a route.
        var routePlan = KitaronRoutePlanner.Plan(
            snapshot.RouteSteps ?? [],
            stations ?? new Dictionary<int, KitaronStationRecord>(),
            cases.Select(item => item.PartNumber).ToHashSet(StringComparer.OrdinalIgnoreCase),
            parentParts,
            warnings);
        var rawOperations = parsed.Where(row => row.OperationNumber > 0
                && !routePlan.PartsWithRoute.Contains(row.Part))
            .GroupBy(row => $"{row.Part}\u001f{row.OperationNumber}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                var operationNumber = first.OperationNumber!.Value;
                return new RawOperation(group.Key, first.Part, operationNumber,
                    group.Min(item => item.RoutePosition ?? operationNumber),
                    ChooseText(group.Select(item => item.OperationName)) ?? $"Operation {operationNumber}",
                    Consistent(group.Select(item => item.RequiredMachineType), group.Key, "Machine Type", warnings),
                    ChooseInt(group.Select(item => item.SetupSeconds)),
                    ChooseInt(group.Select(item => item.CycleSeconds)));
            }).ToArray();
        // Planning-view operations form a sequence in route order like the route master's.
        var viewOperations = new List<KitaronSyncOperation>();
        foreach (var group in rawOperations.GroupBy(item => item.CaseSourceKey, StringComparer.OrdinalIgnoreCase))
        {
            string? previousKey = null;
            var index = 0;
            foreach (var item in group.OrderBy(item => item.SourcePosition).ThenBy(item => item.OperationNumber))
            {
                viewOperations.Add(new KitaronSyncOperation(
                    item.SourceKey, item.CaseSourceKey, item.OperationNumber, index, item.Name,
                    item.RequiredMachineType, item.SetupSeconds, item.CycleSeconds,
                    Hash(item.SourceKey, index, item.Name, item.RequiredMachineType, item.SetupSeconds, item.CycleSeconds, previousKey),
                    previousKey));
                previousKey = item.SourceKey;
                index++;
            }
        }
        var operations = viewOperations
            .Concat(routePlan.Operations)
            .OrderBy(item => item.SourceKey, StringComparer.OrdinalIgnoreCase).ToArray();

        var materialRows = snapshot.MaterialRows ?? [];
        var materialOrders = materialRows.Select(row =>
            {
                var sourceKey = Text(row, Field("material_orders", "source_key"));
                var purchaseOrder = Text(row, Field("material_orders", "purchase_order_number"));
                var line = Text(row, Field("material_orders", "line_number"));
                var material = Text(row, Field("material_orders", "material_number"));
                var ordered = OptionalNumber(row, byTarget, "material_orders.ordered_quantity");
                if (sourceKey is null || purchaseOrder is null || line is null || material is null
                    || ordered is null || ordered <= 0 || !double.IsFinite(ordered.Value))
                {
                    AddWarning(warnings, "A Kitaron material purchase row with an invalid key, material, or quantity was skipped.");
                    return null;
                }
                var item = new KitaronSyncMaterialOrder(
                    sourceKey, purchaseOrder, line, material,
                    OptionalText(row, byTarget, "material_orders.description"),
                    OptionalText(row, byTarget, "material_orders.supplier"),
                    ordered.Value,
                    OptionalNumber(row, byTarget, "material_orders.received_quantity"),
                    OptionalText(row, byTarget, "material_orders.unit"),
                    OptionalDate(row, byTarget, "material_orders.requested_delivery_date"),
                    OptionalDate(row, byTarget, "material_orders.approved_delivery_date"),
                    OptionalNumber(row, byTarget, "material_orders.approved_quantity"),
                    OptionalText(row, byTarget, "material_orders.approval_note"),
                    OptionalText(row, byTarget, "material_orders.status"),
                    OptionalBoolean(row, byTarget, "material_orders.closed"),
                    "")
                {
                    UnitPrice = OptionalNumber(row, byTarget, "material_orders.unit_price"),
                    LineTotal = OptionalNumber(row, byTarget, "material_orders.line_total"),
                    CustomerOrderReference = OptionalText(row, byTarget, "material_orders.customer_order_reference")
                };
                return item with { SourceHash = Hash(
                    item.SourceKey, item.PurchaseOrderNumber, item.LineNumber, item.MaterialNumber,
                    item.Description, item.Supplier, item.OrderedQuantity, item.ReceivedQuantity,
                    item.Unit, item.RequestedDeliveryDate, item.ApprovedDeliveryDate,
                    item.ApprovedQuantity, item.ApprovalNote, item.Status, item.Closed,
                    item.UnitPrice, item.LineTotal, item.CustomerOrderReference) };
            })
            .Where(item => item is not null)
            .Cast<KitaronSyncMaterialOrder>()
            .GroupBy(item => item.SourceKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.SourceKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var batches = BuildBatches(
            snapshot.WorkOrders, snapshot.WorkOrderLinks, snapshot.WorkOrderMaterials,
            cases.Select(item => item.PartNumber).ToHashSet(StringComparer.OrdinalIgnoreCase),
            materialOrders, warnings, orders);

        return new KitaronSyncPlan(
            snapshot.WorkRows.Count + snapshot.Orders.Count + snapshot.Components.Count + materialRows.Count
                + (snapshot.RouteSteps?.Count ?? 0) + (snapshot.WorkOrders?.Count ?? 0),
            cases, orders, operations, components,
            snapshot.Components.Select(item => item.SourceKey).ToHashSet(StringComparer.Ordinal),
            warnings, mappingVersion, materialOrders,
            routePlan.Requirements, snapshot.Stations, routePlan.StepsSkipped, routePlan.PartsWithRoute,
            batches,
            snapshot.WorkOrders?.Select(item => new KitaronSyncWorkOrderSnapshot(
                item.Number, item.PartNumber, item.RawMaterialId, item.CustomerOrderNumber, item.Customer,
                Quantity(item.Amount) ?? Quantity(item.ProductionAmount),
                item.SupplyDate is null ? null : DateOnly.FromDateTime(item.SupplyDate.Value))).ToArray());
    }

    /// <summary>
    /// Builds the Production Batches from the open Kitaron work orders, oldest work order first.
    /// The net quantity fills the part's open Orders by earliest due date, each up to its open
    /// demand; the rest is stock, and the launched surplus above the net quantity is the cutting
    /// reserve and becomes the batch's scrap allowance. The material state mirrors Kitaron's own
    /// per-work-order material rows; Kitaron stays authoritative for stock.
    /// </summary>
    internal static IReadOnlyList<KitaronSyncBatch> BuildBatches(
        IReadOnlyList<KitaronSourceWorkOrder>? workOrders,
        IReadOnlyList<KitaronSourceWorkOrderLink>? links,
        IReadOnlyList<KitaronSourceWorkOrderMaterial>? materials,
        IReadOnlySet<string> partNumbers,
        IReadOnlyList<KitaronSyncMaterialOrder> materialOrders,
        ICollection<string> warnings,
        IReadOnlyList<KitaronSyncOrder>? orders = null,
        DateOnly? today = null)
    {
        if (workOrders is null || workOrders.Count == 0) return [];
        // Candidate material orders: every open purchase line of the raw material plus the lines
        // due or delivered within the last CandidateWindowDays - material bought for a work order
        // is often already received when the work order is planned.
        var candidateFrom = (today ?? DateOnly.FromDateTime(DateTime.Today)).AddDays(-CandidateWindowDays);
        var candidatesByMaterial = materialOrders
            .Where(item => (!item.Closed && item.OrderedQuantity > (item.ReceivedQuantity ?? 0))
                || (item.ApprovedDeliveryDate ?? item.RequestedDeliveryDate) >= candidateFrom)
            .ToLookup(item => item.MaterialNumber, StringComparer.OrdinalIgnoreCase);
        _ = links; // Kept in the snapshot for diagnostics; see the allocation note below.
        var activeOrders = (orders ?? [])
            .Where(item => item.Status == "active" && item.Quantity > 0)
            .OrderBy(item => item.WorkFinishDate)
            .ThenBy(item => item.SourceKey.Length)
            .ThenBy(item => item.SourceKey, StringComparer.Ordinal)
            .ToArray();
        var ordersByPart = activeOrders.ToLookup(item => item.CaseSourceKey, StringComparer.OrdinalIgnoreCase);
        var openDemand = activeOrders
            .GroupBy(item => item.SourceKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Quantity, StringComparer.Ordinal);
        var materialsByNumber = (materials ?? []).ToLookup(item => item.WorkOrderNumber);
        var purchasesByMaterial = materialOrders
            .Where(item => !item.Closed)
            .ToLookup(item => item.MaterialNumber, StringComparer.OrdinalIgnoreCase);
        var result = new List<KitaronSyncBatch>();
        foreach (var workOrder in workOrders.OrderBy(item => item.Number))
        {
            if (!partNumbers.Contains(workOrder.PartNumber))
            {
                AddWarning(warnings,
                    $"Kitaron work order {workOrder.Number} was skipped: part {workOrder.PartNumber} is not synchronized.");
                continue;
            }
            var planned = Quantity(workOrder.Amount) ?? Quantity(workOrder.ProductionAmount);
            if (planned is not > 0)
            {
                AddWarning(warnings,
                    $"Kitaron work order {workOrder.Number} was skipped: its quantity is missing or not a whole number.");
                continue;
            }
            // Kitaron links a work order to one order line even when it produces for several
            // (TOrderLinkRoot holds the whole quantity on that line). The net quantity is therefore
            // spread over the part's open Orders, earliest due date first, each up to its open
            // demand that earlier work orders have not taken; what no Order needs is stock. The
            // launched quantity above the net quantity is the cutting reserve (scrap allowance).
            var net = Math.Min(Quantity(workOrder.ProductionAmount) ?? planned.Value, planned.Value);
            var allocations = new List<KitaronSyncBatchAllocation>();
            var remaining = net;
            foreach (var order in ordersByPart[workOrder.PartNumber])
            {
                if (remaining == 0) break;
                var open = openDemand[order.SourceKey];
                if (open <= 0) continue;
                var quantity = Math.Min(open, remaining);
                allocations.Add(new KitaronSyncBatchAllocation(order.SourceKey, quantity));
                openDemand[order.SourceKey] = open - quantity;
                remaining -= quantity;
            }
            if (remaining > 0)
                allocations.Add(new KitaronSyncBatchAllocation(null, remaining));
            if (planned.Value > net)
                allocations.Add(new KitaronSyncBatchAllocation(null, planned.Value - net, ScrapAllowance: true));

            var (materialState, materialDetail) = MaterialCheck(
                materialsByNumber[workOrder.Number].ToArray(), purchasesByMaterial);
            // The work order's raw material ties it to the open Kitaron purchase lines of that
            // material; they are the batch's material orders. When Kitaron keeps no per-work-order
            // material calculation, an open purchase line makes the material "on order".
            var assignedMaterialOrders = workOrder.RawMaterialId is null
                ? []
                : candidatesByMaterial[workOrder.RawMaterialId]
                    // Still-open lines first by due date, then received lines newest first.
                    .OrderBy(item => item.Closed || item.OrderedQuantity <= (item.ReceivedQuantity ?? 0) ? 1 : 0)
                    .ThenBy(item => item.Closed || item.OrderedQuantity <= (item.ReceivedQuantity ?? 0)
                        ? -(item.ApprovedDeliveryDate ?? item.RequestedDeliveryDate ?? DateOnly.MinValue).DayNumber
                        : (item.ApprovedDeliveryDate ?? item.RequestedDeliveryDate ?? DateOnly.MaxValue).DayNumber)
                    .ThenBy(item => item.PurchaseOrderNumber, StringComparer.Ordinal)
                    .ThenBy(item => item.LineNumber, StringComparer.Ordinal)
                    .Take(MaximumCandidates)
                    .ToArray();
            var materialOrdersText = assignedMaterialOrders.Length == 0
                ? null
                : string.Join(", ", assignedMaterialOrders.Take(5).Select(item =>
                    $"{item.PurchaseOrderNumber}/{item.LineNumber}"
                    + ((item.ApprovedDeliveryDate ?? item.RequestedDeliveryDate) is DateOnly due ? $" due {due:yyyy-MM-dd}" : "")));
            // Kitaron records no purchase-to-work-order link, so these purchase lines are only
            // candidates for a planner to verify on the Work Order; they do not set the state.
            if (materialState == "unknown" && workOrder.RawMaterialId is not null)
            {
                materialDetail = assignedMaterialOrders.Length > 0
                    ? $"{assignedMaterialOrders.Length} open purchase line(s) for raw material {workOrder.RawMaterialId} ({materialOrdersText}); verify the right ones on the Work Order."
                    : $"No purchase line for raw material {workOrder.RawMaterialId} is open or was due in the last {CandidateWindowDays} days; Kitaron has no stock calculation for this work order.";
            }
            var sourceKey = $"wo:{workOrder.Number.ToString(CultureInfo.InvariantCulture)}";
            var batchNumber = workOrder.Number.ToString(CultureInfo.InvariantCulture);
            result.Add(new KitaronSyncBatch(
                sourceKey, workOrder.PartNumber, batchNumber, planned.Value, allocations,
                materialState, materialDetail,
                Hash(sourceKey, batchNumber, planned.Value,
                    allocations.Select(item => $"{item.OrderSourceKey}\u001f{item.Quantity}\u001f{item.ScrapAllowance}").ToArray()))
            {
                MaterialOrderKeys = assignedMaterialOrders.Select(item => item.SourceKey).ToArray(),
                MaterialOrdersText = materialOrdersText
            });
        }
        return result;
    }

    /// <summary>Received purchase lines stay candidates this many days after their due date.</summary>
    internal const int CandidateWindowDays = 180;

    private const int MaximumCandidates = 20;

    private static (string State, string? Detail) MaterialCheck(
        IReadOnlyList<KitaronSourceWorkOrderMaterial> lines,
        ILookup<string, KitaronSyncMaterialOrder> purchasesByMaterial)
    {
        if (lines.Count == 0)
            return ("unknown", "Kitaron has no material calculation for this work order.");
        var shortLines = new List<string>();
        var allShortOnPurchase = true;
        foreach (var line in lines)
        {
            var issued = line.IssuedAmount ?? 0;
            var covered = issued >= line.RequiredAmount
                || (line.RunningBalance is double balance
                    ? balance >= 0
                    : line.StockAmount is double stock && stock >= line.RequiredAmount);
            if (covered) continue;
            var purchases = purchasesByMaterial[line.MaterialPartNumber]
                .Where(item => item.OrderedQuantity > (item.ReceivedQuantity ?? 0))
                .ToArray();
            var onPurchase = (line.OnPurchaseAmount is > 0) || purchases.Length > 0;
            allShortOnPurchase &= onPurchase;
            var due = purchases
                .Select(item => item.ApprovedDeliveryDate ?? item.RequestedDeliveryDate)
                .Where(item => item is not null)
                .OrderBy(item => item)
                .FirstOrDefault();
            var text = $"{line.MaterialPartNumber}: need {Amount(line.RequiredAmount)}"
                + (line.StockAmount is double stockAmount ? $", stock {Amount(stockAmount)}" : "")
                + (onPurchase
                    ? ", on purchase order" + (due is null ? "" : $" due {due:yyyy-MM-dd}")
                    : ", no open purchase order");
            if (shortLines.Count < 3) shortLines.Add(text);
        }
        if (shortLines.Count == 0)
            return ("available", $"Kitaron stock covers {lines.Count.ToString(CultureInfo.InvariantCulture)} material line(s).");
        return (allShortOnPurchase ? "on_order" : "missing", string.Join("; ", shortLines));
    }

    private static string Amount(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static int? Quantity(double? value) =>
        value is > 0 and <= int.MaxValue && double.IsFinite(value.Value)
            && Math.Truncate(value.Value) == value.Value
            ? (int)value.Value
            : null;

    private static string? OptionalText(KitaronSourceRow row, IReadOnlyDictionary<string, KitaronMappingField> fields,
        string key, bool manualLookupAsNull = false) =>
        fields.TryGetValue(key, out var field) && !manualLookupAsNull ? Text(row, field) : null;

    private static int? OptionalInt(KitaronSourceRow row, IReadOnlyDictionary<string, KitaronMappingField> fields, string key) =>
        fields.TryGetValue(key, out var field) ? Integer(Value(row, field)) : null;

    private static double? OptionalNumber(KitaronSourceRow row, IReadOnlyDictionary<string, KitaronMappingField> fields, string key)
    {
        if (!fields.TryGetValue(key, out var field)) return null;
        var number = Decimal(Value(row, field));
        return number is null ? null : (double)number.Value;
    }

    private static bool OptionalBoolean(KitaronSourceRow row, IReadOnlyDictionary<string, KitaronMappingField> fields, string key)
    {
        if (!fields.TryGetValue(key, out var field)) return false;
        var value = Value(row, field);
        if (value is bool result) return result;
        var number = Decimal(value);
        return number is not null && number != 0;
    }

    private static DateOnly? OptionalDate(KitaronSourceRow row, IReadOnlyDictionary<string, KitaronMappingField> fields, string key) =>
        fields.TryGetValue(key, out var field) ? Date(Value(row, field)) : null;

    private static int? OptionalSeconds(KitaronSourceRow row, IReadOnlyDictionary<string, KitaronMappingField> fields, string key)
    {
        if (!fields.TryGetValue(key, out var field)) return null;
        var value = Decimal(Value(row, field));
        if (value is null) return null;
        var multiplier = field.Transform switch
        {
            "seconds" or "positive_int" or "positive_integer" => 1m,
            "minutes_to_seconds" => 60m,
            "hours_to_seconds" => 3600m,
            _ => throw new KitaronSyncBlockedException($"Transform {field.Transform} is not executable for {key}.")
        };
        var result = decimal.Round(value.Value * multiplier, 0, MidpointRounding.AwayFromZero);
        return result is >= 0 and <= int.MaxValue ? (int)result : null;
    }

    private static object? Value(KitaronSourceRow row, KitaronMappingField field) =>
        field.SourceColumn is not null && row.Values.TryGetValue(field.SourceColumn, out var value) ? value : null;

    private static string? RawText(KitaronSourceRow row, string column)
    {
        if (!row.Values.TryGetValue(column, out var value)) return null;
        return KitaronTextNormalization.Clean(Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static string? Text(KitaronSourceRow row, KitaronMappingField field) =>
        KitaronTextNormalization.Clean(Convert.ToString(Value(row, field), CultureInfo.InvariantCulture));

    private static decimal? Decimal(object? value)
    {
        if (value is null) return null;
        try { return Convert.ToDecimal(value, CultureInfo.InvariantCulture); }
        catch { return decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : null; }
    }

    private static int? Integer(object? value)
    {
        var number = Decimal(value);
        return number is >= 0 and <= int.MaxValue && decimal.Truncate(number.Value) == number.Value ? (int)number.Value : null;
    }

    private static DateOnly? Date(object? value)
    {
        if (value is DateTime dateTime) return DateOnly.FromDateTime(dateTime);
        if (value is DateTimeOffset offset) return DateOnly.FromDateTime(offset.DateTime);
        if (value is DateOnly date) return date;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed) ? parsed : null;
    }

    internal static IReadOnlyList<KitaronSyncOrder> BuildOrders(
        IEnumerable<KitaronSourceOrder> source,
        ICollection<string> warnings)
    {
        return source.Where(row => !IsIgnoredOrderNumber(row.OrderNumber))
            .GroupBy(row => row.SourceKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var valid = group.Where(row => row.Quantity is > 0
                    && row.WorkFinishDate is not null
                    && double.IsFinite(row.Quantity.Value)
                    && row.Quantity.Value <= int.MaxValue
                    && Math.Truncate(row.Quantity.Value) == row.Quantity.Value).ToArray();
                if (valid.Length == 0)
                {
                    AddWarning(warnings,
                        $"Order {group.First().OrderNumber}, row {group.Key}, was skipped because quantity or finish date is invalid.");
                    return null;
                }

                var first = valid[0];
                var quantity = (int)valid.Max(EffectiveDemandQuantity);
                var date = DateOnly.FromDateTime(valid.Min(row => row.WorkFinishDate!.Value));
                var status = valid.Any(row => row.StopProduction)
                    ? "cancelled"
                    : valid.Any(row => row.IsClosed) ? "complete" : "active";
                var reference = $"{first.OrderNumber.Trim()}/{group.Key.Trim()}";
                return new KitaronSyncOrder(group.Key, first.PartNumber, reference, quantity, date, status,
                    Hash(group.Key, reference, quantity, date, status, first.Price))
                {
                    CanonicalOrderNumber = first.OrderNumber.Trim(),
                    Price = first.Price
                };
            })
            .Where(item => item is not null)
            .Cast<KitaronSyncOrder>()
            .ToArray();
    }

    // Meimad Order quantity represents outstanding demand, not the original order size: when
    // Kitaron reports a partial delivery (Supplied) against the row, only the un-supplied
    // remainder is real remaining demand for production planning. Falls back to the full ordered
    // quantity whenever Supplied is unavailable or the row is already fully (or over-)supplied,
    // since a zero/negative remainder is not a valid stored Order quantity.
    private static double EffectiveDemandQuantity(KitaronSourceOrder row)
    {
        if (row.Supplied is double supplied)
        {
            var unsupplied = row.Quantity!.Value - supplied;
            if (unsupplied >= 1)
            {
                return unsupplied;
            }
        }
        return row.Quantity!.Value;
    }

    internal static bool IsIgnoredOrderNumber(string? orderNumber) =>
        string.Equals(orderNumber?.Trim(), "הזמנה לדוגמא 1", StringComparison.OrdinalIgnoreCase);

    private static string OrderFact(KitaronSyncOrder order) => OrderFact(
        order.CaseSourceKey, order.CanonicalOrderNumber, order.Quantity, order.WorkFinishDate, order.Status);

    private static string OrderFact(
        string part, string orderNumber, int quantity, DateOnly date, string status) =>
        $"{part.Trim()}\u001f{orderNumber.Trim()}\u001f{quantity}\u001f{date:yyyy-MM-dd}\u001f{status}";

    internal static string OrderRowIdentity(string part, string sourceRecordId) =>
        $"{part.Trim()}\u001f{sourceRecordId.Trim()}";

    private static string? Consistent(IEnumerable<string?> values, string key, string field, ICollection<string> warnings)
    {
        var distinct = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinct.Length > 1) AddWarning(warnings, $"{key} has multiple {field} values; a deterministic most-frequent value was retained.");
        return ChooseText(values);
    }

    private static string? ChooseText(IEnumerable<string?> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim())
        .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.Key)
        .FirstOrDefault();

    private static int? ChooseInt(IEnumerable<int?> values) => values
        .Where(value => value.HasValue)
        .Select(value => value!.Value)
        .GroupBy(value => value)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key)
        .Select(group => (int?)group.Key)
        .FirstOrDefault();

    private static void AddWarning(ICollection<string> warnings, string message)
    {
        if (warnings.Count < 500) warnings.Add(message);
    }

    private static string SafeFolder(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe[..Math.Min(safe.Length, 120)];
    }

    private static string Hash(params object?[] values) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)))).ToLowerInvariant();

    private static string SafeMessage(Exception exception)
    {
        var text = exception is KitaronSyncDataException ? exception.Message : "Kitaron synchronization failed. Review the Server log.";
        return text.Length <= 2000 ? text : text[..2000];
    }

    private sealed record ParsedRow(string? SourceRecordId, string Part, string Name, string? Revision, string? Customer,
        string? OrderNumber, int? Quantity, DateOnly? WorkFinishDate, int? OperationNumber,
        int? RoutePosition, string? OperationName, string? RequiredMachineType, int? SetupSeconds, int? CycleSeconds);

    private sealed record CaseCandidate(string Part, string Name, string? Revision, string? Customer);

    private sealed record RawOperation(string SourceKey, string CaseSourceKey, int OperationNumber,
        int SourcePosition, string Name, string? RequiredMachineType, int? SetupSeconds, int? CycleSeconds);
}

internal sealed class KitaronSyncHostedService(
    IKitaronConnectionRepository connectionRepository,
    KitaronMappingService mappingService,
    KitaronSyncService syncService,
    TimeProvider timeProvider,
    ILogger<KitaronSyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Read before the checks, so a request that arrives during this pass wakes the next one.
            var runRequested = syncService.RunRequested;
            try
            {
                var connection = await connectionRepository.GetAsync(stoppingToken);
                var mapping = await mappingService.GetAsync(stoppingToken);
                var status = await syncService.GetStatusAsync(stoppingToken);
                var ready = connection.Enabled && mapping.Status == "ready_for_implementation";
                var due = status.LastCompletedAt is null
                    || timeProvider.GetUtcNow() - status.LastCompletedAt >= TimeSpan.FromSeconds(connection.RefreshIntervalSeconds);
                // A station decision asks for a run now; the request waits while the connector is off.
                var requested = ready && syncService.TakeRunRequest();
                if (ready && (due || requested))
                    await syncService.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (KitaronSyncBlockedException)
            {
                // A synchronization started from the Server page is running; run again after it.
                syncService.RequestRun(wake: false);
            }
            catch (Exception exception) { logger.LogError(exception, "Periodic Kitaron synchronization failed."); }
            using var pause = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(30), timeProvider, pause.Token), runRequested);
            pause.Cancel();
        }
    }
}
