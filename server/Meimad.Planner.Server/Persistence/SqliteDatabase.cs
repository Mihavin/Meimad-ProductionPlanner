using Meimad.Planner.Server.Configuration;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteDatabase
{
    private readonly DatabaseOptions options;
    private readonly string connectionString;

    public SqliteDatabase(DatabaseOptions options)
    {
        this.options = options;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
    }

    internal string DatabasePath => options.DatabasePath;

    internal async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var parentDirectory = Path.GetDirectoryName(options.DatabasePath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new InvalidOperationException("The configured database path has no parent directory.");
        }

        Directory.CreateDirectory(parentDirectory);

        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            // A request cancelled mid-query (SQLITE_INTERRUPT) can return its pooled connection with
            // the transaction still open; the next BEGIN on it would fail with "cannot start a
            // transaction within a transaction" (seen on the factory Server, 2026-09-27).
            if (SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle) == 0)
            {
                command.CommandText = "ROLLBACK;";
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }

            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
