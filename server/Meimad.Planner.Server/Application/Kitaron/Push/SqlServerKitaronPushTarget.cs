using System.Text;
using System.Data;
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
            select.Append(" FROM dbo.TSubRootCard s JOIN dbo.TRootCard r ON r.[NUMBER] = s.[NUMBER] WHERE s.[NUMBER] IN (");
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
        try { await sql.OpenAsync(cancellationToken); }
        catch (Exception exception) { throw new KitaronPushNotCommittedException("ERP connection failed before writing.", exception); }
        await WriteAsync(sql, writes, cancellationToken);
    }

    // The caller opens the connection; this overload also permits isolated SQL Server acceptance tests.
    internal static async Task WriteAsync(SqlConnection sql, IReadOnlyList<KitaronPushWrite> writes,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginWriteAsync(sql, cancellationToken);
        var commitAttempted = false;
        try
        {
            // Stable lock order; all rows/fields in this run remain one atomic unit.
            foreach (var group in writes.GroupBy(x => (x.WorkOrderNumber, x.RowId))
                         .OrderBy(x => x.Key.WorkOrderNumber).ThenBy(x => x.Key.RowId))
            {
                var expected = group.First().Expected;
                if (expected.RowId != group.Key.RowId || expected.WorkOrderNumber != group.Key.WorkOrderNumber
                    || group.Select(x => Column(x.Column)).Distinct(StringComparer.Ordinal).Count() != group.Count()
                    || group.Any(x => !KitaronPushComparison.Matches(expected, x.Expected)
                        || x.Expected.Values.Count != expected.Values.Count || !expected.Values.ContainsKey(Column(x.Column))))
                    throw KitaronPushComparison.Conflict(group.Key.RowId);

                var columns = expected.Values.Keys.Select(Column).Order(StringComparer.Ordinal).ToArray();
                await using (var check = sql.CreateCommand())
                {
                    check.Transaction = transaction;
                    check.CommandText = "SELECT s.[auto], s.[NUMBER], s.[ActionNumber], "
                        + "CAST(CASE WHEN r.[RauteClosed] = 1 OR r.[Stoped] = 1 THEN 1 ELSE 0 END AS bit), "
                        + string.Join(", ", columns.Select(c => $"s.[{c}]"))
                        + " FROM dbo.TRootCard r WITH (UPDLOCK, HOLDLOCK)"
                        + " JOIN dbo.TSubRootCard s WITH (UPDLOCK, HOLDLOCK) ON r.[NUMBER] = s.[NUMBER]"
                        + " WHERE s.[NUMBER] = @number;";
                    check.Parameters.AddWithValue("@number", group.Key.WorkOrderNumber);
                    await using var reader = await check.ExecuteReaderAsync(cancellationToken);
                    KitaronOperationRow? current = null;
                    var matchingOperations = 0;
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var action = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                        if (KitaronPushPlanner.ParseActionNumber(action) == KitaronPushPlanner.ParseActionNumber(expected.ActionNumber))
                            matchingOperations++;
                        if (Convert.ToInt64(reader.GetValue(0)) != group.Key.RowId) continue;
                        if (current is not null) throw KitaronPushComparison.Conflict(group.Key.RowId);
                        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        for (var i = 0; i < columns.Length; i++)
                            values[columns[i]] = reader.IsDBNull(i + 4) ? null : reader.GetValue(i + 4);
                        current = new(Convert.ToInt64(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(1)),
                            action, reader.GetBoolean(3), values);
                    }
                    if (!KitaronPushComparison.Matches(expected, current) || matchingOperations != 1)
                        throw KitaronPushComparison.Conflict(group.Key.RowId);
                }

                await using var command = sql.CreateCommand();
                command.Transaction = transaction;
                var fields = group.ToArray();
                // OUTPUT INTO isolates our affected-row count from AFTER-trigger row counts/NOCOUNT.
                // An INSTEAD OF trigger could report a row without applying it, so refuse that unsupported schema.
                command.CommandText = """
                    IF EXISTS (SELECT 1 FROM sys.triggers WHERE parent_id = OBJECT_ID(N'dbo.TSubRootCard')
                        AND is_instead_of_trigger = 1 AND is_disabled = 0)
                        THROW 51000, 'Kitaron push does not support enabled INSTEAD OF triggers.', 1;
                    DECLARE @changed TABLE (id bigint);
                    """
                    + "UPDATE dbo.TSubRootCard SET "
                    + string.Join(", ", fields.Select((x, i) => $"[{Column(x.Column)}] = @value{i}"))
                    + " OUTPUT inserted.[auto] INTO @changed WHERE [auto] = @row AND [NUMBER] = @number;"
                    + " SELECT @changedCount = COUNT_BIG(*) FROM @changed;";
                for (var i = 0; i < fields.Length; i++)
                    command.Parameters.AddWithValue("@value" + i, fields[i].Value);
                command.Parameters.AddWithValue("@row", group.Key.RowId);
                command.Parameters.AddWithValue("@number", group.Key.WorkOrderNumber);
                var count = command.Parameters.Add("@changedCount", SqlDbType.BigInt);
                count.Direction = ParameterDirection.Output;
                await command.ExecuteNonQueryAsync(cancellationToken);
                var changed = Convert.ToInt64(count.Value);
                if (changed != 1) throw KitaronPushComparison.Conflict(group.Key.RowId);
            }
            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            if (commitAttempted) throw; // Connection loss/cancellation at commit is not proof of rollback.
            try
            {
                if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackError)
            {
                throw new InvalidOperationException("Could not establish ERP rollback; outcome is unknown.", rollbackError);
            }
            if (exception is KitaronPushConflictException) throw;
            throw new KitaronPushNotCommittedException("The ERP transaction was rolled back before commit.", exception);
        }
    }

    private static async Task<SqlTransaction> BeginWriteAsync(SqlConnection sql, CancellationToken token)
    {
        try
        {
            await using var settings = sql.CreateCommand();
            settings.CommandText = "SET XACT_ABORT ON; SET LOCK_TIMEOUT 15000;";
            await settings.ExecuteNonQueryAsync(token);
            return (SqlTransaction)await sql.BeginTransactionAsync(IsolationLevel.Serializable, token);
        }
        catch (Exception exception)
        {
            throw new KitaronPushNotCommittedException("ERP transaction could not begin; no write was attempted.", exception);
        }
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
