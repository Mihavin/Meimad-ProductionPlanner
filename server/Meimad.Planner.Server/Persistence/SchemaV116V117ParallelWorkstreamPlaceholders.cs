using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

// BRANCH-ONLY PLACEHOLDERS (ws/tv, 2026-10-12). Schema v116 (measured work offsets, ws/dprnt) and
// v117 (Cimatron simulation package import, ws/pkg) are written in parallel workstreams; the migrator
// requires a contiguous sequence, so this branch carries empty stand-ins to reach v118 (TV
// thumbnails). When the branches are merged, delete this file and its two registry entries and keep
// the real v116/v117 migrations. Their names differ from the real ones on purpose: a database migrated
// with a stand-in is refused by the real migration ("recorded as ..."), so a stand-in can never pass
// for the real schema. Never install a build that contains this file.

internal sealed class SchemaV116ParallelWorkstreamPlaceholderMigration : IDatabaseMigration
{
    public int Version => 116;
    public string Name => "ws_tv_placeholder_v116";

    public Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class SchemaV117ParallelWorkstreamPlaceholderMigration : IDatabaseMigration
{
    public int Version => 117;
    public string Name => "ws_tv_placeholder_v117";

    public Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
