using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Tests.ProductionRuns;

public sealed class ProductionRunWorkflowEventTests
{
    private static readonly DateTimeOffset ServerTime =
        new(2026, 8, 26, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Cycle_start_preserves_raw_server_machine_and_sequence_evidence()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var service = new ProductionRunWorkflowEventService(
            new SqliteProductionRunWorkflowEventRepository(fixture.Database),
            new FixedTimeProvider(ServerTime));
        var machineTime = ServerTime.AddSeconds(-4);

        await service.AppendAsync(new(
            "run-workflow", "machine-workflow", "CYCLE_START",
            "CNC_AGENT", "cycle-start-501", 501, machineTime));

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT start_source_event_id,start_source_sequence,start_server_received_at,
                   start_machine_timestamp,completion_state,end_server_received_at
            FROM production_run_cycle_attempt_timing;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("cycle-start-501", reader.GetString(0));
        Assert.Equal(501L, reader.GetInt64(1));
        Assert.Equal(ServerTime, DateTimeOffset.Parse(reader.GetString(2)));
        Assert.Equal(machineTime, DateTimeOffset.Parse(reader.GetString(3)));
        Assert.Equal("OPEN", reader.GetString(4));
        Assert.True(reader.IsDBNull(5));
        await reader.DisposeAsync();

        command.CommandText = "UPDATE production_run_cycle_attempts SET start_source_sequence=502;";
        var updateError = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Contains("immutable", updateError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Append_is_idempotent_uses_server_time_and_preserves_machine_time()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var service = new ProductionRunWorkflowEventService(
            new SqliteProductionRunWorkflowEventRepository(fixture.Database),
            new FixedTimeProvider(ServerTime));
        var machineTime = ServerTime.AddMinutes(12);
        var command = new AppendProductionRunWorkflowEvent(
            "run-workflow", "machine-workflow", "SETUP_VERIFICATION_SUCCEEDED",
            "CNC_AGENT", "agent-event-42", 42, machineTime,
            MetadataJson: "{\"verification\":\"passed\"}");

        var first = await service.AppendAsync(command);
        var duplicate = await service.AppendAsync(command with
        {
            MachineTimestamp = machineTime.AddHours(1)
        });

        Assert.False(first.WasDuplicate);
        Assert.True(duplicate.WasDuplicate);
        Assert.Equal(first.Event.EventId, duplicate.Event.EventId);
        Assert.Equal(ServerTime, first.Event.ServerReceivedAt);
        Assert.Equal(machineTime, first.Event.MachineTimestamp);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM production_run_workflow_events;";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);

        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE production_run_workflow_events SET event_type = 'QC_PASS';";
        var updateError = await Assert.ThrowsAsync<SqliteException>(() => update.ExecuteNonQueryAsync());
        Assert.Contains("immutable", updateError.Message, StringComparison.OrdinalIgnoreCase);

        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM production_run_workflow_events;";
        var deleteError = await Assert.ThrowsAsync<SqliteException>(() => delete.ExecuteNonQueryAsync());
        Assert.Contains("immutable", deleteError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Append_rejects_a_run_machine_pair_that_is_not_assigned()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var service = new ProductionRunWorkflowEventService(
            new SqliteProductionRunWorkflowEventRepository(fixture.Database),
            new FixedTimeProvider(ServerTime));

        var exception = await Assert.ThrowsAsync<ProductionRunWorkflowTargetException>(() =>
            service.AppendAsync(new(
                "run-workflow", "machine-other", "CYCLE_START", "CNC_AGENT", "event-1")));

        Assert.Contains("not assigned", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sequence_gap_and_out_of_order_events_are_retained_with_anomalies()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var service = new ProductionRunWorkflowEventService(
            new SqliteProductionRunWorkflowEventRepository(fixture.Database),
            new FixedTimeProvider(ServerTime));

        var first = await service.AppendAsync(new(
            "run-workflow", "machine-workflow", "CYCLE_START", "CNC_AGENT", "event-101", 101));
        var gap = await service.AppendAsync(new(
            "run-workflow", "machine-workflow", "CYCLE_END", "CNC_AGENT", "event-105", 105));
        var late = await service.AppendAsync(new(
            "run-workflow", "machine-workflow", "CYCLE_END", "CNC_AGENT", "event-102", 102));

        Assert.Empty(first.Anomalies);
        Assert.Equal("EVENT_SEQUENCE_GAP", Assert.Single(gap.Anomalies).AnomalyType);
        Assert.Equal(102, gap.Anomalies[0].ExpectedSequence);
        Assert.Equal("EVENT_SEQUENCE_OUT_OF_ORDER", Assert.Single(late.Anomalies).AnomalyType);
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM production_run_workflow_events; SELECT COUNT(*) FROM production_run_workflow_anomalies;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
    }

    [Fact]
    public async Task Next_authoritative_setup_closes_prior_session_at_last_valid_end()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        await SeedSessionClosureContextAsync(fixture.Database, false);
        var service = new ProductionRunWorkflowEventService(
            new SqliteProductionRunWorkflowEventRepository(fixture.Database),
            new FixedTimeProvider(ServerTime));
        var command = new AppendProductionRunWorkflowEvent(
            "run-workflow-next", "machine-workflow", "OFFSET_LOADER_COMPLETED",
            "HAAS_DPRINT:MACHINE-WORKFLOW", "NEXT-SETUP-1", 301);

        await service.AppendAsync(command);
        await service.AppendAsync(command);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT observed_end_at,effective_end_at,end_time_inferred,closed_at,
                   triggering_production_run_id
            FROM production_run_session_closures
            WHERE production_run_id='run-workflow';
            """;
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("2026-08-26T08:01:00.0000000+00:00", reader.GetString(0));
        Assert.Equal(reader.GetString(0), reader.GetString(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.Equal(ServerTime, DateTimeOffset.Parse(reader.GetString(3)));
        Assert.Equal("run-workflow-next", reader.GetString(4));
        await reader.DisposeAsync();
        query.CommandText = """
            SELECT COUNT(*) FROM production_run_workflow_events
            WHERE production_run_id='run-workflow'
              AND event_type='PRODUCTION_SESSION_CLOSED';
            """;
        Assert.Equal(1L, (long)(await query.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Open_last_start_uses_minimum_validated_cycle_as_explicit_inference()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        await SeedSessionClosureContextAsync(fixture.Database, true);
        var service = new ProductionRunWorkflowEventService(
            new SqliteProductionRunWorkflowEventRepository(fixture.Database),
            new FixedTimeProvider(ServerTime));

        await service.AppendAsync(new(
            "run-workflow-next", "machine-workflow", "OFFSET_LOADER_COMPLETED",
            "HAAS_DPRINT:MACHINE-WORKFLOW", "NEXT-SETUP-2", 302));

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT observed_end_at,effective_end_at,end_time_inferred,
                   json_extract(inference_basis_json,'$.kind'),
                   json_extract(inference_basis_json,'$.minimumValidatedCycleSeconds')
            FROM production_run_session_closures
            WHERE production_run_id='run-workflow';
            """;
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal("2026-08-26T08:12:00.0000000+00:00", reader.GetString(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal("LAST_START_PLUS_MINIMUM_VALIDATED_CYCLE", reader.GetString(3));
        Assert.Equal(120d, reader.GetDouble(4));
    }

    private static async Task SeedSessionClosureContextAsync(
        SqliteDatabase database,
        bool addOpenStart)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE production_runs
            SET status='DRAFT',structure_locked_at=NULL
            WHERE id='run-workflow';
            INSERT INTO production_run_programs(
                id,production_run_id,manufacturing_program_id,sequence_position,
                target_cycle_count,completed_cycle_count,status,legacy_unmanaged,
                version,created_at,updated_at)
            VALUES('program-workflow','run-workflow',
                   'case-operation:case-operation-workflow',0,10,1,'ACTIVE',1,1,
                   '2026-08-26T08:00:00Z','2026-08-26T08:00:00Z');
            INSERT INTO batch_operations(
                id,production_batch_id,source_case_operation_id,operation_number,
                route_position,name,status)
            VALUES('operation-workflow-next','batch-workflow','case-operation-workflow',
                   20,1,'Next','started');
            INSERT INTO production_runs(
                id,status,shared_setup_seconds,setup_snapshot_json,
                structure_locked_at,version,created_at,updated_at)
            VALUES('run-workflow-next','IN_PROGRESS',0,'{}','2026-08-26T08:20:00Z',1,
                   '2026-08-26T08:20:00Z','2026-08-26T08:20:00Z');
            INSERT INTO machine_assignments(
                id,batch_operation_id,machine_id,backlog_position,planning_mode,
                production_run_id)
            VALUES('assignment-workflow-next','operation-workflow-next',
                   'machine-workflow',1,'manual','run-workflow-next');
            INSERT INTO production_run_workflow_events(
                id,production_run_id,machine_id,event_type,source,source_event_id,
                source_sequence,server_received_at,machine_timestamp,metadata_json)
            VALUES
                ('cycle-start-workflow','run-workflow','machine-workflow',
                 'CYCLE_START','HAAS_DPRINT:MACHINE-WORKFLOW','CYCLE-101',101,
                 '2026-08-26T08:00:00Z','2026-08-26T07:59:00Z',
                 '{"productionRunProgramId":"program-workflow"}'),
                ('cycle-end-workflow','run-workflow','machine-workflow',
                 'CYCLE_END','HAAS_DPRINT:MACHINE-WORKFLOW','CYCLE-102',102,
                 '2026-08-26T08:02:00Z','2026-08-26T08:01:00Z',
                 '{"productionRunProgramId":"program-workflow"}');
            INSERT INTO production_run_cycle_events(
                id,production_run_id,production_run_program_id,source,source_event_id,
                observed_at,completed_cycle_count,created_at,updated_at)
            VALUES('cycle-record-workflow','run-workflow','program-workflow',
                   'HAAS_DPRINT:MACHINE-WORKFLOW','CYCLE-102',
                   '2026-08-26T08:02:00Z',1,
                   '2026-08-26T08:02:00Z','2026-08-26T08:02:00Z');
            UPDATE production_runs
            SET status='IN_PROGRESS',structure_locked_at='2026-08-26T08:00:00Z'
            WHERE id='run-workflow';
            """ + (addOpenStart ? """
            INSERT INTO production_run_workflow_events(
                id,production_run_id,machine_id,event_type,source,source_event_id,
                source_sequence,server_received_at,machine_timestamp,metadata_json)
            VALUES('cycle-start-open-workflow','run-workflow','machine-workflow',
                   'CYCLE_START','HAAS_DPRINT:MACHINE-WORKFLOW','CYCLE-103',103,
                   '2026-08-26T08:10:00Z','2026-08-26T08:10:00Z',
                   '{"productionRunProgramId":"program-workflow"}');
            """ : string.Empty);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedAsync(SqliteDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars(id,name,time_zone_id)
            VALUES('calendar-workflow','Calendar','UTC');
            INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active)
            VALUES
                ('machine-workflow','1','Machine','mill','calendar-workflow','active',1),
                ('machine-other','2','Other','mill','calendar-workflow','active',1);
            INSERT INTO cases(id,part_number,name,working_folder_path)
            VALUES('case-workflow','PART','Part','C:\\Cases\\PART');
            INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity)
            VALUES('batch-workflow','case-workflow','B-1','in_production',1);
            INSERT INTO case_operations(id,case_id,operation_number,route_position,name)
            VALUES('case-operation-workflow','case-workflow',10,0,'Mill');
            INSERT INTO batch_operations(
                id,production_batch_id,source_case_operation_id,operation_number,
                route_position,name,status)
            VALUES('operation-workflow','batch-workflow','case-operation-workflow',10,0,'Mill','started');
            INSERT INTO production_runs(
                id,status,shared_setup_seconds,setup_snapshot_json,
                structure_locked_at,version,created_at,updated_at)
            VALUES('run-workflow','IN_PROGRESS',0,'{}','2026-08-26T08:00:00Z',1,
                   '2026-08-26T08:00:00Z','2026-08-26T08:00:00Z');
            INSERT INTO machine_assignments(
                id,batch_operation_id,machine_id,backlog_position,planning_mode,production_run_id)
            VALUES('assignment-workflow','operation-workflow','machine-workflow',0,'manual','run-workflow');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
