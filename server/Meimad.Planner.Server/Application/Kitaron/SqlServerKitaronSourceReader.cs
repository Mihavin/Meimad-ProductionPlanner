using Microsoft.Data.SqlClient;

namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed class SqlServerKitaronSourceReader : IKitaronSourceReader
{
    private const int MaximumRows = 200_000;

    public async Task<IReadOnlyList<KitaronSourceRow>> ReadAsync(
        StoredKitaronConnectionSettings settings,
        string password,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken)
    {
        if (columns.Count == 0)
        {
            throw new KitaronSyncBlockedException("The ready mapping has no readable source columns.");
        }

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
        await using var command = connection.CreateCommand();
        command.CommandText = BuildQuery(settings.ViewSchema, settings.ViewName, columns);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<KitaronSourceRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (result.Count >= MaximumRows)
            {
                throw new KitaronSyncDataException(
                    $"The Kitaron source exceeded the {MaximumRows:N0}-row safety limit.");
            }
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

    internal static string BuildQuery(string schema, string view, IReadOnlyList<string> columns)
    {
        static string Quote(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
        return $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(schema)}.{Quote(view)};";
    }
}
