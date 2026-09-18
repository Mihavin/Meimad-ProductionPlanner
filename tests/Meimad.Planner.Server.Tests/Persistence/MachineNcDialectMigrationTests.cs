using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class MachineNcDialectMigrationTests
{
    [Fact]
    public async Task Version_76_defaults_existing_Machines_to_Haas_and_makes_the_mapping_triggers_dialect_aware()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await ExecuteAsync(connection, """
            INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-d', 'Dialects', 'UTC');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-haas', '10', 'Haas', 'mill', 'calendar-d', 'active', 1);
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active, nc_dialect)
            VALUES ('machine-fanuc', '11', 'Fanuc', 'mill', 'calendar-d', 'active', 1, 'FANUC_MACRO_B');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active, nc_dialect)
            VALUES ('machine-osp', '12', 'Okuma', 'lathe', 'calendar-d', 'active', 1, 'OKUMA_OSP');
            """);

        Assert.Equal("HAAS_NGC", await ScalarAsync(connection, "SELECT nc_dialect FROM machines WHERE id = 'machine-haas';"));
        Assert.Equal("machine_nc_dialect", await ScalarAsync(connection, "SELECT name FROM schema_migrations WHERE version = 76;"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "UPDATE machines SET nc_dialect = 'SIEMENS_840D' WHERE id = 'machine-haas';"));

        // Haas keeps exactly the v61 rule: #10000-#10999 plus an M109 response variable.
        await ExecuteAsync(connection, Insert("machine-haas", 10501, 500, 10502, 10503, 10504));
        await RejectedAsync(connection, Update("machine-haas", 501, 505, 502, 503, 504));

        // FANUC macro B persists #500-#999 and has no M109 alias.
        await ExecuteAsync(connection, Insert("machine-fanuc", 501, 505, 502, 503, 504));
        await RejectedAsync(connection, Update("machine-fanuc", 10501, 500, 10502, 10503, 10504));

        // Okuma OSP uses common variables VC1-VC200; collisions stay rejected for every dialect.
        await ExecuteAsync(connection, Insert("machine-osp", 1, 2, 3, 4, 5));
        await RejectedAsync(connection, Update("machine-osp", 501, 505, 502, 503, 504));
        await RejectedAsync(connection, Update("machine-osp", 7, 7, 8, 9, 10));
        await ExecuteAsync(connection, Update("machine-osp", 11, 12, 13, 14, 15));

        Assert.Equal(3L, await ScalarAsync(connection, "SELECT COUNT(*) FROM cnc_verification_settings;"));
        Assert.Equal(15L, await ScalarAsync(connection,
            "SELECT event_sequence_variable FROM cnc_verification_settings WHERE machine_id = 'machine-osp';"));
    }

    private static async Task RejectedAsync(SqliteConnection connection, string sql)
    {
        var error = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, sql));
        Assert.Contains("NC dialect", error.Message, StringComparison.Ordinal);
    }

    private static string Insert(string machineId, int nonce, int response, int state, int release, int sequence) =>
        $"""
        INSERT INTO cnc_verification_settings(
            machine_id,dprint_transport,dprint_port,challenge_program_number,verify_program_number,
            custom_gcode_alias,nonce_variable,response_variable,verification_state_variable,
            release_token_variable,expected_macro_version,response_code_digits,verification_timeout_seconds,
            enabled,version,created_at,updated_at,finalize_program_number,event_sequence_variable)
        VALUES('{machineId}','HAAS_DPRNT_TCP',8080,9001,9002,NULL,{nonce},{response},{state},{release},
               6,6,120,0,1,'2026-09-18T00:00:00Z','2026-09-18T00:00:00Z',9003,{sequence});
        """;

    private static string Update(string machineId, int nonce, int response, int state, int release, int sequence) =>
        $"""
        UPDATE cnc_verification_settings
        SET nonce_variable={nonce},response_variable={response},verification_state_variable={state},
            release_token_variable={release},event_sequence_variable={sequence}
        WHERE machine_id='{machineId}';
        """;

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
