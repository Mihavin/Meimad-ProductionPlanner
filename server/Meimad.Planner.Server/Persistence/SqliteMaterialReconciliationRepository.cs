using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Materials;
using Meimad.Planner.Server.Domain.Materials;
using Meimad.Planner.Server.Domain.Readiness;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteMaterialReconciliationRepository(SqliteDatabase database)
    : IMaterialReconciliationRepository
{
    public async Task<IReadOnlyList<VerifiedMaterialReceipt>> ListReceiptsAsync(
        string caseId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadReceiptsAsync(connection, null, caseId, cancellationToken);
    }

    public async Task<VerifiedMaterialReceipt> CreateReceiptAsync(
        CreateVerifiedMaterialReceiptCommand value,
        DateTimeOffset verifiedAt,
        EditAuthority authority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, authority, cancellationToken);
        if (!await ExistsAsync(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM cases WHERE id = $id);", value.CaseId!, cancellationToken))
            throw new MaterialReceiptCaseNotFoundException(value.CaseId!);
        var beforeReadiness = await ReadReadinessAsync(
            connection, transaction, value.CaseId!, null, cancellationToken);

        var id = Guid.NewGuid().ToString("N");
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO verified_material_receipts (
                    id, case_id, quantity, unit, received_at, verified_at, verified_by,
                    external_reference, comment, source, version, created_at, updated_at)
                VALUES (
                    $id, $caseId, $quantity, 'piece', $receivedAt, $verifiedAt, $verifiedBy,
                    $reference, $comment, 'LOCAL_VERIFIED', 1, $verifiedAt, $verifiedAt);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$caseId", value.CaseId!);
            command.Parameters.AddWithValue("$quantity", value.Quantity);
            command.Parameters.AddWithValue("$receivedAt", Iso(value.ReceivedAt));
            command.Parameters.AddWithValue("$verifiedAt", Iso(verifiedAt));
            command.Parameters.AddWithValue("$verifiedBy", actor);
            command.Parameters.AddWithValue("$reference", (object?)value.ExternalReference ?? DBNull.Value);
            command.Parameters.AddWithValue("$comment", (object?)value.Comment ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await SqliteStructuredEventLogRepository.AppendAsync(
            connection, transaction,
            new("verified_material_receipt_recorded", verifiedAt, actor,
                new Dictionary<string, string>
                {
                    ["materialReceiptId"] = id,
                    ["caseId"] = value.CaseId!
                },
                "local_physical_verification", value.Comment, null,
                new { quantity = value.Quantity, unit = "piece", value.ReceivedAt, value.ExternalReference }),
            cancellationToken);
        await AppendReadinessChangesAsync(
            connection, transaction, beforeReadiness,
            await ReadReadinessAsync(connection, transaction, value.CaseId!, null, cancellationToken),
            verifiedAt, actor, "verified_material_receipt_recorded", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(id, value.CaseId!, value.Quantity, value.ReceivedAt, verifiedAt, actor,
            value.ExternalReference, value.Comment, 0, value.Quantity);
    }

    public async Task<BatchMaterialReconciliation?> ReadBatchAsync(
        string batchId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var result = await ReadBatchAsync(connection, transaction, batchId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<BatchMaterialReconciliation?> ReplaceReservationsAsync(
        string batchId,
        IReadOnlyList<MaterialReservationValue> reservations,
        DateTimeOffset now,
        EditAuthority authority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, authority, cancellationToken);
        var before = await ReadBatchAsync(connection, transaction, batchId, cancellationToken);
        if (before is null) return null;
        var beforeReadiness = await ReadReadinessAsync(
            connection, transaction, before.CaseId, batchId, cancellationToken);
        if (reservations.Sum(item => (long)item.Quantity) > before.PlannedQuantity)
            throw new MaterialReconciliationValidationException(
                "reservations", "batch_quantity_exceeded",
                $"Reserved material cannot exceed Batch planned quantity {before.PlannedQuantity}.");

        foreach (var item in reservations)
        {
            var receipt = before.Receipts.FirstOrDefault(value => value.ReceiptId == item.ReceiptId);
            if (receipt is null)
                throw new MaterialReconciliationValidationException(
                    "reservations.receiptId", "receipt_case_mismatch",
                    "Every selected receipt must belong to the Production Batch Case.");
            var existingForBatch = before.Reservations
                .Where(value => value.ReceiptId == item.ReceiptId)
                .Sum(value => value.Quantity);
            if (item.Quantity > receipt.AvailableQuantity + existingForBatch)
                throw new MaterialReconciliationValidationException(
                    "reservations.quantity", "verified_quantity_exceeded",
                    $"Receipt '{item.ReceiptId}' has only {receipt.AvailableQuantity + existingForBatch} piece(s) available to this Batch.");
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM batch_material_reservations WHERE production_batch_id = $batchId;";
            delete.Parameters.AddWithValue("$batchId", batchId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in reservations)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO batch_material_reservations (
                    id, receipt_id, production_batch_id, quantity, reserved_at, reserved_by,
                    comment, version, created_at, updated_at)
                VALUES ($id, $receiptId, $batchId, $quantity, $at, $by,
                        $comment, 1, $at, $at);
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$receiptId", item.ReceiptId!);
            insert.Parameters.AddWithValue("$batchId", batchId);
            insert.Parameters.AddWithValue("$quantity", item.Quantity);
            insert.Parameters.AddWithValue("$at", Iso(now));
            insert.Parameters.AddWithValue("$by", actor);
            insert.Parameters.AddWithValue("$comment", (object?)item.Comment ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var after = await ReadBatchAsync(connection, transaction, batchId, cancellationToken)
            ?? throw new InvalidOperationException("Production Batch disappeared during material reconciliation.");
        await SqliteStructuredEventLogRepository.AppendAsync(
            connection, transaction,
            new("batch_material_reconciled", now, actor,
                new Dictionary<string, string>
                {
                    ["productionBatchId"] = batchId,
                    ["caseId"] = after.CaseId
                },
                "explicit_material_reservation", null,
                new { before.ReservedQuantity, before.State },
                new { after.ReservedQuantity, after.State, after.ShortageQuantity }),
            cancellationToken);
        await AppendReadinessChangesAsync(
            connection, transaction, beforeReadiness,
            await ReadReadinessAsync(connection, transaction, after.CaseId, batchId, cancellationToken),
            now, actor, "material_reconciliation_changed", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return after;
    }

    private static async Task<BatchMaterialReconciliation?> ReadBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        CancellationToken token)
    {
        string caseId;
        string batchNumber;
        int planned;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT case_id, batch_number, planned_quantity FROM production_batches WHERE id = $id;";
            command.Parameters.AddWithValue("$id", batchId);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            caseId = reader.GetString(0);
            batchNumber = reader.GetString(1);
            planned = reader.GetInt32(2);
        }

        var receipts = await ReadReceiptsAsync(connection, transaction, caseId, token);
        var reservations = new List<BatchMaterialReservation>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, receipt_id, production_batch_id, quantity,
                       reserved_at, reserved_by, comment
                FROM batch_material_reservations
                WHERE production_batch_id = $batchId
                ORDER BY reserved_at, id;
                """;
            command.Parameters.AddWithValue("$batchId", batchId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                reservations.Add(new(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                    Parse(reader.GetString(4)), reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        var reserved = reservations.Sum(value => value.Quantity);
        var totalVerified = receipts.Sum(value => value.Quantity);
        var reservedGlobally = receipts.Sum(value => value.ReservedQuantity);
        var availableToBatch = totalVerified - (reservedGlobally - reserved);
        var shortage = Math.Max(planned - availableToBatch, 0);
        string state;
        string message;
        if (reserved >= planned)
        {
            state = MaterialReconciliationStates.Ready;
            message = $"{reserved} of {planned} verified material piece(s) are reserved for this Production Batch.";
        }
        else if (shortage > 0)
        {
            state = MaterialReconciliationStates.Missing;
            message = $"Production Batch requires {planned} material piece(s); {availableToBatch} verified piece(s) are available to it. Shortage: {shortage}.";
        }
        else
        {
            state = MaterialReconciliationStates.Unverified;
            message = $"Production Batch requires {planned} material piece(s); {availableToBatch} verified piece(s) are available, but only {reserved} are explicitly reserved.";
        }
        return new(batchId, caseId, batchNumber, planned, reserved, availableToBatch,
            shortage, state, message, receipts, reservations);
    }

    private static async Task<IReadOnlyList<VerifiedMaterialReceipt>> ReadReceiptsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string caseId,
        CancellationToken token)
    {
        var values = new List<VerifiedMaterialReceipt>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT receipt.id, receipt.case_id, receipt.quantity,
                   receipt.received_at, receipt.verified_at, receipt.verified_by,
                   receipt.external_reference, receipt.comment,
                   COALESCE(SUM(reservation.quantity), 0)
            FROM verified_material_receipts receipt
            LEFT JOIN batch_material_reservations reservation
              ON reservation.receipt_id = receipt.id
            WHERE receipt.case_id = $caseId
            GROUP BY receipt.id
            ORDER BY receipt.received_at, receipt.id;
            """;
        command.Parameters.AddWithValue("$caseId", caseId);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var quantity = reader.GetInt32(2);
            var reserved = reader.GetInt32(8);
            values.Add(new(
                reader.GetString(0), reader.GetString(1), quantity,
                Parse(reader.GetString(3)), Parse(reader.GetString(4)), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reserved, quantity - reserved));
        }
        return values;
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string id,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<IReadOnlyDictionary<string, ReadinessSnapshot>> ReadReadinessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        string? batchId,
        CancellationToken token)
    {
        var operationIds = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT operation.id
                FROM batch_operations operation
                JOIN production_batches batch ON batch.id = operation.production_batch_id
                WHERE batch.case_id = $caseId
                  AND ($batchId IS NULL OR batch.id = $batchId)
                ORDER BY operation.id;
                """;
            command.Parameters.AddWithValue("$caseId", caseId);
            command.Parameters.AddWithValue("$batchId", (object?)batchId ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) operationIds.Add(reader.GetString(0));
        }

        var values = new Dictionary<string, ReadinessSnapshot>(StringComparer.Ordinal);
        foreach (var operationId in operationIds)
        {
            var context = await SqliteProductionReadinessContextReader.ReadAsync(
                connection, transaction, operationId, token);
            if (context is not null)
                values[operationId] = new(context, ProductionReadinessEvaluator.Evaluate(context));
        }
        return values;
    }

    private static async Task AppendReadinessChangesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyDictionary<string, ReadinessSnapshot> before,
        IReadOnlyDictionary<string, ReadinessSnapshot> after,
        DateTimeOffset now,
        string actor,
        string reason,
        CancellationToken token)
    {
        foreach (var pair in after)
        {
            before.TryGetValue(pair.Key, out var prior);
            await SqliteReadinessAudit.AppendEvaluationAsync(
                connection, transaction, pair.Value.Context, prior?.Result, pair.Value.Result,
                now, actor, reason, token);
        }
    }

    private static async Task<string> EnsureEditAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority authority,
        CancellationToken token)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(
            connection, transaction, DateTimeOffset.UtcNow, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, holder_user_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0))
            throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        if (!string.Equals(reader.GetString(0), authority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(2) != authority.Generation)
            throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
        return reader.IsDBNull(1) ? authority.ClientId : reader.GetString(1);
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private sealed record ReadinessSnapshot(
        ProductionReadinessContext Context,
        ProductionReadinessResult Result);
}
