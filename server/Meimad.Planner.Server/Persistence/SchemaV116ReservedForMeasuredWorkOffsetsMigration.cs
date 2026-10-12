using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// MERGE PLACEHOLDER — never ship. Schema v116 belongs to the measured-work-offset (DPRNT) workstream
/// developed in parallel; v117 (Cimatron simulation packages) needs a contiguous migration list on this
/// branch, so this no-op stands in for v116 until that branch merges. On merge, delete this file and
/// put the real v116 migration in its place in <see cref="DatabaseMigrator"/>. A database that recorded
/// this placeholder is a test database only (the migrator would refuse the name change), so no
/// installed database ever carries it.
/// </summary>
internal sealed class SchemaV116ReservedForMeasuredWorkOffsetsMigration : IDatabaseMigration
{
    public int Version => 116;
    public string Name => "reserved_v116_measured_work_offsets_placeholder";

    public Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token) => Task.CompletedTask;
}
