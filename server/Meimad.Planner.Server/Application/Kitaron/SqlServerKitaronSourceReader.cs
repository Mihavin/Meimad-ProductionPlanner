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
        var workRows = await ReadWorkRowsAsync(connection, settings, workColumns, cancellationToken);
        var orders = await ReadOrdersAsync(connection, settings, cancellationToken);
        var components = await ReadComponentsAsync(connection, cancellationToken);
        var materialRows = materialColumns.Count == 0
            ? []
            : await ReadMaterialRowsAsync(connection, materialColumns, cancellationToken);
        var routeSteps = await ReadRouteStepsAsync(connection, cancellationToken);
        var stations = await ReadStationsAsync(connection, cancellationToken);
        return new KitaronSourceSnapshot(workRows, orders, components, materialRows, routeSteps, stations);
    }

    /// <summary>
    /// The complete route master: every header linked to a part and every step of it. The part scope
    /// is applied later against the synchronized Cases; the whole master is 58,569 rows on the
    /// commissioned database, well inside the row limit and cheaper than a scoped join.
    /// </summary>
    private static async Task<IReadOnlyList<KitaronSourceRouteStep>> ReadRouteStepsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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
        StoredKitaronConnectionSettings settings,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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
        StoredKitaronConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        var priceColumn = await FindFirstColumnAsync(
            connection,
            "dbo",
            "TSubOrder",
            OrderPriceColumnCandidates,
            cancellationToken);
        var suppliedColumn = await FindFirstColumnAsync(
            connection,
            "dbo",
            "TSubOrder",
            OrderSuppliedColumnCandidates,
            cancellationToken);
        var rowClosedColumns = await FindColumnsAsync(
            connection,
            "dbo",
            "TSubOrder",
            OrderRowClosedColumnCandidates,
            cancellationToken);
        var headerClosedColumns = await FindColumnsAsync(
            connection,
            "dbo",
            "TOrder",
            OrderHeaderClosedColumnCandidates,
            cancellationToken);
        await using var command = connection.CreateCommand();
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
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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
        string schema,
        string table,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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
        string schema,
        string table,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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
                   CONVERT(bit, CASE WHEN purchase_row.RowClosed = 1 OR purchase_main.OrderClosed = 1
                                    OR purchase_main.Closed = 1 THEN 1 ELSE 0 END) AS Closed
            FROM dbo.TBuyRow purchase_row WITH (NOLOCK)
            JOIN dbo.TBuyMain purchase_main WITH (NOLOCK)
              ON purchase_main.BuyMainID = purchase_row.BuyMainID
            OUTER APPLY (
                SELECT TOP (1) candidate.AppDate, candidate.Amount, candidate.Remark
                FROM dbo.TAppCostOfferBySupplier candidate WITH (NOLOCK)
                WHERE candidate.BuyMainID = purchase_row.BuyMainID
                  AND candidate.BuyID = purchase_row.BuyRowID
                  AND candidate.SupplierName = purchase_main.SupplyerName
                ORDER BY candidate.PresentDate DESC, candidate.AppCostOfferID DESC
            ) approval
            LEFT JOIN (
                SELECT BuyMainID, BuyID, SUM(COALESCE(BuyRecieved, 0)) AS ReceivedAmount
                FROM dbo.TBuyReceptionHeader WITH (NOLOCK)
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
