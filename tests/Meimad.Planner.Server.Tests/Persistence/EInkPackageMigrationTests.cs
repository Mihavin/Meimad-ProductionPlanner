using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class EInkPackageMigrationTests
{
    [Fact]
    public async Task Published_package_metadata_is_path_only_and_immutable()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO cases (id, part_number, name, working_folder_path)
                VALUES ('case-package', 'PN-PKG', 'Package Part', 'C:\Cases\PN-PKG');
                INSERT INTO production_batches (
                    id, case_id, batch_number, status, planned_quantity)
                VALUES ('batch-package', 'case-package', 'B-PKG', 'planned', 1);
                INSERT INTO case_operations (
                    id, case_id, operation_number, route_position, name)
                VALUES ('case-operation-package', 'case-package', 10, 0, 'Mill');
                INSERT INTO batch_operations (
                    id, production_batch_id, source_case_operation_id,
                    operation_number, route_position, name, status)
                VALUES (
                    'operation-package', 'batch-package', 'case-operation-package',
                    10, 0, 'Mill', 'not_started');
                INSERT INTO eink_package_revisions (
                    id, batch_operation_id, revision, published_at)
                VALUES ('package-1', 'operation-package', 'R1', '2026-08-11T10:00:00Z');
                INSERT INTO eink_package_files (
                    id, package_revision_id, logical_path, storage_relative_path,
                    media_type, byte_length, sha256, modified_at)
                VALUES (
                    'package-file-1', 'package-1', 'instructions.txt',
                    'package-1/instructions.txt', 'text/plain', 0,
                    '0000000000000000000000000000000000000000000000000000000000000000',
                    '2026-08-11T10:00:00Z');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info('eink_package_files');";
            var types = new List<string>();
            var names = new List<string>();
            await using var reader = await columns.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(1));
                types.Add(reader.GetString(2));
            }

            Assert.Contains("storage_relative_path", names);
            Assert.Contains("asset_type", names);
            Assert.DoesNotContain(types, type => type.Equals("BLOB", StringComparison.OrdinalIgnoreCase));
        }

        await using (var revisionColumns = connection.CreateCommand())
        {
            revisionColumns.CommandText = "PRAGMA table_info('eink_package_revisions');";
            var names = new List<string>();
            await using var reader = await revisionColumns.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(1));
            }

            Assert.Contains("machine_id", names);
            Assert.Contains("part_number", names);
            Assert.Contains("production_batch_id", names);
            Assert.Contains("operation_number", names);
        }

        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE eink_package_files SET logical_path = 'changed.txt' WHERE id = 'package-file-1';";
        var updateError = await Assert.ThrowsAsync<SqliteException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal(19, updateError.SqliteErrorCode);

        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM eink_package_revisions WHERE id = 'package-1';";
        var deleteError = await Assert.ThrowsAsync<SqliteException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal(19, deleteError.SqliteErrorCode);
    }
}
