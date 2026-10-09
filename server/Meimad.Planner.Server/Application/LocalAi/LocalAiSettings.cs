using System.Text.Json;
using Meimad.Planner.Server.Configuration;
using Microsoft.Data.Sqlite;
namespace Meimad.Planner.Server.Application.LocalAi;

/// <summary>
/// The Server-held local AI setting: whether inference is enabled and which installed model and
/// runtime are pinned. It overrides the <c>LocalAi</c> section of appsettings.json (which keeps the
/// loopback <see cref="BaseUrl"/> and the token budgets) and takes effect without a Server restart.
/// The resource limits keep the model from starving the Server PC: <see cref="CpuThreads"/> caps the
/// threads a request may use, <see cref="KeepAliveSeconds"/> is how long the model stays in memory
/// after a request, and <see cref="MaxModelMemoryMb"/> refuses a model file larger than the budget.
/// </summary>
internal sealed record LocalAiSettings(int Version, bool Enabled, string ModelId, string ModelDigest, string RuntimeVersion,
    string BaseUrl, DateTimeOffset? UpdatedAt, string? UpdatedBy, int CpuThreads, int KeepAliveSeconds, int MaxModelMemoryMb, int ProcessorCount);
internal sealed record LocalAiSettingsUpdate(int ExpectedVersion, bool Enabled, string? ModelId, string? ModelDigest, string? RuntimeVersion,
    int CpuThreads = 0, int KeepAliveSeconds = 300, int MaxModelMemoryMb = 0);
internal sealed record LocalAiInstalledModel(string Name, string Digest, bool Remote, long SizeMb = 0);
/// <summary>What the local runtime on the Server PC answered to a read-only probe; never a download.</summary>
internal sealed record LocalAiRuntimeProbe(bool Reachable, string BaseUrl, string? RuntimeVersion,
    IReadOnlyList<LocalAiInstalledModel> Models, string Detail);

internal sealed partial class LocalAiRequestService
{
    private const string SettingsKey = "local_ai_settings";
    private sealed record StoredSettings(int Version, bool Enabled, string ModelId, string ModelDigest, string RuntimeVersion,
        DateTimeOffset? UpdatedAt, string? UpdatedBy, int CpuThreads = 0, int KeepAliveSeconds = 300, int MaxModelMemoryMb = 0);

    internal async Task<LocalAiSettings> ReadSettingsAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        return View(await ReadStoredSettingsAsync(connection, null, token));
    }

    internal Task<LocalAiRuntimeProbe> ProbeRuntimeAsync(CancellationToken token) => model.ProbeAsync(token);

    internal async Task<LocalAiSettings> UpdateSettingsAsync(LocalAiSettingsUpdate update, string actor, CancellationToken token)
    {
        var modelId = update.ModelId?.Trim() ?? "";
        var digest = (update.ModelDigest?.Trim() ?? "").Replace("sha256:", "", StringComparison.OrdinalIgnoreCase);
        var runtimeVersion = update.RuntimeVersion?.Trim() ?? "";
        // Enabling needs the complete pins; the loopback root and token budgets stay in appsettings.json.
        var configured = Configured(update.Enabled, modelId, digest, runtimeVersion, update);
        if (update.Enabled) configured.Validate();
        else if (modelId.Length > 128 || digest.Length > 64 || runtimeVersion.Length > 64)
            throw new ArgumentException("Model ID, digest and runtime version are limited to 128, 64 and 64 characters.");
        if (update.CpuThreads < 0 || update.CpuThreads > 256 || update.KeepAliveSeconds < 0 || update.KeepAliveSeconds > 86400
            || update.MaxModelMemoryMb < 0 || update.MaxModelMemoryMb > 4_194_304)
            throw new ArgumentException("CPU threads (0-256), keep-alive seconds (0-86400) and the model memory budget in MB (0 = no limit) are out of range.");
        if (update.CpuThreads >= Environment.ProcessorCount && Environment.ProcessorCount > 1)
            throw new ArgumentException($"Leave at least one of the {Environment.ProcessorCount} processor cores to the Server: at most {Environment.ProcessorCount - 1} CPU threads.");
        if (update.Enabled && update.MaxModelMemoryMb > 0)
        {
            // The installed model's size is known only to the runtime; when it answers, a model over
            // the budget cannot be pinned. When it does not, every request still checks the size.
            var probe = await model.ProbeAsync(token);
            var pinned = probe.Models.FirstOrDefault(installed => installed.Name == modelId);
            if (probe.Reachable && pinned is not null && pinned.SizeMb > update.MaxModelMemoryMb)
                throw new ArgumentException($"Model {modelId} is {pinned.SizeMb} MB, over the {update.MaxModelMemoryMb} MB memory budget. Choose a smaller model or raise the budget.");
        }
        await admission.WaitAsync(token);
        try
        {
            StoredSettings next;
            await using (var connection = await database.OpenConnectionAsync(token))
            await using (var tx = connection.BeginTransaction(deferred: false))
            {
                var prior = await ReadStoredSettingsAsync(connection, tx, token);
                if (prior.Version != update.ExpectedVersion) throw new InvalidOperationException("AI settings changed. Refresh and review their current state.");
                next = new StoredSettings(prior.Version + 1, update.Enabled, modelId, digest, runtimeVersion, clock.GetUtcNow(), actor,
                    update.CpuThreads, update.KeepAliveSeconds, update.MaxModelMemoryMb);
                await using var command = connection.CreateCommand(); command.Transaction = tx;
                command.CommandText = "INSERT INTO application_settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
                command.Parameters.AddWithValue("$key", SettingsKey); command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(next));
                await command.ExecuteNonQueryAsync(token);
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO structured_event_log(id,event_type,occurred_at,user_id,related_entity_ids_json,after_data_json,reason_code) VALUES($id,'ai_settings_changed',$at,$actor,'{}',$after,$reason)";
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$at", next.UpdatedAt!.Value.ToString("O"));
                command.Parameters.AddWithValue("$actor", actor);
                command.Parameters.AddWithValue("$after", JsonSerializer.Serialize(new { next.Version, next.Enabled, next.ModelId, next.ModelDigest, next.RuntimeVersion,
                    next.CpuThreads, next.KeepAliveSeconds, next.MaxModelMemoryMb }));
                command.Parameters.AddWithValue("$reason", next.Enabled ? "ai_enabled" : "ai_disabled");
                await command.ExecuteNonQueryAsync(token); await tx.CommitAsync(token);
            }
            Apply(next);
            return View(next);
        }
        finally { admission.Release(); }
    }

    /// <summary>Puts the stored setting into the live options at startup; without a row appsettings.json stays in force.</summary>
    private async Task ApplyStoredSettingsAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        var stored = await ReadStoredSettingsAsync(connection, null, token);
        if (stored.Version > 0) Apply(stored);
    }

    private void Apply(StoredSettings stored)
    {
        options.Enabled = stored.Enabled;
        options.ModelId = stored.ModelId;
        options.ModelDigest = stored.ModelDigest;
        options.RuntimeVersion = stored.RuntimeVersion;
        options.CpuThreads = stored.CpuThreads;
        options.KeepAliveSeconds = stored.KeepAliveSeconds;
        options.MaxModelMemoryMb = stored.MaxModelMemoryMb;
        runtime?.Reconfigure();
    }

    private LocalAiOptions Configured(bool enabled, string modelId, string digest, string runtimeVersion, LocalAiSettingsUpdate limits) => new()
    {
        Enabled = enabled, BaseUrl = options.BaseUrl, ModelId = modelId, ModelDigest = digest, RuntimeVersion = runtimeVersion,
        RequestTimeoutSeconds = options.RequestTimeoutSeconds, MaxQueueWaitSeconds = options.MaxQueueWaitSeconds,
        MaxInputTokens = options.MaxInputTokens, MaxOutputTokens = options.MaxOutputTokens, ForecastTimeoutSeconds = options.ForecastTimeoutSeconds,
        CpuThreads = limits.CpuThreads, KeepAliveSeconds = limits.KeepAliveSeconds, MaxModelMemoryMb = limits.MaxModelMemoryMb
    };

    private LocalAiSettings View(StoredSettings stored) => new(stored.Version, stored.Enabled, stored.ModelId, stored.ModelDigest,
        stored.RuntimeVersion, options.BaseUrl, stored.UpdatedAt, stored.UpdatedBy, stored.CpuThreads, stored.KeepAliveSeconds, stored.MaxModelMemoryMb,
        Environment.ProcessorCount);

    private async Task<StoredSettings> ReadStoredSettingsAsync(SqliteConnection connection, SqliteTransaction? tx, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT value FROM application_settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", SettingsKey);
        var json = (string?)await command.ExecuteScalarAsync(token);
        return json is null
            ? new StoredSettings(0, options.Enabled, options.ModelId, options.ModelDigest, options.RuntimeVersion, null, null,
                options.CpuThreads, options.KeepAliveSeconds, options.MaxModelMemoryMb)
            : JsonSerializer.Deserialize<StoredSettings>(json)!;
    }
}
