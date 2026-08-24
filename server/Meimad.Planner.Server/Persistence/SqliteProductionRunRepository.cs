using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Domain.ProductionRuns;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteProductionRunRepository : IProductionRunRepository
{
    private readonly SqliteDatabase database;
    private readonly TimeProvider timeProvider;
    private readonly ProductionRunCyclePlanner planner;

    public SqliteProductionRunRepository(
        SqliteDatabase database, TimeProvider timeProvider, ProductionRunCyclePlanner planner)
    {
        this.database = database;
        this.timeProvider = timeProvider;
        this.planner = planner;
    }

    public async Task<IReadOnlyList<ProductionRun>> ListAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        var ids = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM production_runs ORDER BY created_at, id;";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) ids.Add(reader.GetString(0));
        }
        var result = new List<ProductionRun>(ids.Count);
        foreach (var id in ids)
        {
            var value = await ReadAsync(connection, null, id, token);
            if (value is not null) result.Add(value);
        }
        return result;
    }

    public async Task<ProductionRun?> GetAsync(string runId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        return await ReadAsync(connection, null, runId, token);
    }

    public async Task<IReadOnlyList<UnallocatedBatchOperation>> ListUnallocatedAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation.id, operation.production_batch_id, batch.case_id,
                   cases.part_number, operation.operation_number, operation.name,
                   batch.planned_quantity,
                   COALESCE(SUM(CASE WHEN run.status IN ('COMPLETED','IN_PROGRESS','SUSPENDED')
                                     THEN output.produced_quantity ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN run.status NOT IN ('CANCELLED','ABORTED')
                                     THEN output.target_quantity ELSE 0 END), 0)
            FROM batch_operations operation
            JOIN production_batches batch ON batch.id = operation.production_batch_id
            JOIN cases ON cases.id = batch.case_id
            LEFT JOIN production_run_outputs output ON output.batch_operation_id = operation.id
            LEFT JOIN production_run_programs program ON program.id = output.production_run_program_id
            LEFT JOIN production_runs run ON run.id = program.production_run_id
            WHERE operation.status <> 'completed'
            GROUP BY operation.id
            ORDER BY cases.part_number, batch.batch_number, operation.route_position, operation.id;
            """;
        var result = new List<UnallocatedBatchOperation>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var required = reader.GetInt64(6);
            var produced = reader.GetInt64(7);
            var allocated = reader.GetInt64(8);
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetString(5), required,
                produced, allocated, Math.Max(0, required - allocated),
                Math.Max(0, required - produced)));
        }
        return result;
    }

    public async Task<ProductionRun> CreateAsync(
        CreateProductionRunCommand command,
        EditAuthority authority,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        var now = timeProvider.GetUtcNow();
        var runId = Guid.NewGuid().ToString("N");

        var prepared = new List<PreparedProgram>();
        foreach (var program in command.Programs.OrderBy(value => value.SequencePosition))
            prepared.Add(await PrepareProgramAsync(connection, transaction, program, null, token));
        planner.Calculate(new(command.SharedSetupSeconds, false,
            prepared.Select(value => new ProductionRunProgramCycleInput(
                value.Command.ManufacturingProgramId, value.Command.SequencePosition,
                value.Command.CycleSeconds, 0,
                value.Outputs.Select(output => new ProductionRunOutputCycleInput(
                    output.Command.RevisionOutputId, output.QuantityPerCycle,
                    output.Command.TargetQuantity, output.RemainingAllocatable)).ToArray())).ToArray()));

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO production_runs (
                    id,status,shared_setup_seconds,setup_snapshot_json,structure_locked_at,
                    legacy_batch_operation_id,version,created_at,updated_at)
                VALUES ($id,'PLANNED',$setup,$snapshot,NULL,NULL,1,$at,$at);
                """;
            insert.Parameters.AddWithValue("$id", runId);
            insert.Parameters.AddWithValue("$setup", command.SharedSetupSeconds);
            insert.Parameters.AddWithValue("$snapshot", command.SetupSnapshotJson);
            insert.Parameters.AddWithValue("$at", Format(now));
            await insert.ExecuteNonQueryAsync(token);
        }

        foreach (var value in prepared)
        {
            var programId = Guid.NewGuid().ToString("N");
            var targetCycles = value.Outputs[0].Command.TargetQuantity / value.Outputs[0].QuantityPerCycle;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO production_run_programs (
                        id,production_run_id,manufacturing_program_id,process_revision_id,
                        selected_gcode_release_id,sequence_position,target_cycle_count,
                        completed_cycle_count,status,cycle_seconds_snapshot,legacy_unmanaged,
                        version,created_at,updated_at)
                    VALUES ($id,$runId,$programId,$revisionId,$releaseId,$sequence,$cycles,
                            0,'PLANNED',$cycleSeconds,0,1,$at,$at);
                    """;
                insert.Parameters.AddWithValue("$id", programId);
                insert.Parameters.AddWithValue("$runId", runId);
                insert.Parameters.AddWithValue("$programId", value.Command.ManufacturingProgramId);
                insert.Parameters.AddWithValue("$revisionId", value.Command.ProcessRevisionId);
                insert.Parameters.AddWithValue("$releaseId", (object?)value.Command.GCodeReleaseId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$sequence", value.Command.SequencePosition);
                insert.Parameters.AddWithValue("$cycles", targetCycles);
                insert.Parameters.AddWithValue("$cycleSeconds", value.Command.CycleSeconds);
                insert.Parameters.AddWithValue("$at", Format(now));
                await insert.ExecuteNonQueryAsync(token);
            }
            foreach (var output in value.Outputs)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO production_run_outputs (
                        id,production_run_program_id,batch_operation_id,revision_output_id,
                        quantity_per_cycle,target_quantity,produced_quantity,status,
                        version,created_at,updated_at)
                    VALUES ($id,$programId,$operationId,$recipeOutputId,$quantity,$target,
                            0,'ALLOCATED',1,$at,$at);
                    """;
                insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                insert.Parameters.AddWithValue("$programId", programId);
                insert.Parameters.AddWithValue("$operationId", output.Command.BatchOperationId);
                insert.Parameters.AddWithValue("$recipeOutputId", output.Command.RevisionOutputId);
                insert.Parameters.AddWithValue("$quantity", output.QuantityPerCycle);
                insert.Parameters.AddWithValue("$target", output.Command.TargetQuantity);
                insert.Parameters.AddWithValue("$at", Format(now));
                await insert.ExecuteNonQueryAsync(token);
            }
        }

        if (command.Assignment is not null)
            await WriteAssignmentAsync(connection, transaction, runId,
                prepared.SelectMany(value => value.Outputs).First().Command.BatchOperationId,
                command.Assignment, actor, now, token);

        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction,
            new("production_run_created", now, actor,
                new Dictionary<string, string> { ["productionRunId"] = runId },
                "PLANNER_COMPOSITION", null, null,
                new { command.SharedSetupSeconds, programCount = prepared.Count,
                    outputCount = prepared.Sum(value => value.Outputs.Count) }), token);
        await transaction.CommitAsync(token);
        return (await GetAsync(runId, token))!;
    }

    public async Task<ProductionRun> AssignAsync(
        string runId, int expectedVersion, AssignProductionRunCommand command,
        EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        var state = await ReadRunStateAsync(connection, transaction, runId, token)
            ?? throw new ProductionRunNotFoundException(runId);
        if (state.Version != expectedVersion) throw new ProductionRunVersionConflictException(runId, expectedVersion);
        if (state.Status is not ("DRAFT" or "PLANNED"))
            throw new ProductionRunStateException("started_run_immutable", "A started or historical Production Run cannot be reassigned.");
        var owner = await ReadFirstOutputOperationAsync(connection, transaction, runId, token);
        await RemoveAssignmentAsync(connection, transaction, runId, token);
        var now = timeProvider.GetUtcNow();
        await WriteAssignmentAsync(connection, transaction, runId, owner, command, actor, now, token);
        await IncrementRunVersionAsync(connection, transaction, runId, now, token);
        await transaction.CommitAsync(token);
        return (await GetAsync(runId, token))!;
    }

    public async Task<ProductionRun> UnassignAsync(
        string runId, int expectedVersion, EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        var state = await ReadRunStateAsync(connection, transaction, runId, token)
            ?? throw new ProductionRunNotFoundException(runId);
        if (state.Version != expectedVersion) throw new ProductionRunVersionConflictException(runId, expectedVersion);
        if (state.Status is not ("DRAFT" or "PLANNED"))
            throw new ProductionRunStateException("started_run_immutable", "A started Production Run cannot be unassigned.");
        var now = timeProvider.GetUtcNow();
        await RemoveAssignmentAsync(connection, transaction, runId, token);
        await IncrementRunVersionAsync(connection, transaction, runId, now, token);
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction,
            new("production_run_unassigned", now, actor,
                new Dictionary<string, string> { ["productionRunId"] = runId },
                "PLANNER_UNASSIGNED", null, null, null), token);
        await transaction.CommitAsync(token);
        return (await GetAsync(runId, token))!;
    }

    public async Task<ProductionRun> UpdateCompositionAsync(
        string runId, int expectedVersion, CreateProductionRunCommand command,
        EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        var state = await ReadRunStateAsync(connection, transaction, runId, token)
            ?? throw new ProductionRunNotFoundException(runId);
        if (state.Version != expectedVersion) throw new ProductionRunVersionConflictException(runId, expectedVersion);
        if (state.Status is not ("DRAFT" or "PLANNED"))
            throw new ProductionRunStateException("started_run_immutable", "Production Run composition is immutable after Start.");
        var prepared = new List<PreparedProgram>();
        foreach (var program in command.Programs.OrderBy(value => value.SequencePosition))
            prepared.Add(await PrepareProgramAsync(connection, transaction, program, runId, token));
        planner.Calculate(new(command.SharedSetupSeconds, false,
            prepared.Select(value => new ProductionRunProgramCycleInput(
                value.Command.ManufacturingProgramId, value.Command.SequencePosition,
                value.Command.CycleSeconds, 0,
                value.Outputs.Select(output => new ProductionRunOutputCycleInput(
                    output.Command.RevisionOutputId, output.QuantityPerCycle,
                    output.Command.TargetQuantity, output.RemainingAllocatable)).ToArray())).ToArray()));
        var now = timeProvider.GetUtcNow();
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM production_run_outputs WHERE production_run_program_id IN
                    (SELECT id FROM production_run_programs WHERE production_run_id=$id);
                DELETE FROM production_run_programs WHERE production_run_id=$id;
                """;
            delete.Parameters.AddWithValue("$id", runId);
            await delete.ExecuteNonQueryAsync(token);
        }
        foreach (var value in prepared)
        {
            var programId = Guid.NewGuid().ToString("N");
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO production_run_programs
                    (id,production_run_id,manufacturing_program_id,process_revision_id,selected_gcode_release_id,
                     sequence_position,target_cycle_count,completed_cycle_count,status,cycle_seconds_snapshot,
                     legacy_unmanaged,version,created_at,updated_at)
                    VALUES($id,$run,$program,$revision,$release,$sequence,$cycles,0,'PLANNED',$seconds,0,1,$at,$at);
                    """;
                insert.Parameters.AddWithValue("$id", programId); insert.Parameters.AddWithValue("$run", runId);
                insert.Parameters.AddWithValue("$program", value.Command.ManufacturingProgramId);
                insert.Parameters.AddWithValue("$revision", value.Command.ProcessRevisionId);
                insert.Parameters.AddWithValue("$release", (object?)value.Command.GCodeReleaseId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$sequence", value.Command.SequencePosition);
                insert.Parameters.AddWithValue("$cycles", value.Outputs[0].Command.TargetQuantity / value.Outputs[0].QuantityPerCycle);
                insert.Parameters.AddWithValue("$seconds", value.Command.CycleSeconds); insert.Parameters.AddWithValue("$at", Format(now));
                await insert.ExecuteNonQueryAsync(token);
            }
            foreach (var output in value.Outputs)
            {
                await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO production_run_outputs
                    (id,production_run_program_id,batch_operation_id,revision_output_id,quantity_per_cycle,
                     target_quantity,produced_quantity,status,version,created_at,updated_at)
                    VALUES($id,$program,$operation,$recipe,$quantity,$target,0,'ALLOCATED',1,$at,$at);
                    """;
                insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); insert.Parameters.AddWithValue("$program", programId);
                insert.Parameters.AddWithValue("$operation", output.Command.BatchOperationId); insert.Parameters.AddWithValue("$recipe", output.Command.RevisionOutputId);
                insert.Parameters.AddWithValue("$quantity", output.QuantityPerCycle); insert.Parameters.AddWithValue("$target", output.Command.TargetQuantity);
                insert.Parameters.AddWithValue("$at", Format(now)); await insert.ExecuteNonQueryAsync(token);
            }
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE production_runs SET shared_setup_seconds=$setup,setup_snapshot_json=$snapshot,version=version+1,updated_at=$at WHERE id=$id;";
            update.Parameters.AddWithValue("$setup", command.SharedSetupSeconds); update.Parameters.AddWithValue("$snapshot", command.SetupSnapshotJson);
            update.Parameters.AddWithValue("$at", Format(now)); update.Parameters.AddWithValue("$id", runId); await update.ExecuteNonQueryAsync(token);
        }
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction,
            new("production_run_composition_changed", now, actor,
                new Dictionary<string, string> { ["productionRunId"] = runId },
                "PLANNER_COMPOSITION", null, null,
                new { command.SharedSetupSeconds, programCount = prepared.Count, outputCount = prepared.Sum(x => x.Outputs.Count) }), token);
        await transaction.CommitAsync(token);
        return (await GetAsync(runId, token))!;
    }

    public async Task<ProductionRun> CancelAsync(
        string runId, int expectedVersion, string reason,
        EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        var state = await ReadRunStateAsync(connection, transaction, runId, token)
            ?? throw new ProductionRunNotFoundException(runId);
        if (state.Version != expectedVersion) throw new ProductionRunVersionConflictException(runId, expectedVersion);
        if (state.Status is not ("DRAFT" or "PLANNED"))
            throw new ProductionRunStateException("started_run_immutable", "Only a not-started Production Run can be cancelled.");
        await RemoveAssignmentAsync(connection, transaction, runId, token);
        var now = timeProvider.GetUtcNow();
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE production_runs SET status='CANCELLED',version=version+1,updated_at=$at WHERE id=$id;
                UPDATE production_run_programs SET status='CANCELLED',version=version+1,updated_at=$at WHERE production_run_id=$id;
                UPDATE production_run_outputs SET status='RELEASED',version=version+1,updated_at=$at
                WHERE production_run_program_id IN (SELECT id FROM production_run_programs WHERE production_run_id=$id);
                """;
            update.Parameters.AddWithValue("$id", runId);
            update.Parameters.AddWithValue("$at", Format(now));
            await update.ExecuteNonQueryAsync(token);
        }
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction,
            new("production_run_cancelled", now, actor,
                new Dictionary<string, string> { ["productionRunId"] = runId },
                "PLANNER_CANCELLED", reason, new { state.Status }, new { status = "CANCELLED" }), token);
        await transaction.CommitAsync(token);
        return (await GetAsync(runId, token))!;
    }

    private async Task<PreparedProgram> PrepareProgramAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        CreateProductionRunProgramCommand command, string? excludedRunId, CancellationToken token)
    {
        await using (var revision = connection.CreateCommand())
        {
            revision.Transaction = transaction;
            revision.CommandText = "SELECT EXISTS(SELECT 1 FROM process_revisions WHERE id=$revisionId AND manufacturing_program_id=$programId);";
            revision.Parameters.AddWithValue("$revisionId", command.ProcessRevisionId);
            revision.Parameters.AddWithValue("$programId", command.ManufacturingProgramId);
            if (Convert.ToInt32(await revision.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 0)
                throw new ProductionRunValidationException("programs.processRevisionId", "revision_program_mismatch", "The revision does not belong to the selected Manufacturing Program.");
        }
        if (command.GCodeReleaseId is not null)
        {
            await using var release = connection.CreateCommand();
            release.Transaction = transaction;
            release.CommandText = "SELECT EXISTS(SELECT 1 FROM gcode_releases WHERE id=$id AND process_revision_id=$revisionId);";
            release.Parameters.AddWithValue("$id", command.GCodeReleaseId);
            release.Parameters.AddWithValue("$revisionId", command.ProcessRevisionId);
            if (Convert.ToInt32(await release.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 0)
                throw new ProductionRunValidationException("programs.gCodeReleaseId", "release_revision_mismatch", "The G-code release does not belong to the selected revision.");
        }
        if (command.Outputs.Count == 0)
            throw new ProductionRunValidationException("programs.outputs", "required", "A run program requires at least one output.");
        var outputs = new List<PreparedOutput>();
        foreach (var output in command.Outputs)
        {
            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = """
                SELECT recipe.quantity_per_cycle, batch.planned_quantity,
                       COALESCE((SELECT SUM(existing.target_quantity)
                           FROM production_run_outputs existing
                           JOIN production_run_programs existing_program ON existing_program.id=existing.production_run_program_id
                           JOIN production_runs existing_run ON existing_run.id=existing_program.production_run_id
                           WHERE existing.batch_operation_id=operation.id
                             AND existing_run.status NOT IN ('CANCELLED','ABORTED')
                             AND ($excludedRunId IS NULL OR existing_program.production_run_id<>$excludedRunId)),0)
                FROM manufacturing_program_revision_outputs recipe
                JOIN batch_operations operation
                  ON operation.id=$operationId
                 AND operation.source_case_operation_id=recipe.case_operation_id
                JOIN production_batches batch ON batch.id=operation.production_batch_id
                WHERE recipe.id=$outputId AND recipe.process_revision_id=$revisionId;
                """;
            query.Parameters.AddWithValue("$operationId", output.BatchOperationId);
            query.Parameters.AddWithValue("$outputId", output.RevisionOutputId);
            query.Parameters.AddWithValue("$revisionId", command.ProcessRevisionId);
            query.Parameters.AddWithValue("$excludedRunId", (object?)excludedRunId ?? DBNull.Value);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                throw new ProductionRunValidationException("programs.outputs", "incompatible_revision_output", "The concrete Batch Operation does not match the selected revision output.");
            var quantity = reader.GetInt64(0);
            var remaining = Math.Max(0, reader.GetInt64(1) - reader.GetInt64(2));
            outputs.Add(new(output, quantity, remaining));
        }
        return new(command, outputs);
    }

    private static async Task WriteAssignmentAsync(
        SqliteConnection connection, SqliteTransaction transaction, string runId,
        string compatibilityOperationId, AssignProductionRunCommand command,
        string actor, DateTimeOffset now, CancellationToken token)
    {
        string machineType;
        await using (var machine = connection.CreateCommand())
        {
            machine.Transaction = transaction;
            machine.CommandText = "SELECT machine_type,is_active FROM machines WHERE id=$id;";
            machine.Parameters.AddWithValue("$id", command.MachineId);
            await using var reader = await machine.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token) || !reader.GetBoolean(1))
                throw new ProductionRunValidationException("machineId", "invalid_machine", "The selected Machine does not exist or is inactive.");
            machineType = reader.GetString(0);
        }
        await using (var compatibility = connection.CreateCommand())
        {
            compatibility.Transaction = transaction;
            compatibility.CommandText = """
                SELECT DISTINCT operation.required_machine_type
                FROM production_run_outputs output
                JOIN production_run_programs program ON program.id=output.production_run_program_id
                JOIN batch_operations operation ON operation.id=output.batch_operation_id
                WHERE program.production_run_id=$id AND operation.required_machine_type IS NOT NULL
                  AND lower(operation.required_machine_type)<>lower($machineType);
                """;
            compatibility.Parameters.AddWithValue("$id", runId);
            compatibility.Parameters.AddWithValue("$machineType", machineType);
            var mismatch = await compatibility.ExecuteScalarAsync(token) as string;
            if (mismatch is not null && !command.ConfirmCompatibilityOverride)
                throw new ProductionRunStateException("machine_type_override_required", "One or more run outputs require an explicit Machine-type override.");
        }

        var assignments = new List<string>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id FROM machine_assignments WHERE machine_id=$id ORDER BY backlog_position,id;";
            read.Parameters.AddWithValue("$id", command.MachineId);
            await using var reader = await read.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) assignments.Add(reader.GetString(0));
        }
        if (command.BacklogPosition > assignments.Count)
            throw new ProductionRunValidationException("backlogPosition", "out_of_range", "Backlog position is outside the selected Machine backlog.");
        await using (var stage = connection.CreateCommand())
        {
            stage.Transaction = transaction;
            stage.CommandText = "UPDATE machine_assignments SET backlog_position=backlog_position+1000000 WHERE machine_id=$id;";
            stage.Parameters.AddWithValue("$id", command.MachineId);
            await stage.ExecuteNonQueryAsync(token);
        }
        var assignmentId = Guid.NewGuid().ToString("N");
        assignments.Insert(command.BacklogPosition, assignmentId);
        for (var position = 0; position < assignments.Count; position++)
        {
            if (assignments[position] == assignmentId) continue;
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE machine_assignments SET backlog_position=$position,version=version+1,updated_at=$at WHERE id=$id;";
            update.Parameters.AddWithValue("$position", position);
            update.Parameters.AddWithValue("$at", Format(now));
            update.Parameters.AddWithValue("$id", assignments[position]);
            await update.ExecuteNonQueryAsync(token);
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO machine_assignments (
                    id,batch_operation_id,machine_id,backlog_position,planning_mode,
                    version,created_at,updated_at,production_run_id)
                VALUES ($id,$operationId,$machineId,$position,$mode,1,$at,$at,$runId);
                """;
            insert.Parameters.AddWithValue("$id", assignmentId);
            insert.Parameters.AddWithValue("$operationId", compatibilityOperationId);
            insert.Parameters.AddWithValue("$machineId", command.MachineId);
            insert.Parameters.AddWithValue("$position", command.BacklogPosition);
            insert.Parameters.AddWithValue("$mode", command.PlanningMode);
            insert.Parameters.AddWithValue("$at", Format(now));
            insert.Parameters.AddWithValue("$runId", runId);
            await insert.ExecuteNonQueryAsync(token);
        }
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction,
            new("production_run_assigned", now, actor,
                new Dictionary<string, string> { ["productionRunId"] = runId,
                    ["machineAssignmentId"] = assignmentId, ["machineId"] = command.MachineId },
                command.ConfirmCompatibilityOverride ? "MACHINE_TYPE_OVERRIDE" : null,
                command.OverrideReason, null, new { command.BacklogPosition, command.PlanningMode }), token);
    }

    private static async Task RemoveAssignmentAsync(
        SqliteConnection connection, SqliteTransaction transaction, string runId, CancellationToken token)
    {
        string? machineId = null;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT machine_id FROM machine_assignments WHERE production_run_id=$id;";
            read.Parameters.AddWithValue("$id", runId);
            machineId = await read.ExecuteScalarAsync(token) as string;
        }
        if (machineId is null) return;
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM machine_assignments WHERE production_run_id=$id;";
            delete.Parameters.AddWithValue("$id", runId);
            await delete.ExecuteNonQueryAsync(token);
        }
        await using var compact = connection.CreateCommand();
        compact.Transaction = transaction;
        compact.CommandText = """
            UPDATE machine_assignments SET backlog_position=backlog_position+1000000 WHERE machine_id=$machineId;
            WITH ranked AS (SELECT id,ROW_NUMBER() OVER(ORDER BY backlog_position,id)-1 position
                            FROM machine_assignments WHERE machine_id=$machineId)
            UPDATE machine_assignments SET backlog_position=(SELECT position FROM ranked WHERE ranked.id=machine_assignments.id)
            WHERE machine_id=$machineId;
            """;
        compact.Parameters.AddWithValue("$machineId", machineId);
        await compact.ExecuteNonQueryAsync(token);
    }

    private static async Task<ProductionRun?> ReadAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string runId, CancellationToken token)
    {
        string status, snapshot;
        int setup, version;
        string? locked, legacy;
        DateTimeOffset created, updated;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT status,shared_setup_seconds,setup_snapshot_json,structure_locked_at,legacy_batch_operation_id,version,created_at,updated_at FROM production_runs WHERE id=$id;";
            read.Parameters.AddWithValue("$id", runId);
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            status=reader.GetString(0); setup=reader.GetInt32(1); snapshot=reader.GetString(2);
            locked=reader.IsDBNull(3)?null:reader.GetString(3); legacy=reader.IsDBNull(4)?null:reader.GetString(4);
            version=reader.GetInt32(5); created=Parse(reader.GetString(6)); updated=Parse(reader.GetString(7));
        }
        var outputMap = new Dictionary<string,List<ProductionRunOutput>>(StringComparer.Ordinal);
        await using (var read = connection.CreateCommand())
        {
            read.Transaction=transaction;
            read.CommandText="""
                SELECT output.id,output.production_run_program_id,output.batch_operation_id,
                       output.revision_output_id,output.quantity_per_cycle,output.target_quantity,
                       output.produced_quantity,output.status,output.version,output.created_at,output.updated_at
                FROM production_run_outputs output JOIN production_run_programs program ON program.id=output.production_run_program_id
                WHERE program.production_run_id=$id ORDER BY program.sequence_position,output.id;
                """;
            read.Parameters.AddWithValue("$id",runId);
            await using var reader=await read.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))
            {
                var pid=reader.GetString(1); if(!outputMap.TryGetValue(pid,out var list)) outputMap[pid]=list=[];
                list.Add(new(reader.GetString(0),pid,reader.GetString(2),reader.IsDBNull(3)?null:reader.GetString(3),
                    reader.GetInt32(4),reader.GetInt32(5),reader.GetInt32(6),reader.GetString(7),reader.GetInt32(8),
                    Parse(reader.GetString(9)),Parse(reader.GetString(10))));
            }
        }
        var programs=new List<ProductionRunProgram>();
        await using (var read=connection.CreateCommand())
        {
            read.Transaction=transaction;
            read.CommandText="""
                SELECT id,manufacturing_program_id,process_revision_id,selected_gcode_release_id,
                       sequence_position,target_cycle_count,completed_cycle_count,status,cycle_seconds_snapshot,
                       production_process_revision_id,production_gcode_release_id,production_tool_table_release_id,
                       production_gcode_file_hash,production_tool_table_file_hash,legacy_unmanaged,version,created_at,updated_at
                FROM production_run_programs WHERE production_run_id=$id ORDER BY sequence_position;
                """;
            read.Parameters.AddWithValue("$id",runId);
            await using var reader=await read.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))
            {
                var id=reader.GetString(0);
                programs.Add(new(id,runId,reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),
                    reader.IsDBNull(3)?null:reader.GetString(3),reader.GetInt32(4),reader.GetInt32(5),reader.GetInt32(6),
                    reader.GetString(7),reader.IsDBNull(8)?null:reader.GetDouble(8),
                    new(reader.IsDBNull(9)?null:reader.GetString(9),reader.IsDBNull(10)?null:reader.GetString(10),
                        reader.IsDBNull(11)?null:reader.GetString(11),reader.IsDBNull(12)?null:reader.GetString(12),
                        reader.IsDBNull(13)?null:reader.GetString(13)),reader.GetBoolean(14),reader.GetInt32(15),
                    Parse(reader.GetString(16)),Parse(reader.GetString(17)),outputMap.GetValueOrDefault(id,[])));
            }
        }
        ProductionRunAssignment? assignment=null;
        await using (var read=connection.CreateCommand())
        {
            read.Transaction=transaction;
            read.CommandText="SELECT id,machine_id,backlog_position,planning_mode,version FROM machine_assignments WHERE production_run_id=$id;";
            read.Parameters.AddWithValue("$id",runId);
            await using var reader=await read.ExecuteReaderAsync(token);
            if(await reader.ReadAsync(token)) assignment=new(reader.GetString(0),reader.GetString(1),reader.GetInt32(2),reader.GetString(3),reader.GetInt32(4));
        }
        return new(runId,status,setup,snapshot,locked is null?null:Parse(locked),legacy,version,created,updated,programs,assignment);
    }

    private static async Task<(string Status,int Version)?> ReadRunStateAsync(SqliteConnection c,SqliteTransaction t,string id,CancellationToken token)
    { await using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT status,version FROM production_runs WHERE id=$id;";q.Parameters.AddWithValue("$id",id);await using var r=await q.ExecuteReaderAsync(token);return await r.ReadAsync(token)?(r.GetString(0),r.GetInt32(1)):null; }
    private static async Task<string> ReadFirstOutputOperationAsync(SqliteConnection c,SqliteTransaction t,string id,CancellationToken token)
    { await using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT output.batch_operation_id FROM production_run_outputs output JOIN production_run_programs program ON program.id=output.production_run_program_id WHERE program.production_run_id=$id ORDER BY program.sequence_position,output.id LIMIT 1;";q.Parameters.AddWithValue("$id",id);return await q.ExecuteScalarAsync(token) as string??throw new ProductionRunStateException("empty_run","Production Run has no output."); }
    private static async Task IncrementRunVersionAsync(SqliteConnection c,SqliteTransaction t,string id,DateTimeOffset at,CancellationToken token)
    { await using var q=c.CreateCommand();q.Transaction=t;q.CommandText="UPDATE production_runs SET version=version+1,updated_at=$at WHERE id=$id;";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$at",Format(at));await q.ExecuteNonQueryAsync(token); }
    private static async Task<string> EnsureEditAuthorityAsync(SqliteConnection c,SqliteTransaction t,EditAuthority a,CancellationToken token)
    { await SqliteEditModeRepository.ApplyExpiredRequestAsync(c,t,DateTimeOffset.UtcNow,token);await using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT holder_client_id,holder_user_id,generation FROM edit_tokens WHERE id=1;";await using var r=await q.ExecuteReaderAsync(token);if(!await r.ReadAsync(token)||r.IsDBNull(0))throw new EditModeMutationException("edit_mode_required","No Windows client currently holds Edit Mode.");if(r.GetString(0)!=a.ClientId||r.GetInt64(2)!=a.Generation)throw new EditModeMutationException("edit_generation_stale","This client does not hold the active Edit Mode generation.");return r.IsDBNull(1)?a.ClientId:r.GetString(1); }
    private static string Format(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
    private sealed record PreparedProgram(CreateProductionRunProgramCommand Command,IReadOnlyList<PreparedOutput> Outputs);
    private sealed record PreparedOutput(CreateProductionRunOutputCommand Command,long QuantityPerCycle,long RemainingAllocatable);
}
