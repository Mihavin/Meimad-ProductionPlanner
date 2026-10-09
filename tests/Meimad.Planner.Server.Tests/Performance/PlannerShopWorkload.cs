using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Tests.Performance;

// Synthetic test data only. No installed database, real supplier or CNC connection.
internal static class PlannerShopWorkload
{
    internal static string OperationId(int index) => index == 0 ? "operation-package" : $"profile-op-{index}";
    private static string SourceId(int step) => step == 0 ? "case-operation-package" : $"profile-source-{step}";
    internal static string MachineId(int index) => index == 0 ? "machine-package" : $"profile-machine-{index}";

    internal static async Task SeedAsync(SqliteCommand command, int count)
    {
        for (var step = 1; step < 4; step++)
        {
            command.CommandText = """
                INSERT INTO case_operations(id,case_id,operation_number,route_position,name,required_machine_type,setup_seconds,cycle_seconds)
                VALUES($id,'case-package',$number,$step,'Shop route step','mill',120,$cycle);
                """;
            command.Parameters.AddWithValue("$id", SourceId(step));
            command.Parameters.AddWithValue("$number", (step + 1) * 10);
            command.Parameters.AddWithValue("$step", step);
            command.Parameters.AddWithValue("$cycle", 30 + step * 15);
            await command.ExecuteNonQueryAsync(); command.Parameters.Clear();
        }
        for (var worker = 0; worker < 7; worker++)
        {
            command.CommandText = """
                INSERT INTO employee_resources(id,employee_number,name,resource_type,first_name,last_name,skills_json,assigned_calendar_id,is_active)
                VALUES($id,$id,$id,$role,'Profile','Worker',$skills,'calendar-package',1);
                """;
            command.Parameters.AddWithValue("$id", $"profile-worker-{worker}");
            command.Parameters.AddWithValue("$role", worker < 4 ? "setup_worker" : worker < 6 ? "regular_worker" : "qa_worker");
            command.Parameters.AddWithValue("$skills", worker < 4 ? JsonSerializer.Serialize(Enumerable.Range(0, 16).Select(MachineId)) : "[]");
            await command.ExecuteNonQueryAsync(); command.Parameters.Clear();
        }
        command.CommandText = """
            INSERT INTO skills(id,name,created_at,updated_at) VALUES('profile-inspect','Profile inspection','2026-10-08','2026-10-08');
            INSERT INTO employee_skills(employee_resource_id,skill_id,assigned_at,assigned_by)
            VALUES('profile-worker-6','profile-inspect','2026-10-08','profile');
            INSERT INTO workstation_types(id,name,created_at,updated_at)
            VALUES('profile-bench-type','Profile bench','2026-10-08','2026-10-08');
            INSERT INTO workstations(id,name,workstation_type_id,working_calendar_id,capacity,created_at,updated_at)
            VALUES('profile-bench-1','Bench 1','profile-bench-type','calendar-package',1,'2026-10-08','2026-10-08'),
                  ('profile-bench-2','Bench 2','profile-bench-type','calendar-package',1,'2026-10-08','2026-10-08');
            INSERT INTO external_resources(id,name,supplier_name,promised_lead_time_minutes,safety_buffer_minutes,created_at,updated_at)
            VALUES('profile-external','External finish','Synthetic supplier',1440,120,'2026-10-08','2026-10-08');
            INSERT INTO operation_resource_requirements(id,case_operation_id,sequence_position,resource_class,workstation_type_id,
                estimated_duration_seconds,direction,created_at,updated_at)
            VALUES('profile-deburr','profile-source-2',0,'WORKSTATION','profile-bench-type',1200,'FORWARD','2026-10-08','2026-10-08');
            INSERT INTO operation_resource_requirements(id,case_operation_id,sequence_position,resource_class,required_skill_id,
                estimated_duration_seconds,direction,created_at,updated_at)
            VALUES('profile-inspection','profile-source-3',0,'EMPLOYEE','profile-inspect',600,'FORWARD','2026-10-08','2026-10-08');
            INSERT INTO operation_resource_requirements(id,case_operation_id,sequence_position,resource_class,external_resource_id,
                predecessor_requirement_id,estimated_duration_seconds,direction,created_at,updated_at)
            VALUES('profile-finish','profile-source-3',1,'EXTERNAL','profile-external','profile-inspection',0,'FORWARD','2026-10-08','2026-10-08');
            """;
        await command.ExecuteNonQueryAsync();
        string[] modes = ["independent", "sequential", "parallel_capable", "locked_simultaneous"];
        for (var i = 0; i < count; i++)
        {
            var group = i / 4;
            var step = i % 4;
            var batch = group == 0 ? "batch-package" : $"profile-batch-{group}";
            var mode = modes[group % modes.Length];
            if (step == 0)
            {
                command.CommandText = """
                    INSERT OR IGNORE INTO production_batches(id,case_id,batch_number,status,planned_quantity)
                    VALUES($batch,'case-package',$batch,'waiting',10);
                    INSERT INTO batch_allocations(id,production_batch_id,allocation_type,quantity)
                    VALUES($allocation,$batch,'stock',10);
                    """;
                command.Parameters.AddWithValue("$batch", batch);
                command.Parameters.AddWithValue("$allocation", $"profile-allocation-{group}");
                await command.ExecuteNonQueryAsync(); command.Parameters.Clear();
            }
            command.CommandText = i == 0 ? """
                UPDATE batch_operations SET dependency_type=$mode,simultaneous_group_key=$group WHERE id=$operation;
                """ : """
                INSERT INTO batch_operations(id,production_batch_id,source_case_operation_id,operation_number,route_position,name,
                    required_machine_type,setup_seconds,cycle_seconds,status,dependency_type,predecessor_source_case_operation_id,simultaneous_group_key)
                VALUES($operation,$batch,$source,$number,$step,'Shop operation','mill',120,$cycle,'not_started',$mode,$predecessor,$group);
                INSERT INTO machine_assignments(id,batch_operation_id,machine_id,backlog_position)
                VALUES($assignment,$operation,$machine,$position);
                """;
            command.Parameters.AddWithValue("$operation", OperationId(i));
            command.Parameters.AddWithValue("$batch", batch);
            command.Parameters.AddWithValue("$source", SourceId(step));
            command.Parameters.AddWithValue("$number", (step + 1) * 10);
            command.Parameters.AddWithValue("$step", step);
            command.Parameters.AddWithValue("$cycle", 30 + step * 15);
            command.Parameters.AddWithValue("$mode", step == 0 && mode != "locked_simultaneous" ? "independent" : mode);
            command.Parameters.AddWithValue("$predecessor", step == 0 || mode == "independent" ? DBNull.Value : SourceId(step - 1));
            command.Parameters.AddWithValue("$group", mode == "locked_simultaneous" ? $"profile-flow-{group}" : DBNull.Value);
            command.Parameters.AddWithValue("$assignment", $"profile-assignment-{i}");
            command.Parameters.AddWithValue("$machine", MachineId(i % 16));
            command.Parameters.AddWithValue("$position", i / 16);
            await command.ExecuteNonQueryAsync(); command.Parameters.Clear();
        }
    }

    internal static async Task SeedHistoryAsync(SqliteCommand command, int count)
    {
        if (count == 0) return;
        command.CommandText = """
            INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active,display_enabled)
            VALUES('profile-history-machine','ARCHIVE','Archived Machine','mill','calendar-package','inactive',0,0);
            INSERT INTO case_operations(id,case_id,operation_number,route_position,name,required_machine_type,setup_seconds,cycle_seconds)
            VALUES('profile-history-source','case-package',990,99,'Historical recipe','mill',60,60);
            INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity)
            VALUES('profile-history-batch','case-package','Historical batch','completed',1);
            INSERT INTO batch_allocations(id,production_batch_id,allocation_type,quantity)
            VALUES('profile-history-allocation','profile-history-batch','stock',1);
            INSERT INTO batch_operations(id,production_batch_id,source_case_operation_id,operation_number,route_position,name,
                required_machine_type,setup_seconds,cycle_seconds,status)
            VALUES('profile-history-operation','profile-history-batch','profile-history-source',990,0,'Historical operation','mill',60,60,'completed');
            INSERT INTO production_runs(id,status,legacy_batch_operation_id)
            VALUES('profile-history-run','COMPLETED','profile-history-operation');
            INSERT INTO production_run_programs(id,production_run_id,manufacturing_program_id,process_revision_id,
                sequence_position,target_cycle_count,completed_cycle_count,status,cycle_seconds_snapshot,legacy_unmanaged,created_at,updated_at)
            VALUES('profile-history-program','profile-history-run','case-operation:profile-history-source',NULL,0,1,1,'COMPLETED',60,1,'2026-01-01','2026-01-01');
            INSERT INTO production_run_outputs(id,production_run_program_id,batch_operation_id,revision_output_id,quantity_per_cycle,
                target_quantity,produced_quantity,status,created_at,updated_at)
            VALUES('profile-history-output','profile-history-program','profile-history-operation',NULL,1,1,1,'COMPLETED','2026-01-01','2026-01-01');
            """;
        await command.ExecuteNonQueryAsync();
        command.CommandText = """
            INSERT INTO production_run_workflow_events(id,production_run_id,machine_id,event_type,source,source_event_id,server_received_at,metadata_json)
            VALUES($id,'profile-history-run','profile-history-machine',$kind,'PROFILE',$id,$at,'{}');
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var at = command.Parameters.Add("$at", SqliteType.Text);
        for (var i = 0; i < count; i++)
        {
            id.Value = $"profile-history-{i:D6}";
            kind.Value = i % 2 == 0 ? "SEND_TO_QC" : "QC_PASS";
            at.Value = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(i).ToString("O", CultureInfo.InvariantCulture);
            await command.ExecuteNonQueryAsync();
        }
        command.Parameters.Clear();
    }
}
