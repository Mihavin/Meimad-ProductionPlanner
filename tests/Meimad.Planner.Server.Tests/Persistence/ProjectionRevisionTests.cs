using Meimad.Planner.Server.Persistence;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class ProjectionRevisionTests
{
    [Fact]
    public async Task Existing_database_upgrade_preserves_rows_and_AI_metadata_changes_do_not_invalidate_planning()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        var migrator = new DatabaseMigrator(fixture.Database, Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(107, default);
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO local_ai_requests(id,owner_id,intent,status,created_at) VALUES('existing','owner','ExplainBlockers','COMPLETED','2026-10-08T00:00:00Z')";
        await command.ExecuteNonQueryAsync();
        await migrator.MigrateAsync();
        var planning = new SqliteProjectionRevisionReader(fixture.Database);
        var evidence = new SqliteProjectionRevisionReader(fixture.Database, includeOperationalEvidence: true);
        var before = await planning.ReadAsync(default);
        var evidenceBefore = await evidence.ReadAsync(default);
        command.CommandText = "UPDATE local_ai_requests SET result_code='DETERMINISTIC' WHERE id='existing'";
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        Assert.Equal(before, await planning.ReadAsync(default));
        Assert.NotEqual(evidenceBefore, await evidence.ReadAsync(default));
        await using (var transaction = connection.BeginTransaction())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM local_ai_requests WHERE id='existing'";
            await command.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }
        Assert.Equal(before, await planning.ReadAsync(default));
        command.Transaction = null;
        command.CommandText = "SELECT count(*) FROM local_ai_requests WHERE id='existing'";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Diagnostic_changes_invalidate_evidence_only_and_manual_moves_invalidate_both_scopes()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var planning = new SqliteProjectionRevisionReader(fixture.Database);
        var evidence = new SqliteProjectionRevisionReader(fixture.Database, includeOperationalEvidence: true);
        var originalPlan = await planning.ReadAsync(default);
        var originalEvidence = await evidence.ReadAsync(default);
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO structured_event_log(id,event_type,occurred_at,user_id,related_entity_ids_json) VALUES('diagnostic','connection_observed','2026-10-08T00:00:00Z','test','{}')";
        await command.ExecuteNonQueryAsync();
        Assert.Equal(originalPlan, await planning.ReadAsync(default));
        var inserted = await evidence.ReadAsync(default);
        Assert.NotEqual(originalEvidence, inserted);
        command.CommandText = "UPDATE structured_event_log SET reason_code='late' WHERE id='diagnostic'";
        await command.ExecuteNonQueryAsync();
        Assert.Equal(originalPlan, await planning.ReadAsync(default));
        Assert.NotEqual(inserted, await evidence.ReadAsync(default));
        command.CommandText = "UPDATE structured_event_log SET event_type='manual_backlog_reorder' WHERE id='diagnostic'";
        await command.ExecuteNonQueryAsync();
        var moved = await planning.ReadAsync(default);
        Assert.NotEqual(originalPlan, moved);
        command.CommandText = "UPDATE structured_event_log SET event_type='timeline_conflict_detected' WHERE id='diagnostic'";
        await command.ExecuteNonQueryAsync();
        Assert.NotEqual(moved, await planning.ReadAsync(default));
        var beforeDelete = await planning.ReadAsync(default);
        var evidenceBeforeDelete = await evidence.ReadAsync(default);
        command.CommandText = "DELETE FROM structured_event_log WHERE id='diagnostic'";
        await command.ExecuteNonQueryAsync();
        Assert.Equal(beforeDelete, await planning.ReadAsync(default));
        Assert.NotEqual(evidenceBeforeDelete, await evidence.ReadAsync(default));
    }

    [Fact]
    public async Task Derived_diagnostics_do_not_invalidate_their_own_projection_but_source_events_do()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var revisions = new SqliteProjectionRevisionReader(fixture.Database);
        var before = await revisions.ReadAsync(default);
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO structured_event_log(id,event_type,occurred_at,user_id,related_entity_ids_json)
            VALUES('diag','timeline_conflict_detected','2026-10-08T00:00:00Z','test','{}'),
                  ('wait','resource_wait_detected','2026-10-08T00:00:00Z','test','{}');
            """;
        await command.ExecuteNonQueryAsync();
        Assert.Equal(before, await revisions.ReadAsync(default));
        command.CommandText = """
            INSERT INTO structured_event_log(id,event_type,occurred_at,user_id,related_entity_ids_json)
            VALUES('move','manual_backlog_reorder','2026-10-08T00:00:00Z','test','{}');
            """;
        await command.ExecuteNonQueryAsync();
        Assert.NotEqual(before, await revisions.ReadAsync(default));
    }
    [Fact]
    public async Task Revision_changes_with_committed_inputs_but_rolls_back_with_them()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var revisions = new SqliteProjectionRevisionReader(fixture.Database);
        var before = await revisions.ReadAsync(default);
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using (var tx = connection.BeginTransaction())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO application_settings(key,value) VALUES('revision-test','first')";
            await command.ExecuteNonQueryAsync();
            // A separate reader cannot observe an uncommitted revision.
            Assert.Equal(before, await revisions.ReadAsync(default));
            await tx.RollbackAsync();
        }
        Assert.Equal(before, await revisions.ReadAsync(default));
        await using var write = connection.CreateCommand();
        write.CommandText = "INSERT INTO application_settings(key,value) VALUES('revision-test','committed')";
        await write.ExecuteNonQueryAsync();
        var inserted = await revisions.ReadAsync(default);
        Assert.NotEqual(before, inserted);
        write.CommandText = "UPDATE application_settings SET value='updated' WHERE key='revision-test'";
        await write.ExecuteNonQueryAsync();
        var updated = await revisions.ReadAsync(default);
        Assert.NotEqual(inserted, updated);
        write.CommandText = "DELETE FROM application_settings WHERE key='revision-test'";
        await write.ExecuteNonQueryAsync();
        Assert.NotEqual(updated, await revisions.ReadAsync(default));
    }

    [Fact]
    public async Task Every_persisted_table_has_invalidation_triggers_except_revision_and_migration_metadata()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.name FROM sqlite_schema t
            WHERE t.type='table' AND t.name NOT LIKE 'sqlite_%' AND t.name NOT IN ('projection_revision','schema_migrations')
            AND (SELECT count(*) FROM sqlite_schema r WHERE r.type='trigger' AND r.tbl_name=t.name
                AND r.name IN ('projection_revision_'||t.name||'_insert','projection_revision_'||t.name||'_update',
                              'projection_revision_'||t.name||'_delete'))<>3;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync(), "A new table needs projection invalidation in its migration.");
    }
}
