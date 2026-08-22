using Microsoft.Data.SqlClient;

namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed class SqlServerKitaronConnectionTester : IKitaronConnectionTester
{
    public async Task<IReadOnlyList<KitaronSourceColumn>> TestAsync(
        StoredKitaronConnectionSettings settings,
        string password,
        CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{settings.ServerHost},{settings.ServerPort}",
            InitialCatalog = settings.DatabaseName,
            UserID = settings.Username,
            Password = password,
            ApplicationIntent = ApplicationIntent.ReadOnly,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 10,
            CommandTimeout = 15,
            Pooling = false
        };

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SchemaQuery(settings.ViewSchema, settings.ViewName) + Environment.NewLine + MaterialSchemaQuery;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<KitaronSourceColumn>();
        do
        {
            for (var index = 0; index < reader.FieldCount; index++)
            {
                if (columns.Any(column => StringComparer.OrdinalIgnoreCase.Equals(column.Name, reader.GetName(index))))
                    continue;
                columns.Add(new KitaronSourceColumn(reader.GetName(index), reader.GetDataTypeName(index)));
            }
        }
        while (await reader.NextResultAsync(cancellationToken));
        return columns;
    }

    internal static string SchemaQuery(string schema, string view) =>
        $"SELECT TOP (0) * FROM [{schema.Replace("]", "]]", StringComparison.Ordinal)}]." +
        $"[{view.Replace("]", "]]", StringComparison.Ordinal)}];";

    internal const string MaterialSchemaQuery = """
        SELECT TOP (0)
            purchase_row.BuyRowID,
            purchase_row.BuyMainID,
            purchase_row.NumberOfString,
            purchase_row.RowMaterialID,
            purchase_row.Information,
            main.SupplyerName,
            purchase_row.Amount,
            CAST(0 AS float) AS ReceivedAmount,
            purchase_row.MeasureUnit,
            purchase_row.DateToRecept,
            CAST(NULL AS datetime) AS SupplierDate,
            CAST(NULL AS float) AS SupplierAmount,
            CAST(NULL AS nvarchar(4000)) AS SupplierRemark,
            purchase_row.Status,
            CAST(0 AS bit) AS Closed
        FROM dbo.TBuyRow purchase_row
        JOIN dbo.TBuyMain main ON main.BuyMainID = purchase_row.BuyMainID;
        """;
}
