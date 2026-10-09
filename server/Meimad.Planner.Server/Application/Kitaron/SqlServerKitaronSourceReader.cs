using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed class SqlServerKitaronSourceReader : IKitaronSourceReader
{
    private const int MaximumRows = 200_000;
    private static readonly string[] OrderSuppliedColumnCandidates = ["Supplied"];
    private static readonly string[] OrderPriceColumnCandidates =
    [
        // On the commissioned Kitaron schema the sales-order line's unit price is CostShkalim
        // (NIS; a foreign-currency order carries its NIS value at order entry) with CostDolar as
        // the USD twin: the invoiced sums (InvSum, InvSumDol) equal these per unit, and they are
        // filled on 97 % of the 20,455 TSubOrder rows. PriceInCurr, the earlier choice, is set on
        // 35 rows only and read every other order as 0. Do not substitute FullCost (the unit price
        // after the order discount), manufacturing cost, BOM cost or row-total fields.
        "CostShkalim",
        "PriceInCurr",
        "UnitPrice",
        "PriceForOne",
        "PricePerUnit",
        "OrderPrice",
        "Price",
        "RowPrice",
        "PriceRow"
    ];
    // "OrderClosed" is deliberately excluded: it is a multi-value status/reason code, not a
    // boolean, and its "closed" value is not consistent across Kitaron installations - on the
    // commissioned schema, OrderClosed=2 means the row is still OPEN (matching TSubOrder.Closed=0)
    // while OrderClosed=4/32/68 means closed (Closed=1). A prior hardcoded "OrderClosed = 2" guess
    // had this backwards and forced genuinely open, not-yet-supplied rows to read as "complete".
    // The real boolean "Closed" column (when present, as it is here) is the reliable signal.
    private static readonly string[] OrderRowClosedColumnCandidates =
    [
        "RecordClosed",
        "RowClosed",
        "Closed",
        "IsClosed",
        "Completed",
        "IsCompleted"
    ];
    private static readonly string[] OrderHeaderClosedColumnCandidates =
    [
        "RecordClosed",
        "Closed",
        "IsClosed",
        "Completed",
        "IsCompleted"
    ];

    public async Task<KitaronSourceSnapshot> ReadAsync(
        StoredKitaronConnectionSettings settings,
        string password,
        IReadOnlyList<string> workColumns,
        IReadOnlyList<string> materialColumns,
        CancellationToken cancellationToken)
    {
        if (workColumns.Count == 0)
            throw new KitaronSyncBlockedException("The ready mapping has no readable source columns.");

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{settings.ServerHost},{settings.ServerPort}",
            InitialCatalog = settings.DatabaseName,
            UserID = settings.Username,
            Password = password,
            ApplicationIntent = ApplicationIntent.ReadOnly,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
            CommandTimeout = 120,
            Pooling = false
        };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        // The ERP database decides whether a SNAPSHOT read is possible; Planner never changes ERP
        // settings (owner decision 2026-10-09: the import must run on the factory database as it
        // is). Without snapshot isolation the queries share one READ COMMITTED transaction, as
        // every import before 0.1.190 did, and the recorded read mode says so.
        var (isolation, mode, caveat) = await ChooseIsolationAsync(connection, cancellationToken);
        var startedAt = DateTimeOffset.UtcNow;
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(isolation, cancellationToken);
        if (isolation == IsolationLevel.Snapshot)
            caveat = await InspectViewAsync(connection, transaction, settings, cancellationToken);

        var workRows = await ReadWorkRowsAsync(connection, transaction, settings, workColumns, cancellationToken);
        var orders = await ReadOrdersAsync(connection, transaction, settings, cancellationToken);
        var components = await ReadComponentsAsync(connection, transaction, cancellationToken);
        var materialRows = materialColumns.Count == 0
            ? []
            : await ReadMaterialRowsAsync(connection, transaction, materialColumns, cancellationToken);
        var routeSteps = await ReadRouteStepsAsync(connection, transaction, cancellationToken);
        var stations = await ReadStationsAsync(connection, transaction, cancellationToken);
        var workOrders = await ReadWorkOrdersAsync(connection, transaction, cancellationToken);
        var workOrderLinks = await ReadWorkOrderLinksAsync(connection, transaction, cancellationToken);
        var workOrderMaterials = await ReadWorkOrderMaterialsAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new KitaronSourceSnapshot(
            workRows, orders, components, materialRows, routeSteps, stations,
            workOrders, workOrderLinks, workOrderMaterials,
            new KitaronSourceConsistency(mode, startedAt, DateTimeOffset.UtcNow, caveat));
    }

    internal const string SnapshotMode = "SQL_SERVER_SNAPSHOT";
    internal const string ReadCommittedMode = "SQL_SERVER_READ_COMMITTED";

    private static async Task<(IsolationLevel Isolation, string Mode, string? Caveat)> ChooseIsolationAsync(
        SqlConnection connection, CancellationToken token)
    {
        object? state;
        try
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT snapshot_isolation_state FROM sys.databases WHERE database_id=DB_ID()";
            state = await check.ExecuteScalarAsync(token);
        }
        catch (SqlException exception)
        {
            return (IsolationLevel.ReadCommitted, ReadCommittedMode,
                "The snapshot isolation state of the Kitaron database could not be read: " + exception.Message);
        }
        return ChooseIsolation(state);
    }

    /// <summary>
    /// <c>sys.databases.snapshot_isolation_state</c> 1 means ON; 0 (OFF), 2/3 (in transition) and
    /// an unreadable value mean the read runs READ COMMITTED.
    /// </summary>
    internal static (IsolationLevel Isolation, string Mode, string? Caveat) ChooseIsolation(object? snapshotIsolationState)
    {
        var enabled = snapshotIsolationState is not null and not DBNull
            && Convert.ToInt32(snapshotIsolationState, CultureInfo.InvariantCulture) == 1;
        return enabled
            ? (IsolationLevel.Snapshot, SnapshotMode, null)
            : (IsolationLevel.ReadCommitted, ReadCommittedMode,
                "The Kitaron database has no SNAPSHOT isolation; the source tables were read in one READ COMMITTED transaction. Planner does not change ERP database settings.");
    }

    /// <summary>
    /// A caveat about the configured view under a SNAPSHOT read, or null when it is a plain local
    /// view. Dirty-read hints inside a view override the session isolation, and an invisible
    /// definition cannot be inspected; the finding is recorded with the read, never a reason to
    /// refuse the import.
    /// </summary>
    private static async Task<string?> InspectViewAsync(SqlConnection connection, SqlTransaction transaction,
        StoredKitaronConnectionSettings settings, CancellationToken token)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = ViewInspectionQuery;
            command.Parameters.AddWithValue("@schema", settings.ViewSchema);
            command.Parameters.AddWithValue("@view", settings.ViewName);
            await using var reader = await command.ExecuteReaderAsync(token);
            var found = false;
            while (await reader.ReadAsync(token))
            {
                found = true;
                var caveat = DefinitionCaveat(reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3));
                if (caveat is not null) return caveat;
            }
            return found ? null : "The configured Kitaron planning view is not an inspectable local SQL view.";
        }
        catch (SqlException exception)
        {
            return "The Kitaron source view could not be inspected: " + exception.Message;
        }
    }

    internal static string? DefinitionCaveat(string? definition, int depth, int externalReferences)
    {
        if (depth >= 32 || externalReferences > 0)
            return "The Kitaron source view has external or deeply nested dependencies; the SNAPSHOT read does not cover them.";
        if (definition is null)
            return "The Kitaron source view definition is not visible to the Planner login (VIEW DEFINITION), so dirty-read hints inside it cannot be excluded.";
        return System.Text.RegularExpressions.Regex.IsMatch(definition,
            @"\b(NOLOCK|READUNCOMMITTED|OPENQUERY|OPENROWSET)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            ? "The Kitaron source view contains a dirty-read hint or an external query that overrides the SNAPSHOT read."
            : null;
    }

    // The configured view and its locally referenced SQL modules.
    private const string ViewInspectionQuery = """
            WITH dependencies AS (
                SELECT OBJECT_ID(QUOTENAME(@schema)+'.'+QUOTENAME(@view)) AS object_id, 0 AS depth
                UNION ALL
                SELECT d.referenced_id, p.depth+1 FROM dependencies p
                JOIN sys.sql_expression_dependencies d ON d.referencing_id=p.object_id
                WHERE d.referenced_id IS NOT NULL AND p.depth<32
            )
            SELECT DISTINCT o.type,m.definition,d.depth,
                (SELECT COUNT(*) FROM sys.sql_expression_dependencies x WHERE x.referencing_id=o.object_id
                 AND (x.referenced_server_name IS NOT NULL OR x.referenced_database_name IS NOT NULL)) AS external_references
            FROM dependencies d JOIN sys.objects o ON o.object_id=d.object_id
            LEFT JOIN sys.sql_modules m ON m.object_id=o.object_id
            WHERE o.type IN ('V','FN','IF','TF','SN') OPTION (MAXRECURSION 33);
            """;

    /// <summary>
    /// Open Kitaron work orders (`TRootCard`): route not closed, the sales-order line still open,
    /// and neither the work order nor the line stopped. These become the Production Batches.
    /// </summary>
    private static async Task<IReadOnlyList<KitaronSourceWorkOrder>> ReadWorkOrdersAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = WorkOrderQuery;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<KitaronSourceWorkOrder>();
        while (await reader.ReadAsync(cancellationToken))
        {
            EnsureWithinLimit(result.Count);
            result.Add(new KitaronSourceWorkOrder(
                reader.GetInt32(0),
                KitaronTextNormalization.CleanRequired(reader.GetString(1)),
                reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : KitaronTextNormalization.Clean(reader.GetString(6)),
                reader.IsDBNull(7) ? null : Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture),
                reader.IsDBNull(8) ? null : KitaronTextNormalization.Clean(reader.GetString(8)),
                reader.IsDBNull(9) ? null : KitaronTextNormalization.Clean(reader.GetString(9)),
                reader.IsDBNull(10) ? null : KitaronTextNormalization.Clean(reader.GetString(10)),
                reader.IsDBNull(11) ? null : KitaronTextNormalization.Clean(reader.GetString(11)),
                reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                reader.GetBoolean(13)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<KitaronSourceWorkOrderLink>> ReadWorkOrderLinksAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = WorkOrderLinkQuery;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<KitaronSourceWorkOrderLink>();
        while (await reader.ReadAsync(cancellationToken))
        {
            EnsureWithinLimit(result.Count);
            result.Add(new KitaronSourceWorkOrderLink(
                reader.GetInt32(0),
                Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture),
                reader.IsDBNull(2) ? null : Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<KitaronSourceWorkOrderMaterial>> ReadWorkOrderMaterialsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = WorkOrderMaterialQuery;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<KitaronSourceWorkOrderMaterial>();
        while (await reader.ReadAsync(cancellationToken))
        {
            EnsureWithinLimit(result.Count);
            result.Add(new KitaronSourceWorkOrderMaterial(
                reader.GetInt32(0),
                KitaronTextNormalization.CleanRequired(reader.GetString(1)),
                Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : Convert.ToDouble(reader.GetValue(6), CultureInfo.InvariantCulture)));
        }
        return result;
    }

    // Every open work order is imported by its own state (route open, not stopped), also when its
    // sales-order line is closed or it has none - production for stock (owner decision 2026-09-28).
    // The last column tells whether the line is still open; those work orders fill demand first.
    internal const string WorkOrderQuery = """
        SELECT rc.NUMBER, d.DetailNumber, rc.RecordID, rc.Amount, rc.ProductionAmount,
               rc.SupplyDate, rc.LotNumber, NULLIF(rc.RowMaterialID, 0), o.OrderNumber, c.CompanyName,
               d.DetailName, d.REV, rc.StartDate,
               CAST(CASE WHEN so.Closed = 0 AND so.StopProduction = 0 THEN 1 ELSE 0 END AS bit)
        FROM dbo.TRootCard rc
        LEFT JOIN dbo.TSubOrder so ON so.RecordID = rc.RecordID
        JOIN dbo.TDetails d ON d.DetailID = rc.DetailID
        LEFT JOIN dbo.TOrder o ON o.OrderID = so.OrderID
        LEFT JOIN dbo.TCustomer c ON c.CustomerID = o.CustomerID
        WHERE rc.RauteClosed = 0 AND rc.Stoped = 0
          AND NULLIF(LTRIM(RTRIM(d.DetailNumber)), N'') IS NOT NULL
        ORDER BY rc.NUMBER;
        """;

    internal const string WorkOrderLinkQuery = """
        SELECT l.RootID, l.RecordID, l.ProdAmount
        FROM dbo.TOrderLinkRoot l
        JOIN dbo.TRootCard rc ON rc.NUMBER = l.RootID
        WHERE rc.RauteClosed = 0 AND rc.Stoped = 0
        ORDER BY l.RootID, l.RecordID;
        """;

    internal const string WorkOrderMaterialQuery = """
        SELECT w.RootID, m.DetailNumber, w.Amount, w.StockMoved, w.StockAmount,
               w.AmountInBuy, w.RunningSum_ForStartDate
        FROM dbo.TBOMWithdrawalByRoot w
        JOIN dbo.TRootCard rc ON rc.NUMBER = w.RootID
        JOIN dbo.TDetails m ON m.DetailID = w.MDetID
        WHERE rc.RauteClosed = 0 AND rc.Stoped = 0
          AND w.Amount IS NOT NULL AND w.Amount > 0
          AND NULLIF(LTRIM(RTRIM(m.DetailNumber)), N'') IS NOT NULL
        ORDER BY w.RootID, w.AutoID;
        """;

    /// <summary>
    /// The complete route master: every header linked to a part and every step of it. The part scope
    /// is applied later against the synchronized Cases; the whole master is 58,569 rows on the
    /// commissioned database, well inside the row limit and cheaper than a scoped join.
    /// </summary>
    private static async Task<IReadOnlyList<KitaronSourceRouteStep>> ReadRouteStepsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RouteStepQuery;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<KitaronSourceRouteStep>();
        while (await reader.ReadAsync(cancellationToken))
        {
            EnsureWithinLimit(result.Count);
            result.Add(new KitaronSourceRouteStep(
                KitaronTextNormalization.CleanRequired(reader.GetString(0)),
                reader.IsDBNull(1) ? null : KitaronTextNormalization.Clean(reader.GetString(1)),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : KitaronTextNormalization.Clean(reader.GetString(3)),
                !reader.IsDBNull(4) && reader.GetBoolean(4),
                !reader.IsDBNull(5) && reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture),
                reader.IsDBNull(9) ? null : Convert.ToString(reader.GetValue(9), CultureInfo.InvariantCulture),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : Convert.ToInt32(reader.GetValue(12), CultureInfo.InvariantCulture),
                !reader.IsDBNull(13) && reader.GetBoolean(13),
                reader.IsDBNull(14) ? null : Convert.ToDouble(reader.GetValue(14), CultureInfo.InvariantCulture),
                reader.IsDBNull(15) ? null : Convert.ToDouble(reader.GetValue(15), CultureInfo.InvariantCulture),
                reader.IsDBNull(16) ? null : Convert.ToInt32(reader.GetValue(16), CultureInfo.InvariantCulture)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<KitaronDiscoveredStation>> ReadStationsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = StationQuery;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<KitaronDiscoveredStation>();
        while (await reader.ReadAsync(cancellationToken))
        {
            EnsureWithinLimit(result.Count);
            result.Add(new KitaronDiscoveredStation(
                reader.GetInt32(0),
                KitaronTextNormalization.Clean(reader.IsDBNull(1) ? null : reader.GetString(1))
                    ?? $"Station {reader.GetInt32(0).ToString(CultureInfo.InvariantCulture)}",
                reader.IsDBNull(2) ? null : KitaronTextNormalization.Clean(reader.GetString(2)),
                !reader.IsDBNull(3) && reader.GetBoolean(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6)));
        }
        return result;
    }

    internal const string RouteStepQuery = """
        SELECT d.DetailNumber, d.REV, l.DirectionHeaderID, l.REV AS HeaderRev, l.ChartMaster,
               h.IsMaster, h.ExpiredDate,
               r.DirectionID, r.NumOrder, r.ActionNumber, r.OperationDescription, o.Operation,
               r.StationID, r.WorkPlanning, r.TimeProduction, r.DirectionTime, r.SupplierID
        FROM dbo.TDetailDirectionList l
        JOIN dbo.TDetails d ON d.DetailID = l.DetailID
        JOIN dbo.TDetailDirectionHeader h ON h.DirectionHeaderID = l.DirectionHeaderID
        JOIN dbo.TDirection r ON r.DirectionHeaderID = l.DirectionHeaderID
        LEFT JOIN dbo.TOperation o ON o.OperationID = r.OperationID
        WHERE NULLIF(LTRIM(RTRIM(d.DetailNumber)), N'') IS NOT NULL
        ORDER BY d.DetailNumber, l.DirectionHeaderID, r.NumOrder, r.DirectionID;
        """;

    internal const string StationQuery = """
        SELECT s.StationID, s.Station, s.StationType, s.Retired,
               COUNT(r.DirectionID) AS RouteRows,
               SUM(CASE WHEN r.WorkPlanning = 1 THEN 1 ELSE 0 END) AS PlannedRows,
               SUM(CASE WHEN r.SupplierID IS NOT NULL AND r.SupplierID > 0 THEN 1 ELSE 0 END) AS SupplierRows
        FROM dbo.TStation s
        LEFT JOIN dbo.TDirection r ON r.StationID = s.StationID
        GROUP BY s.StationID, s.Station, s.StationType, s.Retired
        ORDER BY s.StationID;
        """;

    private static async Task<IReadOnlyList<KitaronSourceRow>> ReadWorkRowsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        StoredKitaronConnectionSettings settings,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BuildQuery(settings.ViewSchema, settings.ViewName, columns);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<KitaronSourceRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            EnsureWithinLimit(result.Count);
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[reader.GetName(index)] = await reader.IsDBNullAsync(index, cancellationToken)
                    ? null
                    : reader.GetValue(index);
            }
            result.Add(new KitaronSourceRow(values));
        }
        return result;
    }

    private static async Task<IReadOnlyList<KitaronSourceOrder>> ReadOrdersAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        StoredKitaronConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        var priceColumn = await FindFirstColumnAsync(
            connection, transaction,
            "dbo",
            "TSubOrder",
            OrderPriceColumnCandidates,
            cancellationToken);
        var suppliedColumn = await FindFirstColumnAsync(
            connection, transaction,
            "dbo",
            "TSubOrder",
            OrderSuppliedColumnCandidates,
            cancellationToken);
        var rowClosedColumns = await FindColumnsAsync(
            connection, transaction,
            "dbo",
            "TSubOrder",
            OrderRowClosedColumnCandidates,
            cancellationToken);
        var headerClosedColumns = await FindColumnsAsync(
            connection, transaction,
            "dbo",
            "TOrder",
            OrderHeaderClosedColumnCandidates,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BuildOrderQuery(
            settings.ViewSchema,
            settings.ViewName,
            priceColumn,
            rowClosedColumns,
            headerClosedColumns,
            suppliedColumn);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<KitaronSourceOrder>();
        while (await reader.ReadAsync(cancellationToken))
        {
            EnsureWithinLimit(result.Count);
            var partNumber = KitaronTextNormalization.CleanRequired(reader.GetString(1));
            result.Add(new KitaronSourceOrder(
                reader.GetInt32(0).ToString(CultureInfo.InvariantCulture),
                partNumber,
                reader.IsDBNull(2) ? partNumber : KitaronTextNormalization.CleanRequired(reader.GetString(2)),
                reader.IsDBNull(3) ? null : KitaronTextNormalization.Clean(reader.GetString(3)),
                KitaronTextNormalization.CleanRequired(reader.GetString(4)),
                reader.IsDBNull(5) ? null : Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                reader.GetBoolean(7),
                !reader.IsDBNull(8) && Convert.ToBoolean(reader.GetValue(8), CultureInfo.InvariantCulture),
                reader.IsDBNull(9) ? null : Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture),
                reader.IsDBNull(10) ? null : Convert.ToDouble(reader.GetValue(10), CultureInfo.InvariantCulture)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<KitaronSourceRow>> ReadMaterialRowsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BuildMaterialQuery(columns);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<KitaronSourceRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            EnsureWithinLimit(result.Count);
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[reader.GetName(index)] = await reader.IsDBNullAsync(index, cancellationToken)
                    ? null
                    : reader.GetValue(index);
            }
            result.Add(new KitaronSourceRow(values));
        }
        return result;
    }

    private static async Task<IReadOnlyList<KitaronSourceComponent>> ReadComponentsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ComponentQuery;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<KitaronSourceComponent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            EnsureWithinLimit(result.Count);
            var parentPartNumber = KitaronTextNormalization.CleanRequired(reader.GetString(2));
            var childPartNumber = KitaronTextNormalization.CleanRequired(reader.GetString(5));
            result.Add(new KitaronSourceComponent(
                $"{reader.GetInt32(0).ToString(CultureInfo.InvariantCulture)}:{reader.GetInt32(1).ToString(CultureInfo.InvariantCulture)}",
                parentPartNumber,
                reader.IsDBNull(3) ? parentPartNumber : KitaronTextNormalization.CleanRequired(reader.GetString(3)),
                reader.IsDBNull(4) ? null : KitaronTextNormalization.Clean(reader.GetString(4)),
                childPartNumber,
                reader.IsDBNull(6) ? childPartNumber : KitaronTextNormalization.CleanRequired(reader.GetString(6)),
                reader.IsDBNull(7) ? null : KitaronTextNormalization.Clean(reader.GetString(7)),
                Convert.ToDouble(reader.GetValue(8), CultureInfo.InvariantCulture),
                reader.GetInt32(9)));
        }
        return result;
    }

    private static void EnsureWithinLimit(int currentCount)
    {
        if (currentCount >= MaximumRows)
            throw new KitaronSyncDataException($"A Kitaron source query exceeded the {MaximumRows:N0}-row safety limit.");
    }

    internal static string BuildQuery(string schema, string view, IReadOnlyList<string> columns) =>
        $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(schema)}.{Quote(view)};";

    internal static string BuildOrderQuery(
        string schema,
        string view,
        string? priceColumn = null,
        IReadOnlyList<string>? rowClosedColumns = null,
        IReadOnlyList<string>? headerClosedColumns = null,
        string? suppliedColumn = null)
    {
        // A zero is Kitaron's "no price entered"; the Order then carries no price rather than 0.
        var price = priceColumn is null ? "CAST(NULL AS decimal(19,4))" : $"NULLIF(so.{Quote(priceColumn)}, 0)";
        var supplied = suppliedColumn is null ? "CAST(NULL AS float)" : $"so.{Quote(suppliedColumn)}";
        var closedChecks = (rowClosedColumns ?? [])
            .Select(column => ClosedCheck("so", column))
            .Concat((headerClosedColumns ?? [])
                .Select(column => ClosedCheck("o", column)))
            .ToArray();
        var closed = closedChecks.Length == 0
            ? "CAST(0 AS bit)"
            : $"CONVERT(bit, CASE WHEN {string.Join(" OR ", closedChecks)} THEN 1 ELSE 0 END)";
        return $$"""
        WITH source_details AS (
            SELECT DISTINCT detail.DetailID
            FROM {{Quote(schema)}}.{{Quote(view)}} work
            JOIN dbo.TDetails detail
              ON LTRIM(RTRIM(detail.DetailNumber)) = LTRIM(RTRIM(work.{{Quote("DetailNumber")}}))
            WHERE NULLIF(LTRIM(RTRIM(work.{{Quote("DetailNumber")}})), N'') IS NOT NULL
            UNION
            SELECT DISTINCT node.TreeHead
            FROM dbo.TTreeNodes node
            WHERE node.Tree = N'Detail'
            UNION
            SELECT DISTINCT node.IDNodeContens
            FROM dbo.TTreeNodes node
            WHERE node.Tree = N'Detail'
              AND node.IDNodeContens <> node.TreeHead
            UNION
            SELECT DISTINCT DetailID
            FROM dbo.TSubOrder
            WHERE StopProduction = 1
            UNION
            SELECT DISTINCT open_work_order.DetailID
            FROM dbo.TRootCard open_work_order
            WHERE open_work_order.RauteClosed = 0
        )
        SELECT so.RecordID, d.DetailNumber, d.DetailName, d.REV,
               o.OrderNumber, so.Number, so.SupplyDate, so.StopProduction,
               {{closed}} AS IsClosed, {{price}} AS Price, {{supplied}} AS Supplied
        FROM source_details source
        JOIN dbo.TSubOrder so
          ON so.DetailID = source.DetailID
        JOIN dbo.TDetails d ON d.DetailID = so.DetailID
        JOIN dbo.TOrder o ON o.OrderID = so.OrderID
        WHERE NULLIF(LTRIM(RTRIM(d.DetailNumber)), N'') IS NOT NULL
          AND NULLIF(LTRIM(RTRIM(o.OrderNumber)), N'') IS NOT NULL
          AND LTRIM(RTRIM(o.OrderNumber)) <> N'הזמנה לדוגמא 1'
        ORDER BY so.RecordID;
        """;
    }

    private static async Task<string?> FindFirstColumnAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string schema,
        string table,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.name
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id=c.object_id
            JOIN sys.schemas s ON s.schema_id=t.schema_id
            WHERE s.name=@schema AND t.name=@table;
            """;
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) available.Add(reader.GetString(0));
        return candidates.FirstOrDefault(available.Contains);
    }

    private static async Task<IReadOnlyList<string>> FindColumnsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string schema,
        string table,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.name
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id=c.object_id
            JOIN sys.schemas s ON s.schema_id=t.schema_id
            WHERE s.name=@schema AND t.name=@table;
            """;
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) available.Add(reader.GetString(0));
        return candidates.Where(available.Contains).ToArray();
    }

    internal static string? SelectOrderPriceColumn(IEnumerable<string> availableColumns)
    {
        var available = availableColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return OrderPriceColumnCandidates.FirstOrDefault(available.Contains);
    }

    internal static string BuildMaterialQuery(IReadOnlyList<string> columns) => $$"""
        WITH material_orders AS (
            SELECT purchase_row.BuyRowID,
                   purchase_row.BuyMainID,
                   purchase_row.NumberOfString,
                   purchase_row.RowMaterialID,
                   purchase_row.Information,
                   purchase_main.SupplyerName,
                   purchase_row.Amount,
                   COALESCE(receipts.ReceivedAmount, 0) AS ReceivedAmount,
                   purchase_row.MeasureUnit,
                   purchase_row.DateToRecept,
                   approval.AppDate AS SupplierDate,
                   approval.Amount AS SupplierAmount,
                   approval.Remark AS SupplierRemark,
                   purchase_row.Status,
                   purchase_row.Price,
                   purchase_row.RowPrice,
                   purchase_row.CustOrderRow,
                   CONVERT(bit, CASE WHEN purchase_row.RowClosed = 1 OR purchase_main.OrderClosed = 1
                                    OR purchase_main.Closed = 1 THEN 1 ELSE 0 END) AS Closed
            FROM dbo.TBuyRow purchase_row
            JOIN dbo.TBuyMain purchase_main
              ON purchase_main.BuyMainID = purchase_row.BuyMainID
            OUTER APPLY (
                SELECT TOP (1) candidate.AppDate, candidate.Amount, candidate.Remark
                FROM dbo.TAppCostOfferBySupplier candidate
                WHERE candidate.BuyMainID = purchase_row.BuyMainID
                  AND candidate.BuyID = purchase_row.BuyRowID
                  AND candidate.SupplierName = purchase_main.SupplyerName
                ORDER BY candidate.PresentDate DESC, candidate.AppCostOfferID DESC
            ) approval
            LEFT JOIN (
                SELECT BuyMainID, BuyID, SUM(COALESCE(BuyRecieved, 0)) AS ReceivedAmount
                FROM dbo.TBuyReceptionHeader
                GROUP BY BuyMainID, BuyID
            ) receipts ON receipts.BuyMainID = purchase_row.BuyMainID
                      AND receipts.BuyID = purchase_row.BuyRowID
        )
        SELECT {{string.Join(", ", columns.Select(Quote))}}
        FROM material_orders
        ORDER BY BuyMainID, NumberOfString, BuyRowID;
        """;

    private const string ComponentQuery = """
        SELECT parent.DetailID, component.DetailID,
               parent.DetailNumber, parent.DetailName, parent.REV,
               component.DetailNumber, component.DetailName, component.REV,
               COALESCE(NULLIF(child.DirectQtyInParent, 0),
                        child.AmountInHead / NULLIF(root.AmountInHead, 0)) AS quantity_per_parent,
               CONVERT(int, ROW_NUMBER() OVER (PARTITION BY parent.DetailID ORDER BY child.KeyNode) - 1) AS sort_order
        FROM dbo.TTreeNodes child
        JOIN dbo.TTreeNodes root
          ON root.Tree = child.Tree
         AND root.TreeHead = child.TreeHead
         AND root.KeyNode = child.RelativeNode
         AND root.RelativeNode = 0
        JOIN dbo.TDetails parent ON parent.DetailID = child.TreeHead
        JOIN dbo.TDetails component ON component.DetailID = child.IDNodeContens
        WHERE child.Tree = N'Detail'
          AND child.IDNodeContens <> child.TreeHead
          AND NULLIF(LTRIM(RTRIM(parent.DetailNumber)), N'') IS NOT NULL
          AND NULLIF(LTRIM(RTRIM(component.DetailNumber)), N'') IS NOT NULL
          AND COALESCE(NULLIF(child.DirectQtyInParent, 0),
                       child.AmountInHead / NULLIF(root.AmountInHead, 0)) > 0
        ORDER BY parent.DetailID, child.KeyNode;
        """;

    private static string Quote(string value) =>
        $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string ClosedCheck(string alias, string column) =>
        $"COALESCE(TRY_CONVERT(int, {alias}.{Quote(column)}), 0) <> 0";
}
