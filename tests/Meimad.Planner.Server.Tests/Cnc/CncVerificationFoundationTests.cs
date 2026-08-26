using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Infrastructure.Haas;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Cnc;

public sealed class CncVerificationFoundationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-26T12:00:00Z");

    [Fact]
    public async Task New_offset_loader_release_is_current_and_prior_release_stays_immutable_history()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var service = Service(fixture.Database);
        var authority = new EditAuthority("verification-client", 1);
        var request = new CreateOffsetLoaderRelease(
            "machine-verification", "gcode-verification", "tools-verification",
            new string('a', 64), "{\"measurementRevision\":7}");

        var first = await service.CreateOffsetLoaderReleaseAsync("run-verification", request, authority);
        var second = await service.CreateOffsetLoaderReleaseAsync("run-verification", request, authority);
        var history = await service.ListOffsetLoaderReleasesAsync("run-verification");

        Assert.NotEqual(first.OffsetLoaderReleaseId, second.OffsetLoaderReleaseId);
        Assert.NotEqual(first.VerificationReleaseToken, second.VerificationReleaseToken);
        Assert.Equal(2, history.Count);
        Assert.True(Assert.Single(history, value => value.OffsetLoaderReleaseId == second.OffsetLoaderReleaseId).IsCurrent);
        Assert.False(Assert.Single(history, value => value.OffsetLoaderReleaseId == first.OffsetLoaderReleaseId).IsCurrent);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE offset_loader_releases SET verification_release_token=1 WHERE id=$id;";
        update.Parameters.AddWithValue("$id", first.OffsetLoaderReleaseId);
        var error = await Assert.ThrowsAsync<SqliteException>(() => update.ExecuteNonQueryAsync());
        Assert.Contains("immutable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verification_secret_is_encrypted_preserved_on_update_and_never_returned()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var service = Service(fixture.Database);
        var authority = new EditAuthority("verification-client", 1);
        var create = Settings("machine-secret-value", enabled: false);

        var first = await service.UpdateSettingsAsync("machine-verification", create, 0, authority);
        var second = await service.UpdateSettingsAsync("machine-verification",
            create with { VerificationSecret = null, Enabled = true }, 1, authority);

        Assert.True(first.SecretConfigured);
        Assert.True(second.SecretConfigured);
        Assert.True(second.Enabled);
        Assert.Equal(2, second.Version);
        var responseJson = System.Text.Json.JsonSerializer.Serialize(second);
        Assert.DoesNotContain("machine-secret-value", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("protectedSecret", responseJson, StringComparison.OrdinalIgnoreCase);
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT protected_secret FROM cnc_verification_settings WHERE machine_id='machine-verification';";
        var stored = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.NotEqual("machine-secret-value", stored);
        Assert.DoesNotContain("machine-secret-value", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offset_loader_token_collision_is_resolved_inside_the_atomic_create_transaction()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var repository = new SqliteCncVerificationFoundationRepository(fixture.Database);
        var authority = new EditAuthority("verification-client", 1);
        var request = new CreateOffsetLoaderRelease(
            "machine-verification", "gcode-verification", "tools-verification");

        var first = await repository.CreateOffsetLoaderReleaseAsync(
            "run-verification", request, 483920, Now, authority, default);
        var second = await repository.CreateOffsetLoaderReleaseAsync(
            "run-verification", request, 483920, Now.AddSeconds(1), authority, default);

        Assert.Equal(483920, first.VerificationReleaseToken);
        Assert.Equal(483921, second.VerificationReleaseToken);
    }

    [Fact]
    public async Task Current_offset_loader_DPRINT_is_ingested_idempotently_with_release_evidence()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var repository = new SqliteCncVerificationFoundationRepository(fixture.Database);
        var service = new CncVerificationFoundationService(repository,
            new FixedTimeProvider(Now), new EphemeralDataProtectionProvider());
        var authority = new EditAuthority("verification-client", 1);
        await service.UpdateSettingsAsync("machine-verification", Settings("machine-secret-value", true), 0, authority);
        var release = await service.CreateOffsetLoaderReleaseAsync("run-verification", new(
            "machine-verification", "gcode-verification", "tools-verification"), authority);
        var workflow = new ProductionRunWorkflowEventService(
            new SqliteProductionRunWorkflowEventRepository(fixture.Database), new FixedTimeProvider(Now));
        var ingestion = new CncDprintEventIngestionService(repository, workflow,
            NullLogger<CncDprintEventIngestionService>.Instance);
        var line = $"MEIMAD/V/1/EVENT/OLC/ID/OFFSET-1/SEQ/101/MACROVERSION/3/PROGRAM/O1234/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/73184";
        var raw = new RawCncTelemetry("machine-verification", "connection", "HAAS_NGC",
            Now, "DPRINT_EVENT", line);

        await ingestion.ConsumeAsync("machine-verification", [raw], default);
        await ingestion.ConsumeAsync("machine-verification", [raw], default);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), offset_loader_release_id, nc_release_id, source_sequence
            FROM production_run_workflow_events
            WHERE source_event_id='OFFSET-1';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(release.OffsetLoaderReleaseId, reader.GetString(1));
        Assert.Equal("gcode-verification", reader.GetString(2));
        Assert.Equal(101, reader.GetInt64(3));
    }

    private static UpdateCncVerificationSettings Settings(string? secret, bool enabled) => new(
        "HAAS_DPRNT_TCP", 8080, 9001, 9002, 605, 10801, 10802, 10803, 10804,
        secret, 3, 6, 300, enabled);

    private static CncVerificationFoundationService Service(SqliteDatabase database) => new(
        new SqliteCncVerificationFoundationRepository(database),
        new FixedTimeProvider(Now), new EphemeralDataProtectionProvider());

    private static async Task SeedAsync(SqliteDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars(id,name,time_zone_id)VALUES('calendar-verification','Calendar','UTC');
            INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active)
            VALUES('machine-verification','1','Machine','mill','calendar-verification','active',1);
            INSERT INTO cases(id,part_number,name,working_folder_path)VALUES('case-verification','PART','Part','C:\\Cases\\PART');
            INSERT INTO case_operations(id,case_id,operation_number,route_position,name)
            VALUES('case-operation-verification','case-verification',10,0,'Mill');
            INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity)
            VALUES('batch-verification','case-verification','B-1','in_production',1);
            INSERT INTO batch_operations(id,production_batch_id,source_case_operation_id,operation_number,route_position,name,status)
            VALUES('operation-verification','batch-verification','case-operation-verification',10,0,'Mill','started');
            INSERT INTO postprocessors(id,name)VALUES('post-verification','Post');
            INSERT INTO tool_table_releases(id,case_operation_id,revision_number,original_file_name,stored_relative_path,file_size,file_hash,released_at,released_by,release_comment,created_at,updated_at)
            VALUES('tools-verification','case-operation-verification',1,'tools.csv','tools/1.csv',1,$hash,$at,'user','release',$at,$at);
            INSERT INTO process_revisions(id,case_operation_id,revision_number,is_active,tool_table_release_id,created_at,created_by,change_description,version,updated_at,manufacturing_program_id)
            VALUES('process-verification','case-operation-verification',1,1,'tools-verification',$at,'user','release',1,$at,'case-operation:case-operation-verification');
            INSERT INTO gcode_releases(id,case_operation_id,process_revision_id,postprocessor_id,post_specific_revision,original_file_name,stored_relative_path,file_size,file_hash,released_at,released_by,change_scope,release_comment,tool_table_release_id,created_at,updated_at)
            VALUES('gcode-verification','case-operation-verification','process-verification','post-verification',1,'part.nc','gcode/part.nc',1,$hash,$at,'user','LOCAL_POST_REVISION','release','tools-verification',$at,$at);
            INSERT INTO gcode_release_verification_hooks(gcode_release_id,hook_version,invocation_kind,invocation_number,nc_identity_token,line_number,created_at,updated_at)
            VALUES('gcode-verification',1,'G65',9002,654321,3,$at,$at);
            INSERT INTO production_runs(id,status,shared_setup_seconds,setup_snapshot_json,structure_locked_at,version,created_at,updated_at)
            VALUES('run-verification','PLANNED',0,'{}',NULL,1,$at,$at);
            INSERT INTO production_run_programs(id,production_run_id,manufacturing_program_id,process_revision_id,selected_gcode_release_id,sequence_position,target_cycle_count,completed_cycle_count,status,legacy_unmanaged,version,created_at,updated_at)
            VALUES('run-program-verification','run-verification','case-operation:case-operation-verification','process-verification','gcode-verification',0,1,0,'ACTIVE',0,1,$at,$at);
            INSERT INTO production_run_outputs(id,production_run_program_id,batch_operation_id,quantity_per_cycle,target_quantity,produced_quantity,status,version,created_at,updated_at)
            VALUES('output-verification','run-program-verification','operation-verification',1,1,0,'ALLOCATED',1,$at,$at);
            INSERT INTO machine_assignments(id,batch_operation_id,machine_id,backlog_position,planning_mode,production_run_id)
            VALUES('assignment-verification','operation-verification','machine-verification',0,'manual','run-verification');
            UPDATE production_runs SET status='IN_PROGRESS',structure_locked_at=$at WHERE id='run-verification';
            UPDATE edit_tokens SET holder_client_id='verification-client',holder_user_id='verification-user',generation=1,acquired_at=$at,updated_at=$at WHERE id=1;
            """;
        command.Parameters.AddWithValue("$at", Now.ToString("O"));
        command.Parameters.AddWithValue("$hash", new string('b', 64));
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
