using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class AuxiliaryPinMigrationTests
{
    [Fact]
    public async Task Existing_start_only_pin_is_preserved_and_clear_stamp_survives_reopen()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        var migrator = new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(96);
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO cases(id,part_number,name,working_folder_path) VALUES('case','A','A','C:\A');
                INSERT INTO case_operations(id,case_id,operation_number,route_position,name) VALUES('co','case',10,0,'A');
                INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity) VALUES('batch','case','B','waiting',1);
                INSERT INTO batch_operations(id,production_batch_id,source_case_operation_id,operation_number,route_position,name,status)
                    VALUES('op','batch','co',10,0,'A','not_started');
                INSERT INTO skills(id,name,created_at,updated_at) VALUES('skill','Skill','2026-10-08T00:00:00Z','2026-10-08T00:00:00Z');
                INSERT INTO operation_resource_requirements(id,case_operation_id,sequence_position,resource_class,required_skill_id,
                    estimated_duration_seconds,created_at,updated_at)
                    VALUES('req','co',0,'EMPLOYEE','skill',60,'2026-10-08T00:00:00Z','2026-10-08T00:00:00Z');
                INSERT INTO resource_schedule_work(id,batch_operation_id,requirement_id,requested_starts_at,planned_duration_seconds,state,version,created_at,updated_at)
                    VALUES('work','op','req','2026-10-08T12:00:00Z',60,'PINNED',3,'2026-10-08T00:00:00Z','2026-10-08T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        {
            var pin = Assert.Single(await SqliteTimelineAuxiliaryPinRepository.ReadPinsAsync(connection, null, default));
            Assert.True(pin.IsActive);
            Assert.Equal(3, pin.Version);
            Assert.Equal(DateTimeOffset.Parse("2026-10-08T12:00:00Z"), pin.StartsAt);
        }
        var repository = new SqliteTimelineAuxiliaryPinRepository(fixture.Database);
        Assert.True(await repository.ClearAsync("op", "req", 3, new EditAuthority("client", 0, "planner"), default));
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        {
            var cleared = Assert.Single(await SqliteTimelineAuxiliaryPinRepository.ReadPinsAsync(connection, null, default));
            Assert.False(cleared.IsActive);
            Assert.Null(cleared.StartsAt);
            Assert.Equal(4, cleared.Version);
        }
    }
}
