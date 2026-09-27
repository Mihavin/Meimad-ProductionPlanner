using System.Net;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Cases;

public sealed class CasePoolFilterApiTests
{
    [Fact]
    public async Task Pool_filters_combine_with_and()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.PoolFilter.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "pool.db")}"],
            webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var seed = connection.CreateCommand())
            {
                // A: released Work Order starting 2026-10-05, supply 2026-10-20, operation, verified material order.
                // B: pending Work Order starting 2026-12-01, supply 2027-01-10, no operations, active Order due 2026-10-10.
                // C: no Work Order, no Order.
                seed.CommandText = """
                    INSERT INTO cases (id, part_number, name, working_folder_path) VALUES
                        ('case-a', 'PN-A', 'A', 'C:\A'), ('case-b', 'PN-B', 'B', 'C:\B'), ('case-c', 'PN-C', 'C', 'C:\C');
                    INSERT INTO case_operations (id, case_id, operation_number, route_position, name) VALUES ('op-a', 'case-a', 10, 0, 'Mill');
                    INSERT INTO case_operations (id, case_id, operation_number, route_position, name, required_machine_type)
                    VALUES ('op-b', 'case-b', 10, 0, 'Note', 'Production Note');
                    INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity, release_state) VALUES
                        ('wo-a', 'case-a', '1001', 'waiting', 5, 'released'), ('wo-b', 'case-b', '1002', 'waiting', 5, 'pending');
                    INSERT INTO kitaron_sync_links (source_entity, source_key, target_id, owns_target, source_hash, first_seen_at, last_seen_at) VALUES
                        ('production_batch', 'wo:1001', 'wo-a', 1, 'h', '2026-09-26T00:00:00Z', '2026-09-26T00:00:00Z'),
                        ('production_batch', 'wo:1002', 'wo-b', 1, 'h', '2026-09-26T00:00:00Z', '2026-09-26T00:00:00Z');
                    INSERT INTO kitaron_work_orders (work_order_number, part_number, supply_date, start_date, imported_at) VALUES
                        (1001, 'PN-A', '2026-10-20', '2026-10-05', '2026-09-26T00:00:00Z'),
                        (1002, 'PN-B', '2027-01-10', '2026-12-01', '2026-09-26T00:00:00Z');
                    INSERT INTO orders (id, case_id, order_reference, quantity, work_finish_date, status)
                    VALUES ('order-b', 'case-b', 'SO-1', 5, '2026-10-10', 'active');
                    INSERT INTO kitaron_material_orders (source_key, purchase_order_number, line_number, material_number,
                        ordered_quantity, closed, active, source_hash, first_imported_at, last_imported_at, updated_at)
                    VALUES ('buy-1', '76504', '1', '7351', 10, 1, 1, 'h', '2026-09-26T00:00:00Z', '2026-09-26T00:00:00Z', '2026-09-26T00:00:00Z');
                    INSERT INTO work_order_material_orders (production_batch_id, material_order_source_key, verified_by, verified_at)
                    VALUES ('wo-a', 'buy-1', 'planner', '2026-09-26T00:00:00Z');
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            using var client = application.GetTestClient();
            async Task<string> Parts(string query)
            {
                using var response = await client.GetAsync("/api/v1/cases" + query);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return string.Join(",", document.RootElement.GetProperty("items").EnumerateArray()
                    .Select(item => item.GetProperty("partNumber").GetString()));
            }

            Assert.Equal("PN-A,PN-B,PN-C", await Parts(""));
            Assert.Equal("PN-A,PN-B", await Parts("?workOrders=with"));
            Assert.Equal("PN-C", await Parts("?workOrders=without"));
            Assert.Equal("PN-B", await Parts("?release=pending"));
            Assert.Equal("PN-A", await Parts("?release=released"));
            Assert.Equal("PN-B", await Parts("?orders=active"));
            // A Production Note alone is not an operation.
            Assert.Equal("PN-A", await Parts("?operations=with"));
            Assert.Equal("PN-A", await Parts("?materialOrders=verified"));
            // Supply date matches a Work Order supply date or an active Order delivery date.
            Assert.Equal("PN-A,PN-B", await Parts("?supplyFrom=2026-10-01&supplyTo=2026-10-31"));
            Assert.Equal("PN-A", await Parts("?startFrom=2026-10-01&startTo=2026-10-31"));
            // AND: pending AND starting in October matches nothing.
            Assert.Equal("", await Parts("?release=pending&startFrom=2026-10-01&startTo=2026-10-31"));

            using var invalid = await client.GetAsync("/api/v1/cases?release=later");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
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
}
