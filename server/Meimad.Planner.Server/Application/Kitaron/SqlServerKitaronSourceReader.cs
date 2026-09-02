using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed class SqlServerKitaronSourceReader : IKitaronSourceReader
{
    private const int MaximumRows = 200_000;
    private static readonly string[] OrderPriceColumnCandidates =
    [
        // The commissioned Kitaron schema stores the sales-order unit price in
        // the order currency as PriceInCurr. Do not substitute manufacturing
        // cost, BOM cost, or calculated row-total fields for this value.
        "PriceInCurr",
        "UnitPrice",
        "PriceForOne",
        "PricePerUnit",
        "OrderPrice",
        "Price",
        "RowPrice",
        "PriceRow"
    ];
    private static readonly string[] OrderRowClosedColumnCandidates =
    [
        "OrderClosed",
        "RecordClosed",
        "RowClosed",
        "Closed",
        "IsClosed",
        "Completed",
        "IsCompleted"
    ];
    private static readonly string[] OrderHeaderClosedColumnCandidates =
    [
        "OrderClosed",
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
        return new KitaronSourceSnapshot(workRows, orders, components, materialRows);
    }

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
            headerClosedColumns);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<KitaronSourceOrder>();
        while (await reader.ReadAsync(cancellationToken))
        {
            EnsureWithinLimit(result.Count);
            result.Add(new KitaronSourceOrder(
                reader.GetInt32(0).ToString(CultureInfo.InvariantCulture),
                reader.GetString(1).Trim(),
                reader.IsDBNull(2) ? reader.GetString(1).Trim() : reader.GetString(2).Trim(),
                reader.IsDBNull(3) ? null : NullIfWhiteSpace(reader.GetString(3)),
                reader.GetString(4).Trim(),
                reader.IsDBNull(5) ? null : Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                reader.GetBoolean(7),
                !reader.IsDBNull(8) && Convert.ToBoolean(reader.GetValue(8), CultureInfo.InvariantCulture),
                reader.IsDBNull(9) ? null : Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture)));
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
            result.Add(new KitaronSourceComponent(
                $"{reader.GetInt32(0).ToString(CultureInfo.InvariantCulture)}:{reader.GetInt32(1).ToString(CultureInfo.InvariantCulture)}",
                reader.GetString(2).Trim(),
                reader.IsDBNull(3) ? reader.GetString(2).Trim() : reader.GetString(3).Trim(),
                reader.IsDBNull(4) ? null : NullIfWhiteSpace(reader.GetString(4)),
                reader.GetString(5).Trim(),
                reader.IsDBNull(6) ? reader.GetString(5).Trim() : reader.GetString(6).Trim(),
                reader.IsDBNull(7) ? null : NullIfWhiteSpace(reader.GetString(7)),
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

    private static string? NullIfWhiteSpace(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    internal static string BuildQuery(string schema, string view, IReadOnlyList<string> columns) =>
        $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(schema)}.{Quote(view)};";

    internal static string BuildOrderQuery(
        string schema,
        string view,
        string? priceColumn = null,
        IReadOnlyList<string>? rowClosedColumns = null,
        IReadOnlyList<string>? headerClosedColumns = null)
    {
        var price = priceColumn is null ? "CAST(NULL AS decimal(19,4))" : $"so.{Quote(priceColumn)}";
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
               {{closed}} AS IsClosed, {{price}} AS Price
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

    private static string ClosedCheck(string alias, string column)
    {
        var comparison = column.Equals("OrderClosed", StringComparison.OrdinalIgnoreCase)
            ? "= 2"
            : "<> 0";
        return $"COALESCE(TRY_CONVERT(int, {alias}.{Quote(column)}), 0) {comparison}";
    }
}
