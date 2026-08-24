using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class OrphanRecordTests
{
    public static TheoryData<string> OrphanInserts => new()
    {
        {
            """
            INSERT INTO orders (
                id, case_id, order_reference, quantity, work_finish_date, status)
            VALUES ('orphan-order', 'missing-case', 'WO-X', 1, '2026-08-20', 'active');
            """
        },
        {
            """
            INSERT INTO machine_assignments (
                id, batch_operation_id, machine_id, backlog_position)
            VALUES ('orphan-assignment', 'missing-operation', 'missing-machine', 0);
            """
        },
        {
            """
            INSERT INTO downtimes (
                id, machine_id, starts_at, ends_at, reason, status)
            VALUES ('orphan-downtime', 'missing-machine', '2026-08-12T06:00:00Z',
                '2026-08-12T07:00:00Z', 'Maintenance', 'planned');
            """
        },
        {
            """
            INSERT INTO device_registry (
                id, device_type, device_name, machine_id)
            VALUES ('orphan-device', 'eink', 'Orphan tablet', 'missing-machine');
            """
        },
        {
            """
            INSERT INTO eink_package_revisions (
                id, batch_operation_id, revision, published_at)
            VALUES ('orphan-package', 'missing-operation', '1', '2026-08-11T00:00:00Z');
            """
        },
        {
            """
            INSERT INTO eink_package_files (
                id, package_revision_id, logical_path, storage_relative_path,
                media_type, byte_length, sha256, modified_at)
            VALUES (
                'orphan-package-file', 'missing-package', 'instructions.txt',
                'package/instructions.txt', 'text/plain', 0,
                '0000000000000000000000000000000000000000000000000000000000000000',
                '2026-08-11T00:00:00Z');
            """
        }
    };

    [Theory]
    [MemberData(nameof(OrphanInserts))]
    public async Task Foreign_key_orphans_are_rejected(string sql)
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var exception = await Assert.ThrowsAsync<SqliteException>(async () =>
            await command.ExecuteNonQueryAsync());

        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Contains(exception.SqliteExtendedErrorCode, new[] { 787, 1811 });
    }
}
