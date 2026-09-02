using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.LegacyImport;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Domain.Cases;
using Meimad.Planner.Server.Domain.LegacyImport;
using Meimad.Planner.Server.Domain.Orders;
using Meimad.Planner.Server.Domain.ProductionBatches;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteLegacyImportRepository : ILegacyImportRepository
{
    private readonly SqliteDatabase database;
    private readonly TimeProvider timeProvider;

    public SqliteLegacyImportRepository(SqliteDatabase database, TimeProvider timeProvider)
    {
        this.database = database;
        this.timeProvider = timeProvider;
    }

    public async Task<LegacyImportCandidatePool> ReadCandidatePoolAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return new LegacyImportCandidatePool(
            await ReadCasesAsync(connection, cancellationToken),
            await ReadOrdersAsync(connection, cancellationToken),
            await ReadBatchesAsync(connection, cancellationToken),
            await ReadCaseOperationsAsync(connection, cancellationToken),
            await ReadBatchOperationsAsync(connection, cancellationToken),
            await ReadMachinesAsync(connection, cancellationToken));
    }

    public async Task<LegacyImportCommitResponse?> TryReplayAsync(
        string workbookSha256,
        string requestSha256,
        bool allowAdditionalCaseOrderReceipt,
        EditAuthority editAuthority,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(
            connection,
            transaction,
            editAuthority,
            now,
            cancellationToken);
        var existing = await ReadReceiptAsync(
            connection,
            transaction,
            workbookSha256,
            requestSha256,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToReplayResponse(existing.ResponseJson);
        }
        if (allowAdditionalCaseOrderReceipt
            || !await HasWorkbookReceiptAsync(connection, transaction, workbookSha256, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        throw new LegacyWorkbookAlreadyImportedException(workbookSha256);
    }

    public async Task<LegacyImportCommitResponse> CommitAsync(
        LegacyImportCommitRequest request,
        LegacyImportPreviewResponse approvedPreview,
        string requestSha256,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var now = timeProvider.GetUtcNow();
        var confirmedByUserId = await EnsureEditAuthorityAsync(
            connection,
            transaction,
            editAuthority,
            now,
            cancellationToken);
        var existing = await ReadReceiptAsync(
            connection,
            transaction,
            request.WorkbookSha256!,
            requestSha256,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToReplayResponse(existing.ResponseJson);
        }
        if (!IsCaseOrderOnlyPass(request)
            && await HasWorkbookReceiptAsync(
                connection,
                transaction,
                request.WorkbookSha256!,
                cancellationToken))
        {
            throw new LegacyWorkbookAlreadyImportedException(request.WorkbookSha256!);
        }

        var issues = new List<LegacyImportIssue>();
        var createdCaseIds = new List<string>();
        var createdOrderIds = new List<string>();
        var createdBatchIds = new List<string>();
        var createdBatchOperationIds = new List<string>();
        var createdAssignmentIds = new List<string>();
        var poolBatchOperationIds = new List<string>();
        var caseIdsBySourceRow = new Dictionary<string, string>(StringComparer.Ordinal);
        var orderIdsBySourceRow = new Dictionary<string, string>(StringComparer.Ordinal);

        var openRows = approvedPreview.OpenOrderRows.ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        var selectedOpenOrders = request.OpenOrderSelections!
            .Where(selection => selection.Action != "skip")
            .OrderBy(selection => openRows[selection.RowKey!].SourceOrder)
            .ToArray();
        foreach (var selection in selectedOpenOrders.Where(selection => selection.Action == "create_case"))
        {
            var source = openRows[selection.RowKey!];
            var caseId = await CreateCaseAsync(
                connection,
                transaction,
                selection.NewCase!,
                now,
                source,
                issues,
                cancellationToken);
            if (caseId is not null)
            {
                caseIdsBySourceRow[source.RowKey] = caseId;
                createdCaseIds.Add(caseId);
                if (selection.Order is not null)
                {
                    var orderId = await CreateOrderAsync(
                        connection,
                        transaction,
                        caseId,
                        selection.Order,
                        now,
                        source,
                        issues,
                        cancellationToken);
                    if (orderId is not null)
                    {
                        orderIdsBySourceRow[source.RowKey] = orderId;
                        createdOrderIds.Add(orderId);
                    }
                }
            }
        }

        foreach (var selection in selectedOpenOrders.Where(selection => selection.Action == "create_order"))
        {
            var source = openRows[selection.RowKey!];
            var caseId = selection.ExistingCaseId;
            if (string.IsNullOrWhiteSpace(caseId)
                && !string.IsNullOrWhiteSpace(selection.CaseSourceRowKey))
            {
                caseIdsBySourceRow.TryGetValue(selection.CaseSourceRowKey, out caseId);
            }

            if (string.IsNullOrWhiteSpace(caseId))
            {
                issues.Add(new LegacyImportIssue(
                    LegacyImportIssueSeverity.Blocking,
                    "case_source_not_created",
                    "The selected source Case was not created, so this Order cannot be imported.",
                    source.SheetName,
                    source.RowNumber,
                    "caseSourceRowKey"));
                continue;
            }

            var orderId = await CreateOrderAsync(
                connection,
                transaction,
                caseId,
                selection.Order!,
                now,
                source,
                issues,
                cancellationToken);
            if (orderId is not null)
            {
                orderIdsBySourceRow[source.RowKey] = orderId;
                createdOrderIds.Add(orderId);
            }
        }

        ThrowIfIssues(issues);
        var planningRows = approvedPreview.Rows.ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        var explicitMachineMap = request.MachineMappings!
            .ToDictionary(mapping => mapping.SectionKey!, mapping => mapping.MachineId!, StringComparer.Ordinal);
        var selectedPlanning = request.PlanningSelections!
            .Where(selection => selection.Action != "skip")
            .Select(selection => new
            {
                Selection = selection,
                Source = planningRows[selection.RowKey!],
                MachineId = selection.Action == "create_batch_to_pool"
                    ? null
                    : string.IsNullOrWhiteSpace(selection.MachineId)
                    ? explicitMachineMap[planningRows[selection.RowKey!].SectionKey]
                    : selection.MachineId!
            })
            .OrderBy(item => item.Source.SheetName, StringComparer.Ordinal)
            .ThenBy(item => item.Source.RowNumber)
            .ToArray();
        var importedBacklogs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var item in selectedPlanning)
        {
            var batchOperationId = item.Selection.BatchOperationId;
            if (item.Selection.Action is "create_batch_and_assign" or "create_batch_to_pool")
            {
                var caseId = ResolveExclusiveReference(
                    item.Selection.CaseId,
                    item.Selection.CaseSourceRowKey,
                    caseIdsBySourceRow,
                    "caseId",
                    item.Source,
                    issues);
                if (caseId is null)
                {
                    continue;
                }

                var created = await CreateBatchAsync(
                    connection,
                    transaction,
                    caseId,
                    item.Selection,
                    item.Source,
                    orderIdsBySourceRow,
                    now,
                    issues,
                    cancellationToken);
                if (created is null)
                {
                    continue;
                }

                createdBatchIds.Add(created.BatchId);
                createdBatchOperationIds.AddRange(created.BatchOperationIds);
                poolBatchOperationIds.AddRange(created.BatchOperationIds);
                batchOperationId = created.SelectedBatchOperationId;
            }

            if (item.Selection.Action == "create_batch_to_pool")
            {
                continue;
            }

            var assignmentId = await AppendAssignmentAsync(
                connection,
                transaction,
                batchOperationId!,
                item.MachineId!,
                item.Selection.CompatibilityOverride,
                editAuthority,
                confirmedByUserId,
                now,
                item.Source,
                issues,
                cancellationToken);
            if (assignmentId is not null)
            {
                createdAssignmentIds.Add(assignmentId);
                poolBatchOperationIds.Remove(batchOperationId!);
                if (!importedBacklogs.TryGetValue(item.MachineId!, out var backlog))
                {
                    backlog = [];
                    importedBacklogs[item.MachineId!] = backlog;
                }
                backlog.Add(assignmentId);
            }
        }

        ThrowIfIssues(issues);
        var commitId = Guid.NewGuid().ToString("N");
        var response = new LegacyImportCommitResponse(
            1,
            request.WorkbookSha256!,
            commitId,
            false,
            new LegacyImportEntityIdsResponse(
                createdCaseIds,
                createdOrderIds,
                createdBatchIds,
                createdAssignmentIds,
                createdBatchOperationIds),
            new LegacyImportEntityIdsResponse([], [], [], [], []),
            importedBacklogs.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new LegacyImportedMachineBacklogResponse(entry.Key, entry.Value))
                .ToArray(),
            poolBatchOperationIds);
        await InsertReceiptAsync(
            connection,
            transaction,
            response,
            requestSha256,
            editAuthority.ClientId,
            confirmedByUserId,
            now,
            cancellationToken);
        await SqliteStructuredEventLogRepository.AppendAsync(
            connection,
            transaction,
            new StructuredEventWrite(
                "legacy_working_plan_import_committed",
                now,
                confirmedByUserId,
                new Dictionary<string, string>
                {
                    ["commitId"] = commitId,
                    ["workbookSha256"] = request.WorkbookSha256!
                },
                AfterData: new
                {
                    approvedRequestSha256 = requestSha256,
                    caseCount = createdCaseIds.Count,
                    orderCount = createdOrderIds.Count,
                    batchCount = createdBatchIds.Count,
                    batchOperationCount = createdBatchOperationIds.Count,
                    poolBatchOperationCount = poolBatchOperationIds.Count,
                    assignmentCount = createdAssignmentIds.Count
                },
                EventKey: $"legacy-working-plan-import:{commitId}"),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    private static async Task<IReadOnlyList<LegacyImportCaseCandidate>> ReadCasesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, part_number, name, revision, customer FROM cases ORDER BY part_number COLLATE NOCASE, id LIMIT 20000;";
        var result = new List<LegacyImportCaseCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LegacyImportCaseCandidate(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                GetNullableString(reader, 3), GetNullableString(reader, 4)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<LegacyImportOrderCandidate>> ReadOrdersAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, case_id, order_reference, quantity, work_finish_date FROM orders ORDER BY order_reference COLLATE NOCASE, id LIMIT 30000;";
        var result = new List<LegacyImportOrderCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LegacyImportOrderCandidate(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<LegacyImportBatchCandidate>> ReadBatchesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, case_id, batch_number, planned_quantity FROM production_batches ORDER BY batch_number COLLATE NOCASE, id LIMIT 20000;";
        var result = new List<LegacyImportBatchCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LegacyImportBatchCandidate(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<LegacyImportCaseOperationCandidate>> ReadCaseOperationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, case_id, operation_number, name, required_machine_type,
                   setup_seconds, cycle_seconds, version
            FROM case_operations
            ORDER BY case_id, route_position, id
            LIMIT 50000;
            """;
        var result = new List<LegacyImportCaseOperationCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LegacyImportCaseOperationCandidate(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                GetNullableString(reader, 4), GetNullableInt32(reader, 5), GetNullableInt32(reader, 6), reader.GetInt32(7)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<LegacyImportBatchOperationCandidate>> ReadBatchOperationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT batch_operations.id, batch_operations.production_batch_id,
                   production_batches.batch_number, production_batches.case_id, cases.part_number,
                   batch_operations.source_case_operation_id, batch_operations.operation_number,
                   batch_operations.name, batch_operations.status,
                   batch_operations.required_machine_type, batch_operations.version,
                   machine_assignments.id, machine_assignments.machine_id, machine_assignments.version
            FROM batch_operations
            INNER JOIN production_batches ON production_batches.id = batch_operations.production_batch_id
            INNER JOIN cases ON cases.id = production_batches.case_id
            LEFT JOIN machine_assignments ON machine_assignments.batch_operation_id = batch_operations.id
            ORDER BY batch_operations.production_batch_id, batch_operations.route_position, batch_operations.id
            LIMIT 50000;
            """;
        var result = new List<LegacyImportBatchOperationCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LegacyImportBatchOperationCandidate(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetInt32(6), reader.GetString(7), reader.GetString(8),
                GetNullableString(reader, 9), reader.GetInt32(10),
                GetNullableString(reader, 11), GetNullableString(reader, 12), GetNullableInt32(reader, 13)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<LegacyImportMachineCandidate>> ReadMachinesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT machines.id, machines.number, machines.name, machines.axis_type,
                   machines.machine_type, machines.capabilities_json,
                   COALESCE(machine_types.capabilities_json, '[]'), machines.is_active
            FROM machines
            LEFT JOIN machine_types ON machine_types.id = machines.machine_type_id
            ORDER BY machines.number COLLATE NOCASE, machines.id
            LIMIT 1000;
            """;
        var result = new List<LegacyImportMachineCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LegacyImportMachineCandidate(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), GetNullableString(reader, 3),
                reader.GetString(4),
                JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [],
                JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [],
                reader.GetBoolean(7)));
        }
        return result;
    }

    private static async Task<string?> CreateCaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LegacyNewCaseRequest request,
        DateTimeOffset now,
        LegacyOpenOrderRowResponse source,
        List<LegacyImportIssue> issues,
        CancellationToken cancellationToken)
    {
        ValidatedCaseValues values;
        try
        {
            values = CaseValidator.ValidateAndNormalize(new CaseValues(
                request.PartNumber, request.Name, request.Revision, request.Customer,
                request.CustomerReference, null, request.WorkingFolderPath,
                null, null, null, null, request.Notes));
        }
        catch (CaseValidationException exception)
        {
            issues.AddRange(exception.Issues.Select(issue => SourceIssue(issue.Code, issue.Message, source, $"newCase.{issue.Field}")));
            return null;
        }

        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = "SELECT id FROM cases WHERE upper(trim(part_number)) = upper(trim($partNumber)) LIMIT 1;";
            duplicate.Parameters.AddWithValue("$partNumber", values.PartNumber);
            if (await duplicate.ExecuteScalarAsync(cancellationToken) is string existingId)
            {
                issues.Add(SourceIssue(
                    "case_already_exists",
                    $"Case '{existingId}' already has Part Number '{values.PartNumber}'; select the existing Case instead.",
                    source,
                    "newCase.partNumber"));
                return null;
            }
        }

        var caseId = Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO cases (
                id, part_number, name, revision, customer, customer_reference,
                preview_reference, working_folder_path, material_type, material_specification,
                raw_material_form, raw_material_dimensions, current_setup_seconds,
                current_cycle_seconds, notes, version, created_at, updated_at)
            VALUES (
                $id, $partNumber, $name, $revision, $customer, $customerReference,
                NULL, $workingFolderPath, NULL, NULL, NULL, NULL, NULL, NULL,
                $notes, 1, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", caseId);
        command.Parameters.AddWithValue("$partNumber", values.PartNumber);
        command.Parameters.AddWithValue("$name", values.Name);
        command.Parameters.AddWithValue("$revision", Db(values.Revision));
        command.Parameters.AddWithValue("$customer", Db(values.Customer));
        command.Parameters.AddWithValue("$customerReference", Db(values.CustomerReference));
        command.Parameters.AddWithValue("$workingFolderPath", values.WorkingFolderPath);
        command.Parameters.AddWithValue("$notes", Db(values.Notes));
        command.Parameters.AddWithValue("$now", FormatInstant(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return caseId;
    }

    private static async Task<string?> CreateOrderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        LegacyNewOrderRequest request,
        DateTimeOffset now,
        LegacyOpenOrderRowResponse source,
        List<LegacyImportIssue> issues,
        CancellationToken cancellationToken)
    {
        ValidatedOrderValues values;
        try
        {
            values = OrderValidator.ValidateAndNormalize(new OrderValues(
                caseId, request.OrderNumber, request.Quantity ?? 0, request.WorkFinishDate, "active", request.Notes));
        }
        catch (OrderValidationException exception)
        {
            issues.AddRange(exception.Issues.Select(issue => SourceIssue(issue.Code, issue.Message, source, $"order.{issue.Field}")));
            return null;
        }

        if (!await ExistsAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM cases WHERE id = $value);", caseId, cancellationToken))
        {
            issues.Add(SourceIssue("case_not_found", $"Case '{caseId}' was not found.", source, "existingCaseId"));
            return null;
        }
        if (await ExistsAsync(
                connection,
                transaction,
                "SELECT EXISTS(SELECT 1 FROM kitaron_sync_links WHERE source_entity = 'case' AND target_id = $value);",
                caseId,
                cancellationToken))
        {
            issues.Add(SourceIssue(
                "kitaron_managed_read_only",
                $"Case '{caseId}' is managed by Kitaron; legacy Excel import cannot add an Order to its authoritative demand set.",
                source,
                "existingCaseId"));
            return null;
        }
        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = "SELECT id FROM orders WHERE case_id = $caseId AND upper(trim(order_reference)) = upper(trim($number)) LIMIT 1;";
            duplicate.Parameters.AddWithValue("$caseId", caseId);
            duplicate.Parameters.AddWithValue("$number", values.OrderNumber);
            if (await duplicate.ExecuteScalarAsync(cancellationToken) is string existingId)
            {
                issues.Add(SourceIssue("order_already_exists", $"Order '{existingId}' already has this Case and Order Number.", source, "order.orderNumber"));
                return null;
            }
        }

        var orderId = Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO orders (
                id, case_id, order_reference, customer_reference, quantity,
                work_finish_date, status, notes, version, created_at, updated_at)
            VALUES ($id, $caseId, $number, NULL, $quantity, $date, 'active', $notes, 1, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", orderId);
        command.Parameters.AddWithValue("$caseId", caseId);
        command.Parameters.AddWithValue("$number", values.OrderNumber);
        command.Parameters.AddWithValue("$quantity", values.Quantity);
        command.Parameters.AddWithValue("$date", values.WorkFinishDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$notes", Db(values.Notes));
        command.Parameters.AddWithValue("$now", FormatInstant(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return orderId;
    }

    private static async Task<CreatedBatch?> CreateBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        LegacyPlanningSelectionRequest selection,
        LegacyPlanningRowResponse source,
        IReadOnlyDictionary<string, string> orderIdsBySourceRow,
        DateTimeOffset now,
        List<LegacyImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var allocations = new List<ResolvedAllocation>();
        foreach (var allocation in selection.Allocations!)
        {
            var isOrder = allocation.Type == "order";
            string? orderId = null;
            if (isOrder)
            {
                orderId = ResolveExclusiveReference(
                    allocation.OrderId,
                    allocation.OrderSourceRowKey,
                    orderIdsBySourceRow,
                    "allocations.orderId",
                    source,
                    issues);
            }
            else if (!string.IsNullOrWhiteSpace(allocation.OrderId)
                     || !string.IsNullOrWhiteSpace(allocation.OrderSourceRowKey))
            {
                issues.Add(SourceIssue("allocation_order_forbidden", "Only an order allocation may contain an Order reference.", source, "allocations.orderId"));
            }
            if (allocation.Quantity is null)
            {
                issues.Add(SourceIssue("allocation_invalid", "Each allocation requires an explicit quantity.", source, "allocations"));
                continue;
            }
            allocations.Add(new ResolvedAllocation(allocation.Type ?? string.Empty, orderId, allocation.Quantity.Value));
        }
        ValidatedProductionBatchValues validatedBatch;
        try
        {
            validatedBatch = ProductionBatchValidator.ValidateAndNormalize(new ProductionBatchValues(
                caseId,
                selection.BatchNumber,
                ProductionBatchValidator.WaitingStatus,
                source.Values.Quantity!.Value,
                allocations.Select(allocation => new BatchAllocationValue(
                    allocation.Type,
                    allocation.OrderId,
                    allocation.Quantity)).ToArray()));
        }
        catch (ProductionBatchValidationException exception)
        {
            issues.AddRange(exception.Issues.Select(issue => SourceIssue(issue.Code, issue.Message, source, issue.Field)));
            return null;
        }
        foreach (var allocation in validatedBatch.Allocations.Where(allocation => allocation.AllocationType == BatchAllocationType.Order))
        {
            await using var order = connection.CreateCommand();
            order.Transaction = transaction;
            order.CommandText = """
                SELECT orders.case_id,
                       orders.status,
                       NOT EXISTS (
                           SELECT 1
                           FROM kitaron_sync_links case_link
                           WHERE case_link.source_entity = 'case'
                             AND case_link.target_id = orders.case_id)
                       OR (
                           orders.kitaron_history_only = 0
                           AND EXISTS (
                               SELECT 1
                               FROM kitaron_sync_links order_link
                               WHERE order_link.source_entity = 'order'
                                 AND order_link.target_id = orders.id))
                           AS is_current_authoritative_demand
                FROM orders
                WHERE orders.id = $id;
                """;
            order.Parameters.AddWithValue("$id", allocation.OrderId!);
            await using var reader = await order.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || !string.Equals(reader.GetString(0), caseId, StringComparison.Ordinal)
                || reader.GetString(1) == "cancelled")
            {
                issues.Add(SourceIssue("allocation_order_invalid", $"Order '{allocation.OrderId}' is missing, cancelled, or belongs to another Case.", source, "allocations.orderId"));
            }
            else if (!reader.GetBoolean(2))
            {
                issues.Add(SourceIssue(
                    "noncurrent_kitaron_order",
                    $"Order '{allocation.OrderId}' is not current authoritative Kitaron demand and cannot receive a new Production Batch allocation.",
                    source,
                    "allocations.orderId"));
            }
        }

        await using var route = connection.CreateCommand();
        route.Transaction = transaction;
        route.CommandText = """
            SELECT id, operation_number, route_position, name, required_machine_type,
                   setup_seconds, cycle_seconds, dependency_type,
                   predecessor_case_operation_id, simultaneous_group_key,
                   qa_seconds, load_unload_seconds, load_unload_requires_worker,
                   automatic_loading, load_unload_every_n_parts, day_shift_only,
                   version
            FROM case_operations
            WHERE case_id = $caseId
            ORDER BY route_position, operation_number, id;
            """;
        route.Parameters.AddWithValue("$caseId", caseId);
        var operations = new List<RouteOperation>();
        await using (var routeReader = await route.ExecuteReaderAsync(cancellationToken))
        {
            while (await routeReader.ReadAsync(cancellationToken))
            {
                operations.Add(new RouteOperation(
                    routeReader.GetString(0), routeReader.GetInt32(1), routeReader.GetInt32(2), routeReader.GetString(3),
                    GetNullableString(routeReader, 4), GetNullableInt32(routeReader, 5), GetNullableInt32(routeReader, 6),
                    routeReader.GetString(7), GetNullableString(routeReader, 8), GetNullableString(routeReader, 9),
                    routeReader.GetInt32(10), routeReader.GetInt32(11), routeReader.GetBoolean(12), routeReader.GetBoolean(13),
                    GetNullableInt32(routeReader, 14), routeReader.GetBoolean(15), routeReader.GetInt32(16)));
            }
        }
        if (operations.Count == 0)
        {
            issues.Add(SourceIssue("case_route_required", "A new Batch can be imported only after the selected existing Case has an explicit operation route.", source, "caseId"));
            return null;
        }
        var expectedRoute = selection.ExpectedCaseRoute?
            .Where(operation => !string.IsNullOrWhiteSpace(operation.CaseOperationId)
                && operation.Version is > 0)
            .ToDictionary(
                operation => operation.CaseOperationId!,
                operation => operation.Version!.Value,
                StringComparer.Ordinal);
        if (expectedRoute is null
            || expectedRoute.Count != operations.Count
            || operations.Any(operation => !expectedRoute.TryGetValue(operation.Id, out var version)
                || version != operation.Version))
        {
            issues.Add(SourceIssue(
                "case_route_changed",
                "The Case route no longer matches the complete route IDs and versions reviewed in Preview; preview it again.",
                source,
                "expectedCaseRoute"));
            return null;
        }
        if (!string.IsNullOrWhiteSpace(selection.CaseOperationId)
            && !operations.Any(operation => operation.Id == selection.CaseOperationId))
        {
            issues.Add(SourceIssue("case_operation_invalid", "caseOperationId does not belong to the selected Case route.", source, "caseOperationId"));
            return null;
        }
        if (await ExistsAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM production_batches WHERE case_id = $caseId AND batch_number = $value);", selection.BatchNumber!, cancellationToken, caseId))
        {
            issues.Add(SourceIssue("batch_number_conflict", "The selected Batch Number already exists for this Case.", source, "batchNumber"));
            return null;
        }
        if (issues.Count > 0)
        {
            return null;
        }

        var batchId = Guid.NewGuid().ToString("N");
        await using (var batch = connection.CreateCommand())
        {
            batch.Transaction = transaction;
            batch.CommandText = """
                INSERT INTO production_batches (
                    id, case_id, batch_number, status, planned_quantity,
                    route_revision, version, created_at, updated_at)
                VALUES ($id, $caseId, $number, 'waiting', $quantity, NULL, 1, $now, $now);
                """;
            batch.Parameters.AddWithValue("$id", batchId);
            batch.Parameters.AddWithValue("$caseId", caseId);
            batch.Parameters.AddWithValue("$number", validatedBatch.BatchNumber);
            batch.Parameters.AddWithValue("$quantity", source.Values.Quantity!.Value);
            batch.Parameters.AddWithValue("$now", FormatInstant(now));
            await batch.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var allocation in validatedBatch.Allocations)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO batch_allocations (
                    id, production_batch_id, allocation_type, order_id, quantity,
                    version, created_at, updated_at)
                VALUES ($id, $batchId, $type, $orderId, $quantity, 1, $now, $now);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$batchId", batchId);
            command.Parameters.AddWithValue("$type", allocation.AllocationType.ToStorageToken());
            command.Parameters.AddWithValue("$orderId", Db(allocation.OrderId));
            command.Parameters.AddWithValue("$quantity", allocation.Quantity);
            command.Parameters.AddWithValue("$now", FormatInstant(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        string? selectedBatchOperationId = null;
        var batchOperationIds = new List<string>(operations.Count);
        foreach (var operation in operations)
        {
            var batchOperationId = Guid.NewGuid().ToString("N");
            batchOperationIds.Add(batchOperationId);
            if (operation.Id == selection.CaseOperationId)
            {
                selectedBatchOperationId = batchOperationId;
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO batch_operations (
                    id, production_batch_id, source_case_operation_id,
                    operation_number, route_position, name, required_machine_type,
                    setup_seconds, cycle_seconds, status, version, created_at, updated_at,
                    dependency_type, predecessor_source_case_operation_id, simultaneous_group_key,
                    qa_seconds, load_unload_seconds, load_unload_requires_worker,
                    automatic_loading, load_unload_every_n_parts, day_shift_only)
                VALUES (
                    $id, $batchId, $sourceId, $number, $position, $name, $machineType,
                    $setup, $cycle, 'not_started', 1, $now, $now,
                    $dependencyType, $predecessor, $groupKey,
                    $qa, $load, $worker, $automatic, $everyN, $dayShiftOnly);
                """;
            command.Parameters.AddWithValue("$id", batchOperationId);
            command.Parameters.AddWithValue("$batchId", batchId);
            command.Parameters.AddWithValue("$sourceId", operation.Id);
            command.Parameters.AddWithValue("$number", operation.OperationNumber);
            command.Parameters.AddWithValue("$position", operation.RoutePosition);
            command.Parameters.AddWithValue("$name", operation.Name);
            command.Parameters.AddWithValue("$machineType", Db(operation.RequiredMachineType));
            command.Parameters.AddWithValue("$setup", Db(operation.SetupSeconds));
            command.Parameters.AddWithValue("$cycle", Db(operation.CycleSeconds));
            command.Parameters.AddWithValue("$now", FormatInstant(now));
            command.Parameters.AddWithValue("$dependencyType", operation.DependencyType);
            command.Parameters.AddWithValue("$predecessor", Db(operation.PredecessorId));
            command.Parameters.AddWithValue("$groupKey", Db(operation.SimultaneousGroupKey));
            command.Parameters.AddWithValue("$qa", operation.QaSeconds);
            command.Parameters.AddWithValue("$load", operation.LoadUnloadSeconds);
            command.Parameters.AddWithValue("$worker", operation.LoadUnloadRequiresWorker ? 1 : 0);
            command.Parameters.AddWithValue("$automatic", operation.AutomaticLoading ? 1 : 0);
            command.Parameters.AddWithValue("$everyN", Db(operation.LoadUnloadEveryNParts));
            command.Parameters.AddWithValue("$dayShiftOnly", operation.DayShiftOnly ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await SqliteOrderLifecycle.RecomputeForBatchAsync(
            connection,
            transaction,
            batchId,
            now,
            cancellationToken);
        return new CreatedBatch(batchId, batchOperationIds, selectedBatchOperationId);
    }

    private static async Task<string?> AppendAssignmentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchOperationId,
        string machineId,
        LegacyCompatibilityOverrideRequest? compatibilityOverride,
        EditAuthority authority,
        string confirmedByUserId,
        DateTimeOffset now,
        LegacyPlanningRowResponse source,
        List<LegacyImportIssue> issues,
        CancellationToken cancellationToken)
    {
        string? requiredMachineType;
        string status;
        await using (var operation = connection.CreateCommand())
        {
            operation.Transaction = transaction;
            operation.CommandText = "SELECT required_machine_type, status FROM batch_operations WHERE id = $id;";
            operation.Parameters.AddWithValue("$id", batchOperationId);
            await using var reader = await operation.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                issues.Add(SourceIssue("batch_operation_not_found", $"Batch Operation '{batchOperationId}' was not found.", source, "batchOperationId"));
                return null;
            }
            requiredMachineType = GetNullableString(reader, 0);
            status = reader.GetString(1);
        }
        if (status != "not_started")
        {
            issues.Add(SourceIssue("operation_not_assignable", $"Batch Operation '{batchOperationId}' status '{status}' cannot be imported into a backlog.", source, "batchOperationId"));
            return null;
        }
        if (await ExistsAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM machine_assignments WHERE batch_operation_id = $value);", batchOperationId, cancellationToken))
        {
            issues.Add(SourceIssue("operation_already_assigned", $"Batch Operation '{batchOperationId}' is already assigned; resolve it on the Planning Board.", source, "batchOperationId"));
            return null;
        }

        MachineRecord? machine;
        await using (var machineCommand = connection.CreateCommand())
        {
            machineCommand.Transaction = transaction;
            machineCommand.CommandText = """
                SELECT machines.machine_type, machines.axis_type, machines.capabilities_json,
                       machines.is_active, COALESCE(machine_types.capabilities_json, '[]')
                FROM machines
                LEFT JOIN machine_types ON machine_types.id = machines.machine_type_id
                WHERE machines.id = $id;
                """;
            machineCommand.Parameters.AddWithValue("$id", machineId);
            await using var reader = await machineCommand.ExecuteReaderAsync(cancellationToken);
            machine = await reader.ReadAsync(cancellationToken)
                ? new MachineRecord(
                    reader.GetString(0), GetNullableString(reader, 1), reader.GetString(2),
                    reader.GetBoolean(3), reader.GetString(4))
                : null;
        }
        if (machine is null)
        {
            issues.Add(SourceIssue("machine_not_found", $"Machine '{machineId}' was not found.", source, "machineId"));
            return null;
        }
        if (!machine.IsActive)
        {
            issues.Add(SourceIssue("machine_inactive", $"Machine '{machineId}' is inactive.", source, "machineId"));
            return null;
        }
        var compatible = IsCompatible(machine, requiredMachineType);
        var overrideReason = compatibilityOverride?.Reason?.Trim();
        if (!compatible && overrideReason?.Length > 1000)
        {
            issues.Add(SourceIssue(
                "override_reason_too_long",
                "Compatibility override reason must contain at most 1000 characters.",
                source,
                "compatibilityOverride.reason"));
            return null;
        }
        if (!compatible && (compatibilityOverride is null
            || !compatibilityOverride.Confirmed
            || string.IsNullOrEmpty(overrideReason)))
        {
            issues.Add(SourceIssue("machine_type_override_required", "The selected Machine is incompatible with the Operation; explicit confirmation and reason are required.", source, "compatibilityOverride"));
            return null;
        }

        int position;
        await using (var backlog = connection.CreateCommand())
        {
            backlog.Transaction = transaction;
            backlog.CommandText = "SELECT COALESCE(MAX(backlog_position), -1) + 1 FROM machine_assignments WHERE machine_id = $machineId;";
            backlog.Parameters.AddWithValue("$machineId", machineId);
            position = Convert.ToInt32(await backlog.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        var assignmentId = Guid.NewGuid().ToString("N");
        await using (var assignment = connection.CreateCommand())
        {
            assignment.Transaction = transaction;
            assignment.CommandText = """
                INSERT INTO machine_assignments (
                    id, batch_operation_id, machine_id, backlog_position,
                    planning_mode, version, created_at, updated_at)
                VALUES ($id, $operationId, $machineId, $position, 'manual', 1, $now, $now);
                """;
            assignment.Parameters.AddWithValue("$id", assignmentId);
            assignment.Parameters.AddWithValue("$operationId", batchOperationId);
            assignment.Parameters.AddWithValue("$machineId", machineId);
            assignment.Parameters.AddWithValue("$position", position);
            assignment.Parameters.AddWithValue("$now", FormatInstant(now));
            await assignment.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!compatible)
        {
            await using var audit = connection.CreateCommand();
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO machine_assignment_overrides (
                    id, batch_operation_id, machine_id, required_machine_type,
                    selected_machine_type, reason, confirmed_by_client_id,
                    confirmed_by_user_id, confirmed_at, version, created_at, updated_at)
                VALUES ($id, $operationId, $machineId, $required, $selected, $reason,
                        $clientId, $userId, $now, 1, $now, $now);
                """;
            audit.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            audit.Parameters.AddWithValue("$operationId", batchOperationId);
            audit.Parameters.AddWithValue("$machineId", machineId);
            audit.Parameters.AddWithValue("$required", requiredMachineType!);
            audit.Parameters.AddWithValue("$selected", machine.ProcessType);
            audit.Parameters.AddWithValue("$reason", overrideReason!);
            audit.Parameters.AddWithValue("$clientId", authority.ClientId);
            audit.Parameters.AddWithValue("$userId", confirmedByUserId);
            audit.Parameters.AddWithValue("$now", FormatInstant(now));
            await audit.ExecuteNonQueryAsync(cancellationToken);
            await SqliteStructuredEventLogRepository.AppendAsync(
                connection,
                transaction,
                new StructuredEventWrite(
                    "cross_machine_type_override",
                    now,
                    confirmedByUserId,
                    new Dictionary<string, string>
                    {
                        ["batchOperationId"] = batchOperationId,
                        ["machineId"] = machineId
                    },
                    "machine_type_incompatible",
                    overrideReason,
                    new { requiredMachineType },
                    new { selectedMachineType = machine.ProcessType }),
                cancellationToken);
        }
        return assignmentId;
    }

    private static bool IsCompatible(MachineRecord machine, string? requiredMachineType)
    {
        var required = requiredMachineType?.Trim();
        if (string.IsNullOrEmpty(required))
        {
            return true;
        }
        var capabilities = JsonSerializer.Deserialize<string[]>(machine.CapabilitiesJson) ?? [];
        var typeCapabilities = JsonSerializer.Deserialize<string[]>(machine.TypeCapabilitiesJson) ?? [];
        return string.Equals(machine.ProcessType, required, StringComparison.OrdinalIgnoreCase)
            || string.Equals(machine.AxisType, required, StringComparison.OrdinalIgnoreCase)
            || capabilities.Contains(required, StringComparer.OrdinalIgnoreCase)
            || typeCapabilities.Contains(required, StringComparer.OrdinalIgnoreCase);
    }

    private static string? ResolveExclusiveReference(
        string? existingId,
        string? sourceRowKey,
        IReadOnlyDictionary<string, string> sourceIds,
        string field,
        LegacyPlanningRowResponse source,
        List<LegacyImportIssue> issues)
    {
        var hasExisting = !string.IsNullOrWhiteSpace(existingId);
        var hasSource = !string.IsNullOrWhiteSpace(sourceRowKey);
        if (hasExisting == hasSource)
        {
            issues.Add(SourceIssue("exclusive_reference_required", $"Provide exactly one of {field} or its source-row reference.", source, field));
            return null;
        }
        if (hasExisting)
        {
            return existingId!.Trim();
        }
        if (!sourceIds.TryGetValue(sourceRowKey!, out var resolved))
        {
            issues.Add(SourceIssue("source_reference_not_found", $"Source-row reference '{sourceRowKey}' did not create the required entity.", source, field));
            return null;
        }
        return resolved;
    }

    private static async Task<string> EnsureEditAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority authority,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(connection, transaction, now, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, holder_user_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        }
        if (!string.Equals(reader.GetString(0), authority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(2) != authority.Generation)
        {
            throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
        }
        return reader.GetString(1);
    }

    private static async Task<StoredReceipt?> ReadReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workbookSha256,
        string requestSha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT approved_request_sha256, response_json
            FROM legacy_working_plan_imports
            WHERE workbook_sha256 = $hash AND approved_request_sha256 = $requestHash;
            """;
        command.Parameters.AddWithValue("$hash", workbookSha256);
        command.Parameters.AddWithValue("$requestHash", requestSha256);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new StoredReceipt(reader.GetString(0), reader.GetString(1)) : null;
    }

    private static async Task<bool> HasWorkbookReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workbookSha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM legacy_working_plan_imports WHERE workbook_sha256 = $hash);";
        command.Parameters.AddWithValue("$hash", workbookSha256);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static bool IsCaseOrderOnlyPass(LegacyImportCommitRequest request) =>
        string.IsNullOrWhiteSpace(request.PlanningSheet)
        && !string.IsNullOrWhiteSpace(request.OpenOrdersSheet)
        && (request.PlanningSelections?.Count ?? 0) == 0
        && (request.MachineMappings?.Count ?? 0) == 0
        && (request.OpenOrderSelections ?? []).Any(selection => selection.Action is "create_case" or "create_order")
        && (request.OpenOrderSelections ?? []).All(selection => selection.Action is "create_case" or "create_order" or "skip")
        && (request.ColumnMappings ?? []).All(mapping =>
            string.Equals(mapping.Scope, "open_orders", StringComparison.Ordinal));

    private static async Task InsertReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LegacyImportCommitResponse response,
        string requestSha256,
        string clientId,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO legacy_working_plan_imports (
                id, workbook_sha256, approved_request_sha256, response_json,
                committed_by_client_id, committed_by_user_id, committed_at)
            VALUES ($id, $workbookHash, $requestHash, $response, $clientId, $userId, $now);
            """;
        command.Parameters.AddWithValue("$id", response.CommitId);
        command.Parameters.AddWithValue("$workbookHash", response.WorkbookSha256);
        command.Parameters.AddWithValue("$requestHash", requestSha256);
        command.Parameters.AddWithValue("$response", JsonSerializer.Serialize(response));
        command.Parameters.AddWithValue("$clientId", clientId);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", FormatInstant(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static LegacyImportCommitResponse ToReplayResponse(string responseJson)
    {
        var prior = JsonSerializer.Deserialize<LegacyImportCommitResponse>(responseJson)
            ?? throw new InvalidDataException("Stored legacy import receipt is invalid.");
        return prior with
        {
            Replayed = true,
            Unchanged = prior.Created,
            Created = new LegacyImportEntityIdsResponse([], [], [], [], [])
        };
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string value,
        CancellationToken cancellationToken,
        string? caseId = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        if (caseId is not null)
        {
            command.Parameters.AddWithValue("$caseId", caseId);
        }
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static void ThrowIfIssues(IReadOnlyList<LegacyImportIssue> issues)
    {
        if (issues.Count > 0)
        {
            throw new LegacyImportValidationException(issues);
        }
    }

    private static LegacyImportIssue SourceIssue(
        string code,
        string message,
        LegacyOpenOrderRowResponse source,
        string field) => new(
            LegacyImportIssueSeverity.Blocking, code, message, source.SheetName, source.RowNumber, field);

    private static LegacyImportIssue SourceIssue(
        string code,
        string message,
        LegacyPlanningRowResponse source,
        string field) => new(
            LegacyImportIssueSeverity.Blocking, code, message, source.SheetName, source.RowNumber, field, source.SectionKey);

    private static object Db(object? value) => value ?? DBNull.Value;

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record StoredReceipt(string RequestSha256, string ResponseJson);
    private sealed record ResolvedAllocation(string Type, string? OrderId, int Quantity);
    private sealed record CreatedBatch(
        string BatchId,
        IReadOnlyList<string> BatchOperationIds,
        string? SelectedBatchOperationId);
    private sealed record MachineRecord(
        string ProcessType,
        string? AxisType,
        string CapabilitiesJson,
        bool IsActive,
        string TypeCapabilitiesJson);
    private sealed record RouteOperation(
        string Id,
        int OperationNumber,
        int RoutePosition,
        string Name,
        string? RequiredMachineType,
        int? SetupSeconds,
        int? CycleSeconds,
        string DependencyType,
        string? PredecessorId,
        string? SimultaneousGroupKey,
        int QaSeconds,
        int LoadUnloadSeconds,
        bool LoadUnloadRequiresWorker,
        bool AutomaticLoading,
        int? LoadUnloadEveryNParts,
        bool DayShiftOnly,
        int Version);
}
