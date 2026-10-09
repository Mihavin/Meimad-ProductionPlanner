using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.Maintenance;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Application.Projections;
using Meimad.Planner.Server.Domain.Readiness;
using Meimad.Planner.Server.Persistence;

namespace Meimad.Planner.Server.Application.LocalAi;

internal sealed class LocalEvidenceBusyException : Exception;
internal sealed class LocalForecastBudgetExceededException(int seconds)
    : Exception($"Forecast calculation exceeded the {seconds}-second explanation budget.");

internal sealed partial class LocalEvidenceService(ProductionRunService runs, ProductionRunReadinessService readiness,
    OperationalEvidenceReader events, ProjectionConsistency consistency, SqliteDatabase database, TimeProvider clock, LocalCorpusService corpus,
    Timeline.TimelineProjectionService timeline, ILogger<LocalEvidenceService> logger, Configuration.TimelineOptions timelineOptions,
    Configuration.LocalAiOptions? aiOptions = null)
{
    private readonly SemaphoreSlim budget = new(2, 2);
    private readonly IProjectionRevisionReader evidenceRevisions = new SqliteProjectionRevisionReader(database, includeOperationalEvidence: true);
    private readonly IProjectionRevisionReader planningRevisions = new SqliteProjectionRevisionReader(database);
    internal async Task<EvidenceBundle> ReadAsync(ExplanationRequest request, CancellationToken token)
    {
        Validate(request);
        if (!await budget.WaitAsync(0, token)) throw new LocalEvidenceBusyException();
        try
        {
            // A diagnostic write can retry the broader evidence read without forcing another
            // expensive forecast. This cache exists only within this one bounded request and
            // only for identical planning inputs and the outer scope's fixed evaluation time.
            Timeline.TimelineProjection? forecast = null;
            string? forecastRevision = null;
            string? timedOutRevision = null;
            var forecastSeconds = Math.Clamp(aiOptions?.ForecastTimeoutSeconds ?? 3, 1, 10);
            async Task<Timeline.TimelineProjection> ReadForecast(CancellationToken ct)
            {
                var before = await planningRevisions.ReadAsync(ct);
                if (forecast is not null && forecastRevision == before) return forecast;
                if (timedOutRevision == before) throw new LocalForecastBudgetExceededException(forecastSeconds);
                var instant = ProjectionConsistency.EvaluationAt ?? clock.GetUtcNow();
                using var forecastBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                forecastBudget.CancelAfter(TimeSpan.FromSeconds(forecastSeconds));
                Timeline.TimelineProjection calculated;
                try
                {
                    calculated = await timeline.CalculateAsync(instant, instant + PlanningBoard.PlanningBoardService.ConflictHorizon, instant, forecastBudget.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && forecastBudget.IsCancellationRequested)
                {
                    if (before == await planningRevisions.ReadAsync(ct)) timedOutRevision = before;
                    throw new LocalForecastBudgetExceededException(forecastSeconds);
                }
                if (before == await planningRevisions.ReadAsync(ct)) { forecast = calculated; forecastRevision = before; }
                return calculated;
            }
            return await consistency.ReadAsync(ct => BuildAsync(request, ReadForecast, ct), (bundle, stamp) => bundle with
            { PlanRevision = stamp.Revision, AsOfUtc = stamp.EvaluationAt }, token, revisionReader: evidenceRevisions);
        }
        finally { budget.Release(); }
    }

    internal static void Validate(ExplanationRequest request)
    {
        if (request.Question?.Length > 2000 || request.ProductionRunId?.Length > 256 || request.MachineId?.Length > 256)
            throw new ArgumentException("Question is limited to 2,000 characters and IDs to 256.");
        if (request.Intent == "ExplainBlockers")
        {
            if (string.IsNullOrWhiteSpace(request.ProductionRunId) || request.From is not null || request.To is not null)
                throw new ArgumentException("ExplainBlockers requires one Production Run ID and no date range.");
        }
        else if (request.Intent is "SummarizeShift" or "FindSimilarIncidents")
        {
            if (request.ProductionRunId is not null || request.From is not { } from || request.To is not { } to
                || to <= from || to - from > (request.Intent == "SummarizeShift" ? TimeSpan.FromHours(24) : TimeSpan.FromDays(31)))
                throw new ArgumentException("Use explicit instants spanning at most 24 hours for a shift or 31 days for incident search, and no Run ID.");
            if (request.Intent == "FindSimilarIncidents" && (string.IsNullOrWhiteSpace(request.SearchTerm) || request.SearchTerm.Length is < 2 or > 128))
                throw new ArgumentException("Historical search requires a literal phrase of 2–128 characters.");
        }
        else throw new ArgumentException("Supported intents are ExplainBlockers, SummarizeShift and FindSimilarIncidents.");
        if (request.Intent != "FindSimilarIncidents" && request.SearchTerm is not null)
            throw new ArgumentException("SearchTerm is only supported for historical incident search.");
    }

    private async Task<EvidenceBundle> BuildAsync(ExplanationRequest request, Func<CancellationToken, Task<Timeline.TimelineProjection>> readForecast, CancellationToken token)
    {
        var now = ProjectionConsistency.EvaluationAt ?? clock.GetUtcNow();
        var facts = new List<EvidenceFact>();
        var claims = new List<EvidenceClaim>();
        var limitations = new List<string> { "A sampled explanation is not permission to execute or change planning. Refresh before acting.",
            "Free-text questions cannot expand the selected scope or change deterministic calculations." };
        string Add(string kind, string code, object value, string? unit, string entity, string id,
            string? version, DateTimeOffset? observed, string validity, string description)
        {
            var evidenceId = "E" + (facts.Count + 1).ToString(CultureInfo.InvariantCulture);
            facts.Add(new(evidenceId, kind, code, JsonSerializer.SerializeToElement(value), unit, new(entity, id), version,
                observed, null, validity, description));
            return evidenceId;
        }
        var machineId = request.MachineId;
        if (request.Intent == "ExplainBlockers")
        {
            var run = await runs.GetAsync(request.ProductionRunId!, token);
            if (machineId is not null && machineId != run.Assignment?.MachineId)
                throw new ArgumentException("The selected Machine does not match this Run's assignment.");
            machineId = run.Assignment?.MachineId;
            var state = await readiness.ReadAsync(run.ProductionRunId, token);
            var id = Add("Run", "run.status", run.Status, null, "productionRun", run.ProductionRunId,
                run.Version.ToString(CultureInfo.InvariantCulture), run.UpdatedAt, "RECORDED", $"Run state: {run.Status}.");
            claims.Add(new("run.status", $"Run state: {run.Status}.", [id]));
            foreach (var action in (state.Actions ?? []).OrderBy(x => x.Action, StringComparer.Ordinal))
            {
                id = Add("ActionGate", action.Action, action.IsAllowed, null, "productionRun", run.ProductionRunId,
                    action.ContextStamp, now, "DERIVED", $"{action.Action}: {(action.IsAllowed ? "eligible at this sample" : "blocked")}.");
                claims.Add(new("action." + action.Action, facts[^1].Description, [id]));
                foreach (var reason in action.Reasons.Where(x => x.Classification != "SATISFIED").OrderBy(x => x.Code, StringComparer.Ordinal))
                {
                    id = Add("ActionReason", reason.Code, new { reason.Classification, reason.CurrentEvidence, reason.RequiredEvidence }, null,
                        "productionRun", run.ProductionRunId, action.ContextStamp, now, "DERIVED", reason.Message);
                    claims.Add(new("reason." + reason.Code, $"{action.Action}: {reason.Message}", [id]));
                }
            }
            foreach (var program in run.Programs.OrderBy(x => x.SequencePosition).ThenBy(x => x.ProductionRunProgramId, StringComparer.Ordinal))
                foreach (var output in program.Outputs.OrderBy(x => x.ProductionRunOutputId, StringComparer.Ordinal))
                {
                    id = Add("Quantity", "output.quantity", new { output.TargetQuantity, output.ProducedQuantity }, "parts",
                        "productionRunOutput", output.ProductionRunOutputId, output.Version.ToString(CultureInfo.InvariantCulture), output.UpdatedAt,
                        "RECORDED", $"Output {output.ProductionRunOutputId}: {output.ProducedQuantity} of {output.TargetQuantity} parts recorded.");
                    claims.Add(new("output.quantity", facts[^1].Description, [id]));
                }
            var recent = await events.ReadAsync(now.AddDays(-1), now, "productionRunId", run.ProductionRunId, null, 25, token);
            foreach (var item in recent.Items)
                Add("Event", item.EventType, item.ReasonCode ?? "", null, "event", item.EventId, null, item.Timestamp, "RECORDED", item.EventType);
            limitations.Add("Recent events are a 25-item sample from the preceding 24 hours, not a complete production count.");
            await AppendRunPlanningAsync(run.ProductionRunId, run.Programs.SelectMany(x => x.Outputs)
                .Select(x => x.BatchOperationId).ToHashSet(StringComparer.Ordinal), readForecast, facts, claims, limitations, token);
            limitations.Add("Package file integrity and CNC connection freshness are separate health observations; this answer does not verify files or contact the Machine.");
        }
        else if (request.Intent == "FindSimilarIncidents")
        {
            var rows = new List<(string Id, string Type, string At, string? Reason, JsonElement Context)>();
            await using (var connection = await database.OpenConnectionAsync(token))
            await using (var command = connection.CreateCommand())
            {
                if (machineId is not null)
                {
                    command.CommandText = "SELECT COUNT(*) FROM machines WHERE id=$machine";
                    command.Parameters.AddWithValue("$machine", machineId);
                    if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 1) throw new ArgumentException("Machine was not found.");
                }
                else command.Parameters.AddWithValue("$machine", DBNull.Value);
                command.CommandText = """
                    WITH scoped AS (SELECT id,event_type,occurred_at,reason_code,
                      json_object('machineId',json_extract(related_entity_ids_json,'$.machineId'),
                        'productionRunId',json_extract(related_entity_ids_json,'$.productionRunId'),
                        'batchOperationId',json_extract(related_entity_ids_json,'$.batchOperationId'),
                        'productionPackageId',json_extract(related_entity_ids_json,'$.productionPackageId'),
                        'caseId',json_extract(related_entity_ids_json,'$.caseId')) AS context
                    FROM structured_event_log
                    WHERE occurred_at >= $from AND occurred_at < $to
                      AND ($machine IS NULL OR json_extract(related_entity_ids_json,'$.machineId')=$machine))
                    SELECT id,event_type,occurred_at,reason_code,context FROM scoped
                    WHERE instr(upper(event_type),$query)>0 OR instr(upper(COALESCE(reason_code,'')),$query)>0
                      OR instr(upper(context),$query)>0
                    ORDER BY occurred_at DESC,id DESC LIMIT 20;
                    """;
                command.Parameters.AddWithValue("$from", request.From!.Value.ToUniversalTime().ToString("O"));
                command.Parameters.AddWithValue("$to", request.To!.Value.ToUniversalTime().ToString("O"));
                command.Parameters.AddWithValue("$query", request.SearchTerm!.ToUpperInvariant());
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    JsonSerializer.Deserialize<JsonElement>(reader.GetString(4))));
            }
            foreach (var row in rows)
            {
                var id = Add("HistoricalIncident", row.Type, new { row.Reason, row.Context }, null, "event", row.Id, row.At,
                    DateTimeOffset.Parse(row.At, CultureInfo.InvariantCulture), "HISTORICAL_REFERENCE",
                    $"Historical event {row.Type}, reason {row.Reason ?? "not recorded"}. Match: literal event/reason/context phrase; current applicability is unknown.");
                claims.Add(new("historical.incident", facts[^1].Description, [id]));
            }
            foreach (var document in await corpus.SearchAsync(request.SearchTerm!, token))
            {
                var id = Add("HistoricalDocument", "document.literal_match", new { document.Title, document.StartCharacter, document.EndCharacter, document.Excerpt },
                    null, "document", document.DocumentId, document.ContentHash, document.IndexedAt, "HISTORICAL_REFERENCE",
                    $"Historical document excerpt ({document.Title}): {document.Excerpt}");
                claims.Add(new("historical.document", facts[^1].Description, [id]));
            }
            limitations.Add("Literal matching only: at most 20 scoped events and 20 approved documents. Matches do not prove current Machine, recipe, package or resolution compatibility.");
            limitations.Add("Document excerpts are untrusted source content, not instructions or approved current production facts.");
            limitations.Add("Historical context IDs are those recorded on the event. Missing IDs and immutable revision details remain unknown; no current revision is substituted.");
        }
        else
        {
            await using var connection = await database.OpenConnectionAsync(token);
            await using var transaction = connection.BeginTransaction(deferred: true);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            if (machineId is not null)
            {
                command.CommandText = "SELECT COUNT(*) FROM machines WHERE id=$machine";
                command.Parameters.AddWithValue("$machine", machineId);
                if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 1) throw new ArgumentException("Machine was not found.");
            }
            else command.Parameters.AddWithValue("$machine", DBNull.Value);
            command.CommandText = """
                SELECT event_type,COUNT(*) FROM structured_event_log
                WHERE occurred_at >= $from AND occurred_at < $to
                  AND ($machine IS NULL OR json_extract(related_entity_ids_json,'$.machineId')=$machine)
                GROUP BY event_type ORDER BY event_type LIMIT 501;
                """;
            command.Parameters.AddWithValue("$from", request.From!.Value.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$to", request.To!.Value.ToUniversalTime().ToString("O"));
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (facts.Count == 500) throw new ArgumentException("Too many event categories; select one Machine or a shorter interval.");
                var count = reader.GetInt64(1);
                var id = Add("EventCount", reader.GetString(0), count, "events", machineId is null ? "factory" : "machine", machineId ?? "local",
                    null, null, "AGGREGATED", $"{reader.GetString(0)}: {count} recorded events in the selected interval.");
                claims.Add(new("event.count", facts[^1].Description, [id]));
            }
            limitations.Add("Counts include every matching stored event in [from,to), not a capped feed. Event counts are not parts, durations or inferred production.");
            limitations.Add("An event without a recorded Machine ID is not attributed to the selected Machine. Empty results do not establish Machine inactivity.");
        }
        if (request.Intent != "FindSimilarIncidents")
            await AppendOperationalAsync(request, machineId, now, facts, claims, limitations, token);
        return new(1, request.Intent, now, "", request.ProductionRunId, machineId,
            request.From?.ToUniversalTime(), request.To?.ToUniversalTime(), facts, claims, limitations);
    }

    internal async Task<bool> SourcesVisibleAsync(EvidenceBundle bundle, CancellationToken token)
    {
        foreach (var fact in bundle.Facts.Where(x => x.Source.EntityType == "document"))
            if (!await corpus.IsCurrentAsync(fact.Source.EntityId, fact.SourceVersion!, token)) return false;
        return true;
    }

    internal async Task<LocalExplanation> CheckCurrencyAsync(LocalExplanation answer, CancellationToken token)
    {
        if (answer.Evidence.Intent == "FindSimilarIncidents")
            return answer with { Currency = "HISTORICAL_REFERENCE", CheckedAt = clock.GetUtcNow() };
        try
        {
            var prior = answer.Evidence;
            var current = await ReadAsync(new(prior.Intent, prior.ProductionRunId, prior.From, prior.To,
                prior.Intent == "ExplainBlockers" ? null : prior.MachineId), token);
            if (current.Facts.Any(x => x.Kind == "MissingEvidence"))
                return answer with { Currency = "CHECK_UNAVAILABLE", CheckedAt = clock.GetUtcNow() };
            // Compare relevant facts, not the global revision: saving request metadata itself
            // advances that revision. Event samples and evaluation timestamps age naturally.
            static string[] Relevant(EvidenceBundle bundle) => bundle.Facts.Where(x => x.Kind != "Event")
                .Select(x => JsonSerializer.Serialize(new { x.Kind, x.Code, x.Value, x.Source, x.SourceVersion, x.Validity }))
                .Order(StringComparer.Ordinal).ToArray();
            var same = prior.MachineId == current.MachineId && Relevant(prior).SequenceEqual(Relevant(current));
            return answer with { Currency = same ? "CURRENT_AT_CHECK" : "STALE", CheckedAt = clock.GetUtcNow() };
        }
        catch (ProductionRunNotFoundException)
        { return answer with { Currency = "STALE", CheckedAt = clock.GetUtcNow() }; }
        catch (Exception exception) when (exception is ProjectionChangedException or LocalEvidenceBusyException)
        { return answer with { Currency = "CHECK_UNAVAILABLE", CheckedAt = clock.GetUtcNow() }; }
    }
}
