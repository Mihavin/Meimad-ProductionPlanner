using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV38NcCycleEstimatesMigration : IDatabaseMigration
{
    public int Version => 38;

    public string Name => "nc_cycle_estimates";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE gcode_release_analyses (
                gcode_release_id TEXT NOT NULL,
                parser_version TEXT NOT NULL CHECK (length(trim(parser_version)) > 0),
                status TEXT NOT NULL CHECK (status IN ('COMPLETE', 'PARTIAL', 'UNAVAILABLE')),
                raw_feed_seconds REAL NOT NULL CHECK (raw_feed_seconds >= 0),
                rapid_distance_mm REAL NOT NULL CHECK (rapid_distance_mm >= 0),
                tool_change_count INTEGER NOT NULL CHECK (tool_change_count >= 0),
                dwell_seconds REAL NOT NULL CHECK (dwell_seconds >= 0),
                detected_units TEXT CHECK (detected_units IS NULL OR detected_units IN ('MILLIMETER', 'INCH')),
                warnings_json TEXT NOT NULL CHECK (json_valid(warnings_json)),
                unsupported_constructs_json TEXT NOT NULL CHECK (json_valid(unsupported_constructs_json)),
                confidence TEXT NOT NULL CHECK (confidence IN ('HIGH', 'MEDIUM', 'LOW', 'UNAVAILABLE')),
                analyzed_at TEXT NOT NULL,
                PRIMARY KEY (gcode_release_id, parser_version),
                FOREIGN KEY (gcode_release_id) REFERENCES gcode_releases (id) ON DELETE RESTRICT
            );

            CREATE TABLE gcode_machine_cycle_estimates (
                id TEXT PRIMARY KEY,
                gcode_release_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                parser_version TEXT NOT NULL,
                raw_feed_seconds REAL NOT NULL CHECK (raw_feed_seconds >= 0),
                rapid_distance_mm REAL NOT NULL CHECK (rapid_distance_mm >= 0),
                rapid_seconds REAL CHECK (rapid_seconds IS NULL OR rapid_seconds >= 0),
                tool_change_count INTEGER NOT NULL CHECK (tool_change_count >= 0),
                tool_change_seconds REAL CHECK (tool_change_seconds IS NULL OR tool_change_seconds >= 0),
                dwell_seconds REAL NOT NULL CHECK (dwell_seconds >= 0),
                machine_rapid_rate_mm_per_min REAL CHECK (machine_rapid_rate_mm_per_min IS NULL OR machine_rapid_rate_mm_per_min > 0),
                machine_tool_change_time_seconds REAL CHECK (machine_tool_change_time_seconds IS NULL OR machine_tool_change_time_seconds >= 0),
                machine_time_factor REAL NOT NULL CHECK (machine_time_factor > 0),
                raw_cycle_seconds REAL CHECK (raw_cycle_seconds IS NULL OR raw_cycle_seconds >= 0),
                estimated_cycle_seconds REAL CHECK (estimated_cycle_seconds IS NULL OR estimated_cycle_seconds >= 0),
                warnings_json TEXT NOT NULL CHECK (json_valid(warnings_json)),
                confidence TEXT NOT NULL CHECK (confidence IN ('HIGH', 'MEDIUM', 'LOW', 'UNAVAILABLE')),
                calculated_at TEXT NOT NULL,
                FOREIGN KEY (gcode_release_id, parser_version)
                    REFERENCES gcode_release_analyses (gcode_release_id, parser_version) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines (id) ON DELETE CASCADE
            );

            CREATE INDEX ix_gcode_machine_cycle_estimates_machine
            ON gcode_machine_cycle_estimates (
                machine_id, gcode_release_id, parser_version, calculated_at DESC, id DESC);

            CREATE VIEW effective_batch_operation_nc_estimates AS
            WITH current_candidates AS (
                SELECT assignment.batch_operation_id,
                       assignment.machine_id,
                       assignment.selected_gcode_release_id,
                       release.id AS gcode_release_id,
                       COUNT(*) OVER (
                           PARTITION BY assignment.batch_operation_id) AS candidate_count,
                       MAX(CASE WHEN release.id = assignment.selected_gcode_release_id
                                THEN release.id END) OVER (
                           PARTITION BY assignment.batch_operation_id) AS selected_current_release_id
                FROM machine_assignments assignment
                JOIN batch_operations operation
                  ON operation.id = assignment.batch_operation_id
                JOIN process_revisions process
                  ON process.case_operation_id = operation.source_case_operation_id
                 AND process.is_active = 1
                JOIN gcode_releases release
                  ON release.process_revision_id = process.id
                JOIN machine_supported_postprocessors compatibility
                  ON compatibility.machine_id = assignment.machine_id
                 AND compatibility.postprocessor_id = release.postprocessor_id
                WHERE NOT EXISTS (
                    SELECT 1 FROM gcode_releases newer
                    WHERE newer.process_revision_id = release.process_revision_id
                      AND newer.postprocessor_id = release.postprocessor_id
                      AND newer.post_specific_revision > release.post_specific_revision)
            ), chosen AS (
                SELECT DISTINCT batch_operation_id, machine_id,
                       CASE WHEN selected_gcode_release_id IS NOT NULL
                            THEN selected_current_release_id
                            WHEN candidate_count = 1 THEN gcode_release_id
                            ELSE NULL END AS gcode_release_id
                FROM current_candidates
            )
            SELECT chosen.batch_operation_id,
                   chosen.machine_id,
                   chosen.gcode_release_id,
                   estimate.parser_version,
                   estimate.estimated_cycle_seconds,
                   estimate.confidence,
                   estimate.warnings_json,
                   estimate.calculated_at
            FROM chosen
            JOIN gcode_machine_cycle_estimates estimate
              ON estimate.gcode_release_id = chosen.gcode_release_id
             AND estimate.machine_id = chosen.machine_id
            JOIN gcode_release_analyses analysis
              ON analysis.gcode_release_id = estimate.gcode_release_id
             AND analysis.parser_version = estimate.parser_version
            WHERE NOT EXISTS (
                SELECT 1 FROM gcode_release_analyses newer_analysis
                WHERE newer_analysis.gcode_release_id = analysis.gcode_release_id
                  AND julianday(newer_analysis.analyzed_at) > julianday(analysis.analyzed_at))
              AND NOT EXISTS (
                SELECT 1 FROM gcode_machine_cycle_estimates newer_estimate
                WHERE newer_estimate.gcode_release_id = estimate.gcode_release_id
                  AND newer_estimate.machine_id = estimate.machine_id
                  AND newer_estimate.parser_version = estimate.parser_version
                  AND (julianday(newer_estimate.calculated_at) > julianday(estimate.calculated_at)
                       OR (newer_estimate.calculated_at = estimate.calculated_at
                           AND newer_estimate.id > estimate.id)));

            CREATE TRIGGER gcode_release_analyses_immutable_update
            BEFORE UPDATE ON gcode_release_analyses
            BEGIN
                SELECT RAISE(ABORT, 'released NC analysis is immutable');
            END;

            CREATE TRIGGER gcode_release_analyses_immutable_delete
            BEFORE DELETE ON gcode_release_analyses
            BEGIN
                SELECT RAISE(ABORT, 'released NC analysis is immutable');
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
