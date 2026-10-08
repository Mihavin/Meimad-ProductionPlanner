using Meimad.Planner.Server.Application.Kitaron.Push;
using Microsoft.Data.SqlClient;

namespace Meimad.Planner.Server.Tests.Kitaron;

// Opt in with MEIMAD_C07_LOCALDB=MSSQLLocalDB. Only a local, disposable database is accepted.
public sealed class KitaronPushSqlServerTests
{
    [LocalSqlFact]
    public async Task Intervening_changes_rollback_the_whole_push_including_independent_rows()
    {
        await using var db = await Database.CreateAsync();
        foreach (var edit in new[]
        {
            "UPDATE dbo.TSubRootCard SET OperationQty=1.001 WHERE [auto]=2",
            "UPDATE dbo.TSubRootCard SET StartDateReal='2026-10-08T09:00:00.003' WHERE [auto]=2",
            "UPDATE dbo.TSubRootCard SET ActionNumber='20' WHERE [auto]=2",
            "UPDATE dbo.TRootCard SET RauteClosed=1 WHERE NUMBER=42",
            "UPDATE dbo.TRootCard SET Stoped=1 WHERE NUMBER=42",
            "DELETE dbo.TSubRootCard WHERE [auto]=2",
            "INSERT dbo.TSubRootCard VALUES (3,42,'010',1,NULL)"
        })
        {
            await db.ResetAsync();
            var writes = await db.PlanAsync();
            await db.ExecuteAsync(edit);
            await Assert.ThrowsAsync<KitaronPushConflictException>(() =>
                SqlServerKitaronPushTarget.WriteAsync(db.Connection, writes, CancellationToken.None));
            Assert.Equal(1m, await db.QuantityAsync(1));
        }
    }

    [LocalSqlFact]
    public async Task Triggers_cannot_hide_failed_updates_or_inflate_affected_row_counts()
    {
        await using var db = await Database.CreateAsync();
        await db.ResetAsync();
        await db.ExecuteAsync("CREATE TABLE dbo.Audit (id bigint);");
        await db.ExecuteAsync("""
            CREATE TRIGGER dbo.PushTest ON dbo.TSubRootCard AFTER UPDATE AS
            BEGIN
                SET NOCOUNT OFF;
                INSERT dbo.Audit SELECT [auto] FROM inserted;
                INSERT dbo.Audit SELECT [auto] FROM inserted;
            END
            """);
        await SqlServerKitaronPushTarget.WriteAsync(db.Connection, await db.PlanAsync(), CancellationToken.None);
        Assert.Equal(6m, await db.QuantityAsync(1));
        Assert.Equal(6m, await db.QuantityAsync(2));
        await db.ExecuteAsync("DROP TRIGGER dbo.PushTest;");
        await db.ResetAsync();
        await db.ExecuteAsync("""
            CREATE TRIGGER dbo.PushTest ON dbo.TSubRootCard AFTER UPDATE AS
            BEGIN
                IF EXISTS (SELECT 1 FROM inserted WHERE [auto]=2)
                    THROW 51001, 'Test ERP validation failure.', 1;
            END
            """);
        var failingWrites = await db.PlanAsync();
        await Assert.ThrowsAsync<SqlException>(() =>
            SqlServerKitaronPushTarget.WriteAsync(db.Connection, failingWrites, CancellationToken.None));
        Assert.Equal(1m, await db.QuantityAsync(1));
        await db.ExecuteAsync("DROP TRIGGER dbo.PushTest;");
        await db.ExecuteAsync("CREATE TRIGGER dbo.PushTest ON dbo.TSubRootCard INSTEAD OF UPDATE AS BEGIN SET NOCOUNT ON; END");
        var writes = await db.PlanAsync();
        await Assert.ThrowsAsync<SqlException>(() => SqlServerKitaronPushTarget.WriteAsync(db.Connection, writes, CancellationToken.None));
        Assert.Equal(1m, await db.QuantityAsync(1));
    }

    private sealed class Database(SqlConnection connection, string master, string name) : IAsyncDisposable
    {
        internal SqlConnection Connection => connection;

        internal static async Task<Database> CreateAsync()
        {
            var instance = Environment.GetEnvironmentVariable("MEIMAD_C07_LOCALDB")!;
            if (string.IsNullOrWhiteSpace(instance) || instance.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                throw new InvalidOperationException("Supply only a LocalDB instance name, never a factory connection string.");
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = @"(localdb)\" + instance, InitialCatalog = "master", IntegratedSecurity = true,
                TrustServerCertificate = true, Pooling = false
            };
            var master = builder.ConnectionString;
            var name = "Meimad_C07_Test_" + Guid.NewGuid().ToString("N");
            await using (var admin = new SqlConnection(master))
            {
                await admin.OpenAsync();
                await using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE [{name}]";
                await create.ExecuteNonQueryAsync();
            }
            builder.InitialCatalog = name;
            var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            var db = new Database(connection, master, name);
            await db.ExecuteAsync("""
                CREATE TABLE dbo.TRootCard (NUMBER int PRIMARY KEY, RauteClosed bit NULL, Stoped bit NULL);
                CREATE TABLE dbo.TSubRootCard ([auto] bigint PRIMARY KEY, NUMBER int, ActionNumber nvarchar(20),
                    OperationQty decimal(18,6) NULL, StartDateReal datetime NULL);
                """);
            return db;
        }

        internal Task ResetAsync() => ExecuteAsync("""
            DELETE dbo.TSubRootCard; DELETE dbo.TRootCard;
            INSERT dbo.TRootCard VALUES (41,0,0),(42,NULL,NULL);
            INSERT dbo.TSubRootCard VALUES (1,41,'10',1,NULL),(2,42,'10',1,'2026-10-08T09:00:00');
            """);

        internal async Task ExecuteAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        internal async Task<IReadOnlyList<KitaronPushWrite>> PlanAsync()
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT [auto],NUMBER,ActionNumber,OperationQty,StartDateReal FROM dbo.TSubRootCard ORDER BY [auto]";
            await using var reader = await command.ExecuteReaderAsync();
            var writes = new List<KitaronPushWrite>();
            while (await reader.ReadAsync())
            {
                var row = new KitaronOperationRow(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), false,
                    new Dictionary<string, object?> { ["OperationQty"] = reader.GetDecimal(3),
                        ["StartDateReal"] = reader.IsDBNull(4) ? null : reader.GetDateTime(4) });
                writes.Add(new(row.RowId, row.WorkOrderNumber, "OperationQty", 6d, row));
            }
            return writes;
        }

        internal async Task<decimal> QuantityAsync(int row)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT OperationQty FROM dbo.TSubRootCard WHERE [auto]=@row";
            command.Parameters.AddWithValue("@row", row);
            return Convert.ToDecimal(await command.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            await using var admin = new SqlConnection(master);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]";
            await drop.ExecuteNonQueryAsync();
        }
    }
}

internal sealed class LocalSqlFactAttribute : FactAttribute
{
    public LocalSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEIMAD_C07_LOCALDB")))
            Skip = "Requires an explicitly selected isolated LocalDB instance (MEIMAD_C07_LOCALDB).";
    }
}
