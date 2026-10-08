using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Domain.Cnc;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// The workflow statuses a planner may report: all of them on a Machine without DPRNT output, and
/// the setup statuses (everything but <see cref="InProduction"/>) on a DPRNT Machine whose package
/// for the operation has no Server verification (owner decision 2026-10-07).
/// </summary>
internal static class ManualWorkflowStatuses
{
    internal const string ReadyForSetup = "READY_FOR_SETUP";
    internal const string InSetupRun = "IN_SETUP_RUN";
    internal const string InQc = "IN_QC";
    internal const string ReadyForProduction = "READY_FOR_PRODUCTION";
    internal const string InProduction = "IN_PRODUCTION";

    /// <summary>The source of every workflow event a planner reports by hand.</summary>
    internal const string Source = "PLANNER_MANUAL";

    internal static readonly IReadOnlyList<string> All = [ReadyForSetup, InSetupRun, InQc, ReadyForProduction, InProduction];

    /// <summary>The workflow event that telemetry would have produced for a status.</summary>
    internal static string EventType(string status) => status switch
    {
        ReadyForSetup => "MANUAL_READY_FOR_SETUP",
        InSetupRun => "MANUAL_SETUP_RUN",
        InQc => "SEND_TO_QC",
        ReadyForProduction => "QC_PASS",
        InProduction => "PRODUCTION_SESSION_OPENED",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    /// <summary>The workflow status of a Production Run after its latest event (as the tablet shows it).</summary>
    internal static string Project(string? latestEventType) => latestEventType switch
    {
        null or "SETUP_RESTARTED" or "MANUAL_READY_FOR_SETUP" => ReadyForSetup,
        "OFFSET_LOADER_COMPLETED" or "SETUP_VERIFICATION_REQUESTED" or "SETUP_VERIFICATION_FAILED" => "IN_SETUP",
        "SETUP_VERIFICATION_SUCCEEDED" or "QC_FAIL" or "MANUAL_SETUP_RUN" => InSetupRun,
        "SEND_TO_QC" => InQc,
        "QC_PASS" => ReadyForProduction,
        "CYCLE_START" or "CYCLE_END" or "CYCLE_INTERRUPTED" or "PRODUCTION_SESSION_OPENED" => InProduction,
        _ => "UNKNOWN"
    };
}

/// <summary>Who reports an operation's workflow statuses.</summary>
internal enum WorkflowReportingMode
{
    /// <summary>The planner reports every status and the machined parts (no DPRNT output).</summary>
    Manual,

    /// <summary>
    /// The planner reports setup, QC and QC pass; the Machine's DPRNT reports production and counts
    /// parts. A DPRNT Machine whose package has no Server verification has no Offset Loader event
    /// that could start the setup (owner decision 2026-10-07).
    /// </summary>
    SetupByHand,

    /// <summary>The Machine reports everything through DPRNT and its package's verification.</summary>
    Machine
}

/// <summary>
/// Which Machines report their own workflow: a CNC Machine whose enabled connection reads a DPRNT
/// source. Every other Machine (manual, no connection, or DPRNT output switched off) gets its
/// workflow statuses reported by hand on the Planning Board (owner decision 2026-09-29). On a DPRNT
/// Machine the setup statuses are still reported by hand unless the operation's current package has
/// Server verification, whose Offset Loader reports the setup (owner decision 2026-10-07).
/// </summary>
internal static class SqliteMachineWorkflowReporting
{
    internal static WorkflowReportingMode Mode(
        string machineId, string batchOperationId, IReadOnlySet<string> machinesWithDprnt,
        IReadOnlySet<(string BatchOperationId, string MachineId)> verifiedPackages) =>
        !machinesWithDprnt.Contains(machineId) ? WorkflowReportingMode.Manual
        : verifiedPackages.Contains((batchOperationId, machineId)) ? WorkflowReportingMode.Machine
        : WorkflowReportingMode.SetupByHand;

    /// <summary>The operations whose current Production Package (on its Machine) has Server verification.</summary>
    internal static async Task<IReadOnlySet<(string BatchOperationId, string MachineId)>> ReadVerifiedPackagesAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken token)
    {
        var operations = new HashSet<(string, string)>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT package.batch_operation_id, package.machine_id
            FROM production_packages package
            JOIN machine_assignments assignment ON assignment.id=package.machine_assignment_id
                AND assignment.production_run_id=package.production_run_id AND assignment.machine_id=package.machine_id
                AND assignment.released_at IS NULL
            JOIN production_runs run ON run.id=assignment.production_run_id
                AND run.status IN ('DRAFT','PLANNED','IN_PROGRESS','SUSPENDED')
            WHERE package.verification_enabled = 1 AND (
                EXISTS (
                    SELECT 1 FROM production_package_context_current current
                    JOIN production_run_outputs output ON output.id=current.production_run_output_id
                        AND output.production_run_program_id=current.production_run_program_id
                        AND output.batch_operation_id=package.batch_operation_id
                        AND output.status IN ('ALLOCATED','IN_PRODUCTION')
                    JOIN production_run_programs program ON program.id=output.production_run_program_id
                        AND program.production_run_id=run.id AND program.status IN ('PLANNED','ACTIVE','SUSPENDED')
                    WHERE current.production_package_id=package.id AND current.machine_assignment_id=assignment.id)
                OR (EXISTS (SELECT 1 FROM production_package_current legacy WHERE legacy.production_package_id=package.id)
                    AND NOT EXISTS (SELECT 1 FROM production_package_context_current current WHERE current.machine_assignment_id=assignment.id)
                    AND (SELECT COUNT(*) FROM production_run_outputs output
                        JOIN production_run_programs program ON program.id=output.production_run_program_id
                        WHERE program.production_run_id=run.id AND output.batch_operation_id=package.batch_operation_id
                            AND program.status IN ('PLANNED','ACTIVE','SUSPENDED')
                            AND output.status IN ('ALLOCATED','IN_PRODUCTION'))=1));
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) operations.Add((reader.GetString(0), reader.GetString(1)));
        return operations;
    }

    internal static async Task<IReadOnlySet<string>> ReadMachinesWithDprntAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken token)
    {
        var machines = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT machine.id, connection.configuration_json
            FROM machines machine
            JOIN machine_connections connection ON connection.machine_id = machine.id AND connection.enabled = 1
            WHERE machine.execution_mode = 'CNC_GCODE';
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (SqliteProductionPackageRepository.DprntSource(reader.IsDBNull(1) ? null : reader.GetString(1))
                is { } source && source != CncDprntSources.None)
                machines.Add(reader.GetString(0));
        }
        return machines;
    }

    /// <summary>The latest workflow event of every Batch Operation's current Production Run.</summary>
    internal static async Task<IReadOnlyDictionary<string, string>> ReadLatestEventsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken token)
    {
        var events = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH latest AS (
                SELECT event.production_run_id, event.event_type,
                       ROW_NUMBER() OVER (PARTITION BY event.production_run_id
                                          ORDER BY event.server_received_at DESC, event.id DESC) AS position
                FROM production_run_workflow_events event)
            SELECT assignment.batch_operation_id, latest.event_type
            FROM machine_assignments assignment
            JOIN latest ON latest.production_run_id = assignment.production_run_id AND latest.position = 1
            WHERE assignment.released_at IS NULL;
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) events[reader.GetString(0)] = reader.GetString(1);
        return events;
    }

    /// <summary>Appends a planner-reported workflow event after the run's latest one.</summary>
    internal static async Task<(string EventId, DateTimeOffset At)> AppendAsync(
        SqliteConnection connection, SqliteTransaction transaction, string productionRunId, string machineId,
        string eventType, string userId, object metadata, DateTimeOffset now, CancellationToken token)
    {
        DateTimeOffset? latest = null;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT MAX(server_received_at) FROM production_run_workflow_events WHERE production_run_id = $runId;";
            read.Parameters.AddWithValue("$runId", productionRunId);
            if (await read.ExecuteScalarAsync(token) is string value)
                latest = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        var at = latest is { } last && last >= now.ToUniversalTime() ? last.AddTicks(1) : now.ToUniversalTime();
        var eventId = Guid.NewGuid().ToString("N");
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO production_run_workflow_events (
                id, production_run_id, machine_id, event_type, source, source_event_id, server_received_at, user_id, metadata_json)
            VALUES ($id, $runId, $machineId, $eventType, $source, $sourceEventId, $at, $userId, $metadata);
            """;
        insert.Parameters.AddWithValue("$id", eventId);
        insert.Parameters.AddWithValue("$runId", productionRunId);
        insert.Parameters.AddWithValue("$machineId", machineId);
        insert.Parameters.AddWithValue("$eventType", eventType);
        insert.Parameters.AddWithValue("$source", ManualWorkflowStatuses.Source);
        insert.Parameters.AddWithValue("$sourceEventId", $"MANUAL:{eventId}");
        insert.Parameters.AddWithValue("$at", at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$userId", userId);
        insert.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await insert.ExecuteNonQueryAsync(token);
        return (eventId, at);
    }
}
