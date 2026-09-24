using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Domain.GCode;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteNcAnalysisRepository : INcAnalysisRepository
{
    private const string BackfillActor = "server";
    private const string BackfillReason = "nc_engine_analysis";
    private readonly SqliteDatabase database;

    public SqliteNcAnalysisRepository(SqliteDatabase database) => this.database = database;

    public async Task<IReadOnlyList<MachineNcInterpretation>> ListPostprocessorMachineInterpretationsAsync(
        string postprocessorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT machines.nc_dialect, machines.nc_viewer_machine
            FROM machines
            JOIN machine_supported_postprocessors compatibility
              ON compatibility.machine_id = machines.id
            WHERE compatibility.postprocessor_id = $postprocessorId
              AND machines.is_active = 1
            ORDER BY machines.id;
            """;
        command.Parameters.AddWithValue("$postprocessorId", postprocessorId);
        var values = new List<MachineNcInterpretation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0)) continue;
            values.Add(new MachineNcInterpretation(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }
        return values;
    }

    public async Task<IReadOnlyList<NcAnalysisCandidate>> ListReleasesWithoutAnalysisAsync(
        string parserVersion,
        int limit,
        IReadOnlyCollection<string> excludedReleaseIds,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Oldest first; excluded releases (failed this run) are skipped in memory so the query
        // stays simple. Over-fetch by the exclusion count to still fill the batch.
        command.CommandText = """
            SELECT release.id, release.postprocessor_id, release.stored_relative_path
            FROM gcode_releases release
            WHERE NOT EXISTS (
                SELECT 1 FROM gcode_release_analyses analysis
                WHERE analysis.gcode_release_id = release.id
                  AND analysis.parser_version = $parserVersion)
            ORDER BY release.released_at, release.id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$parserVersion", parserVersion);
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit) + excludedReleaseIds.Count);
        var values = new List<NcAnalysisCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken) && values.Count < limit)
        {
            var candidate = new NcAnalysisCandidate(reader.GetString(0), reader.GetString(1), reader.GetString(2));
            if (!excludedReleaseIds.Contains(candidate.GCodeReleaseId)) values.Add(candidate);
        }
        return values;
    }

    public async Task<bool> AddAnalysisAsync(
        NcAnalysisCandidate release,
        NcProgramAnalysis analysis,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = """
                SELECT
                    EXISTS (SELECT 1 FROM gcode_releases WHERE id = $releaseId),
                    EXISTS (SELECT 1 FROM gcode_release_analyses
                            WHERE gcode_release_id = $releaseId AND parser_version = $parserVersion);
                """;
            exists.Parameters.AddWithValue("$releaseId", release.GCodeReleaseId);
            exists.Parameters.AddWithValue("$parserVersion", analysis.ParserVersion);
            await using var reader = await exists.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetInt64(0) == 0 || reader.GetInt64(1) == 1)
            {
                return false;
            }
        }

        await SqliteNcCycleEstimateStore.InsertAnalysisAndEstimatesAsync(
            connection, transaction, release.GCodeReleaseId, release.PostprocessorId, analysis,
            BackfillActor, BackfillReason, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
