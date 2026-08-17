using System.Text.Json;
using Meimad.Planner.Server.Application.Kitaron;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteKitaronMappingRepository(SqliteDatabase database)
    : IKitaronMappingRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<StoredKitaronMappingSettings> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, cancellationToken);
    }

    public async Task<StoredKitaronMappingSettings> UpdateAsync(
        StoredKitaronMappingSettings settings,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE kitaron_mapping_settings
            SET model_mode = $modelMode,
                mapping_status = $status,
                mappings_json = $mappings,
                notes = $notes,
                version = version + 1,
                updated_at = $updatedAt
            WHERE id = 1 AND version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$modelMode", settings.ModelMode);
        command.Parameters.AddWithValue("$status", settings.Status);
        command.Parameters.AddWithValue("$mappings", settings.MappingsJson);
        command.Parameters.AddWithValue("$notes", (object?)settings.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", settings.UpdatedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new KitaronMappingConcurrencyException();
        }
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(cancellationToken);
    }

    public async Task RecordDetectedColumnsAsync(
        IReadOnlyList<KitaronSourceColumn> columns,
        DateTimeOffset detectedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE kitaron_mapping_settings
            SET detected_columns_json = $columns,
                version = version + 1,
                updated_at = $detectedAt
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$columns", JsonSerializer.Serialize(columns, JsonOptions));
        command.Parameters.AddWithValue("$detectedAt", detectedAt.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<StoredKitaronMappingSettings> ReadAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT model_mode, mapping_status, mappings_json, detected_columns_json,
                   notes, version, updated_at
            FROM kitaron_mapping_settings
            WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Kitaron mapping settings were not initialized.");
        }
        return new StoredKitaronMappingSettings(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5),
            DateTimeOffset.Parse(reader.GetString(6)));
    }
}
