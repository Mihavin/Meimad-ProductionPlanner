using System.Net;
using System.Text.Json;
using Meimad.Planner.Server.Api.Kitaron;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Kitaron;

public sealed class KitaronMaterialOrderApiTests
{
    private static readonly DateOnly Today = new(2026, 9, 26);

    [Theory]
    [InlineData(10, 10.0, null, null, true, "received")]
    [InlineData(10, 4.0, null, null, true, "closed")]
    [InlineData(10, 10.0, null, null, false, "received")]
    [InlineData(10, 4.0, "2026-10-01", null, false, "partially_received")]
    [InlineData(10, null, "2026-09-01", null, false, "late")]
    [InlineData(10, 0.0, "2026-09-01", "2026-10-05", false, "supplier_confirmed")]
    [InlineData(10, 0.0, "2026-10-10", "2026-09-20", false, "late")]
    [InlineData(10, null, "2026-10-10", null, false, "open")]
    public void Delivery_status_follows_the_kitaron_facts(
        double ordered, double? received, string? requested, string? approved, bool closed, string expected)
    {
        Assert.Equal(expected, KitaronMaterialOrderEndpoints.DeliveryStatus(
            ordered, received, Parse(requested), Parse(approved), closed, Today));
    }

    [Fact]
    public async Task Every_client_reads_all_active_material_order_lines_with_their_kitaron_status()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.MaterialOrders.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "material.db")}"],
            webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO kitaron_material_orders (source_key, purchase_order_number, line_number, material_number,
                        description, supplier, ordered_quantity, received_quantity, unit, requested_delivery_date,
                        approved_delivery_date, status, closed, active, source_hash, first_imported_at, last_imported_at, updated_at)
                    VALUES
                        ('1', '76423', '1', 'AL-6061', 'Bar 50', 'Metals Ltd', 12, 0, 'm', '2099-01-10', '2099-01-12', '(חתימה)', 0, 1, 'h', '2026-09-26T00:00:00Z', '2026-09-26T00:00:00Z', '2026-09-26T00:00:00Z'),
                        ('2', '70378', '2', 'SS-304', NULL, NULL, 5, 5, NULL, '2026-01-01', NULL, NULL, 1, 1, 'h', '2026-09-26T00:00:00Z', '2026-09-26T00:00:00Z', '2026-09-26T00:00:00Z'),
                        ('3', '1', '1', 'OLD', NULL, NULL, 5, 0, NULL, NULL, NULL, NULL, 0, 0, 'h', '2026-09-26T00:00:00Z', '2026-09-26T00:00:00Z', '2026-09-26T00:00:00Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var client = application.GetTestClient();
            using var response = await client.GetAsync("/api/v1/kitaron/material-orders");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            Assert.Equal("76423", items[0].GetProperty("purchaseOrderNumber").GetString());
            Assert.Equal("supplier_confirmed", items[0].GetProperty("deliveryStatus").GetString());
            Assert.Equal("(חתימה)", items[0].GetProperty("kitaronStatus").GetString());
            Assert.Equal("2099-01-12", items[0].GetProperty("approvedDeliveryDate").GetString());
            Assert.Equal("received", items[1].GetProperty("deliveryStatus").GetString());
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            for (var attempt = 0; Directory.Exists(directory); attempt++)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch when (attempt < 5) { await Task.Delay(100); }
            }
        }
    }

    private static DateOnly? Parse(string? value) => value is null ? null : DateOnly.Parse(value);
}
