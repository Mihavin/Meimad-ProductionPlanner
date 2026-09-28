using System.Text;
using Microsoft.Data.SqlClient;

namespace Meimad.Planner.Server.Application.Kitaron.Push;

/// <summary>
/// Reads and writes <c>dbo.TSubRootCard</c> on Kitaron's SQL Server. Column names come only from
/// <see cref="KitaronPushCatalog"/>; every value is a parameter. All writes of a run share one
/// transaction, so Kitaron gets all of them or none, and each update must hit exactly one row.
/// Kitaron's own <c>TSubRootCard_UPDATE</c> trigger runs as it does for Kitaron's own edits.
/// </summary>
internal sealed class SqlServerKitaronPushTarget : IKitaronPushTarget
{
    private const int NumbersPerQuery = 1000;

    public async Task<IReadOnlyList<KitaronOperationRow>> ReadAsync(
        StoredKitaronConnectionSettings connection, string password, IReadOnlyCollection<int> workOrderNumbers,
        IReadOnlyList<string> columns, CancellationToken cancellationToken)
    {
        var safeColumns = columns.Select(Column).Distinct(StringComparer.Ordinal).ToArray();
        var rows = new List<KitaronOperationRow>();
        await using var sql = new SqlConnection(ConnectionString(connection, password, readOnly: true));
        await sql.OpenAsync(cancellationToken);
        foreach (var chunk in workOrderNumbers.Chunk(NumbersPerQuery))
        {
            await using var command = sql.CreateCommand();
            var select = new StringBuilder(
                "SELECT s.[auto], s.[NUMBER], s.[ActionNumber], "
                + "CAST(CASE WHEN r.[RauteClosed] = 1 OR r.[Stoped] = 1 THEN 1 ELSE 0 END AS bit)");
            foreach (var column in safeColumns) select.Append(", s.[").Append(column).Append(']');
            select.Append(" FROM dbo.TSubRootCard s WITH (NOLOCK) JOIN dbo.TRootCard r WITH (NOLOCK) ON r.[NUMBER] = s.[NUMBER] WHERE s.[NUMBER] IN (");
            for (var index = 0; index < chunk.Length; index++)
            {
                if (index > 0) select.Append(", ");
                select.Append("@n").Append(index);
                command.Parameters.AddWithValue("@n" + index, chunk[index]);
            }
            select.Append(");");
            command.CommandText = select.ToString();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < safeColumns.Length; index++)
                    values[safeColumns[index]] = reader.IsDBNull(4 + index) ? null : reader.GetValue(4 + index);
                rows.Add(new KitaronOperationRow(
                    Convert.ToInt64(reader.GetValue(0)),
                    Convert.ToInt32(reader.GetValue(1)),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.GetBoolean(3),
                    values));
            }
        }
        return rows;
    }

    public async Task WriteAsync(
        StoredKitaronConnectionSettings connection, string password, IReadOnlyList<KitaronPushWrite> writes,
        CancellationToken cancellationToken)
    {
        await using var sql = new SqlConnection(ConnectionString(connection, password, readOnly: false));
        await sql.OpenAsync(cancellationToken);
        await using (var settings = sql.CreateCommand())
        {
            // Wait at most 15 seconds for a row a Kitaron user is editing, then give up without changes.
            settings.CommandText = "SET XACT_ABORT ON; SET LOCK_TIMEOUT 15000;";
            await settings.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var transaction = (SqlTransaction)await sql.BeginTransactionAsync(cancellationToken);
        foreach (var write in writes)
        {
            await using var command = sql.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"UPDATE dbo.TSubRootCard SET [{Column(write.Column)}] = @value WHERE [auto] = @row AND [NUMBER] = @number;";
            command.Parameters.AddWithValue("@value", write.Value);
            command.Parameters.AddWithValue("@row", write.RowId);
            command.Parameters.AddWithValue("@number", write.WorkOrderNumber);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken);
            if (changed != 1)
            {
                throw new InvalidOperationException(
                    $"Kitaron operation row {write.RowId} of Work Order {write.WorkOrderNumber} no longer exists.");
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static string Column(string column) =>
        KitaronPushCatalog.Target(column)?.Column
        ?? throw new InvalidOperationException($"Kitaron column '{column}' is not pushable.");

    private static string ConnectionString(StoredKitaronConnectionSettings settings, string password, bool readOnly) =>
        new SqlConnectionStringBuilder
        {
            DataSource = $"{settings.ServerHost},{settings.ServerPort}",
            InitialCatalog = settings.DatabaseName,
            UserID = settings.Username,
            Password = password,
            ApplicationIntent = readOnly ? ApplicationIntent.ReadOnly : ApplicationIntent.ReadWrite,
            ApplicationName = "Meimad Planner Kitaron push",
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
            CommandTimeout = 60,
            Pooling = false
        }.ToString();
}
