using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>Rebuildable, transactionally maintained manual evidence, independent of the log UI's cap.</summary>
internal sealed class SchemaV98ManualTimingMigration : IDatabaseMigration
{
    public int Version => 98;
    public string Name => "durable_manual_timing";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE manual_timing_reports (
                source_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                source_event_id TEXT NOT NULL UNIQUE,
                batch_operation_id TEXT,
                machine_id TEXT,
                production_run_id TEXT,
                report_type TEXT,
                occurred_at TEXT NOT NULL,
                occurred_julian REAL,
                part_seconds REAL,
                warning_code TEXT,
                calculation_version INTEGER NOT NULL DEFAULT 1 CHECK(calculation_version = 1)
            );
            CREATE INDEX ix_manual_timing_context ON manual_timing_reports
                (batch_operation_id, machine_id, production_run_id, occurred_julian, source_sequence);
            CREATE VIEW manual_timing_source AS
            WITH payload AS (
                SELECT rowid AS source_sequence, id AS source_event_id, reason_code AS report_type, occurred_at,
                    julianday(occurred_at) AS occurred_julian,
                    CASE WHEN json_valid(related_entity_ids_json) THEN related_entity_ids_json ELSE '{}' END AS entities,
                    CASE WHEN json_valid(after_data_json) THEN after_data_json ELSE '{}' END AS data,
                    json_valid(related_entity_ids_json) AS valid_entities,
                    COALESCE(json_valid(after_data_json), 0) AS valid_data
                FROM structured_event_log WHERE event_type = 'manual_operation_reported'
            ), parsed AS (
                SELECT *, json_extract(entities, '$.batchOperationId') AS operation_id,
                    json_extract(entities, '$.machineId') AS machine_id,
                    json_extract(entities, '$.productionRunId') AS run_id,
                    CASE WHEN json_type(data, '$.partTimeSeconds') IN ('integer','real')
                        THEN json_extract(data, '$.partTimeSeconds') END AS seconds
                FROM payload
            )
            SELECT source_sequence, source_event_id, operation_id, machine_id, run_id, report_type,
                occurred_at, occurred_julian, seconds,
                CASE
                    WHEN valid_entities = 0 THEN 'malformed_context'
                    WHEN occurred_julian IS NULL THEN 'invalid_timestamp'
                    WHEN operation_id IS NULL OR NOT EXISTS(SELECT 1 FROM batch_operations WHERE id = operation_id)
                        THEN 'missing_operation'
                    WHEN machine_id IS NULL OR NOT EXISTS(SELECT 1 FROM machines WHERE id = parsed.machine_id)
                        THEN 'missing_machine'
                    WHEN run_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM production_runs WHERE id = run_id)
                        THEN 'missing_run'
                    WHEN report_type NOT IN ('setupStart','setupEnd','partTimeUpdate','productionEnd') OR report_type IS NULL
                        THEN 'unsupported_report_type'
                    WHEN report_type = 'partTimeUpdate' AND (valid_data = 0 OR seconds IS NULL OR seconds <= 0 OR seconds > 1.7976931348623157e308)
                        THEN 'invalid_part_duration'
                END AS warning_code, 1 AS calculation_version
            FROM parsed;

            CREATE TRIGGER project_manual_timing_after_insert AFTER INSERT ON structured_event_log
            WHEN NEW.event_type = 'manual_operation_reported'
            BEGIN
                INSERT INTO manual_timing_reports
                    (source_event_id,batch_operation_id,machine_id,production_run_id,report_type,
                     occurred_at,occurred_julian,part_seconds,warning_code,calculation_version)
                SELECT source_event_id,operation_id,machine_id,run_id,report_type,
                    occurred_at,occurred_julian,seconds,warning_code,calculation_version
                FROM manual_timing_source WHERE source_event_id = NEW.id;
            END;

            CREATE VIEW manual_setup_boundaries AS
            SELECT *,
                LAG(source_event_id) OVER session AS previous_event_id,
                LAG(report_type) OVER session AS previous_type,
                LAG(occurred_julian) OVER session AS previous_julian,
                LEAD(report_type) OVER session AS next_type,
                LEAD(occurred_julian) OVER session AS next_julian
            FROM manual_timing_reports
            WHERE warning_code IS NULL AND report_type IN ('setupStart','setupEnd','productionEnd')
            WINDOW session AS (PARTITION BY batch_operation_id, machine_id, production_run_id
                ORDER BY occurred_julian, source_sequence);

            CREATE VIEW manual_timing_samples AS
            SELECT source_event_id, NULL AS session_start_event_id, batch_operation_id, machine_id,
                production_run_id, 'cycle' AS kind, part_seconds AS seconds, occurred_at, source_sequence
            FROM manual_timing_reports WHERE warning_code IS NULL AND report_type = 'partTimeUpdate'
            UNION ALL
            SELECT source_event_id, previous_event_id, batch_operation_id, machine_id,
                production_run_id, 'setup', ROUND((occurred_julian - previous_julian) * 86400.0, 3), occurred_at, source_sequence
            FROM manual_setup_boundaries
            WHERE report_type = 'setupEnd' AND previous_type = 'setupStart' AND occurred_julian > previous_julian;

            CREATE VIEW manual_timing_warnings AS
            SELECT source_event_id, batch_operation_id, machine_id, warning_code FROM manual_timing_reports
            WHERE warning_code IS NOT NULL
            UNION ALL
            SELECT source_event_id, batch_operation_id, machine_id,
                CASE WHEN report_type = 'setupStart' THEN
                    CASE WHEN next_type IS NULL THEN 'open_setup_session' ELSE 'superseded_setup_start' END
                ELSE 'unmatched_setup_end' END
            FROM manual_setup_boundaries
            WHERE (report_type = 'setupStart' AND (next_type IS NULL OR next_type <> 'setupEnd' OR next_julian <= occurred_julian))
               OR (report_type = 'setupEnd' AND (previous_type IS NULL OR previous_type <> 'setupStart' OR occurred_julian <= previous_julian));

            -- The aggregate is a rebuildable view over durable normalized evidence. No asynchronous
            -- projector can lag behind the report transaction. Counts/checkpoints describe its sources.
            CREATE VIEW manual_timing_aggregate AS
            SELECT batch_operation_id, machine_id, production_run_id, kind, COUNT(*) AS sample_count,
                (SELECT MAX(report.source_sequence) FROM manual_timing_reports report
                    WHERE report.batch_operation_id = sample.batch_operation_id AND report.machine_id = sample.machine_id
                      AND report.production_run_id IS sample.production_run_id) AS source_checkpoint, 1 AS calculation_version,
                AVG(seconds) AS average_seconds, MAX(occurred_at) AS last_sample_at
            FROM manual_timing_samples sample GROUP BY batch_operation_id, machine_id, production_run_id, kind;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // All pages are inside the migration transaction; interruption rolls back schema and
        // checkpoint together. Retrying startup replays retained immutable source IDs exactly once.
        long checkpoint = 0;
        while (true)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$checkpoint", checkpoint);
            command.CommandText = """
                INSERT INTO manual_timing_reports
                SELECT * FROM manual_timing_source WHERE source_sequence > $checkpoint
                ORDER BY source_sequence LIMIT 500;
                """;
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) break;
            command.CommandText = "SELECT MAX(source_sequence) FROM manual_timing_reports;";
            checkpoint = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
    }
}
