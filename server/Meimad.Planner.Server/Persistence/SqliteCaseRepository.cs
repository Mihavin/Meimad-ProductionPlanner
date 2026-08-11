using System.Globalization;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.CaseOperations;
using Meimad.Planner.Server.Domain.Cases;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteCaseRepository : ICaseRepository
{
    private const string Projection = """
        id,
        part_number,
        name,
        revision,
        customer,
        customer_reference,
        preview_reference,
        working_folder_path,
        material_type,
        material_specification,
        raw_material_form,
        raw_material_dimensions,
        current_setup_seconds,
        current_cycle_seconds,
        notes,
        version,
        created_at,
        updated_at
        """;

    private readonly SqliteDatabase database;

    public SqliteCaseRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<PlannerCase> CreateAsync(
        PlannerCase plannerCase,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(
            connection,
            transaction,
            editAuthority,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO cases (
                id,
                part_number,
                name,
                revision,
                customer,
                customer_reference,
                preview_reference,
                working_folder_path,
                material_type,
                material_specification,
                raw_material_form,
                raw_material_dimensions,
                current_setup_seconds,
                current_cycle_seconds,
                notes,
                version,
                created_at,
                updated_at)
            VALUES (
                $id,
                $partNumber,
                $name,
                $revision,
                $customer,
                $customerReference,
                $previewPath,
                $workingFolderPath,
                $materialType,
                $materialSpecification,
                $rawMaterialForm,
                $rawMaterialDimensions,
                $currentSetupSeconds,
                $currentCycleSeconds,
                $notes,
                $version,
                $createdAt,
                $updatedAt);
            """;
        AddWriteParameters(command, plannerCase);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return plannerCase;
    }

    public async Task<PlannerCase?> GetByIdAsync(
        string caseId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Projection},
                   EXISTS(
                       SELECT 1
                       FROM orders
                       WHERE orders.case_id = cases.id
                         AND orders.status = 'active')
                   OR EXISTS(
                       SELECT 1
                       FROM production_batches
                       WHERE production_batches.case_id = cases.id
                         AND production_batches.status = 'planned') AS is_active
            FROM cases
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", caseId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadCase(reader, includeActiveProjection: true)
            : null;
    }

    public async Task<IReadOnlyList<PlannerCase>> ListAsync(
        string? search,
        string? customer,
        bool? isActive,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH case_pool AS (
                SELECT {Projection},
                       EXISTS(
                           SELECT 1 FROM orders
                           WHERE orders.case_id = cases.id AND orders.status = 'active')
                       OR EXISTS(
                           SELECT 1 FROM production_batches
                           WHERE production_batches.case_id = cases.id
                             AND production_batches.status = 'planned') AS is_active
                FROM cases
            )
            SELECT *
            FROM case_pool
            WHERE ($search IS NULL
                   OR instr(lower(part_number), lower($search)) > 0
                   OR instr(lower(name), lower($search)) > 0
                   OR instr(lower(coalesce(customer, '')), lower($search)) > 0)
              AND ($customer IS NULL
                   OR instr(lower(coalesce(customer, '')), lower($customer)) > 0)
              AND ($isActive IS NULL OR is_active = $isActive)
            ORDER BY part_number COLLATE NOCASE, id;
            """;
        command.Parameters.AddWithValue(
            "$search",
            string.IsNullOrWhiteSpace(search) ? DBNull.Value : search);
        command.Parameters.AddWithValue(
            "$customer",
            string.IsNullOrWhiteSpace(customer) ? DBNull.Value : customer);
        command.Parameters.AddWithValue(
            "$isActive",
            isActive.HasValue ? isActive.Value : DBNull.Value);

        var items = new List<PlannerCase>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadCase(reader, includeActiveProjection: true));
        }

        return items;
    }

    public async Task<IReadOnlyList<CaseOperationDetails>> ListOperationsAsync(
        string caseId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, case_id, operation_number, route_position, name,
                   required_machine_type, setup_seconds, cycle_seconds,
                   dependency_type, predecessor_case_operation_id, simultaneous_group_key,
                   version, created_at, updated_at
            FROM case_operations
            WHERE case_id = $caseId
            ORDER BY route_position, operation_number, id;
            """;
        command.Parameters.AddWithValue("$caseId", caseId);
        var items = new List<CaseOperationDetails>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new CaseOperationDetails(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetString(4),
                GetNullableString(reader, 5),
                GetNullableInt32(reader, 6),
                GetNullableInt32(reader, 7),
                ToDependencyContractToken(reader.GetString(8)),
                GetNullableString(reader, 9),
                GetNullableString(reader, 10),
                reader.GetInt32(11),
                ParseInstant(reader.GetString(12)),
                ParseInstant(reader.GetString(13))));
        }

        return items;
    }

    public async Task<CaseOperationDetails?> CreateOperationAsync(
        NewCaseOperation operation,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(
            connection,
            transaction,
            editAuthority,
            cancellationToken);

        if (!await CaseExistsAsync(
                connection,
                transaction,
                operation.CaseId,
                cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var current = await ReadOperationsAsync(
            connection,
            transaction,
            operation.CaseId,
            cancellationToken);
        var routePosition = current.Count == 0
            ? 0
            : checked(current.Max(item => item.RoutePosition) + 1);
        var candidate = new CaseOperationDetails(
            operation.CaseOperationId,
            operation.CaseId,
            operation.OperationNumber,
            routePosition,
            operation.Name,
            operation.RequiredMachineType,
            operation.SetupTimeSeconds,
            operation.CycleTimePerPartSeconds,
            operation.DependencyType.ToContractToken(),
            operation.PredecessorCaseOperationId,
            operation.SimultaneousGroupKey,
            1,
            operation.CreatedAt,
            operation.CreatedAt);

        ValidateGraph(operation.CaseId, [.. current, candidate]);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO case_operations (
                id, case_id, operation_number, route_position, name,
                required_machine_type, setup_seconds, cycle_seconds,
                dependency_type, predecessor_case_operation_id, simultaneous_group_key,
                version, created_at, updated_at)
            VALUES (
                $id, $caseId, $operationNumber, $routePosition, $name,
                $requiredMachineType, $setupSeconds, $cycleSeconds,
                $dependencyType, $predecessorId, $groupKey,
                1, $createdAt, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", candidate.CaseOperationId);
        command.Parameters.AddWithValue("$caseId", candidate.CaseId);
        command.Parameters.AddWithValue("$operationNumber", candidate.OperationNumber);
        command.Parameters.AddWithValue("$routePosition", candidate.RoutePosition);
        command.Parameters.AddWithValue("$name", candidate.Name);
        AddNullableText(command, "$requiredMachineType", candidate.RequiredMachineType);
        AddNullableInteger(command, "$setupSeconds", candidate.SetupTimeSeconds);
        AddNullableInteger(command, "$cycleSeconds", candidate.CycleTimePerPartSeconds);
        command.Parameters.AddWithValue(
            "$dependencyType",
            ToDependencyStorageToken(operation.DependencyType));
        AddNullableText(command, "$predecessorId", candidate.PredecessorCaseOperationId);
        AddNullableText(command, "$groupKey", candidate.SimultaneousGroupKey);
        command.Parameters.AddWithValue("$createdAt", FormatInstant(candidate.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return candidate;
    }

    public async Task<PlannerCase?> UpdateAsync(
        PlannerCase plannerCase,
        int expectedVersion,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(
            connection,
            transaction,
            editAuthority,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE cases
            SET part_number = $partNumber,
                name = $name,
                revision = $revision,
                customer = $customer,
                customer_reference = $customerReference,
                preview_reference = $previewPath,
                working_folder_path = $workingFolderPath,
                material_type = $materialType,
                material_specification = $materialSpecification,
                raw_material_form = $rawMaterialForm,
                raw_material_dimensions = $rawMaterialDimensions,
                current_setup_seconds = $currentSetupSeconds,
                current_cycle_seconds = $currentCycleSeconds,
                notes = $notes,
                version = $version,
                updated_at = $updatedAt
            WHERE id = $id AND version = $expectedVersion
            RETURNING {Projection};
            """;
        AddWriteParameters(command, plannerCase);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);

        PlannerCase? updated;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            updated = await reader.ReadAsync(cancellationToken)
                ? ReadCase(reader) with { IsActive = plannerCase.IsActive }
                : null;
        }

        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private static async Task EnsureEditAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(
            connection,
            transaction,
            DateTimeOffset.UtcNow,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT holder_client_id, generation
            FROM edit_tokens
            WHERE id = 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            throw new EditModeMutationException(
                "edit_mode_required",
                "No Windows client currently holds Edit Mode.");
        }

        if (!string.Equals(reader.GetString(0), editAuthority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(1) != editAuthority.Generation)
        {
            throw new EditModeMutationException(
                "edit_generation_stale",
                "This client does not hold the active Edit Mode generation.");
        }
    }

    private static async Task<bool> CaseExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM cases WHERE id = $caseId);";
        command.Parameters.AddWithValue("$caseId", caseId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<IReadOnlyList<CaseOperationDetails>> ReadOperationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, case_id, operation_number, route_position, name,
                   required_machine_type, setup_seconds, cycle_seconds,
                   dependency_type, predecessor_case_operation_id, simultaneous_group_key,
                   version, created_at, updated_at
            FROM case_operations
            WHERE case_id = $caseId
            ORDER BY route_position, operation_number, id;
            """;
        command.Parameters.AddWithValue("$caseId", caseId);
        var items = new List<CaseOperationDetails>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new CaseOperationDetails(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetString(4),
                GetNullableString(reader, 5),
                GetNullableInt32(reader, 6),
                GetNullableInt32(reader, 7),
                ToDependencyContractToken(reader.GetString(8)),
                GetNullableString(reader, 9),
                GetNullableString(reader, 10),
                reader.GetInt32(11),
                ParseInstant(reader.GetString(12)),
                ParseInstant(reader.GetString(13))));
        }

        return items;
    }

    private static void ValidateGraph(
        string caseId,
        IReadOnlyList<CaseOperationDetails> operations)
    {
        var domainOperations = operations.Select(operation => new CaseOperation(
            operation.CaseOperationId,
            operation.CaseId,
            operation.OperationNumber,
            operation.RoutePosition,
            operation.Name,
            operation.RequiredMachineType,
            operation.SetupTimeSeconds,
            operation.CycleTimePerPartSeconds,
            operation.Version,
            operation.CreatedAt,
            operation.UpdatedAt)).ToArray();
        var dependencies = operations
            .Where(operation => operation.PredecessorCaseOperationId is not null)
            .Select(operation => new CaseOperationDependency(
                $"stored:{operation.CaseOperationId}",
                ParseDependencyContractToken(operation.DependencyType),
                operation.PredecessorCaseOperationId!,
                operation.CaseOperationId,
                operation.SimultaneousGroupKey))
            .ToArray();
        CaseOperationGraph.Create(caseId, domainOperations, dependencies);
    }

    private static void AddWriteParameters(SqliteCommand command, PlannerCase plannerCase)
    {
        command.Parameters.AddWithValue("$id", plannerCase.CaseId);
        command.Parameters.AddWithValue("$partNumber", plannerCase.PartNumber);
        command.Parameters.AddWithValue("$name", plannerCase.Name);
        AddNullableText(command, "$revision", plannerCase.Revision);
        AddNullableText(command, "$customer", plannerCase.Customer);
        AddNullableText(command, "$customerReference", plannerCase.CustomerReference);
        AddNullableText(command, "$previewPath", plannerCase.PreviewPath);
        command.Parameters.AddWithValue("$workingFolderPath", plannerCase.WorkingFolderPath);
        AddNullableText(command, "$materialType", plannerCase.MaterialType);
        AddNullableText(command, "$materialSpecification", plannerCase.MaterialSpecification);
        AddNullableText(command, "$rawMaterialForm", plannerCase.RawMaterialForm);
        AddNullableText(command, "$rawMaterialDimensions", plannerCase.RawMaterialDimensions);
        AddNullableInteger(command, "$currentSetupSeconds", plannerCase.CurrentSetupTimeSeconds);
        AddNullableInteger(command, "$currentCycleSeconds", plannerCase.CurrentCycleTimePerPartSeconds);
        AddNullableText(command, "$notes", plannerCase.Notes);
        command.Parameters.AddWithValue("$version", plannerCase.Version);
        command.Parameters.AddWithValue("$createdAt", FormatInstant(plannerCase.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatInstant(plannerCase.UpdatedAt));
    }

    private static PlannerCase ReadCase(
        SqliteDataReader reader,
        bool includeActiveProjection = false) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        GetNullableString(reader, 3),
        GetNullableString(reader, 4),
        GetNullableString(reader, 5),
        GetNullableString(reader, 6),
        reader.GetString(7),
        GetNullableString(reader, 8),
        GetNullableString(reader, 9),
        GetNullableString(reader, 10),
        GetNullableString(reader, 11),
        GetNullableInt32(reader, 12),
        GetNullableInt32(reader, 13),
        GetNullableString(reader, 14),
        includeActiveProjection && reader.GetBoolean(18),
        reader.GetInt32(15),
        ParseInstant(reader.GetString(16)),
        ParseInstant(reader.GetString(17)));

    private static void AddNullableText(SqliteCommand command, string name, string? value)
    {
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value);
    }

    private static void AddNullableInteger(SqliteCommand command, string name, int? value)
    {
        command.Parameters.AddWithValue(name, value.HasValue ? value.Value : DBNull.Value);
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseInstant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string ToDependencyContractToken(string storageToken) => storageToken switch
    {
        "sequential" => "SEQUENTIAL",
        "parallel_capable" => "PARALLEL_CAPABLE",
        "independent" => "INDEPENDENT",
        "locked_simultaneous" => "LOCKED_SIMULTANEOUS",
        _ => throw new InvalidDataException(
            $"Stored Case Operation dependency type '{storageToken}' is invalid.")
    };

    private static CaseOperationDependencyType ParseDependencyContractToken(string token)
    {
        return CaseOperationDependencyTypes.TryParseContractToken(token, out var type)
            ? type
            : throw new InvalidDataException(
                $"Case Operation dependency type '{token}' is invalid.");
    }

    private static string ToDependencyStorageToken(CaseOperationDependencyType type) => type switch
    {
        CaseOperationDependencyType.Sequential => "sequential",
        CaseOperationDependencyType.ParallelCapable => "parallel_capable",
        CaseOperationDependencyType.Independent => "independent",
        CaseOperationDependencyType.LockedSimultaneous => "locked_simultaneous",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}
