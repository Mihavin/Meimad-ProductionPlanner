using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed class SqlServerKitaronSourceReader : IKitaronSourceReader
{
    private const int MaximumRows = 200_000;

    public async Task<KitaronSourceSnapshot> ReadAsync(
        StoredKitaronConnectionSettings settings,
        string password,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken)
    {
        if (columns.Count == 0)
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
        var workRows = await ReadWorkRowsAsync(connection, settings, columns, cancellationToken);
        var orders = await ReadOrdersAsync(connection, settings, cancellationToken);
        var components = await ReadComponentsAsync(connection, cancellationToken);
        return new KitaronSourceSnapshot(workRows, orders, components);
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
        await using var command = connection.CreateCommand();
        command.CommandText = BuildOrderQuery(settings.ViewSchema, settings.ViewName);
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
                reader.GetBoolean(7)));
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

    internal static string BuildOrderQuery(string schema, string view) => $$"""
        WITH source_order_ids AS (
            SELECT DISTINCT TRY_CONVERT(int, {{Quote("RecordID")}}) AS record_id
            FROM {{Quote(schema)}}.{{Quote(view)}}
            WHERE {{Quote("RecordID")}} IS NOT NULL
            UNION
            SELECT RecordID
            FROM dbo.TSubOrder
            WHERE StopProduction = 1
        )
        SELECT so.RecordID, d.DetailNumber, d.DetailName, d.REV,
               o.OrderNumber, so.Number, so.SupplyDate, so.StopProduction
        FROM source_order_ids source
        JOIN dbo.TSubOrder so ON so.RecordID = source.record_id
        JOIN dbo.TDetails d ON d.DetailID = so.DetailID
        JOIN dbo.TOrder o ON o.OrderID = so.OrderID
        WHERE NULLIF(LTRIM(RTRIM(d.DetailNumber)), N'') IS NOT NULL
          AND NULLIF(LTRIM(RTRIM(o.OrderNumber)), N'') IS NOT NULL
        ORDER BY so.RecordID;
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
}
