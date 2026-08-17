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
        command.CommandText = SchemaQuery(settings.ViewSchema, settings.ViewName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<KitaronSourceColumn>(reader.FieldCount);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            columns.Add(new KitaronSourceColumn(
                reader.GetName(index),
                reader.GetDataTypeName(index)));
        }
        return columns;
    }

    internal static string SchemaQuery(string schema, string view) =>
        $"SELECT TOP (0) * FROM [{schema.Replace("]", "]]", StringComparison.Ordinal)}]." +
        $"[{view.Replace("]", "]]", StringComparison.Ordinal)}];";
}
