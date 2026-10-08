using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Domain.ProductionRuns;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteProductionRunExecutionRepository(
    SqliteDatabase database, TimeProvider timeProvider, IProductionRunRepository runs)
    : IProductionRunExecutionRepository
{
    public Task<ProductionRun> StartAsync(string runId, int expectedVersion, EditAuthority authority, CancellationToken token, string? readinessStamp = null) =>
        ChangeRunAsync(runId, expectedVersion, ["PLANNED", "DRAFT"], "IN_PROGRESS", "production_run_started",
            authority, null, async (c, t, now) =>
            {
                await SqliteProductionRunReadinessRepository.EnsureAsync(c, t, runId, token, readinessStamp);
                await using var pin = c.CreateCommand(); pin.Transaction = t;
                pin.CommandText = """
                    UPDATE production_run_programs
                    SET selected_gcode_release_id=(
                        SELECT CASE WHEN COUNT(*)=1 THEN MIN(release.id) END FROM gcode_releases release
                        JOIN machine_assignments assignment ON assignment.production_run_id=$id AND assignment.released_at IS NULL
                        JOIN machine_supported_postprocessors supported ON supported.machine_id=assignment.machine_id
                            AND supported.postprocessor_id=release.postprocessor_id
                        JOIN machines machine ON machine.id=assignment.machine_id AND machine.execution_mode='CNC_GCODE'
                        WHERE release.process_revision_id=production_run_programs.process_revision_id
                            AND NOT EXISTS (SELECT 1 FROM gcode_releases newer
                                WHERE newer.process_revision_id=release.process_revision_id
                                  AND newer.postprocessor_id=release.postprocessor_id
                                  AND newer.post_specific_revision>release.post_specific_revision))
                    WHERE production_run_id=$id AND selected_gcode_release_id IS NULL;
                    UPDATE production_run_programs
                    SET production_process_revision_id=process_revision_id,
                        production_gcode_release_id=selected_gcode_release_id,
                        production_tool_table_release_id=(SELECT tool_table_release_id FROM process_revisions WHERE id=process_revision_id),
                        production_gcode_file_hash=(SELECT file_hash FROM gcode_releases WHERE id=selected_gcode_release_id),
                        production_tool_table_file_hash=(SELECT tool.file_hash FROM process_revisions revision JOIN tool_table_releases tool ON tool.id=revision.tool_table_release_id WHERE revision.id=process_revision_id),
                        version=version+1,updated_at=$at
                    WHERE production_run_id=$id;
                    """;
                pin.Parameters.AddWithValue("$id", runId); pin.Parameters.AddWithValue("$at", Format(now));
                await pin.ExecuteNonQueryAsync(token);
            }, token);

    public async Task<ProductionRun> ActivateProgramAsync(string runId, string programId, int expectedVersion, EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAsync(connection, transaction, authority, token);
        await EnsureRunAsync(connection, transaction, runId, expectedVersion, ["IN_PROGRESS"], token);
        var now = timeProvider.GetUtcNow();
        await using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = """
            UPDATE production_run_programs SET status='ACTIVE',version=version+1,updated_at=$at
            WHERE id=$program AND production_run_id=$run AND status IN ('PLANNED','SUSPENDED');
            """;
        update.Parameters.AddWithValue("$program", programId); update.Parameters.AddWithValue("$run", runId); update.Parameters.AddWithValue("$at", Format(now));
        if (await update.ExecuteNonQueryAsync(token) != 1)
            throw new ProductionRunStateException("program_not_executable", "The selected program is not available for activation.");
        await TouchRunAsync(connection, transaction, runId, now, token);
        await AuditAsync(connection, transaction, "production_run_program_activated", actor, runId, programId, null, now, token);
        await transaction.CommitAsync(token); return (await runs.GetAsync(runId, token))!;
    }

    public async Task<ProductionRunCycleResult> RecordCycleAsync(string runId, string programId, int expectedVersion,
        RecordProductionRunCycleCommand command, EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAsync(connection, transaction, authority, token);
        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = "SELECT completed_cycle_count, production_run_id, production_run_program_id, observed_at FROM production_run_cycle_events WHERE source=$source AND source_event_id=$event;";
            duplicate.Parameters.AddWithValue("$source", command.Source); duplicate.Parameters.AddWithValue("$event", command.SourceEventId);
            await using var prior = await duplicate.ExecuteReaderAsync(token);
            if (await prior.ReadAsync(token))
            {
                if (!string.Equals(prior.GetString(1), runId, StringComparison.Ordinal)
                    || !string.Equals(prior.GetString(2), programId, StringComparison.Ordinal)
                    || DateTimeOffset.Parse(prior.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) != command.ObservedAt)
                    throw new ProductionRunStateException("cycle_event_binding_conflict",
                        "This source event was already recorded for a different Run, program, or observation time. Retry the original event unchanged, or use a new source event ID for a new observation.");
                var completedCycleCount = prior.GetInt32(0);
                await prior.DisposeAsync();
                await transaction.CommitAsync(token);
                return new((await runs.GetAsync(runId, token))!, true, completedCycleCount);
            }
        }
        await EnsureRunAsync(connection, transaction, runId, expectedVersion, ["IN_PROGRESS"], token);
        await SqliteProductionRunReadinessRepository.EnsureAsync(connection, transaction, runId, token, physicalProduction: true);
        var now = timeProvider.GetUtcNow();
        var result = await SqliteProductionRunCycleAccounting.RecordAsync(
            connection, transaction, new(
                runId, programId, command.Source, command.SourceEventId,
                command.ObservedAt, now, actor, "PRODUCTION_EXECUTION"), token);
        await transaction.CommitAsync(token);
        return new((await runs.GetAsync(runId, token))!, false, result.CompletedCycleCount);
    }

    public Task<ProductionRun> SuspendAsync(string runId, int expectedVersion, string reason, EditAuthority authority, CancellationToken token) =>
        ChangeRunAsync(runId, expectedVersion, ["IN_PROGRESS"], "SUSPENDED", "production_run_suspended", authority, reason,
            async (c,t,now) => { await ExecuteAsync(c,t,"UPDATE production_run_programs SET status='SUSPENDED',version=version+1,updated_at=$at WHERE production_run_id=$id AND status='ACTIVE';",runId,now,token); }, token);
    public Task<ProductionRun> ResumeAsync(string runId, int expectedVersion, EditAuthority authority, CancellationToken token) =>
        ChangeRunAsync(runId, expectedVersion, ["SUSPENDED"], "IN_PROGRESS", "production_run_resumed", authority, null, async (c,t,now) => await SqliteProductionRunReadinessRepository.EnsureAsync(c,t,runId,token), token);
    public Task<ProductionRun> ResetAsync(string runId, int expectedVersion, string reason, EditAuthority authority, CancellationToken token) =>
        ChangeRunAsync(runId, expectedVersion, ["IN_PROGRESS", "SUSPENDED"], "PLANNED", "production_run_reset", authority, reason,
            async (c,t,now) =>
            {
                if (await ScalarIntAsync(c,t,"SELECT COALESCE(SUM(completed_cycle_count),0) FROM production_run_programs WHERE production_run_id=$id;",runId,token)>0)
                    throw new ProductionRunStateException("reset_after_production_forbidden", "A run with recorded production cycles cannot be reset.");
                await ExecuteAsync(c,t,"UPDATE production_run_programs SET status='PLANNED',production_process_revision_id=NULL,production_gcode_release_id=NULL,production_tool_table_release_id=NULL,production_gcode_file_hash=NULL,production_tool_table_file_hash=NULL,version=version+1,updated_at=$at WHERE production_run_id=$id;",runId,now,token);
            }, token);

    private async Task<ProductionRun> ChangeRunAsync(string id,int version,string[] allowed,string next,string eventName,EditAuthority authority,string? reason,
        Func<SqliteConnection,SqliteTransaction,DateTimeOffset,Task>? extra,CancellationToken token)
    {
        await using var c=await database.OpenConnectionAsync(token);await using var t=c.BeginTransaction(deferred:false);
        var actor=await EnsureEditAsync(c,t,authority,token);await EnsureRunAsync(c,t,id,version,allowed,token);var now=timeProvider.GetUtcNow();
        if(extra is not null)await extra(c,t,now);await using var q=c.CreateCommand();q.Transaction=t;q.CommandText="UPDATE production_runs SET status=$status,structure_locked_at=COALESCE(structure_locked_at,CASE WHEN $status='IN_PROGRESS' THEN $at END),version=version+1,updated_at=$at WHERE id=$id;";
        q.Parameters.AddWithValue("$status",next);q.Parameters.AddWithValue("$at",Format(now));q.Parameters.AddWithValue("$id",id);await q.ExecuteNonQueryAsync(token);
        await AuditAsync(c,t,eventName,actor,id,null,new { reason },now,token);await t.CommitAsync(token);return(await runs.GetAsync(id,token))!;
    }
    private static async Task EnsureRunAsync(SqliteConnection c,SqliteTransaction t,string id,int version,string[] allowed,CancellationToken token)
    {await using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT status,version FROM production_runs WHERE id=$id;";q.Parameters.AddWithValue("$id",id);await using var r=await q.ExecuteReaderAsync(token);if(!await r.ReadAsync(token))throw new ProductionRunNotFoundException(id);if(r.GetInt32(1)!=version)throw new ProductionRunVersionConflictException(id,version);if(!allowed.Contains(r.GetString(0)))throw new ProductionRunStateException("invalid_run_transition",$"Production Run status '{r.GetString(0)}' does not allow this action.");}
    private static async Task<string> EnsureEditAsync(SqliteConnection c,SqliteTransaction t,EditAuthority a,CancellationToken token)
    {
        // Single Edit Mode is retired: the API authorized the signed-in user for this change.
        await Task.CompletedTask;
        return SignedInActor.Require(a);
    }
    private static Task TouchRunAsync(SqliteConnection c,SqliteTransaction t,string id,DateTimeOffset at,CancellationToken token)=>ExecuteAsync(c,t,"UPDATE production_runs SET version=version+1,updated_at=$at WHERE id=$id;",id,at,token);
    private static async Task ExecuteAsync(SqliteConnection c,SqliteTransaction t,string sql,string id,DateTimeOffset at,CancellationToken token){await using var q=c.CreateCommand();q.Transaction=t;q.CommandText=sql;q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$at",Format(at));await q.ExecuteNonQueryAsync(token);}
    private static async Task<int> ScalarIntAsync(SqliteConnection c,SqliteTransaction t,string sql,string id,CancellationToken token){await using var q=c.CreateCommand();q.Transaction=t;q.CommandText=sql;q.Parameters.AddWithValue("$id",id);return Convert.ToInt32(await q.ExecuteScalarAsync(token));}
    private static async Task AuditAsync(SqliteConnection c,SqliteTransaction t,string name,string actor,string run,string? program,object? after,DateTimeOffset at,CancellationToken token){var ids=new Dictionary<string,string>{{"productionRunId",run}};if(program is not null)ids["productionRunProgramId"]=program;await SqliteStructuredEventLogRepository.AppendAsync(c,t,new(name,at,actor,ids,"PRODUCTION_EXECUTION",null,null,after),token);}
    private static string Format(DateTimeOffset v)=>v.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
}
