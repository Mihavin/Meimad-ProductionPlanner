using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Postprocessors;
using Meimad.Planner.Server.Domain.Postprocessors;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqlitePostprocessorRepository : IPostprocessorRepository
{
    private const string Projection =
        "id, name, description, is_active, version, created_at, updated_at";

    private readonly SqliteDatabase database;

    public SqlitePostprocessorRepository(SqliteDatabase database) => this.database = database;

    public async Task<Postprocessor> CreateAsync(
        Postprocessor value,
        EditAuthority authority,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        await EnsureNameAvailableAsync(connection, transaction, value.Name, null, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO postprocessors (
                id, name, description, is_active, version, created_at, updated_at)
            VALUES ($id, $name, $description, $isActive, $version, $createdAt, $updatedAt);
            """;
        AddParameters(command, value);
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return value;
    }

    public async Task<Postprocessor?> GetByIdAsync(string id, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Projection} FROM postprocessors WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<Postprocessor>> ListAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Projection} FROM postprocessors ORDER BY name COLLATE NOCASE, id;";
        var values = new List<Postprocessor>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            values.Add(Read(reader));
        }

        return values;
    }

    public async Task<Postprocessor?> UpdateAsync(
        Postprocessor value,
        int expectedVersion,
        EditAuthority authority,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        await EnsureNameAvailableAsync(connection, transaction, value.Name, value.PostprocessorId, token);
        if (!value.IsActive)
        {
            await EnsureNotInUseAsync(
                connection, transaction, value.PostprocessorId,
                includeReleasedHistory: false, token);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE postprocessors
            SET name = $name,
                description = $description,
                is_active = $isActive,
                version = $version,
                updated_at = $updatedAt
            WHERE id = $id AND version = $expectedVersion;
            """;
        AddParameters(command, value);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        var affected = await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return affected == 1 ? value : null;
    }

    public async Task<bool> DeleteAsync(string id, EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        await EnsureNotInUseAsync(
            connection, transaction, id, includeReleasedHistory: true, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM postprocessors WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        var deleted = await command.ExecuteNonQueryAsync(token) == 1;
        await transaction.CommitAsync(token);
        return deleted;
    }

    private static async Task EnsureNotInUseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        bool includeReleasedHistory,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM machine_supported_postprocessors
                WHERE postprocessor_id = $id
                UNION ALL
                SELECT 1 FROM gcode_releases
                WHERE postprocessor_id = $id AND $includeReleasedHistory = 1);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$includeReleasedHistory", includeReleasedHistory ? 1 : 0);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1)
        {
            throw new PostprocessorInUseException(id);
        }
    }

    private static async Task EnsureNameAvailableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        string? exceptId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM postprocessors
                WHERE name = $name COLLATE NOCASE
                  AND ($exceptId IS NULL OR id <> $exceptId));
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$exceptId", exceptId is null ? DBNull.Value : exceptId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1)
        {
            throw new PostprocessorNameConflictException(name);
        }
    }

    private static async Task EnsureEditAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority authority,
        CancellationToken token)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(
            connection,
            transaction,
            DateTimeOffset.UtcNow,
            token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0))
        {
            throw new EditModeMutationException(
                "edit_mode_required",
                "No Windows client currently holds Edit Mode.");
        }

        if (!string.Equals(reader.GetString(0), authority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(1) != authority.Generation)
        {
            throw new EditModeMutationException(
                "edit_generation_stale",
                "This client does not hold the active Edit Mode generation.");
        }
    }

    private static void AddParameters(SqliteCommand command, Postprocessor value)
    {
        command.Parameters.AddWithValue("$id", value.PostprocessorId);
        command.Parameters.AddWithValue("$name", value.Name);
        command.Parameters.AddWithValue(
            "$description",
            value.Description is null ? DBNull.Value : value.Description);
        command.Parameters.AddWithValue("$isActive", value.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$version", value.Version);
        command.Parameters.AddWithValue("$createdAt", Format(value.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", Format(value.UpdatedAt));
    }

    private static Postprocessor Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetBoolean(3),
        reader.GetInt32(4),
        Parse(reader.GetString(5)),
        Parse(reader.GetString(6)));

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
