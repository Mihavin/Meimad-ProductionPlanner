using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class ManualTimingTests
{
    [Fact]
    public async Task Backfill_pages_every_retained_report_and_records_bad_evidence_without_inventing_samples()
    {
        await using var db = await OpenAsync();
        await Report(db, "first", "partTimeUpdate", "2026-01-01T08:00:00Z", 120);
        await Execute(db, """
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x < 5100)
            INSERT INTO structured_event_log(id,event_type,occurred_at,user_id,related_entity_ids_json,reason_code,after_data_json)
            SELECT 'unrelated-' || x, 'manual_operation_reported', '2026-02-01T08:00:00Z', 'tester',
                '{"batchOperationId":"missing","machineId":"machine"}', 'partTimeUpdate', '{"partTimeSeconds":30}' FROM n;
            INSERT INTO structured_event_log(id,event_type,occurred_at,user_id,related_entity_ids_json,reason_code,after_data_json)
            VALUES ('bad-json','manual_operation_reported','invalid-date','tester','{','setupEnd','{');
            """);
        await Migrate(db);
        Assert.Equal(5102L, await Scalar(db, "SELECT COUNT(*) FROM manual_timing_reports"));
        Assert.Equal(1L, await Scalar(db, "SELECT COUNT(*) FROM manual_timing_samples"));
        Assert.Equal(120d, await Scalar(db, "SELECT seconds FROM manual_timing_samples"));
        Assert.Equal(5101L, await Scalar(db, "SELECT COUNT(*) FROM manual_timing_warnings"));
        Assert.Equal(1L, await Scalar(db, "SELECT calculation_version FROM manual_timing_aggregate"));
    }

    [Fact]
    public async Task Sessions_pair_once_within_context_and_restart_or_finish_breaks_the_previous_session()
    {
        await using var db = await OpenAsync();
        await Migrate(db);
        await Report(db, "start-1", "setupStart", "2026-01-01T08:00:00Z");
        await Report(db, "end-1", "setupEnd", "2026-01-01T08:01:00Z");
        await Report(db, "end-duplicate", "setupEnd", "2026-01-01T08:02:00Z");
        await Report(db, "start-2", "setupStart", "2026-01-01T09:00:00Z");
        await Report(db, "restart", "setupStart", "2026-01-01T09:01:00Z");
        await Report(db, "end-2", "setupEnd", "2026-01-01T09:04:00Z");
        await Report(db, "start-3", "setupStart", "2026-01-01T10:00:00Z");
        await Report(db, "finished", "productionEnd", "2026-01-01T10:01:00Z");
        await Report(db, "orphan-end", "setupEnd", "2026-01-01T10:02:00Z");
        Assert.Equal(2L, await Scalar(db, "SELECT COUNT(*) FROM manual_timing_samples"));
        Assert.Equal(120d, await Scalar(db, "SELECT average_seconds FROM manual_timing_aggregate"));
        Assert.Equal("restart", await Scalar(db, "SELECT session_start_event_id FROM manual_timing_samples WHERE source_event_id='end-2'"));
        Assert.Equal("superseded_setup_start", await Scalar(db, "SELECT warning_code FROM manual_timing_warnings WHERE source_event_id='start-2'"));
        Assert.Equal("unmatched_setup_end", await Scalar(db, "SELECT warning_code FROM manual_timing_warnings WHERE source_event_id='orphan-end'"));
    }

    [Fact]
    public async Task Duplicate_source_is_idempotent_and_rollback_cannot_publish_partial_projection()
    {
        await using var db = await OpenAsync();
        await Migrate(db);
        await Report(db, "one", "partTimeUpdate", "2026-01-01T08:00:00Z", 60);
        await Report(db, "one", "partTimeUpdate", "2026-01-01T08:00:00Z", 60);
        Assert.Equal(1L, await Scalar(db, "SELECT COUNT(*) FROM manual_timing_samples"));
        await Execute(db, "BEGIN IMMEDIATE;");
        await Report(db, "rolled-back", "partTimeUpdate", "2026-01-01T09:00:00Z", 90);
        await Execute(db, "ROLLBACK;");
        Assert.Equal(1L, await Scalar(db, "SELECT COUNT(*) FROM manual_timing_reports"));
        Assert.Equal(1L, await Scalar(db, "SELECT COUNT(*) FROM structured_event_log"));
    }

    [Fact]
    public async Task Invalid_and_unsupported_corrections_are_visible_and_open_sessions_stay_unknown()
    {
        await using var db = await OpenAsync();
        await Migrate(db);
        await Report(db, "zero", "partTimeUpdate", "2026-01-01T08:00:00Z", 0);
        await Report(db, "negative", "partTimeUpdate", "2026-01-01T08:00:00Z", -1);
        await Report(db, "correction", "corrected", "2026-01-01T08:00:00Z", 30);
        await Report(db, "retraction", "retracted", "2026-01-01T08:00:00Z", 30);
        await Report(db, "open", "setupStart", "2026-01-01T09:00:00Z");
        Assert.Equal(0L, await Scalar(db, "SELECT COUNT(*) FROM manual_timing_samples"));
        Assert.Equal(5L, await Scalar(db, "SELECT COUNT(*) FROM manual_timing_warnings"));
        Assert.Equal("open_setup_session", await Scalar(db, "SELECT warning_code FROM manual_timing_warnings WHERE source_event_id='open'"));
    }

    [Fact]
    public async Task Backdated_report_advances_checkpoint_and_session_pairing_uses_event_time()
    {
        await using var db = await OpenAsync();
        await Migrate(db);
        await Report(db, "end", "setupEnd", "2026-01-01T08:01:00Z");
        Assert.Equal(0L, await Scalar(db, "SELECT COUNT(*) FROM manual_timing_samples"));
        await Report(db, "start", "setupStart", "2026-01-01T08:00:00Z");
        Assert.Equal(60d, await Scalar(db, "SELECT seconds FROM manual_timing_samples"));
        Assert.Equal(2L, await Scalar(db, "SELECT MAX(source_sequence) FROM manual_timing_reports"));
    }

    [Fact]
    public async Task Interrupted_backfill_rolls_back_and_can_restart_from_retained_evidence()
    {
        await using var db = await OpenAsync();
        await Report(db, "retained", "partTimeUpdate", "2026-01-01T08:00:00Z", 75);
        using (var tx = db.BeginTransaction())
        {
            await new SchemaV98ManualTimingMigration().ApplyAsync(db, tx, default);
            tx.Rollback();
        }
        Assert.Equal(0L, await Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE name='manual_timing_reports'"));
        await Migrate(db);
        Assert.Equal(75d, await Scalar(db, "SELECT seconds FROM manual_timing_samples"));
        Assert.Equal(1L, await Scalar(db, "SELECT COUNT(*) FROM structured_event_log"));
    }

    [Fact]
    public async Task Setup_never_pairs_across_machine_or_explicit_run_context()
    {
        await using var db = await OpenAsync();
        await Execute(db, "INSERT INTO machines VALUES('other-machine'); INSERT INTO production_runs VALUES('run-a'),('run-b');");
        await Migrate(db);
        await Report(db, "start", "setupStart", "2026-01-01T08:00:00Z", machine: "machine", run: "run-a");
        await Report(db, "wrong-machine", "setupEnd", "2026-01-01T08:01:00Z", machine: "other-machine", run: "run-a");
        await Report(db, "wrong-run", "setupEnd", "2026-01-01T08:02:00Z", machine: "machine", run: "run-b");
        Assert.Equal(0L, await Scalar(db, "SELECT COUNT(*) FROM manual_timing_samples"));
        await Report(db, "right-end", "setupEnd", "2026-01-01T08:03:00Z", machine: "machine", run: "run-a");
        Assert.Equal(180d, await Scalar(db, "SELECT seconds FROM manual_timing_samples"));
    }

    [Fact]
    public async Task Compaction_of_source_rowids_does_not_reuse_projection_checkpoints()
    {
        await using var db = await OpenAsync();
        await Report(db, "retained", "partTimeUpdate", "2026-01-01T08:00:00Z", 75);
        await Execute(db, "UPDATE structured_event_log SET rowid=100;");
        await Migrate(db);
        await Execute(db, "VACUUM;");
        await Report(db, "new", "partTimeUpdate", "2026-01-01T07:00:00Z", 80);
        Assert.Equal(101L, await Scalar(db, "SELECT MAX(source_sequence) FROM manual_timing_reports"));
        Assert.Equal(2L, await Scalar(db, "SELECT COUNT(*) FROM manual_timing_samples"));
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        await db.OpenAsync();
        await Execute(db, "CREATE TABLE batch_operations(id TEXT PRIMARY KEY); INSERT INTO batch_operations VALUES('operation'); CREATE TABLE machines(id TEXT PRIMARY KEY); INSERT INTO machines VALUES('machine'); CREATE TABLE production_runs(id TEXT PRIMARY KEY);");
        using var tx = db.BeginTransaction();
        await new SchemaV22StructuredEventLogMigration().ApplyAsync(db, tx, default);
        tx.Commit();
        return db;
    }

    private static async Task Migrate(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();
        await new SchemaV98ManualTimingMigration().ApplyAsync(db, tx, default);
        tx.Commit();
    }

    private static async Task Report(SqliteConnection db, string id, string type, string at, int? seconds = null,
        string machine = "machine", string? run = null)
    {
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO structured_event_log(id,event_type,occurred_at,user_id,related_entity_ids_json,reason_code,after_data_json)
            VALUES($id,'manual_operation_reported',$at,'tester',$context,$type,$data);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$at", at);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$data", System.Text.Json.JsonSerializer.Serialize(new { partTimeSeconds = seconds }));
        command.Parameters.AddWithValue("$context", System.Text.Json.JsonSerializer.Serialize(new { batchOperationId = "operation", machineId = machine, productionRunId = run }));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task Execute(SqliteConnection db, string sql)
    {
        await using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private static async Task<object?> Scalar(SqliteConnection db, string sql)
    {
        await using var command = db.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync();
    }
}
