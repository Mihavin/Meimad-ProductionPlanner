using System.Globalization;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.EditMode;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteCaseComponentRepository(SqliteDatabase database) : ICaseComponentRepository
{
    private const string Projection = """
        component.id, component.parent_case_id, parent.part_number, parent.name,
        component.child_case_id, child.part_number, child.name,
        component.quantity_per_parent, component.sort_order, component.notes,
        component.is_active, component.version, component.created_at, component.updated_at
        """;

    public async Task<IReadOnlyList<CaseComponentDetails>> ListComponentsAsync(
        string caseId, CancellationToken cancellationToken) =>
        await ListAsync("component.parent_case_id=$caseId", caseId, "component.sort_order, child.part_number COLLATE NOCASE", cancellationToken);

    public async Task<IReadOnlyList<CaseComponentDetails>> ListWhereUsedAsync(
        string caseId, CancellationToken cancellationToken) =>
        await ListAsync("component.child_case_id=$caseId", caseId, "parent.part_number COLLATE NOCASE, component.sort_order", cancellationToken);

    public async Task<CaseComponentDetails?> GetAsync(
        string componentId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, componentId, cancellationToken);
    }

    public async Task<CaseComponentDetails> CreateAsync(
        string componentId, string parentCaseId, string childCaseId, double quantityPerParent,
        int sortOrder, string? notes, DateTimeOffset now, EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        await EnsureCasesExistAsync(connection, transaction, parentCaseId, childCaseId, cancellationToken);
        await EnsureParentHasNoOperationsAsync(connection, transaction, parentCaseId, cancellationToken);
        await EnsureNoCycleAsync(connection, transaction, parentCaseId, childCaseId, null, cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO case_components (
                    id, parent_case_id, child_case_id, quantity_per_parent, sort_order,
                    notes, is_active, version, created_at, updated_at)
                VALUES ($id, $parent, $child, $quantity, $sort, $notes, 1, 1, $now, $now);
                """;
            Add(command, "$id", componentId); Add(command, "$parent", parentCaseId);
            Add(command, "$child", childCaseId); Add(command, "$quantity", quantityPerParent);
            Add(command, "$sort", sortOrder); Add(command, "$notes", notes); Add(command, "$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new CaseComponentDuplicateException();
        }
        var result = await ReadAsync(connection, transaction, componentId, cancellationToken)
            ?? throw new InvalidOperationException("Created Case Component could not be read.");
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<CaseComponentDetails?> UpdateAsync(
        string componentId, double quantityPerParent, int sortOrder, string? notes, bool isActive,
        int expectedVersion, DateTimeOffset now, EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        var current = await ReadAsync(connection, transaction, componentId, cancellationToken)
            ?? throw new CaseComponentNotFoundException();
        if (isActive)
        {
            await EnsureParentHasNoOperationsAsync(
                connection, transaction, current.ParentCaseId, cancellationToken);
            await EnsureNoCycleAsync(
                connection, transaction, current.ParentCaseId, current.ChildCaseId,
                componentId, cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE case_components
            SET quantity_per_parent=$quantity, sort_order=$sort, notes=$notes, is_active=$active,
                version=version+1, updated_at=$now
            WHERE id=$id AND version=$expectedVersion;
            """;
        Add(command, "$quantity", quantityPerParent); Add(command, "$sort", sortOrder);
        Add(command, "$notes", notes); Add(command, "$active", isActive ? 1 : 0);
        Add(command, "$now", now.ToString("O")); Add(command, "$id", componentId);
        Add(command, "$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        var result = await ReadAsync(connection, transaction, componentId, cancellationToken)
            ?? throw new InvalidOperationException("Updated Case Component could not be read.");
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<ComponentGraphEdge>> ReadActiveGraphAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT component.id, component.parent_case_id, parent.part_number,
                   component.child_case_id, child.part_number, component.quantity_per_parent
            FROM case_components component
            JOIN cases parent ON parent.id=component.parent_case_id
            JOIN cases child ON child.id=component.child_case_id
            WHERE component.is_active=1
            ORDER BY component.parent_case_id, component.sort_order, component.id;
            """;
        var result = new List<ComponentGraphEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ComponentGraphEdge(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetDouble(5)));
        return result;
    }

    public async Task<IReadOnlyDictionary<string, double>> ReadDerivedAllocatedQuantitiesAsync(
        string childCaseId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT allocation.derived_order_key, SUM(allocation.quantity)
            FROM batch_allocations allocation
            JOIN production_batches batch ON batch.id=allocation.production_batch_id
            WHERE batch.case_id=$caseId AND allocation.allocation_type='derived_order'
              AND batch.status <> 'cancelled'
            GROUP BY allocation.derived_order_key;
            """;
        Add(command, "$caseId", childCaseId);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetString(0)] = reader.GetDouble(1);
        return result;
    }

    private async Task<IReadOnlyList<CaseComponentDetails>> ListAsync(
        string predicate, string caseId, string orderBy, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Projection}
            FROM case_components component
            JOIN cases parent ON parent.id=component.parent_case_id
            JOIN cases child ON child.id=component.child_case_id
            WHERE {predicate}
            ORDER BY {orderBy}, component.id;
            """;
        Add(command, "$caseId", caseId);
        var result = new List<CaseComponentDetails>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    private static async Task<CaseComponentDetails?> ReadAsync(
        SqliteConnection connection, SqliteTransaction? transaction,
        string componentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {Projection}
            FROM case_components component
            JOIN cases parent ON parent.id=component.parent_case_id
            JOIN cases child ON child.id=component.child_case_id
            WHERE component.id=$id;
            """;
        Add(command, "$id", componentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static CaseComponentDetails Read(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetDouble(7),
        reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt32(10) == 1,
        reader.GetInt32(11), Parse(reader.GetString(12)), Parse(reader.GetString(13)));

    private static async Task EnsureCasesExistAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        string parentCaseId, string childCaseId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM cases WHERE id IN ($parent, $child);";
        Add(command, "$parent", parentCaseId); Add(command, "$child", childCaseId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 2)
            throw new CaseComponentNotFoundException();
    }

    private static async Task EnsureParentHasNoOperationsAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        string parentCaseId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM case_operations WHERE case_id=$caseId);";
        Add(command, "$caseId", parentCaseId);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1)
            throw new CaseParentOperationsNotAllowedException();
    }

    private static async Task EnsureNoCycleAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        string parentCaseId, string childCaseId, string? excludedId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE descendants(case_id) AS (
                SELECT child_case_id FROM case_components
                WHERE parent_case_id=$child AND is_active=1 AND ($excluded IS NULL OR id<>$excluded)
                UNION
                SELECT component.child_case_id
                FROM case_components component
                JOIN descendants ON component.parent_case_id=descendants.case_id
                WHERE component.is_active=1 AND ($excluded IS NULL OR component.id<>$excluded)
            )
            SELECT EXISTS(SELECT 1 FROM descendants WHERE case_id=$parent);
            """;
        Add(command, "$child", childCaseId); Add(command, "$parent", parentCaseId);
        Add(command, "$excluded", excludedId);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1)
            throw new CaseComponentCycleException();
    }

    private static async Task EnsureEditAuthorityAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        EditAuthority editAuthority, CancellationToken cancellationToken)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(
            connection, transaction, DateTimeOffset.UtcNow, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, generation FROM edit_tokens WHERE id=1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
            throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        if (!StringComparer.Ordinal.Equals(reader.GetString(0), editAuthority.ClientId)
            || reader.GetInt64(1) != editAuthority.Generation)
            throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
