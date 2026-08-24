using System.Globalization;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Application.ProductionRuns;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteProductionRunCncObservationRepository(SqliteDatabase database)
    : IProductionRunCncObservationRepository
{
    public async Task<IReadOnlyList<string>> ConsumeCounterAsync(string machineId,string? partName,string? programNumber,
        int? previousCounter,int currentCounter,DateTimeOffset observedAt,CancellationToken token)
    {
        if(previousCounter is null||currentCounter<=previousCounter)return [];
        var delta=currentCounter-previousCounter.Value;
        if(delta>100){await LogResolutionAsync(machineId,"cnc_counter_jump_ignored",new{previousCounter,currentCounter},observedAt,token);return ["cnc_counter_jump_ignored"];}
        var events=new List<string>();
        for(var counter=previousCounter.Value+1;counter<=currentCounter;counter++)
        {
            var eventId=$"{machineId}:{observedAt.UtcTicks}:{counter}";
            await using var connection=await database.OpenConnectionAsync(token);await using var transaction=connection.BeginTransaction(deferred:false);
            await using(var duplicate=connection.CreateCommand()){duplicate.Transaction=transaction;duplicate.CommandText="SELECT 1 FROM production_run_cycle_events WHERE source='CNC' AND source_event_id=$event;";duplicate.Parameters.AddWithValue("$event",eventId);if(await duplicate.ExecuteScalarAsync(token)is not null){await transaction.CommitAsync(token);continue;}}
            var candidates=new List<(string Run,string Program,int Completed,int Target)>();
            await using(var query=connection.CreateCommand())
            {
                query.Transaction=transaction;query.CommandText="""
                    SELECT DISTINCT run.id,program.id,program.completed_cycle_count,program.target_cycle_count
                    FROM machine_assignments assignment JOIN production_runs run ON run.id=assignment.production_run_id
                    JOIN production_run_programs program ON program.production_run_id=run.id
                    LEFT JOIN gcode_releases release ON release.id=program.production_gcode_release_id
                    WHERE assignment.machine_id=$machine AND run.status='IN_PROGRESS' AND program.status='ACTIVE'
                      AND (($part IS NOT NULL AND EXISTS(
                            SELECT 1 FROM production_run_outputs output JOIN batch_operations operation ON operation.id=output.batch_operation_id
                            JOIN production_batches batch ON batch.id=operation.production_batch_id JOIN cases ON cases.id=batch.case_id
                            WHERE output.production_run_program_id=program.id AND lower(trim(cases.part_number))=lower(trim($part))))
                           OR ($part IS NULL AND $program IS NOT NULL AND lower(COALESCE(release.original_file_name,'')) LIKE '%'||lower(trim($program))||'%'));
                    """;
                query.Parameters.AddWithValue("$machine",machineId);query.Parameters.AddWithValue("$part",(object?)partName??DBNull.Value);query.Parameters.AddWithValue("$program",(object?)programNumber??DBNull.Value);
                await using var reader=await query.ExecuteReaderAsync(token);while(await reader.ReadAsync(token))candidates.Add((reader.GetString(0),reader.GetString(1),reader.GetInt32(2),reader.GetInt32(3)));
            }
            if(candidates.Count!=1)
            {
                await SqliteStructuredEventLogRepository.AppendAsync(connection,transaction,new(candidates.Count==0?"cnc_program_unresolved":"cnc_program_ambiguous",observedAt,"cnc-system",
                    new Dictionary<string,string>{{"machineId",machineId}},"CNC_OBSERVATION",null,null,new{partName,programNumber,candidateCount=candidates.Count}),token);
                await transaction.CommitAsync(token);events.Add(candidates.Count==0?"cnc_program_unresolved":"cnc_program_ambiguous");continue;
            }
            var candidate=candidates[0];if(candidate.Completed>=candidate.Target){await transaction.RollbackAsync(token);events.Add("cnc_completed_program_rejected");continue;}
            var next=candidate.Completed+1;var complete=next==candidate.Target;var at=Format(observedAt);
            await using(var update=connection.CreateCommand())
            {
                update.Transaction=transaction;update.CommandText="""
                    UPDATE production_run_outputs SET produced_quantity=produced_quantity+quantity_per_cycle,
                      status=CASE WHEN produced_quantity+quantity_per_cycle=target_quantity THEN 'COMPLETED' ELSE 'IN_PRODUCTION' END,
                      version=version+1,updated_at=$at
                    WHERE production_run_program_id=$program AND produced_quantity+quantity_per_cycle<=target_quantity;
                    UPDATE production_run_programs SET completed_cycle_count=$next,status=$status,version=version+1,updated_at=$at WHERE id=$program;
                    UPDATE batch_operations SET status=CASE WHEN NOT EXISTS(
                      SELECT 1 FROM production_run_outputs output WHERE output.batch_operation_id=batch_operations.id AND output.target_quantity>output.produced_quantity) THEN 'completed' ELSE 'started' END,
                      version=version+1,updated_at=$at WHERE id IN(SELECT batch_operation_id FROM production_run_outputs WHERE production_run_program_id=$program);
                    """;
                update.Parameters.AddWithValue("$program",candidate.Program);update.Parameters.AddWithValue("$next",next);update.Parameters.AddWithValue("$status",complete?"COMPLETED":"ACTIVE");update.Parameters.AddWithValue("$at",at);await update.ExecuteNonQueryAsync(token);
            }
            var remaining=await ScalarAsync(connection,transaction,"SELECT COUNT(*) FROM production_run_programs WHERE production_run_id=$id AND status<>'COMPLETED';",candidate.Run,token);
            await using(var update=connection.CreateCommand()){update.Transaction=transaction;update.CommandText="UPDATE production_runs SET status=$status,version=version+1,updated_at=$at WHERE id=$id;";update.Parameters.AddWithValue("$status",remaining==0?"COMPLETED":"IN_PROGRESS");update.Parameters.AddWithValue("$at",at);update.Parameters.AddWithValue("$id",candidate.Run);await update.ExecuteNonQueryAsync(token);}
            await using(var insert=connection.CreateCommand()){insert.Transaction=transaction;insert.CommandText="INSERT INTO production_run_cycle_events(id,production_run_id,production_run_program_id,source,source_event_id,observed_at,completed_cycle_count,created_at,updated_at) VALUES($id,$run,$program,'CNC',$event,$at,$next,$at,$at);";insert.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("N"));insert.Parameters.AddWithValue("$run",candidate.Run);insert.Parameters.AddWithValue("$program",candidate.Program);insert.Parameters.AddWithValue("$event",eventId);insert.Parameters.AddWithValue("$at",at);insert.Parameters.AddWithValue("$next",next);await insert.ExecuteNonQueryAsync(token);}
            await SqliteStructuredEventLogRepository.AppendAsync(connection,transaction,new("production_run_program_cycle_completed",observedAt,"cnc-system",new Dictionary<string,string>{{"machineId",machineId},{"productionRunId",candidate.Run},{"productionRunProgramId",candidate.Program}},"CNC_OBSERVATION",null,null,new{partName,programNumber,counter,completedCycleCount=next}),token);
            await transaction.CommitAsync(token);events.Add("production_run_program_cycle_completed");
        }
        return events;
    }
    private async Task LogResolutionAsync(string machine,string name,object payload,DateTimeOffset at,CancellationToken token){await using var c=await database.OpenConnectionAsync(token);await using var t=c.BeginTransaction(deferred:false);await SqliteStructuredEventLogRepository.AppendAsync(c,t,new(name,at,"cnc-system",new Dictionary<string,string>{{"machineId",machine}},"CNC_OBSERVATION",null,null,payload),token);await t.CommitAsync(token);}
    private static async Task<int> ScalarAsync(Microsoft.Data.Sqlite.SqliteConnection c,Microsoft.Data.Sqlite.SqliteTransaction t,string sql,string id,CancellationToken token){await using var q=c.CreateCommand();q.Transaction=t;q.CommandText=sql;q.Parameters.AddWithValue("$id",id);return Convert.ToInt32(await q.ExecuteScalarAsync(token));}
    private static string Format(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
}
