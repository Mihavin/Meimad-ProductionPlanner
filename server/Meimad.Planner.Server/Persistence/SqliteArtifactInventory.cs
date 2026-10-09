using Meimad.Planner.Server.Application.Maintenance;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteArtifactInventory(SqliteDatabase database)
{
    internal async Task<IReadOnlyList<StoredArtifact>> ReadAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 'production',p.id,p.manifest_relative_path,NULL,p.manifest_hash,
                CASE WHEN EXISTS(SELECT 1 FROM production_package_context_current c WHERE c.production_package_id=p.id)
                       OR EXISTS(SELECT 1 FROM production_package_current c WHERE c.production_package_id=p.id)
                     THEN 'ACTIVE' ELSE 'SUPERSEDED' END
            FROM production_packages p
            UNION ALL SELECT 'production',a.production_package_id,a.stored_relative_path,a.file_size,a.file_hash,
                CASE WHEN EXISTS(SELECT 1 FROM production_package_context_current c WHERE c.production_package_id=a.production_package_id)
                       OR EXISTS(SELECT 1 FROM production_package_current c WHERE c.production_package_id=a.production_package_id)
                     THEN 'ACTIVE' ELSE 'SUPERSEDED' END
            FROM production_package_artifacts a
            UNION ALL SELECT 'gcode',id,stored_relative_path,file_size,file_hash,'REFERENCED' FROM gcode_releases
            UNION ALL SELECT 'gcode',id,stored_relative_path,file_size,file_hash,'REFERENCED' FROM tool_table_releases
            UNION ALL SELECT 'gcode',gcode_release_id,stored_relative_path,file_size,file_hash,'REFERENCED' FROM gcode_release_subprograms
            UNION ALL SELECT 'eink',package_revision_id,storage_relative_path,byte_length,sha256,'REFERENCED' FROM eink_package_files
            ORDER BY 1,2,3;
            """;
        var result = new List<StoredArtifact>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetString(4), reader.GetString(5)));
        await transaction.CommitAsync(token);
        return result;
    }
}
