using Meimad.Planner.Server.Persistence;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;

namespace Meimad.Planner.Server.Tests.Timeline;

/// <summary>
/// Schema v109 (audit finding 2026-10-09): CNC polling and connection checks rewrite their tables every
/// few seconds and moved the planning revision on every write, so a Board or Timeline read overlapping
/// three polls answered 503. Those tables now count as non-planning evidence only.
/// </summary>
public sealed class ProjectionPollingScopeTests
{
    [Fact]
    public async Task A_polled_CNC_row_moves_the_evidence_revision_but_not_the_planning_revision()
    {
        await using var fixture = await Fixture.CreateAsync();
        var planning = new SqliteProjectionRevisionReader(fixture.Database);
        var evidence = new SqliteProjectionRevisionReader(fixture.Database, includeOperationalEvidence: true);
        var planningBefore = await planning.ReadAsync(default);
        var evidenceBefore = await evidence.ReadAsync(default);
        string machineId;
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM machines LIMIT 1";
            machineId = (string)(await command.ExecuteScalarAsync())!;
        }

        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO machine_dprnt_lines (machine_id, connection_id, received_at, line) VALUES ($machine, 'conn-1', '2026-10-09T06:00:00Z', 'DPRNT line');
                INSERT INTO machine_dprnt_lines (machine_id, connection_id, received_at, line) VALUES ($machine, 'conn-1', '2026-10-09T06:00:01Z', 'MEIMAD/V/1/EVENT/CST');
                UPDATE kitaron_connection_settings SET last_test_at='2026-10-09T06:00:00Z' WHERE id=1;
                """;
            command.Parameters.AddWithValue("$machine", machineId);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(planningBefore, await planning.ReadAsync(default));
        Assert.NotEqual(evidenceBefore, await evidence.ReadAsync(default));

        // A planning fact still moves the planning revision.
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE machines SET version=version+1 WHERE id=$machine";
            command.Parameters.AddWithValue("$machine", machineId);
            await command.ExecuteNonQueryAsync();
        }
        Assert.NotEqual(planningBefore, await planning.ReadAsync(default));

        foreach (var table in SchemaV109ProjectionPollingScopeMigration.Tables)
            Assert.Equal(3L, await fixture.ScalarAsync($"SELECT COUNT(*) FROM sqlite_schema WHERE type='trigger' AND name LIKE 'projection_non_planning_{table}_%'"));
    }
}
