using Meimad.Planner.Server.Application.Kitaron.Push;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Kitaron;

public sealed class KitaronPushIntentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T09:00:00Z");
    private static KitaronPushIntent Intent() => new("isolated-test", 1433, "test", 1,
        new(false, 15, [], 1, Now, "planner"), [], new Dictionary<string, KitaronPushForecast>(),
        [new(1, 42, "10", "OperationQty", KitaronStoredValue.From(6d),
            new Dictionary<string, KitaronStoredValue> { ["OperationQty"] = KitaronStoredValue.From(1.000001m) })], [], "stamp");

    [Theory]
    [InlineData("Preparing", "FailedBeforeCommit")]
    [InlineData("Prepared", "FailedBeforeCommit")]
    [InlineData("Writing", "OutcomeUnknown")]
    public async Task Restart_never_replays_an_intent_and_preserves_its_evidence(string phase, string result)
    {
        await using var db = await TemporaryDatabase.CreateAsync();
        var repository = new SqliteKitaronPushRepository(db.Database);
        await repository.StartRunAsync("run", "manual", "planner", Now, default);
        if (phase != "Preparing") await repository.PrepareIntentAsync("run", Intent(), Now, default);
        if (phase == "Writing") Assert.True(await repository.ClaimIntentAsync("run", default));
        var restarted = new SqliteKitaronPushRepository(db.Database);
        await restarted.RecoverInterruptedAsync(default);
        var resource = await restarted.ReadIntentAsync("run", default);
        Assert.Equal(result, resource.State);
        Assert.False(await restarted.ClaimIntentAsync("run", default));
        if (phase != "Preparing")
            Assert.Equal(1.000001m, resource.Intent!.Writes[0].ExpectedValues["OperationQty"].Value());
        var version = resource.Version;
        await restarted.RecoverInterruptedAsync(default);
        Assert.Equal(version, (await restarted.ReadIntentAsync("run", default)).Version);
    }

    [Fact]
    public async Task Claim_and_concurrent_reconciliation_are_compare_and_swap_and_unresolved_runs_are_not_pruned()
    {
        await using var db = await TemporaryDatabase.CreateAsync();
        var repository = new SqliteKitaronPushRepository(db.Database);
        await repository.StartRunAsync("run", "manual", "planner", Now.AddDays(-100), default);
        await repository.PrepareIntentAsync("run", Intent(), Now, default);
        var claims = await Task.WhenAll(repository.ClaimIntentAsync("run", default),
            new SqliteKitaronPushRepository(db.Database).ClaimIntentAsync("run", default));
        Assert.Single(claims, x => x);
        await repository.RecoverInterruptedAsync(default);
        var unknown = await repository.ReadIntentAsync("run", default);
        await Assert.ThrowsAsync<KitaronPushBlockedException>(() => repository.StartRunAsync("other", "automatic", null, Now, default));
        await Assert.ThrowsAsync<KitaronPushConflictException>(() => repository.AcknowledgeAsync("run", unknown.Version, "planner", Now, default));
        var evidence = new KitaronReconciliation("reviewer", Now, [], "No attribution proven.");
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Record.ExceptionAsync(() =>
            new SqliteKitaronPushRepository(db.Database).RecordReconciliationAsync("run", unknown.Version, evidence, default))));
        Assert.Single(results, x => x is null);
        Assert.IsType<KitaronPushConflictException>(Assert.Single(results, x => x is not null));
        var inspected = await repository.ReadIntentAsync("run", default);
        Assert.Equal("OutcomeUnknown", inspected.State);
        await repository.AcknowledgeAsync("run", inspected.Version, "reviewer", Now, default);
        var reviewed = await repository.ReadIntentAsync("run", default);
        Assert.Equal("Reconciled", reviewed.State);
        Assert.Equal(2, reviewed.Reconciliations.Count);
        Assert.False(await repository.ClaimIntentAsync("run", default));
        await repository.StartRunAsync("other", "automatic", null, Now, default);
    }

    [Fact]
    public async Task Intent_is_immutable_and_typed_values_round_trip_without_display_rounding()
    {
        await using var db = await TemporaryDatabase.CreateAsync();
        var repository = new SqliteKitaronPushRepository(db.Database);
        await repository.StartRunAsync("run", "manual", "planner", Now, default);
        await repository.PrepareIntentAsync("run", Intent(), Now, default);
        await using var sql = await db.Database.OpenConnectionAsync();
        await using var command = sql.CreateCommand();
        command.CommandText = "UPDATE kitaron_push_intents SET payload_json='{}' WHERE run_id='run'";
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        foreach (var value in new object?[] { null, 0, (short)1, (byte)2, 3L, 1.000001m, 1.000000001d, 1.25f,
            new DateTime(2026, 10, 8, 9, 0, 0).AddTicks(30000), "010" })
        {
            var serialized = System.Text.Json.JsonSerializer.Serialize(KitaronStoredValue.From(value));
            Assert.Equal(value, System.Text.Json.JsonSerializer.Deserialize<KitaronStoredValue>(serialized)!.Value());
        }
    }

    [Fact]
    public async Task Upgrade_preserves_history_and_does_not_invent_intents_or_commit_proof()
    {
        await using var db = await TemporaryDatabase.CreateAsync();
        await using var sql = await db.Database.OpenConnectionAsync();
        await using var command = sql.CreateCommand();
        command.CommandText = """
            DROP TABLE kitaron_push_reconciliations;
            DROP TABLE kitaron_push_intents;
            ALTER TABLE kitaron_push_runs DROP COLUMN lifecycle_state;
            ALTER TABLE kitaron_push_runs DROP COLUMN intent_version;
            DELETE FROM schema_migrations WHERE version=101;
            PRAGMA user_version=100;
            INSERT INTO kitaron_push_runs(id,trigger,started_at,status)
            VALUES('old-failed','manual','2026-10-07T09:00:00Z','failed'),
                ('old-running','manual','2026-10-07T09:01:00Z','running'),
                ('old-success','manual','2026-10-07T09:02:00Z','succeeded');
            """;
        await command.ExecuteNonQueryAsync();
        await new DatabaseMigrator(db.Database, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();
        var repository = new SqliteKitaronPushRepository(db.Database);
        Assert.Equal("OutcomeUnknown", (await repository.ReadIntentAsync("old-failed", default)).State);
        Assert.Equal("OutcomeUnknown", (await repository.ReadIntentAsync("old-running", default)).State);
        Assert.Null((await repository.ReadIntentAsync("old-running", default)).Intent);
        Assert.Equal("Succeeded", (await repository.ReadIntentAsync("old-success", default)).State);
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", await command.ExecuteScalarAsync());
    }
}
