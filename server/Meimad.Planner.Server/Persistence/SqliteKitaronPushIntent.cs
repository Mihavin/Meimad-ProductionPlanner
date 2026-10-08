using System.Text.Json;
using Meimad.Planner.Server.Application.Kitaron.Push;

namespace Meimad.Planner.Server.Persistence;

internal sealed partial class SqliteKitaronPushRepository
{
    public async Task PrepareIntentAsync(string runId, KitaronPushIntent intent, DateTimeOffset now, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE kitaron_push_runs SET lifecycle_state='Prepared', intent_version=intent_version+1 WHERE id=$id AND lifecycle_state='Preparing'";
        command.Parameters.AddWithValue("$id", runId);
        if (await command.ExecuteNonQueryAsync(token) != 1) throw Changed();
        command.CommandText = "INSERT INTO kitaron_push_intents(run_id,payload_json,prepared_at) VALUES($id,$payload,$at)";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(intent, JsonOptions));
        command.Parameters.AddWithValue("$at", Format(now));
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<bool> ClaimIntentAsync(string runId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE kitaron_push_runs SET lifecycle_state='Writing', intent_version=intent_version+1
            WHERE id=$id AND lifecycle_state='Prepared' AND EXISTS(SELECT 1 FROM kitaron_push_intents WHERE run_id=$id);
            """;
        command.Parameters.AddWithValue("$id", runId);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    // Called once at Server startup, never by an ordinary request against a live worker.
    public async Task RecoverInterruptedAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE kitaron_push_runs SET
                lifecycle_state=CASE WHEN lifecycle_state='Writing' THEN 'OutcomeUnknown' ELSE 'FailedBeforeCommit' END,
                status='failed', intent_version=intent_version+1,
                finished_at=COALESCE(finished_at,strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                message=CASE WHEN lifecycle_state='Writing'
                    THEN 'Server restarted during ERP write. Outcome unknown; inspect/reconcile before another push.'
                    ELSE 'Server restarted before external write was claimed. Nothing was written by this run.' END
            WHERE lifecycle_state IN ('Preparing','Prepared','Writing');
            """;
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<KitaronPushIntentResource> ReadIntentAsync(string runId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.lifecycle_state,r.intent_version,i.payload_json FROM kitaron_push_runs r
            LEFT JOIN kitaron_push_intents i ON i.run_id=r.id WHERE r.id=$id;
            """;
        command.Parameters.AddWithValue("$id", runId);
        string state;
        int version;
        KitaronPushIntent? intent;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) throw new KitaronPushBlockedException("Push run not found.");
            state = reader.GetString(0);
            version = reader.GetInt32(1);
            intent = reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<KitaronPushIntent>(reader.GetString(2), JsonOptions);
        }
        command.CommandText = "SELECT evidence_json FROM kitaron_push_reconciliations WHERE run_id=$id ORDER BY id";
        var evidence = new List<KitaronReconciliation>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                evidence.Add(JsonSerializer.Deserialize<KitaronReconciliation>(reader.GetString(0), JsonOptions)!);
        await transaction.CommitAsync(token);
        return new(runId, state, version, intent, evidence);
    }

    public Task RecordReconciliationAsync(string runId, int expectedVersion, KitaronReconciliation evidence, CancellationToken token) =>
        RecordEvidenceAsync(runId, expectedVersion, evidence, false, token);

    public Task AcknowledgeAsync(string runId, int expectedVersion, string actor, DateTimeOffset now, CancellationToken token) =>
        RecordEvidenceAsync(runId, expectedVersion, new(actor, now, [],
            "Outcome reviewed and acknowledged by " + actor + ". Historical commit attribution remains unknown. "
            + "Future pushes may calculate fresh changes; this intent was not replayed.", "Acknowledgement"), true, token);

    private async Task RecordEvidenceAsync(string runId, int version, KitaronReconciliation evidence, bool acknowledge, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE kitaron_push_runs SET intent_version=intent_version+1, lifecycle_state=$phase, message=$message
            WHERE id=$id AND intent_version=$version AND lifecycle_state='OutcomeUnknown'
                AND ($ack=0 OR EXISTS(SELECT 1 FROM kitaron_push_reconciliations WHERE run_id=$id));
            """;
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$ack", acknowledge ? 1 : 0);
        command.Parameters.AddWithValue("$phase", acknowledge ? "Reconciled" : "OutcomeUnknown");
        command.Parameters.AddWithValue("$message", evidence.Message);
        if (await command.ExecuteNonQueryAsync(token) != 1) throw Changed();
        command.CommandText = "INSERT INTO kitaron_push_reconciliations(run_id,actor,observed_at,evidence_json) VALUES($id,$actor,$at,$evidence)";
        command.Parameters.AddWithValue("$actor", evidence.Actor);
        command.Parameters.AddWithValue("$at", Format(evidence.ObservedAt));
        command.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(evidence, JsonOptions));
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    private static KitaronPushConflictException Changed() => new("The push state changed or needs reconciliation first. Refresh its run record.");
}
