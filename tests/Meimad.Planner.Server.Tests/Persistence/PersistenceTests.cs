namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class PersistenceTests
{
    [Fact]
    public async Task Complete_planning_graph_persists_across_connections()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();

        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO working_calendars (id, name, time_zone_id)
                VALUES ('calendar-1', 'Day shift', 'Asia/Jerusalem');

                INSERT INTO machines (
                    id, number, name, machine_type, working_calendar_id, status)
                VALUES ('machine-1', 'M-01', 'Mill 1', 'mill', 'calendar-1', 'available');

                INSERT INTO cases (
                    id, part_number, revision, name, working_folder_path,
                    current_setup_seconds, current_cycle_seconds)
                VALUES ('case-1', 'PN-100', 'A', 'Test part', 'C:\Engineering\PN-100', 600, 45);

                INSERT INTO orders (
                    id, case_id, order_reference, quantity, work_finish_date, status)
                VALUES ('order-1', 'case-1', 'WO-100', 10, '2026-08-20', 'active');

                INSERT INTO case_operations (
                    id, case_id, operation_number, route_position, name,
                    required_machine_type, setup_seconds, cycle_seconds, dependency_type)
                VALUES ('case-operation-1', 'case-1', 10, 0, 'First milling',
                    'mill', 600, 45, 'independent');

                INSERT INTO production_batches (
                    id, case_id, batch_number, status, planned_quantity, route_revision)
                VALUES ('batch-1', 'case-1', 'B-100', 'planned', 11, 1);

                INSERT INTO batch_allocations (
                    id, production_batch_id, allocation_type, order_id, quantity)
                VALUES ('allocation-order-1', 'batch-1', 'order', 'order-1', 10);

                INSERT INTO batch_allocations (
                    id, production_batch_id, allocation_type, quantity)
                VALUES ('allocation-scrap-1', 'batch-1', 'scrap_allowance', 1);

                INSERT INTO batch_operations (
                    id, production_batch_id, source_case_operation_id, operation_number,
                    route_position, name, required_machine_type, setup_seconds,
                    cycle_seconds, status)
                VALUES ('batch-operation-1', 'batch-1', 'case-operation-1', 10,
                    0, 'First milling', 'mill', 600, 45, 'planned');

                INSERT INTO machine_assignments (
                    id, batch_operation_id, machine_id, backlog_position)
                VALUES ('assignment-1', 'batch-operation-1', 'machine-1', 0);

                INSERT INTO downtimes (
                    id, machine_id, starts_at, ends_at, reason, status)
                VALUES ('downtime-1', 'machine-1', '2026-08-12T06:00:00Z',
                    '2026-08-12T07:00:00Z', 'Maintenance', 'planned');

                UPDATE edit_tokens
                SET holder_client_id = 'client-1',
                    holder_user_id = 'planner-1',
                    acquired_at = '2026-08-11T08:00:00Z',
                    generation = 1,
                    version = 2,
                    updated_at = '2026-08-11T08:00:00Z'
                WHERE id = 1;

                INSERT INTO application_settings (key, value)
                VALUES ('factory.name', 'Meimad');

                INSERT INTO device_registry (
                    id, device_type, device_name, machine_id, credential_hash)
                VALUES ('device-1', 'eink', 'Machine 1 tablet', 'machine-1', 'test-hash');
                """;
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        await using var reopenedConnection = await fixture.Database.OpenConnectionAsync();
        Assert.Equal("PN-100", await ReadScalarAsync(
            reopenedConnection,
            "SELECT part_number FROM cases WHERE id = 'case-1';"));
        Assert.Equal("machine-1", await ReadScalarAsync(
            reopenedConnection,
            "SELECT machine_id FROM machine_assignments WHERE id = 'assignment-1';"));
        Assert.Equal("client-1", await ReadScalarAsync(
            reopenedConnection,
            "SELECT holder_client_id FROM edit_tokens WHERE id = 1;"));
        Assert.Equal("read_only", await ReadScalarAsync(
            reopenedConnection,
            "SELECT access_mode FROM device_registry WHERE id = 'device-1';"));

        var createdAt = await ReadScalarAsync(
            reopenedConnection,
            "SELECT created_at FROM production_batches WHERE id = 'batch-1';");
        var updatedAt = await ReadScalarAsync(
            reopenedConnection,
            "SELECT updated_at FROM production_batches WHERE id = 'batch-1';");
        Assert.False(string.IsNullOrWhiteSpace(createdAt));
        Assert.False(string.IsNullOrWhiteSpace(updatedAt));
    }

    private static async Task<string?> ReadScalarAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string?)await command.ExecuteScalarAsync();
    }
}
