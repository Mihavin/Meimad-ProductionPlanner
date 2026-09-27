using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class SqliteDatabasePoolTests
{
    [Fact]
    public async Task A_pooled_connection_left_inside_a_transaction_is_rolled_back_before_reuse()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using (var abandoned = await fixture.Database.OpenConnectionAsync())
        await using (var command = abandoned.CreateCommand())
        {
            // What an interrupted request leaves behind: an open transaction and a pending write.
            command.CommandText = "CREATE TABLE pool_probe (value TEXT); BEGIN IMMEDIATE; INSERT INTO pool_probe VALUES ('abandoned');";
            await command.ExecuteNonQueryAsync();
        }

        await using var reused = await fixture.Database.OpenConnectionAsync();
        await using var transaction = reused.BeginTransaction(deferred: false);
        await using var read = reused.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT COUNT(*) FROM pool_probe;";
        Assert.Equal(0L, (long)(await read.ExecuteScalarAsync())!);
    }
}
