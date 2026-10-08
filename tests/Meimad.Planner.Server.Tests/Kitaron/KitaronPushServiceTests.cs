using Meimad.Planner.Server.Application.Concurrency;
using Meimad.Planner.Server.Application.Kitaron;
using Meimad.Planner.Server.Application.Kitaron.Push;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Kitaron;

public sealed class KitaronPushServiceTests
{
    [Fact]
    public async Task External_write_requires_a_committed_intent_and_Writing_claim()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(fixture.Database, protection, connectorEnabled: true);
        var repository = new SqliteKitaronPushRepository(fixture.Database);
        var target = new FakeKitaron
        {
            BeforeWrite = async () =>
            {
                var run = Assert.Single(await repository.ListRunsAsync(20, default));
                var intent = await repository.ReadIntentAsync(run.RunId, default);
                Assert.Equal("Writing", intent.State);
                Assert.Equal(4, intent.Intent!.Writes.Count);
                Assert.Single(intent.Intent.Forecasts);
            }
        };
        await Service(fixture.Database, protection, target).RunAsync("manual", "planner", default);
        Assert.Equal(1, target.WriteCalls);
    }

    [Fact]
    public async Task Failure_to_persist_intent_never_calls_ERP_writer()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(fixture.Database, protection, connectorEnabled: true);
        await using var sql = await fixture.Database.OpenConnectionAsync();
        await using var command = sql.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_intent BEFORE INSERT ON kitaron_push_intents BEGIN SELECT RAISE(ABORT,'injected storage failure'); END;";
        await command.ExecuteNonQueryAsync();
        var target = new FakeKitaron();
        var service = Service(fixture.Database, protection, target);
        await Assert.ThrowsAsync<KitaronPushBlockedException>(() => service.RunAsync("manual", "planner", default));
        Assert.Equal(0, target.WriteCalls);
        Assert.Equal("FailedBeforeCommit", Assert.Single(await service.ListRunsAsync(default)).LifecycleState);
    }

    [Fact]
    public async Task Lost_response_after_external_write_preserves_intent_and_blocks_retry_until_review()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(fixture.Database, protection, connectorEnabled: true);
        var target = new FakeKitaron { FailAfterWrites = true };
        var service = Service(fixture.Database, protection, target);
        var failure = await Assert.ThrowsAsync<KitaronPushOutcomeUnknownException>(() =>
            service.RunAsync("manual", "planner", CancellationToken.None));
        Assert.DoesNotContain("nothing was written", failure.Message, StringComparison.OrdinalIgnoreCase);
        var intent = await service.ReadIntentAsync(failure.RunId, CancellationToken.None);
        Assert.Equal("OutcomeUnknown", intent.State);
        Assert.Equal(4, intent.Intent!.Writes.Count);
        Assert.Single(intent.Intent.Operations);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(intent), StringComparison.Ordinal);
        Assert.Equal(4, target.Written.Count);
        await Assert.ThrowsAsync<KitaronPushBlockedException>(() => service.RunAsync("automatic", null, CancellationToken.None));
        Assert.Equal(1, target.WriteCalls);

        var inspected = await service.ReconcileAsync(intent.RunId, intent.Version, "reviewer", CancellationToken.None);
        Assert.Equal("OutcomeUnknown", inspected.State);
        Assert.All(Assert.Single(inspected.Reconciliations).Values, x => Assert.Equal("MatchesIntent", x.Comparison));
        await Assert.ThrowsAsync<KitaronPushConflictException>(() => service.AcknowledgeAsync(intent.RunId, intent.Version, "reviewer", CancellationToken.None));
        var resolved = await service.AcknowledgeAsync(intent.RunId, inspected.Version, "reviewer", CancellationToken.None);
        Assert.Equal("Reconciled", resolved.State);
        Assert.Equal("Acknowledgement", resolved.Reconciliations.Last().Kind);
        Assert.Equal(1, target.WriteCalls);
        Assert.Empty((await service.RunAsync("manual", "planner", CancellationToken.None)).Changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receipt_failure_after_commit_keeps_uncertainty_durable_across_restart(bool failEveryReceipt)
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(fixture.Database, protection, connectorEnabled: true);
        await using var sql = await fixture.Database.OpenConnectionAsync();
        await using var command = sql.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_receipt BEFORE UPDATE ON kitaron_push_runs WHEN NEW.lifecycle_state IN ("
            + (failEveryReceipt ? "'Succeeded','OutcomeUnknown'" : "'Succeeded'")
            + ") BEGIN SELECT RAISE(ABORT,'injected receipt failure'); END;";
        await command.ExecuteNonQueryAsync();
        var target = new FakeKitaron();
        var service = Service(fixture.Database, protection, target);
        var failure = await Assert.ThrowsAsync<KitaronPushOutcomeUnknownException>(() => service.RunAsync("manual", "planner", CancellationToken.None));
        var durable = await service.ReadIntentAsync(failure.RunId, CancellationToken.None);
        Assert.Equal(failEveryReceipt ? "Writing" : "OutcomeUnknown", durable.State);
        Assert.Equal(4, target.Written.Count);
        command.CommandText = "DROP TRIGGER fail_receipt;";
        await command.ExecuteNonQueryAsync();
        var restarted = Service(fixture.Database, protection, target);
        await restarted.RecoverInterruptedAsync(CancellationToken.None);
        Assert.Equal("OutcomeUnknown", (await restarted.ReadIntentAsync(failure.RunId, CancellationToken.None)).State);
        await Assert.ThrowsAsync<KitaronPushBlockedException>(() => restarted.RunAsync("automatic", null, CancellationToken.None));
        Assert.Equal(1, target.WriteCalls);
    }

    [Theory]
    [InlineData(false, "FailedBeforeCommit")]
    [InlineData(true, "OutcomeUnknown")]
    public async Task Cancellation_records_whether_an_external_write_was_attempted(bool duringWrite, string expected)
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(fixture.Database, protection, connectorEnabled: true);
        var service = Service(fixture.Database, protection, new FakeKitaron { CancelReads = !duringWrite, CancelWrites = duringWrite });
        var error = await Record.ExceptionAsync(() => service.RunAsync("manual", "planner", CancellationToken.None));
        Assert.NotNull(error);
        if (duringWrite) Assert.IsType<KitaronPushOutcomeUnknownException>(error);
        else Assert.IsType<OperationCanceledException>(error);
        Assert.Equal(expected, Assert.Single(await service.ListRunsAsync(CancellationToken.None)).LifecycleState);
    }

    [Fact]
    public async Task A_stale_displayed_preview_is_refused_before_external_write()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(fixture.Database, protection, connectorEnabled: true);
        var target = new FakeKitaron();
        var service = Service(fixture.Database, protection, target);
        var preview = await service.PreviewAsync(CancellationToken.None);
        Assert.NotNull(preview.PreviewStamp);
        target.EditQuantity(7d);
        await Assert.ThrowsAsync<KitaronPushConflictException>(() =>
            service.RunAsync("manual", "planner", CancellationToken.None, preview.PreviewStamp));
        Assert.Empty(target.Written);
        Assert.Equal("failed", Assert.Single(await service.ListRunsAsync(CancellationToken.None)).Status);
        var fresh = await service.PreviewAsync(CancellationToken.None);
        Assert.NotEqual(preview.PreviewStamp, fresh.PreviewStamp);
        Assert.True((await service.RunAsync("manual", "planner", CancellationToken.None, fresh.PreviewStamp)).Applied);
    }

    [Fact]
    public async Task A_transaction_comparison_conflict_is_logged_without_retry()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(fixture.Database, protection, connectorEnabled: true);
        var target = new FakeKitaron { ConflictWrites = true };
        var service = Service(fixture.Database, protection, target);
        var error = await Assert.ThrowsAsync<KitaronPushConflictException>(() =>
            service.RunAsync("manual", "planner", CancellationToken.None));
        Assert.Contains("Refresh Preview", error.Message, StringComparison.Ordinal);
        Assert.Empty(target.Written);
        Assert.Equal(1, target.WriteCalls);
        Assert.Equal(0, Assert.Single(await service.ListRunsAsync(CancellationToken.None)).ValuesWritten);
    }

    [Fact]
    public async Task A_push_writes_the_Planner_values_logs_them_and_a_second_push_writes_nothing()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(fixture.Database, protection, connectorEnabled: true);
        var kitaron = new FakeKitaron();
        var service = Service(fixture.Database, protection, kitaron);

        var preview = await service.PreviewAsync(CancellationToken.None);
        Assert.False(preview.Applied);
        Assert.Equal(4, preview.Changes.Count);
        Assert.Empty(kitaron.Written);

        var result = await service.RunAsync("manual", "planner", CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(1, result.OperationsMatched);
        var written = kitaron.Written.ToDictionary(write => write.Column, write => write.Value);
        Assert.Equal(6.0, written["OperationQty"]);                                        // produced so far
        Assert.Equal(new DateTime(2026, 9, 28, 9, 0, 0), written["StartDateReal"]);       // 06:00Z in Israel
        Assert.Equal(90.0, written["SetupTimeReal"]);                                     // start to QC PASS
        Assert.Equal(new DateTime(2026, 9, 30, 17, 30, 0), written["FinishDateCalc"]);    // Timeline finish
        Assert.All(kitaron.Written, write => Assert.Equal(5001L, write.RowId));

        var run = Assert.Single(await service.ListRunsAsync(CancellationToken.None));
        Assert.Equal("succeeded", run.Status);
        Assert.Equal("manual", run.Trigger);
        Assert.Equal("planner", run.RequestedBy);
        Assert.Equal(4, run.ValuesWritten);
        var changes = await service.ListChangesAsync(run.RunId, CancellationToken.None);
        Assert.Contains(changes, change => change is { KitaronColumn: "SetupTimeReal", NewValue: "90", OldValue: null });

        var again = await service.RunAsync("manual", "planner", CancellationToken.None);
        Assert.Empty(again.Changes);
        Assert.Equal(4, kitaron.Written.Count);
    }

    [Fact]
    public async Task A_switched_off_connector_blocks_the_push_and_the_failed_run_is_logged()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(fixture.Database, protection, connectorEnabled: false);
        var kitaron = new FakeKitaron();
        var service = Service(fixture.Database, protection, kitaron);

        var blocked = await Assert.ThrowsAsync<KitaronPushBlockedException>(() =>
            service.RunAsync("automatic", null, CancellationToken.None));

        Assert.Contains("switched off", blocked.Message, StringComparison.Ordinal);
        Assert.Empty(kitaron.Written);
        var run = Assert.Single(await service.ListRunsAsync(CancellationToken.None));
        Assert.Equal("failed", run.Status);
        Assert.Equal(blocked.Message, run.Message);
    }

    [Fact]
    public async Task A_Kitaron_failure_writes_nothing_and_is_reported()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(fixture.Database, protection, connectorEnabled: true);
        var service = Service(fixture.Database, protection, new FakeKitaron { FailWrites = true });

        var blocked = await Assert.ThrowsAsync<KitaronPushBlockedException>(() =>
            service.RunAsync("manual", "planner", CancellationToken.None));

        Assert.StartsWith("Push failed before ERP commit; nothing was written by this run.", blocked.Message, StringComparison.Ordinal);
        var run = Assert.Single(await service.ListRunsAsync(CancellationToken.None));
        Assert.Equal("failed", run.Status);
        Assert.Equal(0, run.ValuesWritten);
    }

    [Fact]
    public async Task Settings_accept_only_pushable_columns_with_matching_values_and_refuse_a_stale_version()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var service = Service(fixture.Database, new EphemeralDataProtectionProvider(), new FakeKitaron());
        var settings = await service.GetSettingsAsync(CancellationToken.None);
        Assert.False(settings.Enabled);
        Assert.Equal(
            ["OperationQty", "StartDateReal", "FinishDateCalc", "SetupTimeReal"],
            settings.Mappings.Select(mapping => mapping.KitaronColumn));

        var wrongKind = await Assert.ThrowsAsync<KitaronPushValidationException>(() => service.UpdateSettingsAsync(
            true, 15, [new("StartDateReal", "good_quantity", true)], settings.Version, "admin", CancellationToken.None));
        Assert.Contains("holds a date", wrongKind.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<KitaronPushValidationException>(() => service.UpdateSettingsAsync(
            true, 15, [new("StartDate", "actual_start", true)], settings.Version, "admin", CancellationToken.None));
        await Assert.ThrowsAsync<KitaronPushValidationException>(() => service.UpdateSettingsAsync(
            true, 15, [new("OperationQty", "good_quantity", true), new("operationqty", "planned_quantity", true)],
            settings.Version, "admin", CancellationToken.None));
        await Assert.ThrowsAsync<KitaronPushValidationException>(() => service.UpdateSettingsAsync(
            true, 2, [], settings.Version, "admin", CancellationToken.None));

        var saved = await service.UpdateSettingsAsync(
            true, 30, [new("finishdatecalc", "FORECAST_FINISH", true)], settings.Version, "admin", CancellationToken.None);
        Assert.True(saved.Enabled);
        Assert.Equal(30, saved.IntervalMinutes);
        Assert.Equal(new KitaronPushMapping("FinishDateCalc", "forecast_finish", true), Assert.Single(saved.Mappings));
        Assert.Equal("admin", saved.UpdatedBy);

        var stale = await Assert.ThrowsAsync<EditConflictException>(() => service.UpdateSettingsAsync(
            false, 15, [], settings.Version, "rina", CancellationToken.None));
        Assert.Equal("admin", stale.ChangedBy);
    }

    private static KitaronPushService Service(SqliteDatabase database, IDataProtectionProvider protection, FakeKitaron kitaron) =>
        new(new SqliteKitaronPushRepository(database), new SqliteKitaronConnectionRepository(database), kitaron,
            new FakeForecast(), protection, new TimelineOptions(), TimeProvider.System,
            NullLogger<KitaronPushService>.Instance);

    private static async Task SeedAsync(SqliteDatabase database, IDataProtectionProvider protection, bool connectorEnabled)
    {
        var password = protection.CreateProtector("Meimad.Planner.Kitaron.SqlPassword.v1").Protect("secret");
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        // The run chain only needs its keys here; the other planning references are not under test.
        command.CommandText = """
            PRAGMA foreign_keys = OFF;
            UPDATE kitaron_connection_settings SET enabled = $enabled, protected_password = $password WHERE id = 1;
            INSERT INTO cases (id, part_number, name, working_folder_path) VALUES ('case-1', 'PN-1', 'Part', 'C:\Cases\PN-1');
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-1', 'case-1', '41043', 'in_production', 20);
            INSERT INTO kitaron_sync_links (source_entity, source_key, target_id, owns_target, source_hash, first_seen_at, last_seen_at)
            VALUES ('production_batch', 'wo:41043', 'batch-1', 1, 'hash', '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z');
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name) VALUES ('case-op-1', 'case-1', 10, 0, 'Mill');
            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id, operation_number, route_position, name, status, actual_start)
            VALUES ('op-1', 'batch-1', 'case-op-1', 10, 0, 'Mill', 'in_progress', '2026-09-28T06:00:00.0000000+00:00');
            INSERT INTO production_runs (id, status, created_at, updated_at) VALUES ('run-1', 'IN_PROGRESS', '2026-09-28T06:00:00Z', '2026-09-28T06:00:00Z');
            INSERT INTO production_run_programs (
                id, production_run_id, manufacturing_program_id, sequence_position, target_cycle_count, completed_cycle_count,
                status, legacy_unmanaged, created_at, updated_at)
            VALUES ('program-1', 'run-1', 'mp-1', 0, 20, 6, 'ACTIVE', 1, '2026-09-28T06:00:00Z', '2026-09-28T06:00:00Z');
            INSERT INTO production_run_outputs (
                id, production_run_program_id, batch_operation_id, quantity_per_cycle, target_quantity, produced_quantity,
                status, created_at, updated_at)
            VALUES ('output-1', 'program-1', 'op-1', 1, 20, 6, 'IN_PRODUCTION', '2026-09-28T06:00:00Z', '2026-09-28T06:00:00Z');
            INSERT INTO production_run_workflow_events (
                id, production_run_id, machine_id, event_type, source, source_event_id, server_received_at, user_id, metadata_json)
            VALUES ('qc-early', 'run-1', 'machine-1', 'QC_PASS', 'WINDOWS_QC', 'QC:early', '2026-09-27T12:00:00.0000000+00:00', 'qc', '{}'),
                   ('qc-pass', 'run-1', 'machine-1', 'QC_PASS', 'WINDOWS_QC', 'QC:1', '2026-09-28T07:30:00.0000000+00:00', 'qc', '{}');
            PRAGMA foreign_keys = ON;
            """;
        command.Parameters.AddWithValue("$enabled", connectorEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$password", password);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FakeForecast : IKitaronPushForecast
    {
        public Task<IReadOnlyDictionary<string, KitaronPushForecast>> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, KitaronPushForecast>>(new Dictionary<string, KitaronPushForecast>
            {
                ["op-1"] = new(DateTimeOffset.Parse("2026-09-28T06:00:00Z"), DateTimeOffset.Parse("2026-09-30T14:30:00Z"))
            });
    }

    /// <summary>One Kitaron operation row of Work Order 41043 that keeps what the push writes.</summary>
    private sealed class FakeKitaron : IKitaronPushTarget
    {
        private readonly Dictionary<string, object?> values = new(StringComparer.OrdinalIgnoreCase);

        internal bool FailWrites { get; init; }
        internal bool ConflictWrites { get; init; }
        internal bool FailAfterWrites { get; init; }
        internal bool CancelReads { get; init; }
        internal bool CancelWrites { get; init; }
        internal Func<Task>? BeforeWrite { get; init; }
        internal int WriteCalls { get; private set; }
        internal void EditQuantity(double value) => values["OperationQty"] = value;

        internal List<KitaronPushWrite> Written { get; } = [];

        public Task<IReadOnlyList<KitaronOperationRow>> ReadAsync(
            StoredKitaronConnectionSettings connection, string password, IReadOnlyCollection<int> workOrderNumbers,
            IReadOnlyList<string> columns, CancellationToken cancellationToken)
        {
            if (CancelReads) throw new OperationCanceledException();
            Assert.Equal("secret", password);
            Assert.Equal([41043], workOrderNumbers);
            return Task.FromResult<IReadOnlyList<KitaronOperationRow>>(
            [
                new(5001, 41043, "10", false, columns.ToDictionary(column => column, column => values.GetValueOrDefault(column)))
            ]);
        }

        public async Task WriteAsync(
            StoredKitaronConnectionSettings connection, string password, IReadOnlyList<KitaronPushWrite> writes,
            CancellationToken cancellationToken)
        {
            WriteCalls++;
            if (BeforeWrite is not null) await BeforeWrite();
            if (CancelWrites) throw new OperationCanceledException();
            if (ConflictWrites) throw KitaronPushComparison.Conflict(5001);
            if (FailWrites) throw new KitaronPushNotCommittedException("Transaction rolled back before commit.");
            foreach (var write in writes)
            {
                values[write.Column] = write.Value;
                Written.Add(write);
            }
            if (FailAfterWrites) throw new IOException("Connection lost around ERP commit.");
        }
    }
}
