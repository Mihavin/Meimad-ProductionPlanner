using Meimad.Planner.Server.Application.Kitaron;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteKitaronConnectionRepository(SqliteDatabase database)
    : IKitaronConnectionRepository
{
    public async Task<StoredKitaronConnectionSettings> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, cancellationToken);
    }

    public async Task<StoredKitaronConnectionSettings> UpdateAsync(
        StoredKitaronConnectionSettings settings,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE kitaron_connection_settings
            SET server_host = $host,
                server_port = $port,
                database_name = $database,
                view_schema = $schema,
                view_name = $view,
                username = $username,
                protected_password = $password,
                enabled = $enabled,
                refresh_interval_seconds = $interval,
                last_test_status = 'not_tested',
                last_test_at = NULL,
                last_test_message = NULL,
                last_test_column_count = NULL,
                version = version + 1,
                updated_at = $updatedAt
            WHERE id = 1 AND version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$host", settings.ServerHost);
        command.Parameters.AddWithValue("$port", settings.ServerPort);
        command.Parameters.AddWithValue("$database", settings.DatabaseName);
        command.Parameters.AddWithValue("$schema", settings.ViewSchema);
        command.Parameters.AddWithValue("$view", settings.ViewName);
        command.Parameters.AddWithValue("$username", settings.Username);
        command.Parameters.AddWithValue("$password", (object?)settings.ProtectedPassword ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", settings.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$interval", settings.RefreshIntervalSeconds);
        command.Parameters.AddWithValue("$updatedAt", settings.UpdatedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new KitaronConnectionConcurrencyException();
        }
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(cancellationToken);
    }

    public async Task<StoredKitaronConnectionSettings> RecordTestAsync(
        bool succeeded,
        DateTimeOffset testedAt,
        string message,
        int? columnCount,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE kitaron_connection_settings
            SET last_test_status = $status,
                last_test_at = $testedAt,
                last_test_message = $message,
                last_test_column_count = $columnCount,
                version = version + 1,
                updated_at = $testedAt
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$status", succeeded ? "succeeded" : "failed");
        command.Parameters.AddWithValue("$testedAt", testedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$columnCount", (object?)columnCount ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await ReadAsync(connection, null, cancellationToken);
    }

    private static async Task<StoredKitaronConnectionSettings> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT server_host, server_port, database_name, view_schema, view_name,
                   username, protected_password, enabled, refresh_interval_seconds,
                   last_test_status, last_test_at, last_test_message,
                   last_test_column_count, version, updated_at
            FROM kitaron_connection_settings
            WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Kitaron connection settings were not initialized.");
        }
        return new StoredKitaronConnectionSettings(
            reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt32(7) == 1,
            reader.GetInt32(8), reader.GetString(9),
            reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetInt32(12), reader.GetInt32(13),
            DateTimeOffset.Parse(reader.GetString(14)));
    }
}
