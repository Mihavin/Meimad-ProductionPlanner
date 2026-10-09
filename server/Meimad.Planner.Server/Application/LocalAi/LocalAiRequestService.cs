using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;

namespace Meimad.Planner.Server.Application.LocalAi;

internal sealed record LocalAiRequestStatus(string RequestId, string Status, DateTimeOffset CreatedAt,
    LocalExplanation? Result = null, string? ResultCode = null);
internal sealed class LocalAiBusyException : Exception;

internal sealed partial class LocalAiRequestService(LocalEvidenceService evidence, ILocalModelClient model,
    LocalAiOptions options, SqliteDatabase database, TimeProvider clock, ILogger<LocalAiRequestService> logger,
    LocalAiRuntimeDiagnostics? runtime = null) : BackgroundService
{
    private sealed class Work(string id, string owner, ExplanationRequest request, DateTimeOffset at)
    {
        internal readonly string Owner = owner;
        internal readonly ExplanationRequest Request = request;
        internal readonly CancellationTokenSource Cancel = new();
        internal volatile LocalAiRequestStatus State = new(id, "QUEUED", at);
    }
    private readonly Channel<Work> queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(8)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<string, Work> requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> activeOwners = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim admission = new(1, 1);
    internal async Task<LocalAiRequestStatus> SubmitAsync(string owner, ExplanationRequest request, CancellationToken token)
    {
        LocalEvidenceService.Validate(request);
        await admission.WaitAsync(token);
        try
        {
            if (!activeOwners.TryAdd(owner, 0)) throw new LocalAiBusyException();
            var work = new Work(Guid.NewGuid().ToString("N"), owner, request with { Question = null }, clock.GetUtcNow());
            try
            {
                // Discard terminal answers first; durable metadata remains queryable after eviction/restart.
                foreach (var prior in requests.Values.Where(x => IsTerminal(x.State.Status)).OrderBy(x => x.State.CreatedAt)
                    .Take(Math.Max(0, requests.Count - 119)))
                    if (requests.TryRemove(prior.State.RequestId, out _)) prior.Cancel.Dispose();
                await SaveAsync(work, token);
                requests[work.State.RequestId] = work;
                if (!queue.Writer.TryWrite(work))
                {
                    work.State = work.State with { Status = "REJECTED", ResultCode = "queue_full" };
                    await SaveAsync(work, CancellationToken.None);
                    throw new LocalAiBusyException();
                }
                return work.State;
            }
            catch { activeOwners.TryRemove(owner, out _); throw; }
        }
        finally { admission.Release(); }
    }
    internal async Task<LocalAiRequestStatus?> ReadAsync(string owner, string id, CancellationToken token)
    {
        if (requests.TryGetValue(id, out var work))
        {
            if (work.Owner != owner) return null;
            var state = work.State;
            if (state.Result is { } answer && !await evidence.SourcesVisibleAsync(answer.Evidence, token))
                return state with { Status = "SOURCE_UNAVAILABLE", Result = null, ResultCode = "source_changed_or_revoked_refresh" };
            if (state.Result is { } sampled)
                return state with { Result = await evidence.CheckCurrencyAsync(sampled, token) };
            return state;
        }
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status,created_at,result_code FROM local_ai_requests WHERE id=$id AND owner_id=$owner";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$owner", owner);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(id, reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
            null, reader.IsDBNull(2) ? "answer_not_retained_refresh" : reader.GetString(2)) : null;
    }
    internal bool Cancel(string owner, string id)
    {
        if (!requests.TryGetValue(id, out var work) || work.Owner != owner) return false;
        if (!IsTerminal(work.State.Status)) work.Cancel.Cancel();
        return true;
    }
    public override async Task StartAsync(CancellationToken stoppingToken)
    {
        await ApplyStoredSettingsAsync(stoppingToken);
        suspended = (await ReadControlAsync(stoppingToken)).Suspended;
        if (suspended) runtime?.Suspend();
        await using (var connection = await database.OpenConnectionAsync(stoppingToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE local_ai_requests SET status='INTERRUPTED',result_code='server_restarted' WHERE status IN ('QUEUED','RUNNING')";
            await command.ExecuteNonQueryAsync(stoppingToken);
        }
        await base.StartAsync(stoppingToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, work.Cancel.Token);
            cancel.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 1, 120)));
            var token = cancel.Token;
            var runtimeAttempted = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (clock.GetUtcNow() - work.State.CreatedAt > TimeSpan.FromSeconds(Math.Clamp(options.MaxQueueWaitSeconds, 1, 60)))
                { work.State = work.State with { Status = "EXPIRED", ResultCode = "queue_wait_expired" }; continue; }
                work.State = work.State with { Status = "RUNNING" }; await SaveAsync(work, token);
                var bundle = await evidence.ReadAsync(work.Request, token);
                var result = new LocalExplanation(work.State.RequestId, "DETERMINISTIC", bundle, bundle.Claims, "Local model is disabled.");
                ModelResult? metadata = null;
                if (options.Enabled && !suspended)
                {
                    try
                    {
                        if (!await evidence.SourcesVisibleAsync(bundle, token)) throw new InvalidOperationException("Evidence source approval or content changed.");
                        runtimeAttempted = true; runtime?.Begin();
                        metadata = await model.CompleteAsync(bundle, token);
                        result = result with { Mode = "MODEL_ORGANIZED", Claims = ModelClaimValidator.Validate(metadata.Content, bundle), FallbackReason = null };
                        runtime?.End(true, "AVAILABLE");
                    }
                    catch (OperationCanceledException) when (!work.Cancel.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                    { runtime?.End(false, "TIMED_OUT"); result = result with { FallbackReason = "Local runtime timed out; deterministic evidence is shown." }; }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { if (runtimeAttempted) runtime?.End(false, "UNAVAILABLE_OR_REJECTED"); result = result with { FallbackReason = "Local runtime unavailable, uncommissioned or response rejected; deterministic evidence is shown." }; }
                }
                else if (suspended) result = result with { FallbackReason = "Local inference is suspended; deterministic evidence is shown." };
                work.Cancel.Token.ThrowIfCancellationRequested();
                var completed = work.State with { Status = "COMPLETED", Result = result, ResultCode = result.Mode };
                await SaveAsync(work, CancellationToken.None, metadata, completed);
                work.State = completed;
            }
            catch (OperationCanceledException)
            { if (runtimeAttempted) runtime?.End(false, "CANCELLED"); work.State = work.State with { Status = stoppingToken.IsCancellationRequested ? "INTERRUPTED" : "CANCELLED", ResultCode = "cancelled_or_timed_out" }; }
            catch (Exception)
            { work.State = work.State with { Status = "FAILED", ResultCode = "evidence_unavailable_refresh" }; }
            finally
            {
                try { await SaveAsync(work, CancellationToken.None); }
                catch (Exception) { logger.LogWarning("Local AI request metadata could not be saved for {RequestId}.", work.State.RequestId); }
                finally { activeOwners.TryRemove(work.Owner, out _); }
            }
        }
    }
    private static bool IsTerminal(string status) => status is not ("QUEUED" or "RUNNING");
    private async Task SaveAsync(Work work, CancellationToken token, ModelResult? metadata = null, LocalAiRequestStatus? snapshot = null)
    {
        var state = snapshot ?? work.State;
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_ai_requests(id,owner_id,intent,status,created_at,completed_at,result_code,plan_revision,evidence_json,
                model_id,model_digest,runtime_version,input_tokens,output_tokens,duration_nanoseconds)
            VALUES($id,$owner,$intent,$status,$created,$completed,$code,$revision,$evidence,$model,$digest,$runtime,$input,$output,$duration)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status,completed_at=excluded.completed_at,result_code=excluded.result_code,
                plan_revision=excluded.plan_revision,evidence_json=excluded.evidence_json,
                input_tokens=COALESCE(excluded.input_tokens,input_tokens),output_tokens=COALESCE(excluded.output_tokens,output_tokens),
                duration_nanoseconds=COALESCE(excluded.duration_nanoseconds,duration_nanoseconds);
            """;
        command.Parameters.AddWithValue("$id", state.RequestId); command.Parameters.AddWithValue("$owner", work.Owner);
        command.Parameters.AddWithValue("$intent", work.Request.Intent); command.Parameters.AddWithValue("$status", state.Status);
        command.Parameters.AddWithValue("$created", state.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$completed", IsTerminal(state.Status) ? clock.GetUtcNow().ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$code", (object?)state.ResultCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", (object?)state.Result?.Evidence.PlanRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$evidence", state.Result is { } answer
            ? JsonSerializer.Serialize(answer.Evidence.Facts.Select(x => new { x.EvidenceId, x.Source, x.SourceVersion })) : DBNull.Value);
        command.Parameters.AddWithValue("$model", options.Enabled ? options.ModelId : DBNull.Value);
        command.Parameters.AddWithValue("$digest", options.Enabled ? options.ModelDigest : DBNull.Value);
        command.Parameters.AddWithValue("$runtime", options.Enabled ? options.RuntimeVersion : DBNull.Value);
        command.Parameters.AddWithValue("$input", (object?)metadata?.InputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$output", (object?)metadata?.OutputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (object?)metadata?.DurationNanoseconds ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }
}
