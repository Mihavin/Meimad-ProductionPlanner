using System.Text.Json;
using Meimad.Planner.Server.Application.Preparation;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Domain.Readiness;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

// Queue reads need a validity bit, not the artifact list or another copy of readiness.
// All facts are read under the caller's snapshot; parity tests compare the full package read.
internal static class SqlitePreparationPackageValidity
{
    internal static async Task<IReadOnlySet<string>> ReadAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<PreparationQueueSource> sources, CancellationToken token)
    {
        var byOutput = sources.Where(x => x.Context is not null).ToDictionary(x => x.Context!.ProductionRunOutputId, StringComparer.Ordinal);
        var valid = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,tool_table_release_id FROM process_revisions WHERE is_active=1";
        var active = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) active.Add(reader.GetString(0), reader.GetString(1));
        command.CommandText = """
            SELECT context.context_json, package.machine_assignment_id,package.machine_id,package.batch_operation_id,
                package.production_run_id,package.execution_mode,package.tool_offset_mode,
                package.tool_table_release_id,package.gcode_release_id,package.verification_enabled,
                package.verification_configuration_version,package.verification_macro_version,package.tool_preparation_id,
                machine.execution_mode,COALESCE(capability.allow_manual_dummy_tool_offsets,0),
                COALESCE(settings.enabled,0),settings.version,settings.expected_macro_version,
                (SELECT p.id FROM tool_preparations p WHERE p.batch_operation_id=package.batch_operation_id
                    AND p.machine_id=machine.id ORDER BY p.version_number DESC LIMIT 1)
            FROM production_package_context_current current
            JOIN production_packages package ON package.id=current.production_package_id
            JOIN production_package_contexts context ON context.production_package_id=package.id
            JOIN machine_assignments assignment ON assignment.id=package.machine_assignment_id
                AND assignment.machine_id=package.machine_id AND assignment.released_at IS NULL
            JOIN machines machine ON machine.id=assignment.machine_id
            LEFT JOIN machine_package_capabilities capability ON capability.machine_id=machine.id
            LEFT JOIN cnc_verification_settings settings ON settings.machine_id=machine.id;
            """;
        await using var rows = await command.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token))
        {
            var saved = JsonSerializer.Deserialize<ProductionPackageContext>(rows.GetString(0));
            if (saved is null || !byOutput.TryGetValue(saved.ProductionRunOutputId, out var source)) continue;
            var live = source.Context!;
            var readiness = source.ReadinessContext;
            if (live.MachineAssignmentId != saved.MachineAssignmentId || live.ProductionRunId != saved.ProductionRunId
                || live.ProductionRunProgramId != saved.ProductionRunProgramId || live.MachineId != saved.MachineId
                || live.TargetQuantity != saved.TargetQuantity || live.OutputAllocationStamp != saved.OutputAllocationStamp) continue;
            if (rows.GetString(1) != live.MachineAssignmentId || rows.GetString(2) != live.MachineId
                || rows.GetString(3) != live.BatchOperationId || Text(rows, 4) != live.ProductionRunId) continue;
            var mode = rows.GetString(5);
            var offsetMode = rows.GetString(6);
            var tools = rows.GetString(7);
            var effective = mode == "MANUAL" ? null : ProductionReadinessEvaluator.Evaluate(readiness).EffectiveGCodeReleaseId;
            if (readiness.ActiveProcessRevisionId != saved.ProcessRevisionId || readiness.ActiveToolTableReleaseId != tools
                || effective != Text(rows, 8) || saved.ProcessRevisionId is null
                || !active.TryGetValue(saved.ProcessRevisionId, out var activeTools) || activeTools != tools) continue;
            if (mode != rows.GetString(13) || offsetMode != "MEASURED" && !rows.GetBoolean(14)) continue;
            var verification = rows.GetBoolean(9);
            if (mode != "MANUAL" && (verification != rows.GetBoolean(15)
                || verification && (rows.IsDBNull(10) || rows.IsDBNull(11) || rows.IsDBNull(16) || rows.IsDBNull(17)
                    || rows.GetInt32(10) != rows.GetInt32(16) || rows.GetInt32(11) != rows.GetInt32(17)))) continue;
            if (offsetMode == "MEASURED" && Text(rows, 12) != Text(rows, 18)) continue;
            valid.Add(live.ProductionRunOutputId);
        }
        return valid;
    }

    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
