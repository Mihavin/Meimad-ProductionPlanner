using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.MachineAssignments;
using Meimad.Planner.Server.Application.Machines;
using Meimad.Planner.Server.Domain.Machines;
using Meimad.Planner.Server.Persistence;

namespace Meimad.Planner.Server.Tests.Machines;

public sealed class MachineAssignmentPersistenceTests
{
    [Fact]
    public async Task Assign_move_within_move_between_and_unassign_preserve_stable_order()
    {
        await using var fixture = await Persistence.TemporaryDatabase.CreateAsync();
        await SeedCalendarAndOperationsAsync(fixture.Database);
        var authority = await GrantEditModeAsync(fixture.Database);
        var machineService = CreateMachineService(fixture.Database);
        var first = await CreateMachineAsync(
            machineService,
            authority,
            "M-1",
            "milling",
            ["mill"]);
        var second = await CreateMachineAsync(
            machineService,
            authority,
            "M-2",
            "machining",
            ["mill", "laser"]);
        var assignments = CreateAssignmentService(fixture.Database);

        await assignments.AssignOrMoveAsync("op-a", first.MachineId, 0, authority);
        var createdB = await assignments.AssignOrMoveAsync("op-b", first.MachineId, 1, authority);
        await assignments.AssignOrMoveAsync("op-c", first.MachineId, 1, authority);
        await AssertBacklogAsync(assignments, first.MachineId, "op-a", "op-c", "op-b");

        var movedWithin = await assignments.AssignOrMoveAsync(
            "op-b",
            first.MachineId,
            0,
            authority);
        Assert.False(movedWithin.WasCreated);
        Assert.Equal(
            createdB.Assignment.MachineAssignmentId,
            movedWithin.Assignment.MachineAssignmentId);
        Assert.True(movedWithin.Assignment.Version > createdB.Assignment.Version);
        await AssertBacklogAsync(assignments, first.MachineId, "op-b", "op-a", "op-c");

        await assignments.AssignOrMoveAsync("op-a", second.MachineId, 0, authority);
        await AssertBacklogAsync(assignments, first.MachineId, "op-b", "op-c");
        await AssertBacklogAsync(assignments, second.MachineId, "op-a");

        await assignments.AssignOrMoveAsync("op-c", second.MachineId, 1, authority);
        await AssertBacklogAsync(assignments, first.MachineId, "op-b");
        await AssertBacklogAsync(assignments, second.MachineId, "op-a", "op-c");

        Assert.True(await assignments.UnassignAsync("op-b", authority));
        Assert.False(await assignments.UnassignAsync("op-b", authority));
        await AssertBacklogAsync(assignments, first.MachineId);
    }

    [Fact]
    public async Task Incompatible_inactive_and_out_of_range_commands_are_atomic()
    {
        await using var fixture = await Persistence.TemporaryDatabase.CreateAsync();
        await SeedCalendarAndOperationsAsync(fixture.Database);
        var authority = await GrantEditModeAsync(fixture.Database);
        var machineService = CreateMachineService(fixture.Database);
        var mill = await CreateMachineAsync(machineService, authority, "M-MILL", "mill", []);
        var inactive = await CreateMachineAsync(
            machineService,
            authority,
            "M-INACTIVE",
            "laser",
            ["laser"],
            isActive: false);
        var assignments = CreateAssignmentService(fixture.Database);
        await assignments.AssignOrMoveAsync("op-a", mill.MachineId, 0, authority);

        await Assert.ThrowsAsync<IncompatibleMachineException>(() =>
            assignments.AssignOrMoveAsync("op-laser", mill.MachineId, 1, authority));
        await Assert.ThrowsAsync<IncompatibleMachineException>(() =>
            assignments.AssignOrMoveAsync("op-laser", inactive.MachineId, 0, authority));
        await Assert.ThrowsAsync<BacklogPositionOutOfRangeException>(() =>
            assignments.AssignOrMoveAsync("op-b", mill.MachineId, 2, authority));

        await AssertBacklogAsync(assignments, mill.MachineId, "op-a");
        await AssertBacklogAsync(assignments, inactive.MachineId);
    }

    [Fact]
    public async Task Machine_master_validates_calendar_device_projection_and_assigned_compatibility()
    {
        await using var fixture = await Persistence.TemporaryDatabase.CreateAsync();
        await SeedCalendarAndOperationsAsync(fixture.Database);
        var authority = await GrantEditModeAsync(fixture.Database);
        var machines = CreateMachineService(fixture.Database);
        var machine = await CreateMachineAsync(
            machines,
            authority,
            " M-DEVICE ",
            "mill",
            ["probe"]);
        await SeedDeviceAsync(fixture.Database, machine.MachineId);

        var reopened = await machines.GetByIdAsync(machine.MachineId);
        Assert.NotNull(reopened);
        Assert.Equal("M-DEVICE", reopened.Number);
        Assert.Equal("device-1", reopened.DisplayDeviceId);
        Assert.True(reopened.DisplayEnabled);

        await Assert.ThrowsAsync<WorkingCalendarNotFoundException>(() =>
            machines.CreateAsync(
                new CreateMachineCommand(
                    "M-BAD-CALENDAR",
                    "Bad calendar",
                    "mill",
                    null,
                    [],
                    "missing-calendar",
                    true,
                    false),
                authority));

        var assignments = CreateAssignmentService(fixture.Database);
        await assignments.AssignOrMoveAsync("op-a", machine.MachineId, 0, authority);
        await Assert.ThrowsAsync<MachineBacklogCompatibilityException>(() =>
            machines.UpdateAsync(
                machine.MachineId,
                machine.Version,
                new UpdateMachineCommand(
                    MachineField<string?>.Unspecified,
                    MachineField<string?>.Unspecified,
                    MachineField<string?>.Unspecified,
                    MachineField<string?>.Unspecified,
                    MachineField<IReadOnlyList<string?>?>.Unspecified,
                    MachineField<string?>.Unspecified,
                    MachineField<bool?>.Specified(false),
                    MachineField<bool?>.Unspecified),
                authority));
    }

    private static MachineService CreateMachineService(SqliteDatabase database) =>
        new(new SqliteMachineRepository(database), TimeProvider.System);

    private static MachineAssignmentService CreateAssignmentService(SqliteDatabase database) =>
        new(new SqliteMachineAssignmentRepository(database), TimeProvider.System);

    private static async Task<Machine> CreateMachineAsync(
        MachineService service,
        EditAuthority authority,
        string number,
        string processType,
        IReadOnlyList<string?> capabilities,
        bool isActive = true) => await service.CreateAsync(
        new CreateMachineCommand(
            number,
            $"Machine {number}",
            processType,
            null,
            capabilities,
            "calendar-1",
            isActive,
            true),
        authority);

    private static async Task AssertBacklogAsync(
        MachineAssignmentService service,
        string machineId,
        params string[] expectedOperationIds)
    {
        var backlog = await service.GetBacklogAsync(machineId);
        Assert.Equal(expectedOperationIds, backlog.Select(item => item.Assignment.BatchOperationId));
        Assert.Equal(
            Enumerable.Range(0, expectedOperationIds.Length),
            backlog.Select(item => item.Assignment.BacklogPosition));
    }

    private static async Task SeedCalendarAndOperationsAsync(SqliteDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id)
            VALUES ('calendar-1', 'Factory', 'Asia/Jerusalem');

            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-1', 'PN-M', 'Machine Test', 'C:\Cases\PN-M');

            INSERT INTO production_batches (
                id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-1', 'case-1', 'B-M', 'waiting', 1);

            INSERT INTO case_operations (
                id, case_id, operation_number, route_position, name, required_machine_type)
            VALUES
                ('case-op-a', 'case-1', 10, 0, 'A', 'mill'),
                ('case-op-b', 'case-1', 20, 1, 'B', 'mill'),
                ('case-op-c', 'case-1', 30, 2, 'C', 'mill'),
                ('case-op-laser', 'case-1', 40, 3, 'Laser', 'laser');

            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, required_machine_type, status)
            VALUES
                ('op-a', 'batch-1', 'case-op-a', 10, 0, 'A', 'mill', 'not_started'),
                ('op-b', 'batch-1', 'case-op-b', 20, 1, 'B', 'mill', 'not_started'),
                ('op-c', 'batch-1', 'case-op-c', 30, 2, 'C', 'mill', 'not_started'),
                ('op-laser', 'batch-1', 'case-op-laser', 40, 3, 'Laser', 'laser', 'not_started');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedDeviceAsync(SqliteDatabase database, string machineId)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device_registry (
                id, device_type, device_name, machine_id, access_mode, is_enabled)
            VALUES ('device-1', 'eink', 'Tablet 1', $machineId, 'read_only', 1);
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<EditAuthority> GrantEditModeAsync(SqliteDatabase database)
    {
        var authority = new EditAuthority("machine-test-client", 1);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = $clientId,
                holder_user_id = 'machine-test-user',
                generation = $generation,
                acquired_at = '2026-08-11T00:00:00Z',
                version = version + 1,
                updated_at = '2026-08-11T00:00:00Z'
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$clientId", authority.ClientId);
        command.Parameters.AddWithValue("$generation", authority.Generation);
        await command.ExecuteNonQueryAsync();
        return authority;
    }
}
