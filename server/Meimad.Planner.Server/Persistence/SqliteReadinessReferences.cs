using Meimad.Planner.Server.Domain.Readiness;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal static partial class SqliteProductionReadinessContextReader
{
    // Reference data for exact output enrichment, scoped to the same SQLite snapshot.
    // Coverage sets distinguish an absent fact from a key that was never loaded.
    internal sealed record ReferenceFacts(
        SqliteConnection Connection, SqliteTransaction Transaction,
        IReadOnlySet<string> Machines, IReadOnlySet<string> CaseOperations, IReadOnlySet<string> Operations,
        IReadOnlyDictionary<string, HashSet<string>> Supported,
        IReadOnlyDictionary<string, List<ReadinessRelease>> Releases,
        IReadOnlyDictionary<(string BatchOperationId, string MachineId), ToolPreparationReadinessFact> Preparations)
    {
        internal void ValidateScope(SqliteConnection connection, SqliteTransaction? transaction)
        {
            if (!ReferenceEquals(Connection, connection) || !ReferenceEquals(Transaction, transaction)
                || !ReferenceEquals(transaction?.Connection, connection))
                throw new InvalidOperationException("Readiness references belong to a different or completed snapshot.");
        }
    }

    internal static async Task<ReferenceFacts> ReadReferencesAsync(SqliteConnection connection, SqliteTransaction transaction,
        IEnumerable<string> machineIds, IEnumerable<string> processRevisionIds, IEnumerable<string> operationIds,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var machines = machineIds.ToHashSet(StringComparer.Ordinal);
        var operations = operationIds.ToHashSet(StringComparer.Ordinal);
        var recipeOperations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in processRevisionIds.Distinct(StringComparer.Ordinal).Chunk(ChunkSize))
        {
            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = $"SELECT DISTINCT case_operation_id FROM process_revisions WHERE id IN ({InList(query, chunk)});";
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) recipeOperations.Add(reader.GetString(0));
        }
        return new(connection, transaction, machines, recipeOperations, operations,
            await ReadSupportedPostprocessorsAsync(connection, transaction, machines.ToArray(), token),
            await ReadReleasesAsync(connection, transaction, recipeOperations.ToArray(), token),
            await ReadToolPreparationFactsAsync(connection, transaction, operations.ToArray(), token));
    }
}
