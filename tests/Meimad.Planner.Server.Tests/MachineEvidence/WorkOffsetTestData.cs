using Meimad.Planner.Server.Persistence;

namespace Meimad.Planner.Server.Tests.MachineEvidence;

/// <summary>
/// Machines 10 (Haas NGC), 11 (FANUC), 12 (Mazak Matrix), 13 (Okuma OSP) and 14 (Haas, inactive is
/// not set), Case Operation 20 with two NC releases of one postprocessor: <see cref="OldRelease"/>
/// (NC identity 654320, revision 1, no longer current) and <see cref="CurrentRelease"/> (654321, revision 2).
/// </summary>
internal static class WorkOffsetTestData
{
    internal const string Operation = "case-operation-wo";
    internal const string OtherOperation = "case-operation-wo-30";
    internal const string CurrentRelease = "gcode-wo-current";
    internal const string OldRelease = "gcode-wo-old";
    internal const string OtherOperationRelease = "gcode-wo-op30";
    internal static readonly DateTimeOffset At = new(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);

    internal static async Task SeedAsync(SqliteDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars(id,name,time_zone_id)VALUES('calendar-wo','Calendar','UTC');
            INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active,nc_dialect) VALUES
              ('machine-10','10','Haas VF-3SS','mill','calendar-wo','active',1,'HAAS_NGC'),
              ('machine-11','11','FANUC mill','mill','calendar-wo','active',1,'FANUC_MACRO_B'),
              ('machine-12','12','Mazak Variaxis i-500','mill','calendar-wo','active',1,'MAZAK_MATRIX_EIA'),
              ('machine-13','13','Okuma Genos L200E-M','lathe','calendar-wo','active',1,'OKUMA_OSP'),
              ('machine-14','14','Haas UMC-500SS','mill','calendar-wo','active',1,'HAAS_NGC'),
              ('machine-15','15','Retired Haas','mill','calendar-wo','active',0,'HAAS_NGC');
            INSERT INTO cases(id,part_number,name,working_folder_path)VALUES('case-wo','30P647','Bracket','C:\\Cases\\30P647');
            INSERT INTO case_operations(id,case_id,operation_number,route_position,name) VALUES
              ('case-operation-wo','case-wo',20,0,'OP20 mill'),
              ('case-operation-wo-30','case-wo',30,1,'OP30 mill');
            INSERT INTO postprocessors(id,name)VALUES('post-wo','Haas 5X');
            INSERT INTO tool_table_releases(id,case_operation_id,revision_number,original_file_name,stored_relative_path,file_size,file_hash,released_at,released_by,release_comment,created_at,updated_at,required_tool_count) VALUES
              ('tools-wo','case-operation-wo',1,'tools.csv','tools/wo.csv',1,$hash,$at,'user','release',$at,$at,0),
              ('tools-wo-30','case-operation-wo-30',1,'tools.csv','tools/wo30.csv',1,$hash,$at,'user','release',$at,$at,0);
            INSERT INTO process_revisions(id,case_operation_id,revision_number,is_active,tool_table_release_id,created_at,created_by,change_description,version,updated_at,manufacturing_program_id) VALUES
              ('process-wo','case-operation-wo',1,1,'tools-wo',$at,'user','release',1,$at,'case-operation:case-operation-wo'),
              ('process-wo-30','case-operation-wo-30',1,1,'tools-wo-30',$at,'user','release',1,$at,'case-operation:case-operation-wo-30');
            INSERT INTO manufacturing_program_revision_outputs(id,process_revision_id,case_operation_id,quantity_per_cycle,display_order,execution_metadata_json,created_at) VALUES
              ('recipe-wo','process-wo','case-operation-wo',1,0,'{}',$at),
              ('recipe-wo-30','process-wo-30','case-operation-wo-30',1,0,'{}',$at);
            INSERT INTO gcode_releases(id,case_operation_id,process_revision_id,postprocessor_id,post_specific_revision,original_file_name,stored_relative_path,file_size,file_hash,released_at,released_by,change_scope,release_comment,tool_table_release_id,created_at,updated_at) VALUES
              ('gcode-wo-old','case-operation-wo','process-wo','post-wo',1,'op20.nc','gcode/wo-1.nc',1,$oldHash,$at,'user','LOCAL_POST_REVISION','first','tools-wo',$at,$at),
              ('gcode-wo-current','case-operation-wo','process-wo','post-wo',2,'op20.nc','gcode/wo-2.nc',1,$hash,$at,'user','LOCAL_POST_REVISION','second','tools-wo',$at,$at),
              ('gcode-wo-op30','case-operation-wo-30','process-wo-30','post-wo',1,'op30.nc','gcode/wo30.nc',1,$hash30,$at,'user','LOCAL_POST_REVISION','first','tools-wo-30',$at,$at);
            INSERT INTO gcode_release_verification_hooks(gcode_release_id,hook_version,invocation_kind,invocation_number,nc_identity_token,line_number,created_at,updated_at) VALUES
              ('gcode-wo-old',1,'G65',9002,654320,3,$at,$at),
              ('gcode-wo-current',1,'G65',9002,654321,3,$at,$at),
              ('gcode-wo-op30',1,'G65',9002,654330,3,$at,$at);
            """;
        command.Parameters.AddWithValue("$at", At.ToString("O"));
        command.Parameters.AddWithValue("$hash", new string('b', 64));
        command.Parameters.AddWithValue("$oldHash", new string('a', 64));
        command.Parameters.AddWithValue("$hash30", new string('c', 64));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The Haas report frame of <paramref name="machine"/> for a release with the given X/Y/Z.</summary>
    internal static string[] Frame(
        string session, string machine = "10", string release = "654321", string operation = "20",
        string offset = "G54", double x = 125.402, double y = -80.016, double z = 42.691, string units = "21") =>
    [
        $"MEIMAD/OFFSET-START/SESSION/{session}/MACHINE/{machine}/PROGRAM/{release}/OPERATION/{operation}/RELEASE/{release}",
        $"MEIMAD/OFFSET/SESSION/{session}/OFFSET/{offset}/UNITS/{units}/AXES/XYZ",
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"MEIMAD/OFFSET/SESSION/{session}/X/{x:0.0000}/Y/{y:0.0000}/Z/{z:0.0000}"),
        $"MEIMAD/OFFSET-END/SESSION/{session}"
    ];
}
