using Meimad.Planner.Server.Application.Anomalies;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.EInk;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Application.Qc;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Infrastructure.Haas;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;

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

    [Theory]
    [InlineData(10802, 10504, "unsupported_m109_variable")]
    [InlineData(10500, 10503, "variable_collision")]
    public async Task Verification_v6_rejects_invalid_M109_or_sequence_mapping(
        int responseVariable,
        int eventSequenceVariable,
        string expectedCode)
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var command = Settings("machine-secret-value", enabled: false) with
        {
            ResponseVariable = responseVariable,
            EventSequenceVariable = eventSequenceVariable
        };

        var error = await Assert.ThrowsAsync<CncVerificationValidationException>(() =>
            Service(fixture.Database).UpdateSettingsAsync(
                "machine-verification", command, 0,
                new EditAuthority("verification-client", 1)));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public async Task Verification_v6_programs_must_be_distinct_and_sequence_must_be_persistent()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var service = Service(fixture.Database);
        var authority = new EditAuthority("verification-client", 1);

        var programError = await Assert.ThrowsAsync<CncVerificationValidationException>(() =>
            service.UpdateSettingsAsync("machine-verification",
                Settings("machine-secret-value", false) with { FinalizeProgramNumber = 9002 },
                0, authority));
        Assert.Equal("program_collision", programError.Code);

        var variableError = await Assert.ThrowsAsync<CncVerificationValidationException>(() =>
            service.UpdateSettingsAsync("machine-verification",
                Settings("machine-secret-value", false) with { EventSequenceVariable = 549 },
                0, authority));
        Assert.Equal("out_of_range", variableError.Code);
        Assert.Equal("eventSequenceVariable", variableError.Field);
    }

    [Fact]
    public async Task Upgraded_verification_settings_do_not_guess_v6_controller_mappings()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO cnc_verification_settings(
                    machine_id,dprint_transport,dprint_port,challenge_program_number,
                    verify_program_number,custom_gcode_alias,nonce_variable,response_variable,
                    verification_state_variable,release_token_variable,protected_secret,
                    expected_macro_version,response_code_digits,verification_timeout_seconds,
                    enabled,version,created_at,updated_at)
                VALUES('machine-verification','HAAS_DPRNT_TCP',8080,9001,9002,NULL,
                    10501,10500,10502,10503,'legacy-protected-value',5,6,300,0,1,$at,$at);
                """;
            command.Parameters.AddWithValue("$at", Now.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var settings = Assert.IsType<CncVerificationSettings>(
            await Service(fixture.Database).GetSettingsAsync("machine-verification"));

        Assert.Null(settings.FinalizeProgramNumber);
        Assert.Null(settings.EventSequenceVariable);
        Assert.False(settings.Enabled);
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
    public async Task Historical_NC_without_generic_hook_cannot_create_verification_offset_loader()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var service = Service(fixture.Database);

        var error = await Assert.ThrowsAsync<CncVerificationTargetException>(() =>
            service.CreateOffsetLoaderReleaseAsync("run-verification", new(
                "machine-verification", "gcode-historical", "tools-verification"),
                new EditAuthority("verification-client", 1)));

        Assert.Equal("offset_loader_context_invalid", error.Code);
        Assert.Contains("hook-eligible", error.Message, StringComparison.OrdinalIgnoreCase);
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
            new SqliteProductionRunCncObservationRepository(fixture.Database, new FixedTimeProvider(Now)),
            new OperationalAnomalyService(new SqliteOperationalAnomalyRepository(fixture.Database)),
            new FixedTimeProvider(Now),
            NullLogger<CncDprintEventIngestionService>.Instance);
        var line = $"MEIMAD/V/1/EVENT/OLC/ID/OFFSET-1/SEQ/101/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731841";
        var raw = new RawCncTelemetry("machine-verification", "connection", "HAAS_NGC",
            Now, "DPRINT_EVENT", line);

        await ingestion.ConsumeAsync("machine-verification", [raw], default);
        await ingestion.ConsumeAsync("machine-verification", [raw], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            "MEIMAD/V/1/EVENT/OLC/ID/OFFSET-STALE/SEQ/102/MACROVERSION/3/OFFSETRELEASE/999999/NONCE/731842")], default);

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

        await reader.DisposeAsync();
        command.CommandText = """
            SELECT COUNT(*),production_run_id,machine_id,nc_release_id,
                   offset_loader_release_id,nonce,macro_version,response_code_digits,
                   state,created_at,expires_at
            FROM cnc_setup_verification_sessions;
            """;
        await using var session = await command.ExecuteReaderAsync();
        Assert.True(await session.ReadAsync());
        Assert.Equal(1, session.GetInt32(0));
        Assert.Equal("run-verification", session.GetString(1));
        Assert.Equal("machine-verification", session.GetString(2));
        Assert.Equal("gcode-verification", session.GetString(3));
        Assert.Equal(release.OffsetLoaderReleaseId, session.GetString(4));
        Assert.Equal(731841, session.GetInt32(5));
        Assert.Equal(3, session.GetInt32(6));
        Assert.Equal(6, session.GetInt32(7));
        Assert.Equal("PENDING", session.GetString(8));
        Assert.Equal(Now, DateTimeOffset.Parse(session.GetString(9)));
        Assert.Equal(Now.AddSeconds(300), DateTimeOffset.Parse(session.GetString(10)));
        await session.DisposeAsync();
        command.CommandText = """
            SELECT COUNT(*) FROM operational_anomalies
            WHERE anomaly_type='duplicate_cnc_event'
              AND source_event_id='OFFSET-1';
            """;
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = """
            SELECT COUNT(*) FROM operational_anomalies
            WHERE anomaly_type='stale_offset_loader'
              AND source_event_id='OFFSET-STALE';
            """;
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task New_offset_loader_release_supersedes_live_verification_session()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var repository = new SqliteCncVerificationFoundationRepository(fixture.Database);
        var service = new CncVerificationFoundationService(repository,
            new FixedTimeProvider(Now), new EphemeralDataProtectionProvider());
        var authority = new EditAuthority("verification-client", 1);
        await service.UpdateSettingsAsync("machine-verification", Settings("machine-secret-value", true), 0, authority);
        var first = await service.CreateOffsetLoaderReleaseAsync("run-verification", new(
            "machine-verification", "gcode-verification", "tools-verification"), authority);
        var workflow = new ProductionRunWorkflowEventService(
            new SqliteProductionRunWorkflowEventRepository(fixture.Database), new FixedTimeProvider(Now));
        var ingestion = new CncDprintEventIngestionService(repository, workflow,
            new SqliteProductionRunCncObservationRepository(fixture.Database, new FixedTimeProvider(Now)),
            new OperationalAnomalyService(new SqliteOperationalAnomalyRepository(fixture.Database)),
            new FixedTimeProvider(Now),
            NullLogger<CncDprintEventIngestionService>.Instance);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/OLC/ID/OFFSET-OLD/SEQ/101/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{first.VerificationReleaseToken}/NONCE/731841")], default);

        await service.CreateOffsetLoaderReleaseAsync("run-verification", new(
            "machine-verification", "gcode-verification", "tools-verification"), authority);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state,resolved_at FROM cnc_setup_verification_sessions;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("SUPERSEDED", reader.GetString(0));
        Assert.Equal(Now, DateTimeOffset.Parse(reader.GetString(1)));
    }

    [Fact]
    public async Task Protected_macro_result_resolves_session_idempotently_and_enforces_version()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var repository = new SqliteCncVerificationFoundationRepository(fixture.Database);
        var service = new CncVerificationFoundationService(repository,
            new FixedTimeProvider(Now), new EphemeralDataProtectionProvider());
        var authority = new EditAuthority("verification-client", 1);
        await service.UpdateSettingsAsync(
            "machine-verification", Settings("machine-secret-value", true), 0, authority);
        var release = await service.CreateOffsetLoaderReleaseAsync("run-verification", new(
            "machine-verification", "gcode-verification", "tools-verification"), authority);
        var ingestion = VerificationIngestion(fixture.Database, repository);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/SVS/ID/VERIFY-NO-LOADER/SEQ/100/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731840")], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/OLC/ID/VERIFY-OLC/SEQ/101/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731841")], default);
        var wrongVersion = new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/SVS/ID/VERIFY-OLD/SEQ/102/MACROVERSION/2/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731841");
        await ingestion.ConsumeAsync("machine-verification", [wrongVersion], default);
        var success = new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/SVS/ID/VERIFY-OK/SEQ/102/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731841");
        await ingestion.ConsumeAsync("machine-verification", [success], default);
        await ingestion.ConsumeAsync("machine-verification", [success], default);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state,resolution_workflow_event_id
            FROM cnc_setup_verification_sessions;
            """;
        await using var session = await command.ExecuteReaderAsync();
        Assert.True(await session.ReadAsync());
        Assert.Equal("SUCCEEDED", session.GetString(0));
        Assert.False(session.IsDBNull(1));
        await session.DisposeAsync();
        command.CommandText = """
            SELECT anomaly_type,COUNT(*) FROM operational_anomalies
            WHERE source_event_id IN('VERIFY-OLD','VERIFY-OK')
            GROUP BY anomaly_type ORDER BY anomaly_type;
            """;
        await using var anomalies = await command.ExecuteReaderAsync();
        Assert.True(await anomalies.ReadAsync());
        Assert.Equal("duplicate_cnc_event", anomalies.GetString(0));
        Assert.Equal(1, anomalies.GetInt32(1));
        Assert.True(await anomalies.ReadAsync());
        Assert.Equal("verification_macro_version_mismatch", anomalies.GetString(0));
        Assert.Equal(1, anomalies.GetInt32(1));
        await anomalies.DisposeAsync();
        command.CommandText = """
            SELECT COUNT(*) FROM operational_anomalies
            WHERE anomaly_type='offset_loader_not_executed'
              AND source_event_id='VERIFY-NO-LOADER';
            """;
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Setup_verification_requires_the_exact_six_digit_NC_identity_at_each_boundary()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var repository = new SqliteCncVerificationFoundationRepository(fixture.Database);
        var service = new CncVerificationFoundationService(repository,
            new FixedTimeProvider(Now), new EphemeralDataProtectionProvider());
        var authority = new EditAuthority("verification-client", 1);
        await service.UpdateSettingsAsync(
            "machine-verification", Settings("machine-secret-value", true), 0, authority);
        var release = await service.CreateOffsetLoaderReleaseAsync("run-verification", new(
            "machine-verification", "gcode-verification", "tools-verification"), authority);
        var ingestion = VerificationIngestion(fixture.Database, repository);

        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/OLC/ID/IDENTITY-MISSING-OLC/SEQ/101/MACROVERSION/3/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731841")], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/OLC/ID/IDENTITY-WRONG-OLC/SEQ/102/MACROVERSION/3/PROGRAM/123456/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731842")], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/OLC/ID/IDENTITY-VALID-OLC/SEQ/103/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731843")], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/SVS/ID/IDENTITY-MISSING-SVS/SEQ/104/MACROVERSION/3/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731843")], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/SVS/ID/IDENTITY-WRONG-SVS/SEQ/105/MACROVERSION/3/PROGRAM/123456/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731843")], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/SVS/ID/IDENTITY-VALID-SVS/SEQ/106/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731843")], default);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*),MAX(state) FROM cnc_setup_verification_sessions;";
        await using var session = await command.ExecuteReaderAsync();
        Assert.True(await session.ReadAsync());
        Assert.Equal(1, session.GetInt32(0));
        Assert.Equal("SUCCEEDED", session.GetString(1));
        await session.DisposeAsync();
        command.CommandText = """
            SELECT COUNT(*) FROM production_run_workflow_events
            WHERE source_event_id IN(
                'IDENTITY-MISSING-OLC','IDENTITY-WRONG-OLC',
                'IDENTITY-MISSING-SVS','IDENTITY-WRONG-SVS');
            """;
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = """
            SELECT anomaly_type,COUNT(*) FROM operational_anomalies
            WHERE source_event_id LIKE 'IDENTITY-%'
              AND source_event_id<>'IDENTITY-VALID-SVS'
              AND source_event_id<>'IDENTITY-VALID-OLC'
            GROUP BY anomaly_type ORDER BY anomaly_type;
            """;
        await using var anomalyRows = await command.ExecuteReaderAsync();
        Assert.True(await anomalyRows.ReadAsync());
        Assert.Equal("active_nc_identity_unavailable", anomalyRows.GetString(0));
        Assert.Equal(2, anomalyRows.GetInt32(1));
        Assert.True(await anomalyRows.ReadAsync());
        Assert.Equal("wrong_nc_program", anomalyRows.GetString(0));
        Assert.Equal(2, anomalyRows.GetInt32(1));
        Assert.False(await anomalyRows.ReadAsync());
    }

    [Fact]
    public async Task Verification_result_replays_are_classified_without_false_expiry()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var repository = new SqliteCncVerificationFoundationRepository(fixture.Database);
        var service = new CncVerificationFoundationService(repository,
            new FixedTimeProvider(Now), new EphemeralDataProtectionProvider());
        var authority = new EditAuthority("verification-client", 1);
        await service.UpdateSettingsAsync(
            "machine-verification", Settings("machine-secret-value", true), 0, authority);
        var release = await service.CreateOffsetLoaderReleaseAsync("run-verification", new(
            "machine-verification", "gcode-verification", "tools-verification"), authority);
        var ingestion = VerificationIngestion(fixture.Database, repository);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/OLC/ID/REPLAY-OLC/SEQ/101/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731841")], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/SVS/ID/REPLAY-SUCCESS/SEQ/102/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731841")], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/SVS/ID/REPLAY-AFTER-SUCCESS/SEQ/103/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731841")], default);
        await service.InvalidateVerificationAsync(
            "run-verification", "machine-verification", "Controlled regression test.", authority);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/SVS/ID/REPLAY-AFTER-SUPERSEDE/SEQ/104/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731841")], default);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT anomaly_type,source_event_id FROM operational_anomalies
            WHERE source_event_id IN('REPLAY-AFTER-SUCCESS','REPLAY-AFTER-SUPERSEDE')
            ORDER BY source_event_id;
            """;
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal("duplicate_cnc_event", rows.GetString(0));
        Assert.Equal("REPLAY-AFTER-SUCCESS", rows.GetString(1));
        Assert.True(await rows.ReadAsync());
        Assert.Equal("stale_offset_loader", rows.GetString(0));
        Assert.Equal("REPLAY-AFTER-SUPERSEDE", rows.GetString(1));
        Assert.False(await rows.ReadAsync());
    }

    [Fact]
    public async Task Delayed_verification_result_cannot_resolve_a_newer_challenge()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var repository = new SqliteCncVerificationFoundationRepository(fixture.Database);
        var service = new CncVerificationFoundationService(repository,
            new FixedTimeProvider(Now), new EphemeralDataProtectionProvider());
        var authority = new EditAuthority("verification-client", 1);
        await service.UpdateSettingsAsync(
            "machine-verification", Settings("machine-secret-value", true), 0, authority);
        var first = await service.CreateOffsetLoaderReleaseAsync("run-verification", new(
            "machine-verification", "gcode-verification", "tools-verification"), authority);
        var ingestion = VerificationIngestion(fixture.Database, repository);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/OLC/ID/CORRELATE-OLC-1/SEQ/101/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{first.VerificationReleaseToken}/NONCE/731841")], default);

        var second = await service.CreateOffsetLoaderReleaseAsync("run-verification", new(
            "machine-verification", "gcode-verification", "tools-verification"), authority);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/OLC/ID/CORRELATE-OLC-2/SEQ/102/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{second.VerificationReleaseToken}/NONCE/731842")], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/SVS/ID/CORRELATE-OLD-RESULT/SEQ/103/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{first.VerificationReleaseToken}/NONCE/731841")], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/SVS/ID/CORRELATE-WRONG-NONCE/SEQ/104/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{second.VerificationReleaseToken}/NONCE/731841")], default);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state FROM cnc_setup_verification_sessions
            WHERE source_workflow_event_id=(
                SELECT id FROM production_run_workflow_events
                WHERE source_event_id='CORRELATE-OLC-2');
            """;
        Assert.Equal("PENDING", await command.ExecuteScalarAsync());

        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/SVS/ID/CORRELATE-CURRENT/SEQ/105/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{second.VerificationReleaseToken}/NONCE/731842")], default);
        Assert.Equal("SUCCEEDED", await command.ExecuteScalarAsync());
        command.CommandText = """
            SELECT anomaly_type,source_event_id FROM operational_anomalies
            WHERE source_event_id IN('CORRELATE-OLD-RESULT','CORRELATE-WRONG-NONCE')
            ORDER BY source_event_id;
            """;
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal("stale_offset_loader", rows.GetString(0));
        Assert.Equal("CORRELATE-OLD-RESULT", rows.GetString(1));
        Assert.True(await rows.ReadAsync());
        Assert.Equal("offset_loader_not_executed", rows.GetString(0));
        Assert.Equal("CORRELATE-WRONG-NONCE", rows.GetString(1));
        Assert.False(await rows.ReadAsync());
    }

    [Fact]
    public async Task Authorized_recovery_invalidates_session_and_revokes_current_offset_loader_with_audit()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var repository = new SqliteCncVerificationFoundationRepository(fixture.Database);
        var service = new CncVerificationFoundationService(repository,
            new FixedTimeProvider(Now), new EphemeralDataProtectionProvider());
        var authority = new EditAuthority("verification-client", 1);
        await service.UpdateSettingsAsync(
            "machine-verification", Settings("machine-secret-value", true), 0, authority);
        var release = await service.CreateOffsetLoaderReleaseAsync("run-verification", new(
            "machine-verification", "gcode-verification", "tools-verification"), authority);
        var ingestion = VerificationIngestion(fixture.Database, repository);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/OLC/ID/RECOVERY-OLC/SEQ/101/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731841")], default);

        var invalidated = await service.InvalidateVerificationAsync(
            "run-verification", "machine-verification", "Operator loaded different fixtures.",
            authority);
        Assert.Equal("INVALIDATE_VERIFICATION", invalidated.Action);
        var revoked = await service.RevokeCurrentOffsetLoaderAsync(
            "run-verification", "machine-verification", "Offsets must be remeasured.",
            authority);
        Assert.Equal(release.OffsetLoaderReleaseId, revoked.OffsetLoaderReleaseId);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM production_run_current_offset_loaders;";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT(*) FROM offset_loader_releases WHERE id=$id;";
        command.Parameters.AddWithValue("$id", release.OffsetLoaderReleaseId);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = """
            SELECT COUNT(*) FROM structured_event_log
            WHERE event_type IN(
                'cnc_verification_session_invalidated','current_offset_loader_revoked')
              AND user_id='verification-user';
            """;
        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Late_verification_result_expires_session_once_and_never_succeeds()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var repository = new SqliteCncVerificationFoundationRepository(fixture.Database);
        var service = new CncVerificationFoundationService(repository,
            new FixedTimeProvider(Now), new EphemeralDataProtectionProvider());
        var authority = new EditAuthority("verification-client", 1);
        await service.UpdateSettingsAsync(
            "machine-verification", Settings("machine-secret-value", true), 0, authority);
        var release = await service.CreateOffsetLoaderReleaseAsync("run-verification", new(
            "machine-verification", "gcode-verification", "tools-verification"), authority);
        await VerificationIngestion(fixture.Database, repository).ConsumeAsync(
            "machine-verification", [new RawCncTelemetry(
                "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
                $"MEIMAD/V/1/EVENT/OLC/ID/EXPIRE-OLC/SEQ/101/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731841")], default);

        var lateTime = new FixedTimeProvider(Now.AddSeconds(301));
        var late = new CncDprintEventIngestionService(
            repository,
            new ProductionRunWorkflowEventService(
                new SqliteProductionRunWorkflowEventRepository(fixture.Database), lateTime),
            new SqliteProductionRunCncObservationRepository(fixture.Database, lateTime),
            new OperationalAnomalyService(
                new SqliteOperationalAnomalyRepository(fixture.Database)),
            lateTime, NullLogger<CncDprintEventIngestionService>.Instance);
        await late.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now.AddSeconds(301),
            "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/SVS/ID/EXPIRE-SVS/SEQ/102/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731841")], default);
        await late.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now.AddSeconds(301),
            "DPRINT_EVENT",
            $"MEIMAD/V/1/EVENT/SVS/ID/EXPIRE-SVS/SEQ/102/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731841")], default);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM cnc_setup_verification_sessions WHERE source_workflow_event_id=(SELECT id FROM production_run_workflow_events WHERE source_event_id='EXPIRE-OLC');";
        Assert.Equal("EXPIRED", await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM operational_anomalies WHERE anomaly_type='verification_expired' AND production_run_id='run-verification';";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT(*) FROM production_run_workflow_events WHERE source_event_id='EXPIRE-SVS';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Cycle_start_and_matching_end_after_QC_PASS_complete_exactly_one_cycle()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        await AddCoupledOutputAsync(fixture.Database);
        await PrepareProductionApprovalAsync(fixture.Database);
        var ingestion = CycleIngestion(fixture.Database);
        var start = new RawCncTelemetry("machine-verification", "connection", "HAAS_NGC", Now,
            "DPRINT_EVENT", "MEIMAD/V/1/EVENT/CST/ID/CYCLE-201/SEQ/201/MACROVERSION/3/RUN/RUN-VERIFICATION/PROGRAM/654321");
        var end = new RawCncTelemetry("machine-verification", "connection", "HAAS_NGC", Now,
            "DPRINT_EVENT", "MEIMAD/V/1/EVENT/CEN/ID/CYCLE-202/SEQ/202/MACROVERSION/3/RUN/RUN-VERIFICATION/PROGRAM/654321");

        await ingestion.ConsumeAsync("machine-verification", [start], default);
        await ingestion.ConsumeAsync("machine-verification", [end], default);
        await ingestion.ConsumeAsync("machine-verification", [end], default);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT completed_cycle_count,status FROM production_run_programs
            WHERE id='run-program-verification';
            """;
        await using var program = await command.ExecuteReaderAsync();
        Assert.True(await program.ReadAsync());
        Assert.Equal(1, program.GetInt32(0));
        Assert.Equal("COMPLETED", program.GetString(1));
        await program.DisposeAsync();

        command.CommandText = """
            SELECT produced_quantity,status FROM production_run_outputs
            WHERE id='output-verification';
            """;
        await using var output = await command.ExecuteReaderAsync();
        Assert.True(await output.ReadAsync());
        Assert.Equal(1, output.GetInt32(0));
        Assert.Equal("COMPLETED", output.GetString(1));
        await output.DisposeAsync();

        command.CommandText = """
            SELECT produced_quantity,status FROM production_run_outputs
            WHERE id='output-verification-coupled';
            """;
        await using var coupled = await command.ExecuteReaderAsync();
        Assert.True(await coupled.ReadAsync());
        Assert.Equal(2, coupled.GetInt32(0));
        Assert.Equal("COMPLETED", coupled.GetString(1));
        await coupled.DisposeAsync();

        command.CommandText = "SELECT COUNT(*) FROM production_run_cycle_events WHERE source_event_id='CYCLE-202';";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT group_concat(event_type,',') FROM production_run_workflow_events WHERE source_event_id IN ('CYCLE-201','CYCLE-202') ORDER BY source_sequence;";
        Assert.Equal("CYCLE_START,CYCLE_END", await command.ExecuteScalarAsync());
        command.CommandText = """
            SELECT COUNT(*) FROM operational_anomalies
            WHERE anomaly_type='duplicate_cnc_event'
              AND source_event_id='CYCLE-202';
            """;
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);

        command.CommandText = """
            SELECT start_source_event_id,start_source_sequence,start_server_received_at,
                   start_machine_timestamp,completion_state,boundary_source_event_id,
                   boundary_source_sequence,end_server_received_at,end_machine_timestamp
            FROM production_run_cycle_attempt_timing
            WHERE start_source_event_id='CYCLE-201';
            """;
        await using var timing = await command.ExecuteReaderAsync();
        Assert.True(await timing.ReadAsync());
        Assert.Equal("CYCLE-201", timing.GetString(0));
        Assert.Equal(201L, timing.GetInt64(1));
        var startReceivedAt = DateTimeOffset.Parse(timing.GetString(2));
        Assert.True(startReceivedAt >= Now);
        Assert.True(timing.IsDBNull(3));
        Assert.Equal("COMPLETED", timing.GetString(4));
        Assert.Equal("CYCLE-202", timing.GetString(5));
        Assert.Equal(202L, timing.GetInt64(6));
        Assert.True(DateTimeOffset.Parse(timing.GetString(7)) > startReceivedAt);
        Assert.True(timing.IsDBNull(8));
        await timing.DisposeAsync();

        command.CommandText = """
            UPDATE production_run_cycle_attempt_outcomes
            SET completion_state='INTERRUPTED';
            """;
        var immutableError = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Contains("immutable", immutableError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Schema_v58_backfills_existing_valid_cycle_attempt_timing()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        await PrepareProductionApprovalAsync(fixture.Database);
        var ingestion = CycleIngestion(fixture.Database);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            "MEIMAD/V/1/EVENT/CST/ID/BACKFILL-301/SEQ/301/MACROVERSION/3/PROGRAM/654321")], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            "MEIMAD/V/1/EVENT/CEN/ID/BACKFILL-302/SEQ/302/MACROVERSION/3/PROGRAM/654321")], default);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using (var removeProjection = connection.CreateCommand())
        {
            removeProjection.CommandText = """
                DROP VIEW production_run_cycle_attempt_timing;
                DROP TRIGGER production_run_cycle_attempt_from_start;
                DROP TRIGGER production_run_cycle_attempt_interrupted;
                DROP TRIGGER production_run_cycle_attempt_completed;
                DROP TABLE production_run_cycle_attempt_outcomes;
                DROP TABLE production_run_cycle_attempts;
                """;
            await removeProjection.ExecuteNonQueryAsync();
        }

        await using (var transaction = connection.BeginTransaction())
        {
            await new SchemaV58CycleAttemptTimingMigration().ApplyAsync(
                connection, transaction, default);
            await transaction.CommitAsync();
        }

        await using var assertion = connection.CreateCommand();
        assertion.CommandText = """
            SELECT start_source_event_id,start_source_sequence,completion_state,
                   boundary_source_event_id,boundary_source_sequence
            FROM production_run_cycle_attempt_timing
            WHERE start_source_event_id='BACKFILL-301';
            """;
        await using var reader = await assertion.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("BACKFILL-301", reader.GetString(0));
        Assert.Equal(301L, reader.GetInt64(1));
        Assert.Equal("COMPLETED", reader.GetString(2));
        Assert.Equal("BACKFILL-302", reader.GetString(3));
        Assert.Equal(302L, reader.GetInt64(4));
    }

    [Fact]
    public async Task Cycle_start_before_QC_PASS_never_advances_output()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        await PrepareProductionApprovalAsync(fixture.Database, false);
        var repository = new SqliteProductionRunCncObservationRepository(
            fixture.Database, new FixedTimeProvider(Now));

        var result = await repository.ConsumeCycleEventAsync(new(
            "machine-verification", "CYCLE_START", "INVALID-201", 201, 3,
            "RUN-VERIFICATION", "654321",
            "MEIMAD/V/1/EVENT/CST/ID/INVALID-201/SEQ/201/MACROVERSION/3"), default);

        Assert.False(result.Accepted);
        Assert.Equal("cycle_start_requires_qc_pass_or_completed_cycle", result.Code);
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT completed_cycle_count FROM production_run_programs WHERE id='run-program-verification';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        await CycleIngestion(fixture.Database).ConsumeAsync(
            "machine-verification", [new RawCncTelemetry(
                "machine-verification", "connection", "HAAS_NGC", Now,
                "DPRINT_EVENT",
                "MEIMAD/V/1/EVENT/CST/ID/INVALID-202/SEQ/202/MACROVERSION/3/RUN/RUN-VERIFICATION/PROGRAM/654321")], default);
        command.CommandText = """
            SELECT COUNT(*) FROM operational_anomalies
            WHERE anomaly_type='cycle_started_before_qc_pass'
              AND source_event_id='INVALID-202';
            """;
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT(*) FROM production_run_workflow_events WHERE source_event_id=$id;";
        command.Parameters.AddWithValue("$id", "INVALID-201");
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Second_cycle_start_records_interruption_and_only_new_attempt_can_complete()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        await PrepareProductionApprovalAsync(fixture.Database);
        var ingestion = CycleIngestion(fixture.Database);
        var first = new RawCncTelemetry("machine-verification", "connection", "HAAS_NGC", Now,
            "DPRINT_EVENT", "MEIMAD/V/1/EVENT/CST/ID/INT-201/SEQ/201/MACROVERSION/3/PROGRAM/654321");
        var second = new RawCncTelemetry("machine-verification", "connection", "HAAS_NGC", Now,
            "DPRINT_EVENT", "MEIMAD/V/1/EVENT/CST/ID/INT-202/SEQ/202/MACROVERSION/3/PROGRAM/654321");
        var end = new RawCncTelemetry("machine-verification", "connection", "HAAS_NGC", Now,
            "DPRINT_EVENT", "MEIMAD/V/1/EVENT/CEN/ID/INT-203/SEQ/203/MACROVERSION/3/PROGRAM/654321");

        await ingestion.ConsumeAsync("machine-verification", [first], default);
        await ingestion.ConsumeAsync("machine-verification", [second], default);
        await ingestion.ConsumeAsync("machine-verification", [second], default);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM production_run_workflow_events
            WHERE event_type='CYCLE_INTERRUPTED'
              AND json_extract(metadata_json,'$.interruptedSourceEventId')='INT-201'
              AND json_extract(metadata_json,'$.interruptedBySourceEventId')='INT-202';
            """;
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = """
            SELECT COUNT(*) FROM operational_anomalies
            WHERE anomaly_type='cycle_interrupted'
              AND production_run_id='run-verification';
            """;
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT completed_cycle_count FROM production_run_programs WHERE id='run-program-verification';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);

        await ingestion.ConsumeAsync("machine-verification", [end], default);

        command.CommandText = "SELECT completed_cycle_count FROM production_run_programs WHERE id='run-program-verification';";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = """
            SELECT start_source_event_id,completion_state,boundary_source_event_id,
                   boundary_source_sequence
            FROM production_run_cycle_attempt_timing
            WHERE start_source_event_id IN ('INT-201','INT-202')
            ORDER BY start_source_sequence;
            """;
        await using var attempts = await command.ExecuteReaderAsync();
        Assert.True(await attempts.ReadAsync());
        Assert.Equal("INT-201", attempts.GetString(0));
        Assert.Equal("INTERRUPTED", attempts.GetString(1));
        Assert.Equal("INT-202", attempts.GetString(2));
        Assert.Equal(202L, attempts.GetInt64(3));
        Assert.True(await attempts.ReadAsync());
        Assert.Equal("INT-202", attempts.GetString(0));
        Assert.Equal("COMPLETED", attempts.GetString(1));
        Assert.Equal("INT-203", attempts.GetString(2));
        Assert.Equal(203L, attempts.GetInt64(3));
        Assert.False(await attempts.ReadAsync());
    }

    [Fact]
    public async Task End_without_start_is_retained_once_as_data_quality_anomaly()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        await PrepareProductionApprovalAsync(fixture.Database);
        var repository = new SqliteProductionRunCncObservationRepository(
            fixture.Database, new FixedTimeProvider(Now));
        var observation = new CncCycleObservation(
            "machine-verification", "CYCLE_END", "ORPHAN-202", 202, 3,
            "RUN-VERIFICATION", "654321",
            "MEIMAD/V/1/EVENT/CEN/ID/ORPHAN-202/SEQ/202/MACROVERSION/3");

        var first = await repository.ConsumeCycleEventAsync(observation, default);
        var retry = await repository.ConsumeCycleEventAsync(observation, default);

        Assert.True(first.Accepted);
        Assert.False(first.CycleCompleted);
        Assert.Equal("cycle_end_unmatched", first.Code);
        Assert.True(retry.WasDuplicate);
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM production_run_workflow_anomalies
            WHERE source_event_id='ORPHAN-202'
              AND anomaly_type='CYCLE_END_WITHOUT_START';
            """;
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT completed_cycle_count FROM production_run_programs WHERE id='run-program-verification';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Full_development_workflow_reaches_production_and_closes_on_next_setup()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var clock = new AdvancingTimeProvider(Now);
        var protection = new EphemeralDataProtectionProvider();
        var repository = new SqliteCncVerificationFoundationRepository(fixture.Database);
        var verification = new CncVerificationFoundationService(repository, clock, protection);
        var workflow = new ProductionRunWorkflowEventService(
            new SqliteProductionRunWorkflowEventRepository(fixture.Database), clock);
        var ingestion = new CncDprintEventIngestionService(
            repository, workflow,
            new SqliteProductionRunCncObservationRepository(fixture.Database, clock),
            new OperationalAnomalyService(new SqliteOperationalAnomalyRepository(fixture.Database)),
            clock, NullLogger<CncDprintEventIngestionService>.Instance);
        var authority = new EditAuthority("verification-client", 1);
        const string tabletToken = "mp_eink_e2e-token";

        await SeedEndToEndDeviceAndNextRunAsync(fixture.Database, tabletToken);
        await using (var readyConnection = await fixture.Database.OpenConnectionAsync())
        await using (var readyEvidence = readyConnection.CreateCommand())
        {
            readyEvidence.CommandText = """
                SELECT
                  (SELECT COUNT(*) FROM eink_package_revisions
                   WHERE batch_operation_id='operation-verification')
                  +
                  (SELECT COUNT(*) FROM tool_table_releases
                   WHERE id='tools-verification');
                """;
            Assert.Equal(2L, (long)(await readyEvidence.ExecuteScalarAsync())!);
        }
        var tabletStatus = new TabletStatusService(
            new SqliteTabletStatusRepository(fixture.Database),
            new SqliteEInkDeviceRegistrationRepository(fixture.Database), protection,
            NullLogger<TabletStatusService>.Instance);
        Task<TabletStatusResponse> Status() => tabletStatus.ReadAsync(
            "E2E-TABLET", tabletToken, clock.GetUtcNow(), 4.5m, 90,
            "simulator", "127.0.0.1", -35);

        Assert.Equal("READY_FOR_SETUP", (await Status()).Status);
        await verification.UpdateSettingsAsync(
            "machine-verification", Settings("machine-secret-value", true), 0, authority);
        clock.Advance();
        var release = await verification.CreateOffsetLoaderReleaseAsync(
            "run-verification", new("machine-verification", "gcode-verification", "tools-verification"), authority);

        async Task Dprint(string payload)
        {
            clock.Advance();
            await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
                "machine-verification", "e2e-simulator", "HAAS_NGC", clock.GetUtcNow(),
                "DPRINT_EVENT", payload)], default);
        }

        await Dprint($"MEIMAD/V/1/EVENT/OLC/ID/E2E-OLC-1/SEQ/101/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731841");
        var setup = await Status();
        Assert.Equal("IN_SETUP", setup.Status);
        Assert.Matches("^[0-9]{6}$", Assert.IsType<string>(setup.Verification?.ResponseCode));

        await Dprint($"MEIMAD/V/1/EVENT/SVF/ID/E2E-VERIFY-FAIL/SEQ/102/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731841");
        Assert.Equal("IN_SETUP", (await Status()).Status);
        await Dprint($"MEIMAD/V/1/EVENT/OLC/ID/E2E-OLC-2/SEQ/103/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731842");
        await Dprint($"MEIMAD/V/1/EVENT/SVS/ID/E2E-VERIFY-OK/SEQ/104/MACROVERSION/3/PROGRAM/654321/OFFSETRELEASE/{release.VerificationReleaseToken}/NONCE/731842");
        Assert.Equal("IN_SETUP_RUN", (await Status()).Status);

        var tabletEvents = new TabletEventService(
            new SqliteTabletEventRepository(fixture.Database), clock);
        clock.Advance();
        await tabletEvents.SubmitAsync(new(
            "E2E-TABLET", tabletToken, "SEND_TO_QC", 4.4m, 88,
            "simulator", "127.0.0.1", -36));
        Assert.Equal("IN_QC", (await Status()).Status);

        var qc = new QcWorkflowService(new SqliteQcWorkflowRepository(fixture.Database), clock);
        clock.Advance();
        Assert.Equal("IN_SETUP_RUN", (await qc.DecideAsync(
            new("run-verification", "FAIL", "verification-user", "First article dimension out."), authority)).ResultingStatus);
        clock.Advance();
        await tabletEvents.SubmitAsync(new(
            "E2E-TABLET", tabletToken, "SEND_TO_QC", null, null, null, null, null));
        clock.Advance();
        Assert.Equal("READY_FOR_PRODUCTION", (await qc.DecideAsync(
            new("run-verification", "PASS", "verification-user", "First article accepted."), authority)).ResultingStatus);
        Assert.Equal("READY_FOR_PRODUCTION", (await Status()).Status);

        await Dprint("MEIMAD/V/1/EVENT/CST/ID/E2E-CYCLE-201/SEQ/201/MACROVERSION/3/RUN/RUN-VERIFICATION/PROGRAM/654321");
        Assert.Equal("IN_PRODUCTION", (await Status()).Status);
        await Dprint("MEIMAD/V/1/EVENT/CEN/ID/E2E-CYCLE-202/SEQ/202/MACROVERSION/3/RUN/RUN-VERIFICATION/PROGRAM/654321");
        await Dprint("MEIMAD/V/1/EVENT/CST/ID/E2E-CYCLE-203/SEQ/203/MACROVERSION/3/RUN/RUN-VERIFICATION/PROGRAM/654321");
        await Dprint("MEIMAD/V/1/EVENT/CST/ID/E2E-CYCLE-204/SEQ/204/MACROVERSION/3/RUN/RUN-VERIFICATION/PROGRAM/654321");
        await Dprint("MEIMAD/V/1/EVENT/CST/ID/E2E-CYCLE-204/SEQ/204/MACROVERSION/3/RUN/RUN-VERIFICATION/PROGRAM/654321");
        await Dprint("MEIMAD/V/1/EVENT/CEN/ID/E2E-CYCLE-205/SEQ/205/MACROVERSION/3/RUN/RUN-VERIFICATION/PROGRAM/654321");
        await Dprint("MEIMAD/V/1/EVENT/CEN/ID/E2E-CYCLE-207/SEQ/207/MACROVERSION/3/RUN/RUN-VERIFICATION/PROGRAM/654321");

        clock.Advance();
        await ReassignToNextRunAsync(fixture.Database);
        await workflow.AppendAsync(new(
            "run-verification-next", "machine-verification", "OFFSET_LOADER_COMPLETED",
            "DPRINT", "E2E-NEXT-SETUP", 208));

        var timeline = await new ProductionRunDebugTimelineService(
            new SqliteProductionRunDebugTimelineRepository(fixture.Database))
            .ReadAsync("machine-verification", "run-verification", 500);
        Assert.Contains(timeline.Items, item => item.EventType == "SETUP_VERIFICATION_FAILED");
        Assert.Contains(timeline.Items, item => item.EventType == "QC_FAIL");
        Assert.Contains(timeline.Items, item => item.EventType == "QC_PASS");
        Assert.Contains(timeline.Items, item => item.EventType == "CYCLE_INTERRUPTED");
        Assert.Contains(timeline.Items, item => item.EventType == "PRODUCTION_SESSION_CLOSED");

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT completed_cycle_count FROM production_run_programs WHERE id='run-program-verification';";
        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT observed_end_at,end_time_inferred FROM production_run_session_closures WHERE production_run_id='run-verification';";
        await using var closure = await command.ExecuteReaderAsync();
        Assert.True(await closure.ReadAsync());
        Assert.False(closure.IsDBNull(0));
        Assert.Equal(0, closure.GetInt32(1));
        await closure.DisposeAsync();
        command.CommandText = "SELECT COUNT(*) FROM operational_anomalies WHERE production_run_id='run-verification' AND anomaly_type IN('verification_failed','cycle_interrupted','duplicate_cnc_event','cnc_event_sequence_gap','cycle_end_without_start');";
        Assert.True((long)(await command.ExecuteScalarAsync())! >= 5);
        command.CommandText = "SELECT COUNT(*) FROM structured_event_log WHERE event_type IN('cnc_verification_configuration_updated','offset_loader_release_created');";
        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Cycle_end_with_nonconsecutive_sequence_is_not_a_valid_completion()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        await PrepareProductionApprovalAsync(fixture.Database);
        var ingestion = CycleIngestion(fixture.Database);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            "MEIMAD/V/1/EVENT/CST/ID/GAP-201/SEQ/201/MACROVERSION/3/PROGRAM/654321")], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            "MEIMAD/V/1/EVENT/CEN/ID/GAP-203/SEQ/203/MACROVERSION/3/PROGRAM/654321")], default);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT completed_cycle_count FROM production_run_programs WHERE id='run-program-verification';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT(*) FROM production_run_workflow_events WHERE source_event_id='GAP-203';";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = """
            SELECT COUNT(*) FROM production_run_workflow_anomalies
            WHERE source_event_id='GAP-203'
              AND anomaly_type='CYCLE_END_SEQUENCE_MISMATCH';
            """;
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Cycle_identity_is_required_and_conflicting_source_event_ids_are_anomalies()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        await PrepareProductionApprovalAsync(fixture.Database);
        var ingestion = CycleIngestion(fixture.Database);

        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            "MEIMAD/V/1/EVENT/CST/ID/CYCLE-IDENTITY-MISSING/SEQ/200/MACROVERSION/3/RUN/RUN-VERIFICATION")], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            "MEIMAD/V/1/EVENT/CST/ID/CYCLE-CONFLICT/SEQ/201/MACROVERSION/3/RUN/RUN-VERIFICATION/PROGRAM/654321")], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            "MEIMAD/V/1/EVENT/CEN/ID/CYCLE-CONFLICT/SEQ/202/MACROVERSION/3/RUN/RUN-VERIFICATION/PROGRAM/654321")], default);
        await ingestion.ConsumeAsync("machine-verification", [new RawCncTelemetry(
            "machine-verification", "connection", "HAAS_NGC", Now, "DPRINT_EVENT",
            "MEIMAD/V/1/EVENT/CEN/ID/CYCLE-CONFLICT-END/SEQ/202/MACROVERSION/3/RUN/RUN-VERIFICATION/PROGRAM/654321")], default);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT completed_cycle_count FROM production_run_programs WHERE id='run-program-verification';";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = """
            SELECT anomaly_type,source_event_id FROM operational_anomalies
            WHERE source_event_id IN('CYCLE-IDENTITY-MISSING','CYCLE-CONFLICT')
            ORDER BY source_event_id;
            """;
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal("duplicate_cnc_event", rows.GetString(0));
        Assert.Equal("CYCLE-CONFLICT", rows.GetString(1));
        Assert.True(await rows.ReadAsync());
        Assert.Equal("active_nc_identity_unavailable", rows.GetString(0));
        Assert.Equal("CYCLE-IDENTITY-MISSING", rows.GetString(1));
        Assert.False(await rows.ReadAsync());
    }

    private static CncDprintEventIngestionService CycleIngestion(SqliteDatabase database)
    {
        var time = new FixedTimeProvider(Now);
        return new(new SqliteCncVerificationFoundationRepository(database),
            new ProductionRunWorkflowEventService(
                new SqliteProductionRunWorkflowEventRepository(database), time),
            new SqliteProductionRunCncObservationRepository(database, time),
            new OperationalAnomalyService(new SqliteOperationalAnomalyRepository(database)),
            time,
            NullLogger<CncDprintEventIngestionService>.Instance);
    }

    private static CncDprintEventIngestionService VerificationIngestion(
        SqliteDatabase database,
        ICncVerificationFoundationRepository repository)
    {
        var time = new FixedTimeProvider(Now);
        return new(repository,
            new ProductionRunWorkflowEventService(
                new SqliteProductionRunWorkflowEventRepository(database), time),
            new SqliteProductionRunCncObservationRepository(database, time),
            new OperationalAnomalyService(new SqliteOperationalAnomalyRepository(database)),
            time,
            NullLogger<CncDprintEventIngestionService>.Instance);
    }

    private static async Task PrepareProductionApprovalAsync(
        SqliteDatabase database, bool approve = true)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE production_run_programs
            SET production_gcode_release_id='gcode-verification'
            WHERE id='run-program-verification';
            """ + (approve ? """
            INSERT INTO production_run_workflow_events(
                id,production_run_id,machine_id,event_type,source,source_event_id,
                server_received_at,user_id,metadata_json)
            VALUES('qc-pass-cycle','run-verification','machine-verification','QC_PASS',
                   'WINDOWS_QC','QC-CYCLE',$at,'qc-user','{}');
            """ : string.Empty);
        command.Parameters.AddWithValue("$at", Now.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AddCoupledOutputAsync(SqliteDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cases(id,part_number,name,working_folder_path)
            VALUES('case-verification-coupled','PART-B','Part B','C:\\Cases\\PART-B');
            INSERT INTO case_operations(id,case_id,operation_number,route_position,name)
            VALUES('case-operation-verification-coupled','case-verification-coupled',10,0,'Mill');
            INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity)
            VALUES('batch-verification-coupled','case-verification-coupled','B-2','in_production',2);
            INSERT INTO batch_operations(id,production_batch_id,source_case_operation_id,
                operation_number,route_position,name,status)
            VALUES('operation-verification-coupled','batch-verification-coupled',
                   'case-operation-verification-coupled',10,0,'Mill','started');
            INSERT INTO production_run_outputs(id,production_run_program_id,batch_operation_id,
                quantity_per_cycle,target_quantity,produced_quantity,status,version,created_at,updated_at)
            VALUES('output-verification-coupled','run-program-verification',
                   'operation-verification-coupled',2,2,0,'ALLOCATED',1,$at,$at);
            """;
        command.Parameters.AddWithValue("$at", Now.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static UpdateCncVerificationSettings Settings(string? secret, bool enabled) => new(
        "HAAS_DPRNT_TCP", 8080, 9001, 9002, 605, 10501, 10500, 10502, 10503,
        9003, 10504, secret, 3, 6, 300, enabled);

    private static CncVerificationFoundationService Service(SqliteDatabase database) => new(
        new SqliteCncVerificationFoundationRepository(database),
        new FixedTimeProvider(Now), new EphemeralDataProtectionProvider());

    private static async Task SeedEndToEndDeviceAndNextRunAsync(
        SqliteDatabase database, string tabletToken)
    {
        var credentialHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(tabletToken))).ToLowerInvariant();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE production_runs
            SET status='PLANNED',structure_locked_at=NULL
            WHERE id='run-verification';
            UPDATE production_run_programs
            SET target_cycle_count=3,production_gcode_release_id='gcode-verification'
            WHERE id='run-program-verification';
            UPDATE production_run_outputs
            SET target_quantity=3
            WHERE id='output-verification';
            INSERT INTO device_registry(
                id,device_type,device_name,machine_id,credential_hash,is_enabled,
                tablet_id,hardware_id,created_at,updated_at)
            VALUES('device-e2e','eink','E2E tablet','machine-verification',$hash,1,
                   'E2E-TABLET','AA:BB:CC:DD:EE:01',$at,$at);
            INSERT INTO eink_package_revisions(
                id,batch_operation_id,revision,published_at)
            VALUES('package-verification-e2e','operation-verification','R1',$at);
            INSERT INTO production_runs(
                id,status,shared_setup_seconds,setup_snapshot_json,version,created_at,updated_at)
            VALUES('run-verification-next','PLANNED',0,'{}',1,$at,$at);
            INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity)
            VALUES('batch-verification-next','case-verification','B-NEXT','planned',1);
            INSERT INTO batch_operations(
                id,production_batch_id,source_case_operation_id,operation_number,
                route_position,name,status)
            VALUES('operation-verification-next','batch-verification-next',
                   'case-operation-verification',10,0,'Mill','not_started');
            INSERT INTO production_run_programs(
                id,production_run_id,manufacturing_program_id,process_revision_id,
                selected_gcode_release_id,production_gcode_release_id,sequence_position,
                target_cycle_count,completed_cycle_count,status,legacy_unmanaged,version,
                created_at,updated_at)
            VALUES('run-program-verification-next','run-verification-next',
                   'case-operation:case-operation-verification','process-verification',
                   'gcode-verification','gcode-verification',0,1,0,'PLANNED',0,1,$at,$at);
            INSERT INTO production_run_outputs(
                id,production_run_program_id,batch_operation_id,quantity_per_cycle,
                target_quantity,produced_quantity,status,version,created_at,updated_at)
            VALUES('output-verification-next','run-program-verification-next',
                   'operation-verification-next',1,1,0,'ALLOCATED',1,$at,$at);
            UPDATE production_runs
            SET status='IN_PROGRESS',structure_locked_at=$at
            WHERE id='run-verification';
            """;
        command.Parameters.AddWithValue("$hash", credentialHash);
        command.Parameters.AddWithValue("$at", Now.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ReassignToNextRunAsync(SqliteDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE machine_assignments
            SET production_run_id='run-verification-next',
                batch_operation_id='operation-verification-next'
            WHERE id='assignment-verification';
            """;
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task SeedAsync(SqliteDatabase database)
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
            INSERT INTO gcode_releases(id,case_operation_id,process_revision_id,postprocessor_id,post_specific_revision,original_file_name,stored_relative_path,file_size,file_hash,released_at,released_by,change_scope,release_comment,tool_table_release_id,created_at,updated_at)
            VALUES('gcode-historical','case-operation-verification','process-verification','post-verification',2,'old.nc','gcode/old.nc',1,$hash,$at,'user','LOCAL_POST_REVISION','historical','tools-verification',$at,$at);
            INSERT INTO gcode_release_verification_hooks(gcode_release_id,hook_version,invocation_kind,invocation_number,nc_identity_token,line_number,created_at,updated_at)
            VALUES('gcode-verification',1,'G65',9002,654321,3,$at,$at);
            INSERT INTO production_runs(id,status,shared_setup_seconds,setup_snapshot_json,structure_locked_at,version,created_at,updated_at)
            VALUES('run-verification','PLANNED',0,'{}',NULL,1,$at,$at);
            INSERT INTO production_run_programs(id,production_run_id,manufacturing_program_id,process_revision_id,selected_gcode_release_id,production_gcode_release_id,sequence_position,target_cycle_count,completed_cycle_count,status,legacy_unmanaged,version,created_at,updated_at)
            VALUES('run-program-verification','run-verification','case-operation:case-operation-verification','process-verification','gcode-verification','gcode-historical',0,1,0,'ACTIVE',0,1,$at,$at);
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

    private sealed class AdvancingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        internal void Advance() => current = current.AddSeconds(1);
    }
}
