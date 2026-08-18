using Meimad.Planner.Server.Application.Kitaron;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteKitaronSyncRepository(
    SqliteDatabase database) : IKitaronSyncRepository
{
    public async Task<KitaronSyncStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadStatusAsync(connection, null, cancellationToken);
    }

    public async Task<KitaronSyncStatus> MarkStartedAsync(
        int mappingVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE kitaron_sync_state
            SET sync_status = 'running', message = 'Reading the configured Kitaron view.',
                last_started_at = $now, last_completed_at = NULL,
                mapping_version = $mappingVersion, version = version + 1,
                updated_at = $now
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$mappingVersion", mappingVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await ReadStatusAsync(connection, null, cancellationToken);
    }

    public async Task<KitaronSyncStatus> MarkFailedAsync(
        string status,
        string message,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (status is not ("failed" or "blocked")) throw new ArgumentOutOfRangeException(nameof(status));
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE kitaron_sync_state
            SET sync_status = $status, message = $message, last_completed_at = $now,
                version = version + 1, updated_at = $now
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$message", Limit(message, 2000));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await ReadStatusAsync(connection, null, cancellationToken);
    }

    public async Task<KitaronSyncStatus> ApplyAsync(
        KitaronSyncPlan plan,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var counts = new MutableCounts();
        var caseIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in plan.Cases)
        {
            var resolved = await ResolveCaseAsync(connection, transaction, item, now, counts, cancellationToken);
            caseIds[item.SourceKey] = resolved;
        }
        foreach (var item in plan.Orders)
        {
            if (!caseIds.TryGetValue(item.CaseSourceKey, out var caseId))
                throw new KitaronSyncDataException($"Order {item.SourceKey} references an unresolved Case.");
            await ResolveOrderAsync(connection, transaction, item, caseId, now, counts, cancellationToken);
        }
        foreach (var item in plan.Operations)
        {
            if (!caseIds.TryGetValue(item.CaseSourceKey, out var caseId))
                throw new KitaronSyncDataException($"Operation {item.SourceKey} references an unresolved Case.");
            await ResolveOperationAsync(connection, transaction, item, caseId, now, counts, cancellationToken);
        }
        foreach (var item in plan.Components)
        {
            if (!caseIds.TryGetValue(item.ParentCaseSourceKey, out var parentCaseId)
                || !caseIds.TryGetValue(item.ChildCaseSourceKey, out var childCaseId))
            {
                throw new KitaronSyncDataException($"Component {item.SourceKey} references an unresolved Case.");
            }
            await ResolveComponentAsync(
                connection, transaction, item, parentCaseId, childCaseId, now, counts, cancellationToken);
        }
        await DeactivateMissingComponentsAsync(
            connection, transaction, plan.KnownComponentSourceKeys, now, counts, cancellationToken);

        var message = $"Synchronized {plan.SourceRows:N0} source rows: " +
            $"{counts.CasesCreated} Case(s), {counts.OrdersCreated} Order(s), and " +
            $"{counts.OperationsCreated} Case Operation(s), and {counts.ComponentsCreated} Case Component(s) created. " +
            "Existing or linked records were reused safely.";
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE kitaron_sync_state
                SET sync_status = 'succeeded', message = $message, last_completed_at = $now,
                    source_rows = $sourceRows,
                    cases_created = $casesCreated, cases_updated = $casesUpdated, cases_matched = $casesMatched,
                    orders_created = $ordersCreated, orders_updated = $ordersUpdated, orders_matched = $ordersMatched,
                    operations_created = $operationsCreated, operations_updated = $operationsUpdated,
                    operations_matched = $operationsMatched, warning_count = $warnings,
                    components_created = $componentsCreated, components_updated = $componentsUpdated,
                    components_matched = $componentsMatched,
                    mapping_version = $mappingVersion, version = version + 1, updated_at = $now
                WHERE id = 1;
                """;
            command.Parameters.AddWithValue("$message", message);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$sourceRows", plan.SourceRows);
            command.Parameters.AddWithValue("$casesCreated", counts.CasesCreated);
            command.Parameters.AddWithValue("$casesUpdated", counts.CasesUpdated);
            command.Parameters.AddWithValue("$casesMatched", counts.CasesMatched);
            command.Parameters.AddWithValue("$ordersCreated", counts.OrdersCreated);
            command.Parameters.AddWithValue("$ordersUpdated", counts.OrdersUpdated);
            command.Parameters.AddWithValue("$ordersMatched", counts.OrdersMatched);
            command.Parameters.AddWithValue("$operationsCreated", counts.OperationsCreated);
            command.Parameters.AddWithValue("$operationsUpdated", counts.OperationsUpdated);
            command.Parameters.AddWithValue("$operationsMatched", counts.OperationsMatched);
            command.Parameters.AddWithValue("$componentsCreated", counts.ComponentsCreated);
            command.Parameters.AddWithValue("$componentsUpdated", counts.ComponentsUpdated);
            command.Parameters.AddWithValue("$componentsMatched", counts.ComponentsMatched);
            command.Parameters.AddWithValue("$warnings", plan.Warnings.Count + counts.Warnings);
            command.Parameters.AddWithValue("$mappingVersion", plan.MappingVersion);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var result = await ReadStatusAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<string> ResolveCaseAsync(
        SqliteConnection connection, SqliteTransaction transaction, KitaronSyncCase item,
        DateTimeOffset now, MutableCounts counts, CancellationToken cancellationToken)
    {
        var link = await ReadLinkAsync(connection, transaction, "case", item.SourceKey, cancellationToken);
        if (link is null)
        {
            var matches = await FindIdsAsync(connection, transaction,
                "SELECT id FROM cases WHERE part_number = $key COLLATE NOCASE ORDER BY id;",
                item.PartNumber, cancellationToken);
            if (matches.Count > 1)
                throw new KitaronSyncDataException($"Part {item.PartNumber} matches multiple Planner Cases.");
            var id = matches.Count == 1 ? matches[0] : StableId("kit-case", item.SourceKey);
            var owns = matches.Count == 0;
            if (owns)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO cases (id, part_number, name, revision, customer, working_folder_path,
                        version, created_at, updated_at)
                    VALUES ($id, $part, $name, $revision, $customer, $folder, 1, $now, $now);
                    """;
                Add(insert, "$id", id); Add(insert, "$part", item.PartNumber); Add(insert, "$name", item.Name);
                Add(insert, "$revision", item.Revision); Add(insert, "$customer", item.Customer);
                Add(insert, "$folder", item.WorkingFolderPath); Add(insert, "$now", now.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
                counts.CasesCreated++;
            }
            else counts.CasesMatched++;
            await UpsertLinkAsync(connection, transaction, "case", item.SourceKey, id, owns, item.SourceHash, now, cancellationToken);
            return id;
        }
        await EnsureTargetExistsAsync(connection, transaction, "cases", link.Value.TargetId, cancellationToken);
        if (link.Value.OwnsTarget && !StringComparer.Ordinal.Equals(link.Value.SourceHash, item.SourceHash))
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE cases SET part_number=$part, name=$name, revision=$revision, customer=$customer,
                    working_folder_path=$folder, version=version+1, updated_at=$now WHERE id=$id;
                """;
            Add(update, "$part", item.PartNumber); Add(update, "$name", item.Name);
            Add(update, "$revision", item.Revision); Add(update, "$customer", item.Customer);
            Add(update, "$folder", item.WorkingFolderPath); Add(update, "$now", now.ToString("O"));
            Add(update, "$id", link.Value.TargetId);
            await update.ExecuteNonQueryAsync(cancellationToken);
            counts.CasesUpdated++;
        }
        else counts.CasesMatched++;
        await UpsertLinkAsync(connection, transaction, "case", item.SourceKey, link.Value.TargetId,
            link.Value.OwnsTarget, item.SourceHash, now, cancellationToken);
        return link.Value.TargetId;
    }

    private static async Task ResolveOrderAsync(
        SqliteConnection connection, SqliteTransaction transaction, KitaronSyncOrder item, string caseId,
        DateTimeOffset now, MutableCounts counts, CancellationToken cancellationToken)
    {
        var link = await ReadLinkAsync(connection, transaction, "order", item.SourceKey, cancellationToken);
        if (link is null)
        {
            var legacyKey = $"{item.CaseSourceKey}\u001f{item.OrderNumber}";
            var legacyLink = await ReadLinkAsync(connection, transaction, "order", legacyKey, cancellationToken);
            if (legacyLink is not null)
            {
                await DeleteLinkAsync(connection, transaction, "order", legacyKey, cancellationToken);
                await UpsertLinkAsync(connection, transaction, "order", item.SourceKey,
                    legacyLink.Value.TargetId, legacyLink.Value.OwnsTarget, legacyLink.Value.SourceHash,
                    now, cancellationToken);
                link = legacyLink;
            }
        }
        if (link is null)
        {
            var matches = await FindIdsAsync(connection, transaction,
                """
                SELECT id FROM orders
                WHERE case_id=$caseId AND order_reference=$key COLLATE NOCASE
                  AND NOT EXISTS (
                      SELECT 1 FROM kitaron_sync_links
                      WHERE source_entity='order' AND target_id=orders.id)
                ORDER BY id;
                """,
                item.OrderNumber, cancellationToken, caseId);
            if (matches.Count > 1)
                throw new KitaronSyncDataException($"Order {item.OrderNumber} matches multiple Planner Orders.");
            var id = matches.Count == 1 ? matches[0] : StableId("kit-order", item.SourceKey);
            var owns = matches.Count == 0;
            if (owns)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO orders (id, case_id, order_reference, quantity, work_finish_date, status,
                        version, created_at, updated_at)
                    VALUES ($id, $caseId, $number, $quantity, $date, $status, 1, $now, $now);
                    """;
                Add(insert, "$id", id); Add(insert, "$caseId", caseId); Add(insert, "$number", item.OrderNumber);
                Add(insert, "$quantity", item.Quantity); Add(insert, "$date", item.WorkFinishDate.ToString("yyyy-MM-dd"));
                Add(insert, "$status", item.Status);
                Add(insert, "$now", now.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
                counts.OrdersCreated++;
            }
            else counts.OrdersMatched++;
            await UpsertLinkAsync(connection, transaction, "order", item.SourceKey, id, owns, item.SourceHash, now, cancellationToken);
            return;
        }
        await EnsureTargetExistsAsync(connection, transaction, "orders", link.Value.TargetId, cancellationToken);
        if (link.Value.OwnsTarget && !StringComparer.Ordinal.Equals(link.Value.SourceHash, item.SourceHash))
        {
            var allocated = await ScalarIntAsync(connection, transaction,
                "SELECT COALESCE(SUM(quantity),0) FROM batch_allocations WHERE order_id=$id;",
                "$id", link.Value.TargetId, cancellationToken);
            if (allocated <= item.Quantity)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE orders SET quantity=$quantity, work_finish_date=$date, status=$status,
                        version=version+1, updated_at=$now WHERE id=$id;
                    """;
                Add(update, "$quantity", item.Quantity); Add(update, "$date", item.WorkFinishDate.ToString("yyyy-MM-dd"));
                Add(update, "$status", item.Status);
                Add(update, "$now", now.ToString("O")); Add(update, "$id", link.Value.TargetId);
                await update.ExecuteNonQueryAsync(cancellationToken);
                counts.OrdersUpdated++;
            }
            else
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE orders SET work_finish_date=$date, status=$status,
                        version=version+1, updated_at=$now WHERE id=$id;
                    """;
                Add(update, "$date", item.WorkFinishDate.ToString("yyyy-MM-dd"));
                Add(update, "$status", item.Status); Add(update, "$now", now.ToString("O"));
                Add(update, "$id", link.Value.TargetId);
                await update.ExecuteNonQueryAsync(cancellationToken);
                counts.OrdersUpdated++; counts.Warnings++;
            }
        }
        else counts.OrdersMatched++;
        await UpsertLinkAsync(connection, transaction, "order", item.SourceKey, link.Value.TargetId,
            link.Value.OwnsTarget, item.SourceHash, now, cancellationToken);
    }

    private static async Task ResolveOperationAsync(
        SqliteConnection connection, SqliteTransaction transaction, KitaronSyncOperation item, string caseId,
        DateTimeOffset now, MutableCounts counts, CancellationToken cancellationToken)
    {
        var link = await ReadLinkAsync(connection, transaction, "case_operation", item.SourceKey, cancellationToken);
        if (link is null)
        {
            var matches = await FindIdsAsync(connection, transaction,
                "SELECT id FROM case_operations WHERE case_id=$caseId AND operation_number=$key ORDER BY id;",
                item.OperationNumber, cancellationToken, caseId);
            if (matches.Count > 1)
                throw new KitaronSyncDataException($"Operation {item.OperationNumber} matches multiple Case Operations.");
            var id = matches.Count == 1 ? matches[0] : StableId("kit-op", item.SourceKey);
            var owns = matches.Count == 0;
            if (owns)
            {
                var route = await NextRoutePositionAsync(connection, transaction, caseId, item.RoutePosition, cancellationToken);
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO case_operations (id, case_id, operation_number, route_position, name,
                        required_machine_type, setup_seconds, cycle_seconds, dependency_type,
                        version, created_at, updated_at)
                    VALUES ($id, $caseId, $number, $route, $name, $type, $setup, $cycle,
                        'independent', 1, $now, $now);
                    """;
                Add(insert, "$id", id); Add(insert, "$caseId", caseId); Add(insert, "$number", item.OperationNumber);
                Add(insert, "$route", route); Add(insert, "$name", item.Name); Add(insert, "$type", item.RequiredMachineType);
                Add(insert, "$setup", item.SetupSeconds); Add(insert, "$cycle", item.CycleSeconds);
                Add(insert, "$now", now.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
                counts.OperationsCreated++;
            }
            else counts.OperationsMatched++;
            await UpsertLinkAsync(connection, transaction, "case_operation", item.SourceKey, id, owns, item.SourceHash, now, cancellationToken);
            return;
        }
        await EnsureTargetExistsAsync(connection, transaction, "case_operations", link.Value.TargetId, cancellationToken);
        if (link.Value.OwnsTarget && !StringComparer.Ordinal.Equals(link.Value.SourceHash, item.SourceHash))
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE case_operations SET name=$name, required_machine_type=$type,
                    setup_seconds=$setup, cycle_seconds=$cycle, version=version+1, updated_at=$now
                WHERE id=$id;
                """;
            Add(update, "$name", item.Name); Add(update, "$type", item.RequiredMachineType);
            Add(update, "$setup", item.SetupSeconds); Add(update, "$cycle", item.CycleSeconds);
            Add(update, "$now", now.ToString("O")); Add(update, "$id", link.Value.TargetId);
            await update.ExecuteNonQueryAsync(cancellationToken);
            counts.OperationsUpdated++;
        }
        else counts.OperationsMatched++;
        await UpsertLinkAsync(connection, transaction, "case_operation", item.SourceKey, link.Value.TargetId,
            link.Value.OwnsTarget, item.SourceHash, now, cancellationToken);
    }

    private static async Task ResolveComponentAsync(
        SqliteConnection connection, SqliteTransaction transaction, KitaronSyncComponent item,
        string parentCaseId, string childCaseId, DateTimeOffset now, MutableCounts counts,
        CancellationToken cancellationToken)
    {
        if (parentCaseId == childCaseId)
            throw new KitaronSyncDataException($"Kitaron component {item.SourceKey} contains itself.");
        if (await ScalarIntAsync(connection, transaction,
                "SELECT COUNT(*) FROM case_operations WHERE case_id=$caseId;",
                "$caseId", parentCaseId, cancellationToken) > 0)
            throw new KitaronSyncDataException(
                $"Kitaron parent Case {item.ParentCaseSourceKey} still has direct Operations. Remove or migrate that route before activating its Components.");

        var link = await ReadLinkAsync(connection, transaction, "case_component", item.SourceKey, cancellationToken);
        if (link is null)
        {
            var matches = await FindComponentIdsAsync(
                connection, transaction, parentCaseId, childCaseId, cancellationToken);
            if (matches.Count > 1)
                throw new KitaronSyncDataException($"Component {item.SourceKey} matches multiple Planner relationships.");
            var id = matches.Count == 1 ? matches[0] : StableId("kit-component", item.SourceKey);
            var owns = matches.Count == 0;
            if (owns)
            {
                await EnsureNoComponentCycleAsync(
                    connection, transaction, parentCaseId, childCaseId, null, cancellationToken);
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO case_components (
                        id, parent_case_id, child_case_id, quantity_per_parent, sort_order,
                        notes, is_active, version, created_at, updated_at)
                    VALUES ($id, $parent, $child, $quantity, $sort, NULL, 1, 1, $now, $now);
                    """;
                Add(insert, "$id", id); Add(insert, "$parent", parentCaseId); Add(insert, "$child", childCaseId);
                Add(insert, "$quantity", item.QuantityPerParent); Add(insert, "$sort", item.SortOrder);
                Add(insert, "$now", now.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
                counts.ComponentsCreated++;
            }
            else counts.ComponentsMatched++;
            await UpsertLinkAsync(connection, transaction, "case_component", item.SourceKey,
                id, owns, item.SourceHash, now, cancellationToken);
            return;
        }

        await EnsureTargetExistsAsync(connection, transaction, "case_components", link.Value.TargetId, cancellationToken);
        if (link.Value.OwnsTarget && !StringComparer.Ordinal.Equals(link.Value.SourceHash, item.SourceHash))
        {
            await EnsureNoComponentCycleAsync(
                connection, transaction, parentCaseId, childCaseId, link.Value.TargetId, cancellationToken);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE case_components
                SET parent_case_id=$parent, child_case_id=$child, quantity_per_parent=$quantity,
                    sort_order=$sort, is_active=1, version=version+1, updated_at=$now
                WHERE id=$id;
                """;
            Add(update, "$parent", parentCaseId); Add(update, "$child", childCaseId);
            Add(update, "$quantity", item.QuantityPerParent); Add(update, "$sort", item.SortOrder);
            Add(update, "$now", now.ToString("O")); Add(update, "$id", link.Value.TargetId);
            await update.ExecuteNonQueryAsync(cancellationToken);
            counts.ComponentsUpdated++;
        }
        else counts.ComponentsMatched++;
        await UpsertLinkAsync(connection, transaction, "case_component", item.SourceKey, link.Value.TargetId,
            link.Value.OwnsTarget, item.SourceHash, now, cancellationToken);
    }

    private static async Task DeactivateMissingComponentsAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlySet<string> seen, DateTimeOffset now, MutableCounts counts,
        CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT source_key, target_id FROM kitaron_sync_links
            WHERE source_entity='case_component' AND owns_target=1;
            """;
        var staleIds = new List<string>();
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!seen.Contains(reader.GetString(0))) staleIds.Add(reader.GetString(1));
            }
        }
        foreach (var id in staleIds)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE case_components SET is_active=0, version=version+1, updated_at=$now
                WHERE id=$id AND is_active=1;
                """;
            Add(update, "$now", now.ToString("O")); Add(update, "$id", id);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 1) counts.ComponentsUpdated++;
        }
    }

    private static async Task EnsureNoComponentCycleAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        string parentCaseId, string childCaseId, string? excludedId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE descendants(case_id) AS (
                SELECT child_case_id FROM case_components
                WHERE parent_case_id=$child AND is_active=1 AND ($excluded IS NULL OR id<>$excluded)
                UNION
                SELECT component.child_case_id
                FROM case_components component
                JOIN descendants ON component.parent_case_id=descendants.case_id
                WHERE component.is_active=1 AND ($excluded IS NULL OR component.id<>$excluded)
            )
            SELECT EXISTS(SELECT 1 FROM descendants WHERE case_id=$parent);
            """;
        Add(command, "$child", childCaseId); Add(command, "$parent", parentCaseId);
        Add(command, "$excluded", excludedId);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1)
            throw new KitaronSyncDataException("The Kitaron component structure contains a circular relationship.");
    }

    private static async Task<IReadOnlyList<string>> FindComponentIdsAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        string parentCaseId, string childCaseId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id FROM case_components
            WHERE parent_case_id=$parent AND child_case_id=$child
            ORDER BY id;
            """;
        Add(command, "$parent", parentCaseId); Add(command, "$child", childCaseId);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<int> NextRoutePositionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string caseId, int preferred,
        CancellationToken cancellationToken)
    {
        var used = await ScalarIntAsync(connection, transaction,
            "SELECT COUNT(*) FROM case_operations WHERE case_id=$caseId AND route_position=$route;",
            "$caseId", caseId, cancellationToken, ("$route", preferred));
        if (used == 0) return preferred;
        return await ScalarIntAsync(connection, transaction,
            "SELECT COALESCE(MAX(route_position),-1)+1 FROM case_operations WHERE case_id=$caseId;",
            "$caseId", caseId, cancellationToken);
    }

    private static async Task<(string TargetId, bool OwnsTarget, string SourceHash)?> ReadLinkAsync(
        SqliteConnection connection, SqliteTransaction transaction, string entity, string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT target_id, owns_target, source_hash FROM kitaron_sync_links WHERE source_entity=$entity AND source_key=$key;";
        Add(command, "$entity", entity); Add(command, "$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetInt32(1) != 0, reader.GetString(2)) : null;
    }

    private static async Task DeleteLinkAsync(
        SqliteConnection connection, SqliteTransaction transaction, string entity, string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM kitaron_sync_links WHERE source_entity=$entity AND source_key=$key;";
        Add(command, "$entity", entity); Add(command, "$key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertLinkAsync(
        SqliteConnection connection, SqliteTransaction transaction, string entity, string key,
        string targetId, bool owns, string hash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO kitaron_sync_links (source_entity, source_key, target_id, owns_target,
                source_hash, first_seen_at, last_seen_at)
            VALUES ($entity, $key, $target, $owns, $hash, $now, $now)
            ON CONFLICT(source_entity, source_key) DO UPDATE SET
                target_id=excluded.target_id, owns_target=excluded.owns_target,
                source_hash=excluded.source_hash, last_seen_at=excluded.last_seen_at;
            """;
        Add(command, "$entity", entity); Add(command, "$key", key); Add(command, "$target", targetId);
        Add(command, "$owns", owns ? 1 : 0); Add(command, "$hash", hash); Add(command, "$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureTargetExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string table, string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE id=$id;";
        Add(command, "$id", id);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
            throw new KitaronSyncDataException($"A Kitaron link points to a missing {table} record.");
    }

    private static async Task<IReadOnlyList<string>> FindIdsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql, object key,
        CancellationToken cancellationToken, string? caseId = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction; command.CommandText = sql;
        Add(command, "$key", key); if (caseId is not null) Add(command, "$caseId", caseId);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<int> ScalarIntAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql,
        string name, object value, CancellationToken cancellationToken,
        params (string Name, object Value)[] extras)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        Add(command, name, value); foreach (var extra in extras) Add(command, extra.Name, extra.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string StableId(string prefix, string key)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return $"{prefix}-{hash[..24]}";
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static async Task<KitaronSyncStatus> ReadStatusAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT sync_status, message, last_started_at, last_completed_at, source_rows,
                cases_created, cases_updated, cases_matched, orders_created, orders_updated, orders_matched,
                operations_created, operations_updated, operations_matched,
                components_created, components_updated, components_matched,
                warning_count, mapping_version, version
            FROM kitaron_sync_state WHERE id=1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Kitaron sync state is missing.");
        static DateTimeOffset? Date(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), System.Globalization.CultureInfo.InvariantCulture);
        return new KitaronSyncStatus(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            Date(reader, 2), Date(reader, 3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
            reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
            reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15), reader.GetInt32(16),
            reader.GetInt32(17), reader.IsDBNull(18) ? null : reader.GetInt32(18), reader.GetInt32(19));
    }

    private sealed class MutableCounts
    {
        internal int CasesCreated, CasesUpdated, CasesMatched, OrdersCreated, OrdersUpdated, OrdersMatched;
        internal int OperationsCreated, OperationsUpdated, OperationsMatched, Warnings;
        internal int ComponentsCreated, ComponentsUpdated, ComponentsMatched;
    }
}
