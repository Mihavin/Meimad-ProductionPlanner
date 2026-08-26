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
