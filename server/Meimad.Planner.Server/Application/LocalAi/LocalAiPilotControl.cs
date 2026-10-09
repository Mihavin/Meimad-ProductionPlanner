using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Meimad.Planner.Server.Application.LocalAi;

internal sealed record LocalAiControl(int Version, bool Suspended, bool ConfiguredEnabled);
internal sealed record LocalAiControlUpdate(int ExpectedVersion, bool Suspended);
internal sealed record LocalAiFeedback(int Version, bool Useful, string Correction, DateTimeOffset UpdatedAt);
internal sealed record LocalAiFeedbackUpdate(int ExpectedVersion, bool Useful, string? Correction = null);

internal sealed partial class LocalAiRequestService
{
    private volatile bool suspended;
    /// <summary>Optional model work (chat) may run: AI is enabled and an administrator has not suspended it.</summary>
    internal bool InferenceAvailable => options.Enabled && !suspended;
    private const string ControlKey = "local_ai_pilot_control";
    internal async Task<LocalAiControl> ReadControlAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        return await ReadControlAsync(connection, null, token);
    }
    private async Task<LocalAiControl> ReadControlAsync(SqliteConnection connection, SqliteTransaction? tx, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT value FROM application_settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", ControlKey);
        var json = (string?)await command.ExecuteScalarAsync(token);
        var control = json is null ? new LocalAiControl(0, false, options.Enabled) : JsonSerializer.Deserialize<LocalAiControl>(json)!;
        return control with { ConfiguredEnabled = options.Enabled };
    }
    internal async Task<LocalAiControl> UpdateControlAsync(LocalAiControlUpdate update, string actor, CancellationToken token)
    {
        if (!update.Suspended)
        {
            if (!options.Enabled) throw new ArgumentException("Local inference is disabled in Server configuration. Commission the host before enabling it.");
            options.Validate();
        }
        await admission.WaitAsync(token);
        try
        {
            await using var connection = await database.OpenConnectionAsync(token);
            await using var tx = connection.BeginTransaction(deferred: false);
            var prior = await ReadControlAsync(connection, tx, token);
            if (prior.Version != update.ExpectedVersion) throw new InvalidOperationException("AI control changed. Refresh and review its current state.");
            var next = new LocalAiControl(prior.Version + 1, update.Suspended, options.Enabled);
            await using var command = connection.CreateCommand(); command.Transaction = tx;
            command.CommandText = "INSERT INTO application_settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$key", ControlKey); command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(next));
            await command.ExecuteNonQueryAsync(token);
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO structured_event_log(id,event_type,occurred_at,user_id,related_entity_ids_json,reason_code) VALUES($id,'ai_control_changed',$at,$actor,'{}',$reason)";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$at", clock.GetUtcNow().ToString("O"));
            command.Parameters.AddWithValue("$actor", actor); command.Parameters.AddWithValue("$reason", update.Suspended ? "inference_suspended" : "inference_resumed");
            await command.ExecuteNonQueryAsync(token); await tx.CommitAsync(token);
            suspended = next.Suspended;
            if (suspended)
            {
                foreach (var work in requests.Values.Where(x => !IsTerminal(x.State.Status))) work.Cancel.Cancel();
                runtime?.Suspend();
            }
            else runtime?.Resume();
            return next;
        }
        finally { admission.Release(); }
    }
    internal async Task<LocalAiFeedback?> ReadFeedbackAsync(string owner, string id, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT f.version,f.useful,f.correction,f.updated_at FROM local_ai_feedback f JOIN local_ai_requests r ON r.id=f.request_id WHERE r.id=$id AND r.owner_id=$owner";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$owner", owner);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetInt32(0), reader.GetBoolean(1), reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture)) : null;
    }
    internal async Task<LocalAiFeedback?> SaveFeedbackAsync(string owner, string id, LocalAiFeedbackUpdate update, CancellationToken token)
    {
        if (update.ExpectedVersion < 0 || update.Correction?.Length > 500) throw new ArgumentException("Feedback requires a nonnegative version and at most 500 correction characters.");
        await using var connection = await database.OpenConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT COUNT(*) FROM local_ai_requests WHERE id=$id AND owner_id=$owner AND status='COMPLETED'";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$owner", owner);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 1) return null;
        command.CommandText = "SELECT version FROM local_ai_feedback WHERE request_id=$id";
        var prior = Convert.ToInt32(await command.ExecuteScalarAsync(token));
        if (prior != update.ExpectedVersion) throw new InvalidOperationException("Feedback changed. Refresh before replacing it.");
        var saved = new LocalAiFeedback(prior + 1, update.Useful, update.Correction?.Trim() ?? "", clock.GetUtcNow());
        command.CommandText = """
            INSERT INTO local_ai_feedback VALUES($id,$useful,$correction,$version,$at)
            ON CONFLICT(request_id) DO UPDATE SET useful=excluded.useful,correction=excluded.correction,version=excluded.version,updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$useful", saved.Useful); command.Parameters.AddWithValue("$correction", saved.Correction);
        command.Parameters.AddWithValue("$version", saved.Version); command.Parameters.AddWithValue("$at", saved.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(token); await tx.CommitAsync(token); return saved;
    }
}
